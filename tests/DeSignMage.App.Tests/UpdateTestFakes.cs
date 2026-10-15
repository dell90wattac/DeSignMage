using System.Runtime.CompilerServices;
using DeSignMage.App.Updates;
using Velopack;

namespace DeSignMage.App.Tests;

/// <summary>What Program.Main does first, so the real update library can be created in a test (it finds no install, like a portable copy).</summary>
internal static class VelopackTestSetup
{
    [ModuleInitializer]
    internal static void Init() => VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
}

/// <summary>A scripted update client: nothing here touches the network, an install or the disk.</summary>
internal sealed class FakeUpdateClient : IUpdateClient
{
    public bool IsInstalled { get; set; } = true;
    public string CurrentVersion { get; set; } = "1.0.0";
    /// <summary>Already on the computer, waiting to be installed.</summary>
    public UpdateOffer? Downloaded { get; set; }
    /// <summary>What the server offers (null: up to date).</summary>
    public UpdateOffer? Available { get; set; }
    /// <summary>Thrown by <see cref="CheckAsync"/> when set (offline, server down).</summary>
    public Exception? CheckFails { get; set; }
    public Exception? DownloadFails { get; set; }
    public Exception? ApplyFails { get; set; }
    /// <summary>When set, a check waits for this (never completes unless the test says so).</summary>
    public TaskCompletionSource<UpdateOffer?>? CheckGate { get; set; }
    public int[] ProgressSteps { get; set; } = [10, 55, 100];

    public int Checks { get; private set; }
    public int Downloads { get; private set; }
    public List<(UpdateOffer Offer, string[] Args)> Applied { get; } = [];

    public UpdateOffer? DownloadedUpdate() => Downloaded;

    public Task<UpdateOffer?> CheckAsync(CancellationToken ct)
    {
        Checks++;
        if (CheckFails is not null) return Task.FromException<UpdateOffer?>(CheckFails);
        return CheckGate?.Task ?? Task.FromResult(Available);
    }

    public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct)
    {
        Downloads++;
        if (DownloadFails is not null) return Task.FromException(DownloadFails);
        foreach (int p in ProgressSteps) progress(p);
        Downloaded = offer;
        return Task.CompletedTask;
    }

    public void ApplyAndRestart(UpdateOffer offer, IReadOnlyList<string> restartArgs)
    {
        if (ApplyFails is not null) throw ApplyFails;
        Applied.Add((offer, restartArgs.ToArray()));
    }
}

internal sealed class FakeGuard : IInstanceGuard
{
    public bool IsOnlyInstance { get; set; } = true;
    public bool UpdateRunningElsewhere { get; set; }
    public bool Updating { get; private set; }
    public void SetUpdating(bool updating) => Updating = updating;
}
