namespace DeSignMage.App.Updates;

/// <summary>A newer version that can be installed. <see cref="Handle"/> is whatever the <see cref="IUpdateClient"/> needs to find it again.</summary>
internal sealed record UpdateOffer(string Version, object? Handle = null);

/// <summary>
/// What the program needs from the update machinery. The real one is <see cref="VelopackUpdateClient"/>; tests use fakes, so no
/// test ever touches the network or an install.
/// </summary>
internal interface IUpdateClient
{
    /// <summary>True only when the program was installed by the setup program (not the portable zip, not a development run).</summary>
    bool IsInstalled { get; }

    string CurrentVersion { get; }

    /// <summary>An update that was downloaded earlier and waits to be installed (no network needed); null when there is none.</summary>
    UpdateOffer? DownloadedUpdate();

    /// <summary>Asks the update server whether there is a newer version. Null: up to date. Throws when the server cannot be reached.</summary>
    Task<UpdateOffer?> CheckAsync(CancellationToken ct);

    /// <summary>Downloads the update; <paramref name="progress"/> gets 0..100.</summary>
    Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct);

    /// <summary>Closes this program, installs the downloaded update and starts the new version with <paramref name="restartArgs"/>. Does not return when it works.</summary>
    void ApplyAndRestart(UpdateOffer offer, IReadOnlyList<string> restartArgs);
}

/// <summary>Knows whether other copies of the program are running.</summary>
internal interface IInstanceGuard
{
    /// <summary>True when no other DeSign Mage was running when this one started: a "fresh start".</summary>
    bool IsOnlyInstance { get; }

    /// <summary>True while another copy is showing the "Update required" window (a second copy must not start under it).</summary>
    bool UpdateRunningElsewhere { get; }

    /// <summary>This copy is (or is no longer) showing the "Update required" window.</summary>
    void SetUpdating(bool updating);
}
