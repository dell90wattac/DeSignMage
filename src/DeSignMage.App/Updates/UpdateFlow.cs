namespace DeSignMage.App.Updates;

internal enum UpdateStep
{
    /// <summary>Not started.</summary>
    Idle,
    /// <summary>Downloading; <see cref="UpdateFlow.Percent"/> says how far.</summary>
    Downloading,
    /// <summary>On this computer and waiting for the person to press the button.</summary>
    Ready,
    /// <summary>Handed over to the installer; the program is closing.</summary>
    Applying,
    /// <summary>The download or the installation failed: the program starts normally and tries again at the next fresh start.</summary>
    Failed,
}

/// <summary>
/// What the "Update required" window does, without any window: download (when the update is not on the computer yet), wait for the
/// person, then install and restart. A failure never throws; it ends in <see cref="UpdateStep.Failed"/> and is logged.
/// </summary>
internal sealed class UpdateFlow(IUpdateClient client, GateDecision decision, IReadOnlyList<string> restartArgs, Action<string> log)
{
    private int _percent;

    public UpdateOffer Offer { get; } = decision.Offer ?? throw new ArgumentException("The decision has no update.", nameof(decision));
    public UpdateStep Step { get; private set; }
    public int Percent => _percent;
    /// <summary>A sentence for the person when <see cref="Step"/> is <see cref="UpdateStep.Failed"/>.</summary>
    public string? FailureText { get; private set; }

    /// <summary>Raised when <see cref="Step"/> or <see cref="Percent"/> changed, on whichever thread did it.</summary>
    public event Action? Changed;

    /// <summary>Gets the update onto this computer (immediately ready when it already is).</summary>
    public async Task PrepareAsync(CancellationToken ct = default)
    {
        if (decision.Downloaded) { SetStep(UpdateStep.Ready); return; }
        try
        {
            SetStep(UpdateStep.Downloading);
            await client.DownloadAsync(Offer, p => { _percent = Math.Clamp(p, 0, 100); Changed?.Invoke(); }, ct).ConfigureAwait(false);
            _percent = 100;
            SetStep(UpdateStep.Ready);
        }
        catch (Exception ex)   // including a download given up by the stall limit
        {
            Fail($"downloading version {Offer.Version} failed ({ex.GetType().Name}: {ex.Message})",
                 "The update could not be downloaded. DeSign Mage will start now and try again the next time you open it.");
        }
    }

    /// <summary>Installs and restarts. Returns only when that did not work (or in a test); the real client ends the process.</summary>
    public void Apply()
    {
        if (Step != UpdateStep.Ready) return;
        try
        {
            SetStep(UpdateStep.Applying);
            client.ApplyAndRestart(Offer, restartArgs);
        }
        catch (Exception ex)
        {
            Fail($"installing version {Offer.Version} failed ({ex.GetType().Name}: {ex.Message})",
                 "The update could not be installed. DeSign Mage will start now and try again the next time you open it.");
        }
    }

    private void Fail(string logText, string personText)
    {
        log(logText);
        FailureText = personText;
        SetStep(UpdateStep.Failed);
    }

    private void SetStep(UpdateStep step)
    {
        Step = step;
        Changed?.Invoke();
    }
}
