using System.Text.Json;

namespace DeSignMage.Feedback;

public enum SubmitStatus
{
    /// <summary>The relay accepted it ("Thanks, sent").</summary>
    Sent,
    /// <summary>Stored in the outbox; it goes out when she is online ("Saved, will send when online").</summary>
    Queued,
    /// <summary>Stored, but this run has no sender (tests and headless runs: "Sending isn't set up yet").</summary>
    NotConfigured,
    /// <summary>Could not even be stored.</summary>
    Failed,
}

/// <param name="Why">For <see cref="SubmitStatus.Queued"/>: what the relay or the network said, so the message can be specific.</param>
public sealed record SubmitResult(SubmitStatus Status, string Id, SendStatus? Why = null)
{
    public string Message => Status switch
    {
        SubmitStatus.Sent => "Thanks, sent.",
        SubmitStatus.Queued => Why switch
        {
            SendStatus.RateLimited => "Saved. A lot of reports were sent from this computer lately, so this one will be sent a little later.",
            SendStatus.Rejected or SendStatus.ServerError => "Saved. The feedback service isn't taking reports right now; this one will be sent later.",
            _ => "Saved, will send when online.",
        },
        SubmitStatus.NotConfigured => "Saved on this computer. Sending isn't set up yet.",
        _ => "Sorry, the report could not be saved.",
    };
}

/// <summary>The remembered attachment choices in the settings file. Both are off until the person ticks them once.</summary>
public static class FeedbackSettings
{
    public const string IncludeDesign = "feedback.includeDesign";
    public const string IncludeScreenshot = "feedback.includeScreenshot";

    /// <summary>"Always send crash reports without asking": off until the person ticks it in the crash prompt.</summary>
    public const string AlwaysSendCrashes = "feedback.alwaysSendCrashes";
}

/// <summary>Crashes from an earlier run that wait for the person's answer: how many, and where the recovered design is.</summary>
public sealed record CrashPrompt(int Count, string? RecoveredFolder, IReadOnlyList<string> RecoveredFiles);

