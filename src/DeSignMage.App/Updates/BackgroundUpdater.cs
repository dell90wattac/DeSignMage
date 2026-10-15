namespace DeSignMage.App.Updates;

internal enum UpdateState
{
    /// <summary>Portable or development copy: updates are not automatic.</summary>
    NotInstalled,
    /// <summary>Not looked yet this session.</summary>
    Idle,
    Checking,
    Downloading,
    /// <summary>The newest version is already running.</summary>
    UpToDate,
    /// <summary>An update is on this computer; it is installed at the next fresh start.</summary>
    Downloaded,
    /// <summary>The last check or download did not work (offline, server down); it is tried again later.</summary>
    CheckFailed,
}

internal sealed record UpdateStatus(UpdateState State, string? Version = null, DateTime? CheckedAt = null)
{
    public static readonly UpdateStatus Unknown = new(UpdateState.Idle);

    /// <summary>The line Help ▸ About shows (<paramref name="current"/> is the running version).</summary>
    public string Describe(string current) => State switch
    {
        UpdateState.NotInstalled => "This copy does not update itself. To update it, download the new version from the Releases page.",
        UpdateState.Idle => "Updates are installed automatically; the next check happens shortly.",
        UpdateState.Checking => "Checking for a new version…",
        UpdateState.Downloading => $"Downloading version {Version}…",
        UpdateState.UpToDate => CheckedAt is { } t ? $"You have the newest version (checked {t:HH:mm})." : "You have the newest version.",
        UpdateState.Downloaded => $"Version {Version} is downloaded. It is installed the next time you start DeSign Mage (close every DeSign Mage window first).",
        UpdateState.CheckFailed => "Could not look for a new version just now (no connection?). It is tried again later.",
        _ => "",
    };
}

/// <summary>
/// While the program runs: look for a newer version now and then and download it quietly. It never installs, never restarts and
/// never shows anything; the installed update is offered at the next fresh start (<see cref="UpdateGate"/>).
/// </summary>
internal sealed class BackgroundUpdater(IUpdateClient client, Action<string> log, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private UpdateStatus _status = UpdateStatus.Unknown;

    public UpdateStatus Status
    {
        get => _status;
        private set { _status = value; Changed?.Invoke(); }
    }

    /// <summary>Raised when <see cref="Status"/> changed, on a background thread.</summary>
    public event Action? Changed;

    /// <summary>The check loop: first check shortly after start, then every few hours, until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (!SafeIsInstalled()) { Status = new UpdateStatus(UpdateState.NotInstalled); return; }
            await _delay(FirstCheckDelay, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                await CheckOnceAsync(ct).ConfigureAwait(false);
                await _delay(CheckInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One look: an update already on the computer is reported as such; a newer one is downloaded. Never throws.</summary>
    public async Task CheckOnceAsync(CancellationToken ct)
    {
        try
        {
            if (!SafeIsInstalled()) { Status = new UpdateStatus(UpdateState.NotInstalled); return; }
            if (client.DownloadedUpdate() is { } ready) { Status = new UpdateStatus(UpdateState.Downloaded, ready.Version, DateTime.Now); return; }

            Status = new UpdateStatus(UpdateState.Checking);
            var offer = await client.CheckAsync(ct).ConfigureAwait(false);
            if (offer is null) { Status = new UpdateStatus(UpdateState.UpToDate, client.CurrentVersion, DateTime.Now); return; }

            log($"background: version {offer.Version} is available, downloading");
            Status = new UpdateStatus(UpdateState.Downloading, offer.Version);
            await client.DownloadAsync(offer, _ => { }, ct).ConfigureAwait(false);
            log($"background: version {offer.Version} downloaded; it is installed at the next fresh start");
            Status = new UpdateStatus(UpdateState.Downloaded, offer.Version, DateTime.Now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log($"background update check failed ({ex.GetType().Name}: {ex.Message}); trying again later");
            Status = new UpdateStatus(UpdateState.CheckFailed, CheckedAt: DateTime.Now);
        }
    }

    private bool SafeIsInstalled()
    {
        try { return client.IsInstalled; }
        catch (Exception) { return false; }
    }
}
