using System.Text.Json;

namespace DeSignMage.Feedback;

public enum SubmitStatus
{
    /// <summary>Discord accepted it ("Thanks, sent").</summary>
    Sent,
    /// <summary>Stored in the outbox; it goes out when she is online ("Saved, will send when online").</summary>
    Queued,
    /// <summary>Stored, but no webhook address is set up ("Sending isn't set up yet").</summary>
    NotConfigured,
    /// <summary>Could not even be stored.</summary>
    Failed,
}

public sealed record SubmitResult(SubmitStatus Status, string Id)
{
    public string Message => Status switch
    {
        SubmitStatus.Sent => "Thanks, sent.",
        SubmitStatus.Queued => "Saved, will send when online.",
        SubmitStatus.NotConfigured => "Saved on this computer. Sending isn't set up yet.",
        _ => "Sorry, the report could not be saved.",
    };
}

/// <summary>What happened to the crash reports found at startup, for the one-line notice.</summary>
public sealed record CrashNotice(int Count, bool Sent, bool Configured, string? RecoveredFolder, IReadOnlyList<string> RecoveredFiles)
{
    public string Text
    {
        get
        {
            string what = Count == 1 ? "A crash report from last time" : $"{Count} crash reports from last time";
            string state = Sent ? "was sent" : Configured ? "is saved and will be sent when online" : "was saved (sending isn't set up yet)";
            if (Count > 1) state = state.Replace("was ", "were ").Replace("is saved", "are saved");
            string design = RecoveredFiles.Count > 0 && RecoveredFolder is not null ? $" Your design was recovered to: {RecoveredFolder}" : "";
            return $"{what} {state}.{design}";
        }
    }
}

/// <summary>
/// The feedback system as the program uses it: builds reports, stores them in the outbox, sends them (now, and again every few
/// minutes in the background), and picks up crash reports left by an earlier run.
/// </summary>
public sealed class FeedbackService : IDisposable
{
    private readonly IReportSender? _sender;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public FeedbackService(AppPaths paths, IReportSender? sender, ReportBuilderOptions? options = null)
    {
        Paths = paths;
        Settings = new SettingsStore(paths.SettingsFile);
        _sender = sender;
        BuilderOptions = options ?? new ReportBuilderOptions();
        Outbox = new Outbox(paths.Outbox, paths.Expired);
        Crash = new CrashReporter(paths);
    }

    public AppPaths Paths { get; }
    public Outbox Outbox { get; }
    public CrashReporter Crash { get; }
    public ReportBuilderOptions BuilderOptions { get; set; }

    /// <summary>False when no webhook address is set up: reports are still saved in the outbox.</summary>
    public bool CanSend => _sender is not null;

    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>How long the "Send" button waits for Discord before falling back to "will send when online".</summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(20);

    // ---------------------------------------------------------------- the person's name

    /// <summary>The program's remembered values (this file also holds the name below). Shared by the whole program.</summary>
    public SettingsStore Settings { get; }

    public string? SavedName => Settings.Get<string?>("name", null);

    public void SaveName(string? name) => Settings.Set("name", name?.Trim() ?? "");

    // ---------------------------------------------------------------- sending

    /// <summary>
    /// Builds the report, stores it in the outbox, and tries to send it right away (up to <see cref="SendTimeout"/>).
    /// What was stored stays in the outbox until Discord says 2xx.
    /// </summary>
    public async Task<SubmitResult> SubmitAsync(ReportRequest request, CancellationToken ct = default)
    {
        string id;
        try
        {
            PreparedReport prepared;
            try { prepared = await Task.Run(() => ReportBuilder.Build(request, BuilderOptions), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Something odd in the attachments: keep the words, drop the rest.
                AppLog.Error("report", "Building the full report failed; saving the text only", ex);
                prepared = ReportBuilder.Build(request with { ScreenshotPng = null, Designs = [], LogLines = [], Notes = [.. request.Notes, "Attachments could not be prepared."] }, BuilderOptions);
            }
            id = Outbox.Add(prepared);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("report", "Could not save the report", ex);
            return new SubmitResult(SubmitStatus.Failed, "");
        }
        AppLog.Info("report", $"Report {id} ({request.Kind}) saved");

        if (_sender is null) return new SubmitResult(SubmitStatus.NotConfigured, id);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SendTimeout);
        var run = await Outbox.ProcessAsync(_sender, timeout.Token, onlyId: id, ignoreBackoff: true).ConfigureAwait(false);
        bool sent = run.Results.TryGetValue(id, out var result) && result.IsSent;
        return new SubmitResult(sent ? SubmitStatus.Sent : SubmitStatus.Queued, id);
    }

    // ---------------------------------------------------------------- crashes and background retries

    /// <summary>
    /// At startup: queues crash reports left by an earlier run, tries to send them (and anything else waiting), and describes the
    /// outcome for the notice. Null when there was no crash.
    /// </summary>
    public async Task<CrashNotice?> RecoverFromCrashAsync(CancellationToken ct = default)
    {
        IReadOnlyList<CrashRecord> queued;
        try { queued = CrashRecovery.QueuePending(Paths, Outbox, BuilderOptions); }
        catch (Exception ex) { AppLog.Error("crash", "Looking for earlier crashes failed", ex); return null; }
        if (queued.Count == 0) return null;

        if (_sender is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await Outbox.ProcessAsync(_sender, timeout.Token, ignoreBackoff: true).ConfigureAwait(false);
        }
        bool allSent = queued.All(c => c.OutboxId is null || Outbox.Get(c.OutboxId) is null);
        var withDesign = queued.FirstOrDefault(c => c.Recovered.Count > 0);
        return new CrashNotice(queued.Count, allSent && _sender is not null, _sender is not null, withDesign?.Directory, withDesign?.Recovered ?? []);
    }

    /// <summary>One background pass: queue crash reports written since startup, then send what is waiting. Safe to call at any time.</summary>
    public async Task RunBackgroundOnceAsync(CancellationToken ct = default)
    {
        try
        {
            CrashRecovery.QueuePending(Paths, Outbox, BuilderOptions);
            CrashRecovery.Prune(Paths, TimeSpan.FromDays(60), DateTime.UtcNow, id => Outbox.Get(id) is not null);
            if (_sender is null || Outbox.Count == 0) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var run = await Outbox.ProcessAsync(_sender, timeout.Token).ConfigureAwait(false);
            if (run.Sent > 0 || run.Failed > 0) AppLog.Info("outbox", $"Background pass: {run.Sent} sent, {run.Failed} still waiting");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Error("outbox", "Background pass failed", ex);
        }
    }

    /// <summary>Retries the outbox a few seconds after startup and then every <see cref="RetryInterval"/> until disposed.</summary>
    public void StartBackground()
    {
        if (_loop is not null) return;
        _loop = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StartupDelay, _stop.Token).ConfigureAwait(false);
                await RunBackgroundOnceAsync(_stop.Token).ConfigureAwait(false);
                using var timer = new PeriodicTimer(RetryInterval);
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                    await RunBackgroundOnceAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
    }
}
