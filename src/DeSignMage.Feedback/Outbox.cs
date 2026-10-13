using System.Text.Json;

namespace DeSignMage.Feedback;

public enum SendStatus
{
    Sent,
    /// <summary>No connection, a timeout, or the attempt was cancelled: try again later.</summary>
    Offline,
    RateLimited,
    /// <summary>Discord answered 5xx.</summary>
    ServerError,
    /// <summary>Discord refused the request (4xx other than 429): wrong address, deleted webhook, ...</summary>
    Rejected,
    NotConfigured,
}

public sealed record SendResult(SendStatus Status, string? Detail = null, TimeSpan? RetryAfter = null, int? HttpStatus = null, bool AttachmentsDropped = false)
{
    public bool IsSent => Status == SendStatus.Sent;
}

/// <summary>Delivers one outbox report. The real one talks to Discord; tests use fakes.</summary>
public interface IReportSender
{
    Task<SendResult> SendAsync(OutboxItem item, CancellationToken ct);
}

/// <summary>A report waiting in the outbox: its manifest and the folder holding the attachments.</summary>
public sealed class OutboxItem(string directory, OutgoingReport report)
{
    public string Directory { get; } = directory;
    public OutgoingReport Report { get; } = report;
    public string Id => Report.Id;

    public byte[] ReadFile(string fileName) => File.ReadAllBytes(Path.Combine(Directory, fileName));
}

public sealed class OutboxRunResult
{
    public int Sent { get; internal set; }
    public int Failed { get; internal set; }
    public int Skipped { get; internal set; }
    public Dictionary<string, SendResult> Results { get; } = [];
}