/// <summary>What happened to the crash reports found at startup, for the one-line notice.</summary>
/// <param name="Declined">The person chose not to send: nothing left the computer.</param>
public sealed record CrashNotice(int Count, bool Sent, bool Configured, string? RecoveredFolder, IReadOnlyList<string> RecoveredFiles, bool Declined = false)
{
    public string Text
    {
        get
        {
            string what = Count == 1 ? "A crash report from last time" : $"{Count} crash reports from last time";
            string state = Declined ? "was not sent (as you chose)"
                : Sent ? "was sent" : Configured ? "is saved and will be sent when online" : "was saved (sending isn't set up yet)";
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

    /// <summary>False when this run has no sender (tests, headless runs): reports are still saved in the outbox.</summary>
    public bool CanSend => _sender is not null;

    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>How long the "Send" button waits for the relay before falling back to "will send when online".</summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(20);

    // ---------------------------------------------------------------- the person's name

    /// <summary>The program's remembered values (this file also holds the name below). Shared by the whole program.</summary>
    public SettingsStore Settings { get; }

    public string? SavedName => Settings.Get<string?>("name", null);

    public void SaveName(string? name) => Settings.Set("name", name?.Trim() ?? "");

    // ---------------------------------------------------------------- what may leave the computer

    /// <summary>"Include my design" as the person last chose it (off until they tick it). Also decides whether crash reports carry the design.</summary>
    public bool IncludeDesign
    {
        get => Settings.Get(FeedbackSettings.IncludeDesign, false);
        set => Settings.Set(FeedbackSettings.IncludeDesign, value);
    }

    public bool IncludeScreenshot
    {
        get => Settings.Get(FeedbackSettings.IncludeScreenshot, false);
        set => Settings.Set(FeedbackSettings.IncludeScreenshot, value);
    }

    /// <summary>
    /// "Always send crash reports": a crash report is only ever sent after the person said yes, either in the crash prompt (this run)
    /// or by turning this on (every run). Off until they do.
    /// </summary>
    public bool AlwaysSendCrashes
    {
        get => Settings.Get(FeedbackSettings.AlwaysSendCrashes, false);
        set => Settings.Set(FeedbackSettings.AlwaysSendCrashes, value);
    }

    /// <summary>The builder options for a crash report written now: the design is attached only when the person allowed designs.</summary>
    private ReportBuilderOptions CrashOptions() => BuilderOptions with { AttachCrashDesigns = BuilderOptions.AttachCrashDesigns && IncludeDesign };

    // ---------------------------------------------------------------- sending

    /// <summary>
    /// Builds the report, stores it in the outbox, and tries to send it right away (up to <see cref="SendTimeout"/>).
    /// What was stored stays in the outbox until the relay says 2xx.
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
        return new SubmitResult(sent ? SubmitStatus.Sent : SubmitStatus.Queued, id, sent ? null : result?.Status);
    }

    // ---------------------------------------------------------------- crashes and background retries

    /// <summary>
    /// At startup: the crashes of earlier runs that wait for an answer, or null when there are none or the person already chose
    /// "always send" (then <see cref="RecoverFromCrashAsync"/> handles them). Nothing is queued or sent by looking.
    /// </summary>
    public CrashPrompt? FindCrashesToAsk()
    {
        if (AlwaysSendCrashes) return null;
        try
        {
            var pending = CrashRecovery.Pending(Paths);
            if (pending.Count == 0) return null;
            var withDesign = pending.FirstOrDefault(c => c.Recovered.Count > 0);
            return new CrashPrompt(pending.Count, withDesign?.Directory, withDesign?.Recovered ?? []);
        }
        catch (Exception ex) { AppLog.Error("crash", "Looking for earlier crashes failed", ex); return null; }
    }

    /// <summary>
    /// At startup, for a person who chose "always send crash reports": queues the crash reports left by an earlier run, tries to send
    /// them (and anything else waiting), and describes the outcome for the notice. Null when there was no crash or sending crash
    /// reports is not allowed yet (then nothing is queued and nothing is sent: use <see cref="FindCrashesToAsk"/> and ask).
    /// </summary>
    public async Task<CrashNotice?> RecoverFromCrashAsync(CancellationToken ct = default)
    {
        if (!AlwaysSendCrashes) return null;
        return await QueueAndSendCrashesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The person's answer to the crash prompt. <paramref name="send"/>: queue and send the waiting crash reports now (and when
    /// <paramref name="always"/>, remember to send future ones without asking). Not sending: nothing leaves the computer; the crash
    /// folders (and the recovered design) stay, and these crashes are not asked about again.
    /// </summary>
    public async Task<CrashNotice?> AnswerCrashPromptAsync(bool send, bool always = false, CancellationToken ct = default)
    {
        if (!send)
        {
            var prompt = FindCrashesToAsk();
            if (prompt is null) return null;
            CrashRecovery.Decline(Paths);
            return new CrashNotice(prompt.Count, false, _sender is not null, prompt.RecoveredFolder, prompt.RecoveredFiles, Declined: true);
        }
        if (always) AlwaysSendCrashes = true;
        return await QueueAndSendCrashesAsync(ct).ConfigureAwait(false);
    }

    private async Task<CrashNotice?> QueueAndSendCrashesAsync(CancellationToken ct)
    {
        IReadOnlyList<CrashRecord> queued;
        try { queued = CrashRecovery.QueuePending(Paths, Outbox, CrashOptions()); }
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

    /// <summary>
    /// One background pass: with "always send crash reports" on, queue crash reports written since startup; then send what is waiting
    /// (feedback the person sent, and crash reports they agreed to). Safe to call at any time.
    /// </summary>
    public async Task RunBackgroundOnceAsync(CancellationToken ct = default)
    {
        try
        {
            if (AlwaysSendCrashes) CrashRecovery.QueuePending(Paths, Outbox, CrashOptions());
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
