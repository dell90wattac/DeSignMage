using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DeSignMage.Feedback;

/// <summary>An open document that an emergency save can write as .dsm (the app supplies the writer).</summary>
public sealed record CrashDocument(string Name, Action<string> SaveTo);

/// <summary>What <c>crash.json</c> says about one crash folder.</summary>
public sealed class CrashRecord
{
    public string Id { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    /// <summary>Which hook caught it: AppDomain, TaskScheduler, Dispatcher.</summary>
    public string Source { get; set; } = "";
    public bool Fatal { get; set; }
    public string ExceptionType { get; set; } = "";
    public string Summary { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string Windows { get; set; } = "";
    /// <summary>Files saved by the emergency save (names inside the folder).</summary>
    public List<string> Recovered { get; set; } = [];
    public string? DocumentSummary { get; set; }
    /// <summary>The system block (version, build date, Windows, .NET, displays) and the undo-history names at the time of the crash.</summary>
    public string? SystemInfoText { get; set; }
    public List<string> UndoHistory { get; set; } = [];
    /// <summary>"new" until the report is in the outbox, then "queued".</summary>
    public string Status { get; set; } = "new";
    public string? OutboxId { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Directory { get; set; } = "";
}

/// <summary>
/// Crash capture: on an unhandled exception writes <c>crash\crash-&lt;time&gt;\</c> (the exception and stack, the recent log, system info)
/// and does an emergency save of the open documents as .dsm. Sending happens on the next launch (<see cref="CrashRecovery"/>).
/// </summary>
public sealed class CrashReporter
{
    private const int MaxReportsPerSession = 6;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly AppPaths _paths;
    private readonly Func<DateTime> _utcNow;
    private readonly HashSet<string> _seen = [];
    private readonly object _lock = new();
    [ThreadStatic] private static bool _inside;
    private int _written;
    private bool _installed;

    public CrashReporter(AppPaths paths, Func<DateTime>? utcNow = null)
    {
        _paths = paths;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The open documents (called when a crash happens).</summary>
    public Func<IReadOnlyList<CrashDocument>>? Documents { get; set; }

    public Func<SystemInfo>? SystemInfoProvider { get; set; }
    public Func<IReadOnlyList<string>>? UndoHistory { get; set; }
    public Func<string?>? DocumentSummary { get; set; }

    /// <summary>Called after a fatal crash was written (the app shows "your design was saved to ..."). Must not throw.</summary>
    public Action<CrashRecord>? FatalNotice { get; set; }

    /// <summary>Reports written in this run.</summary>
    public int Written => _written;

    /// <summary>Hooks the exceptions nobody caught: any thread, and tasks nobody awaited. (Avalonia's dispatcher hook lives in the app.)</summary>
    public void Install()
    {
        if (_installed) return;
        _installed = true;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Report(ex, "AppDomain", fatal: e.IsTerminating);
            else AppLog.Error("crash", $"Unhandled non-exception object: {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Usually a forgotten background task; the program is fine, but a developer should hear about it.
            Report(e.Exception.Flatten(), "TaskScheduler", fatal: false, saveDocuments: false);
            e.SetObserved();
        };
    }

    /// <summary>
    /// Writes a crash report folder. <paramref name="fatal"/>: the program is about to die (always written, with an emergency save).
    /// A non-fatal one is written once per kind of error and at most a few times per run. Never throws; null if nothing was written.
    /// </summary>
    public CrashRecord? Report(Exception ex, string source, bool fatal, bool saveDocuments = true)
    {
        if (_inside) return null;   // an error while writing the report
        _inside = true;
        try
        {
            string signature = Signature(ex);
            lock (_lock)
            {
                if (!fatal && (_written >= MaxReportsPerSession || !_seen.Add(signature))) return null;
                _seen.Add(signature);
                _written++;
            }
            AppLog.Error("crash", $"{(fatal ? "FATAL " : "")}unhandled exception in {source}", ex);
            AppLog.Flush();
            var record = Write(ex, source, fatal, saveDocuments);
            if (fatal && record is not null)
            {
                try { FatalNotice?.Invoke(record); }
                catch (Exception) { }
            }
            return record;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _inside = false;
        }
    }

    private CrashRecord? Write(Exception ex, string source, bool fatal, bool saveDocuments)
    {
        var now = _utcNow();
        var id = string.Create(CultureInfo.InvariantCulture, $"crash-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}");
        var dir = Path.Combine(_paths.Crash, id);
        Directory.CreateDirectory(dir);

        SystemInfo? info = null;
        try { info = SystemInfoProvider?.Invoke(); } catch (Exception) { }
        var record = new CrashRecord
        {
            Id = id,
            CreatedUtc = now,
            Source = source,
            Fatal = fatal,
            ExceptionType = ex.GetType().FullName ?? ex.GetType().Name,
            Summary = Redactor.Scrub(TextUtil.Truncate($"{ex.GetType().Name}: {ex.Message}".Replace('\n', ' ').Replace('\r', ' '), 240)),
            AppVersion = info?.AppVersion ?? "",
            Windows = info?.WindowsShort ?? "",
            Directory = dir,
        };
        try { record.DocumentSummary = DocumentSummary?.Invoke(); } catch (Exception) { }

        // Emergency save first: the design matters more than the report.
        var notes = new List<string>();
        if (saveDocuments)
        {
            IReadOnlyList<CrashDocument> docs = [];
            try { docs = Documents?.Invoke() ?? []; }
            catch (Exception e) { notes.Add($"Could not list the open documents ({e.GetType().Name})."); }
            int n = 0;
            foreach (var doc in docs)
            {
                var file = $"recovered-{++n}-{TextUtil.SafeFileName(Path.GetFileNameWithoutExtension(doc.Name), "design")}.dsm";
                try
                {
                    doc.SaveTo(Path.Combine(dir, file));
                    record.Recovered.Add(file);
                }
                catch (Exception e)
                {
                    notes.Add($"Emergency save of {doc.Name} failed ({e.GetType().Name}: {Redactor.Scrub(e.Message)}).");
                    try { File.Delete(Path.Combine(dir, file)); } catch (Exception) { }
                }
            }
        }

        var report = new StringBuilder();
        report.AppendLine($"DeSign Mage crash report {id}");
        report.AppendLine($"Time (UTC) : {now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"Caught by  : {source}{(fatal ? " (the program was terminating)" : " (the program kept running)")}");
        report.AppendLine().AppendLine(Redactor.Scrub(ex.ToString())).AppendLine();
        if (info is not null) report.AppendLine("== System ==").AppendLine(info.ToText()).AppendLine();
        if (record.DocumentSummary is { Length: > 0 } summary) report.AppendLine("== Document ==").AppendLine(Redactor.Scrub(summary)).AppendLine();
        record.SystemInfoText = info?.ToText();
        try
        {
            var undo = UndoHistory?.Invoke() ?? [];
            record.UndoHistory = undo.TakeLast(100).ToList();
            if (undo.Count > 0)
            {
                report.AppendLine("== Recent undo history (oldest first) ==");
                foreach (var u in undo.TakeLast(100)) report.AppendLine("  " + u);
                report.AppendLine();
            }
        }
        catch (Exception) { }
        report.AppendLine(record.Recovered.Count > 0 ? $"Recovered designs: {string.Join(", ", record.Recovered)}" : "No design was recovered.");
        foreach (var note in notes) report.AppendLine(note);

        File.WriteAllText(Path.Combine(dir, "crash-report.txt"), report.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "log.txt"), string.Join("\r\n", AppLog.Tail(500)) + "\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "crash.json"), JsonSerializer.Serialize(record, Json));
        return record;
    }

    private static string Signature(Exception ex)
    {
        var frame = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return $"{ex.GetType().FullName}|{frame}";
    }
}

/// <summary>Finds crash folders that were never sent, queues them in the outbox, and cleans up old ones.</summary>
public static class CrashRecovery
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>All crash folders, newest first.</summary>
    public static IReadOnlyList<CrashRecord> List(AppPaths paths)
    {
        var records = new List<CrashRecord>();
        if (!Directory.Exists(paths.Crash)) return records;
        foreach (var dir in Directory.EnumerateDirectories(paths.Crash))
        {
            try
            {
                var file = Path.Combine(dir, "crash.json");
                if (!File.Exists(file)) continue;
                var record = JsonSerializer.Deserialize<CrashRecord>(File.ReadAllText(file), Json);
                if (record is null) continue;
                record.Directory = dir;
                records.Add(record);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return records.OrderByDescending(r => r.CreatedUtc).ToList();
    }

    /// <summary>Builds the crash's report from its folder.</summary>
    public static PreparedReport BuildReport(CrashRecord crash, ReportBuilderOptions? options = null)
    {
        // Only the exception part of crash-report.txt: the report file adds the system block, document, undo history and log itself.
        string text = ReadText(Path.Combine(crash.Directory, "crash-report.txt"));
        int system = text.IndexOf("== System ==", StringComparison.Ordinal);
        if (system > 0) text = text[..system];
        else if (text.IndexOf("== Document ==", StringComparison.Ordinal) is > 0 and var doc) text = text[..doc];
        var designs = crash.Recovered
            .Where(f => File.Exists(Path.Combine(crash.Directory, f)))
            .Select(f => new ReportFile(f, "application/octet-stream", File.ReadAllBytes(Path.Combine(crash.Directory, f))))
            .ToList();
        var lines = ReadText(Path.Combine(crash.Directory, "log.txt")).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        var request = new ReportRequest
        {
            Kind = ReportKind.Crash,
            Description = $"The program stopped unexpectedly ({(crash.Fatal ? "fatal" : "recovered")}, caught by {crash.Source}).",
            CrashSummary = crash.Summary,
            CrashText = text,
            SystemInfoText = crash.SystemInfoText,
            UndoHistory = crash.UndoHistory,
            AppVersion = crash.AppVersion,
            Windows = crash.Windows,
            DocumentSummary = crash.DocumentSummary,
            Designs = designs,
            LogLines = lines,
            Notes = crash.Recovered.Count == 0 ? ["No open design could be saved."] : [],
            Id = crash.Id,
            CreatedUtc = crash.CreatedUtc,
        };
        return ReportBuilder.Build(request, options);
    }

    /// <summary>Puts every unsent crash into the outbox. Returns the ones that were queued now.</summary>
    public static IReadOnlyList<CrashRecord> QueuePending(AppPaths paths, Outbox outbox, ReportBuilderOptions? options = null)
    {
        var queued = new List<CrashRecord>();
        foreach (var crash in List(paths).Where(r => r.Status == "new").OrderBy(r => r.CreatedUtc))
        {
            try
            {
                var prepared = BuildReport(crash, options);
                crash.OutboxId = outbox.Add(prepared);
                crash.Status = "queued";
                File.WriteAllText(Path.Combine(crash.Directory, "crash.json"), JsonSerializer.Serialize(crash, Json));
                queued.Add(crash);
                AppLog.Info("crash", $"Queued the crash report {crash.Id} for sending");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                AppLog.Error("crash", $"Could not queue crash report {crash.Id}", ex);
            }
        }
        return queued;
    }

    /// <summary>Removes crash folders that were sent, older than <paramref name="maxAge"/>. Unsent ones stay.</summary>
    public static void Prune(AppPaths paths, TimeSpan maxAge, DateTime utcNow, Func<string, bool>? isStillInOutbox = null)
    {
        foreach (var crash in List(paths))
        {
            if (crash.Status != "queued" || utcNow - crash.CreatedUtc < maxAge) continue;
            if (crash.OutboxId is { } id && isStillInOutbox?.Invoke(id) == true) continue;
            try { Directory.Delete(crash.Directory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}