/// <summary>
/// Reports on disk (<c>feedback\outbox\&lt;id&gt;\report.json</c> + attachments). A report goes in here first and comes out only after
/// Discord answered 2xx, so nothing is lost when she is offline, the program closes, or a crash follows.
/// </summary>
public sealed class Outbox
{
    private const string Manifest = "report.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly string _directory;
    private readonly string? _expiredDirectory;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="expiredDirectory">Reports older than <see cref="MaxAge"/> are moved here (never deleted).</param>
    public Outbox(string directory, string? expiredDirectory = null, Func<DateTime>? utcNow = null)
    {
        _directory = directory;
        _expiredDirectory = expiredDirectory;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string Directory => _directory;

    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Stores the report (attachments first, then the manifest, then an atomic rename). Returns its id.</summary>
    public string Add(PreparedReport prepared)
    {
        var report = prepared.Report;
        var final = Path.Combine(_directory, TextUtil.SafeFileName(report.Id, "report"));
        var partial = final + ".partial";
        System.IO.Directory.CreateDirectory(_directory);
        if (System.IO.Directory.Exists(partial)) System.IO.Directory.Delete(partial, true);
        System.IO.Directory.CreateDirectory(partial);
        try
        {
            foreach (var f in prepared.Files) File.WriteAllBytes(Path.Combine(partial, f.FileName), f.Data);
            File.WriteAllText(Path.Combine(partial, Manifest), JsonSerializer.Serialize(report, Json));
            if (System.IO.Directory.Exists(final)) System.IO.Directory.Delete(final, true);
            System.IO.Directory.Move(partial, final);
        }
        catch
        {
            try { System.IO.Directory.Delete(partial, true); } catch (Exception) { }
            throw;
        }
        AppLog.Info("outbox", $"Stored report {report.Id} ({report.Kind}, {report.Files.Count} files, {TextUtil.FormatBytes(report.Files.Sum(f => f.Size))})");
        return report.Id;
    }

    public IReadOnlyList<OutboxItem> List()
    {
        var items = new List<OutboxItem>();
        if (!System.IO.Directory.Exists(_directory)) return items;
        foreach (var dir in System.IO.Directory.EnumerateDirectories(_directory))
        {
            if (dir.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            {
                // A crash in the middle of Add; the report was never promised to anyone.
                try { if (System.IO.Directory.GetLastWriteTimeUtc(dir) < _utcNow().AddHours(-1)) System.IO.Directory.Delete(dir, true); } catch (Exception) { }
                continue;
            }
            if (Read(dir) is { } item) items.Add(item);
        }
        return items.OrderBy(i => i.Report.CreatedUtc).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    public OutboxItem? Get(string id) => Read(Path.Combine(_directory, TextUtil.SafeFileName(id, "report")));

    public int Count => List().Count;

    public void Delete(string id)
    {
        var dir = Path.Combine(_directory, TextUtil.SafeFileName(id, "report"));
        try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("outbox", $"Could not delete report {id}: {ex.GetType().Name}"); }
    }

    private static OutboxItem? Read(string dir)
    {
        var path = Path.Combine(dir, Manifest);
        try
        {
            if (!File.Exists(path)) return null;
            var report = JsonSerializer.Deserialize<OutgoingReport>(File.ReadAllText(path), Json);
            return report is null || report.Id.Length == 0 ? null : new OutboxItem(dir, report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("outbox", $"Unreadable report in {Path.GetFileName(dir)}: {ex.GetType().Name}");
            return null;
        }
    }

    private static void Save(OutboxItem item)
    {
        var path = Path.Combine(item.Directory, Manifest);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(item.Report, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Tries to deliver waiting reports, oldest first; a report is deleted only after a successful send. Stops early when the network is
    /// down or Discord rate-limits (the same webhook serves every report). <paramref name="onlyId"/> limits it to one report;
    /// <paramref name="ignoreBackoff"/> also tries reports that are waiting out a back-off.
    /// </summary>
    public async Task<OutboxRunResult> ProcessAsync(IReportSender sender, CancellationToken ct, string? onlyId = null, bool ignoreBackoff = false)
    {
        var run = new OutboxRunResult();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var item in List())
            {
                if (ct.IsCancellationRequested) break;
                if (onlyId is not null && item.Id != onlyId) continue;
                var now = _utcNow();

                if (_expiredDirectory is not null && now - item.Report.CreatedUtc > MaxAge)
                {
                    Expire(item);
                    run.Skipped++;
                    continue;
                }
                if (!ignoreBackoff && item.Report.NotBeforeUtc is { } notBefore && notBefore > now)
                {
                    run.Skipped++;
                    continue;
                }

                SendResult result;
                try { result = await sender.SendAsync(item, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { result = new SendResult(SendStatus.Offline, "cancelled"); }
                catch (Exception ex)
                {
                    result = new SendResult(SendStatus.Offline, $"{ex.GetType().Name}: {Redactor.Scrub(ex.Message)}");
                    AppLog.Error("outbox", $"Sending report {item.Id} failed unexpectedly", ex);
                }
                run.Results[item.Id] = result;

                if (result.IsSent)
                {
                    Delete(item.Id);
                    run.Sent++;
                    AppLog.Info("outbox", $"Report {item.Id} delivered" + (result.AttachmentsDropped ? " (without attachments: Discord refused them)" : ""));
                    continue;
                }

                run.Failed++;
                item.Report.Attempts++;
                item.Report.LastError = Redactor.Scrub($"{result.Status}{(result.HttpStatus is { } code ? $" (HTTP {code})" : "")}{(result.Detail is { } d ? ": " + d : "")}");
                item.Report.NotBeforeUtc = result.Status switch
                {
                    SendStatus.RateLimited => now + (result.RetryAfter ?? TimeSpan.FromSeconds(30)) + TimeSpan.FromSeconds(1),
                    SendStatus.ServerError or SendStatus.Rejected => now + Backoff(item.Report.Attempts),
                    _ => null,
                };
                try { Save(item); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                AppLog.Warn("outbox", $"Report {item.Id} not delivered yet: {item.Report.LastError}");

                // Nothing else will get through either right now.
                if (result.Status is SendStatus.Offline or SendStatus.RateLimited or SendStatus.NotConfigured) break;
            }
        }
        finally
        {
            _gate.Release();
        }
        return run;
    }

    internal static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(360, 5 * Math.Pow(2, Math.Max(0, attempts - 1))));

    private void Expire(OutboxItem item)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_expiredDirectory!);
            var target = Path.Combine(_expiredDirectory!, Path.GetFileName(item.Directory));
            if (System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, true);
            System.IO.Directory.Move(item.Directory, target);
            AppLog.Warn("outbox", $"Report {item.Id} could not be delivered for {MaxAge.TotalDays:0} days; moved to the expired folder");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
