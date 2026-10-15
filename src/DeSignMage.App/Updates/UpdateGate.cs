namespace DeSignMage.App.Updates;

internal enum StartupAction
{
    /// <summary>Open the program as usual (nothing to install, or nothing that may be installed now).</summary>
    StartNormally,
    /// <summary>Show the "Update required" window first.</summary>
    RequireUpdate,
    /// <summary>Another copy is installing an update: this one just ends, so nothing runs under the installer.</summary>
    Exit,
}

/// <param name="Reason">Why, in a sentence for the log.</param>
/// <param name="Downloaded">For <see cref="StartupAction.RequireUpdate"/>: the update is already on this computer (nothing to download).</param>
internal sealed record GateDecision(StartupAction Action, string Reason, UpdateOffer? Offer = null, bool Downloaded = false);

/// <summary>
/// The rule for every start of the program: only a *fresh* start (no other copy running) of an *installed* copy may require an update,
/// and a network problem never keeps anyone out. Everything else starts normally; an update that cannot be applied now waits for the
/// next fresh start.
/// </summary>
internal sealed class UpdateGate(IUpdateClient client, IInstanceGuard guard, Action<string> log, TimeSpan? checkTimeout = null)
{
    /// <summary>How long a start may wait for the update server (it runs while the window is being built, so usually nothing is waited for).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(4);

    private readonly TimeSpan _timeout = checkTimeout ?? DefaultTimeout;

    public async Task<GateDecision> DecideAsync(CancellationToken ct = default)
    {
        var decision = await DecideCoreAsync(ct).ConfigureAwait(false);
        // From this moment a copy started by a second double-click must not start next to the installer (the window itself follows a moment later).
        if (decision.Action == StartupAction.RequireUpdate) guard.SetUpdating(true);
        return decision;
    }

    private async Task<GateDecision> DecideCoreAsync(CancellationToken ct)
    {
        try
        {
            if (!client.IsInstalled)
                return Normal("not an installed copy (portable or development run): no automatic updates");
            if (guard.UpdateRunningElsewhere)
                return new GateDecision(StartupAction.Exit, "another copy is installing an update");
            if (!guard.IsOnlyInstance)
                return Normal("another copy is running: an update waits for the next fresh start");

            if (client.DownloadedUpdate() is { } ready)
                return Require(ready, downloaded: true, "an update was downloaded earlier");

            var check = client.CheckAsync(ct);
            _ = check.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);   // a check that outlives the wait must not surface later
            var offer = await check.WaitAsync(_timeout, ct).ConfigureAwait(false);
            return offer is null ? Normal("up to date") : Require(offer, downloaded: false, "a newer version is available");
        }
        catch (TimeoutException)
        {
            return Normal($"the update server did not answer within {_timeout.TotalSeconds:0} s: starting without an update check");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log($"update check failed ({ex.GetType().Name}: {ex.Message}): starting normally, will try again next time");
            return Normal("the update check failed");
        }
    }

    private GateDecision Normal(string reason) => new(StartupAction.StartNormally, reason);

    private static GateDecision Require(UpdateOffer offer, bool downloaded, string why) =>
        new(StartupAction.RequireUpdate, $"{why} ({offer.Version})", offer, downloaded);
}
