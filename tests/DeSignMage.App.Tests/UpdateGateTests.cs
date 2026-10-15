using DeSignMage.App.Updates;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>The rule for every start: who may be required to update, and when a start must go on normally.</summary>
public class UpdateGateTests
{
    private static readonly UpdateOffer V2 = new("2.0.0");

    private static (UpdateGate Gate, List<string> Log) Make(FakeUpdateClient client, FakeGuard? guard = null, TimeSpan? timeout = null)
    {
        var log = new List<string>();
        return (new UpdateGate(client, guard ?? new FakeGuard(), log.Add, timeout ?? TimeSpan.FromSeconds(5)), log);
    }

    [Fact]
    public async Task A_copy_that_is_not_installed_never_updates_and_never_asks_the_server()
    {
        var client = new FakeUpdateClient { IsInstalled = false, Available = V2, Downloaded = V2 };
        var (gate, _) = Make(client);
        var d = await gate.DecideAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StartupAction.StartNormally, d.Action);
        Assert.Equal(0, client.Checks);
    }

    [Fact]
    public async Task A_fresh_start_with_an_update_already_downloaded_requires_it_without_asking_the_server()
    {
        var client = new FakeUpdateClient { Downloaded = V2 };
        var (gate, _) = Make(client);
        var d = await gate.DecideAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StartupAction.RequireUpdate, d.Action);
        Assert.True(d.Downloaded);
        Assert.Equal("2.0.0", d.Offer!.Version);
        Assert.Equal(0, client.Checks);
    }

    [Fact]
    public async Task A_fresh_start_with_a_newer_version_on_the_server_requires_it_and_says_it_still_has_to_be_downloaded()
    {
        var client = new FakeUpdateClient { Available = V2 };
        var (gate, _) = Make(client);
        var d = await gate.DecideAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StartupAction.RequireUpdate, d.Action);
        Assert.False(d.Downloaded);
        Assert.Equal(V2, d.Offer);
        Assert.Equal(1, client.Checks);
    }

    [Fact]
    public async Task Requiring_an_update_marks_this_copy_as_updating_at_once_and_other_outcomes_do_not()
    {
        var guard = new FakeGuard();
        var (gate, _) = Make(new FakeUpdateClient { Available = V2 }, guard);
        await gate.DecideAsync(TestContext.Current.CancellationToken);
        Assert.True(guard.Updating);

        var other = new FakeGuard();
        var (quiet, _) = Make(new FakeUpdateClient(), other);
        await quiet.DecideAsync(TestContext.Current.CancellationToken);
        Assert.False(other.Updating);
    }

    [Fact]
    public async Task A_fresh_start_that_is_up_to_date_starts_normally()
    {
        var client = new FakeUpdateClient();
        var (gate, _) = Make(client);
        var d = await gate.DecideAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StartupAction.StartNormally, d.Action);
        Assert.Null(d.Offer);
        Assert.Equal(1, client.Checks);
    }

    [Fact]
    public async Task While_another_copy_is_running_nothing_is_required_even_with_an_update_waiting()
    {
        var client = new FakeUpdateClient { Downloaded = V2, Available = V2 };
        var (gate, _) = Make(client, new FakeGuard { IsOnlyInstance = false });
        var d = await gate.DecideAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StartupAction.StartNormally, d.Action);
        Assert.Equal(0, client.Checks);
        Assert.Contains("another copy", d.Reason);
    }

    [Fact]
    public async Task A_copy_opened_while_another_installs_an_update_ends_at_once()
    {
        var client = new FakeUpdateClient { Available = V2 };
        var (gate, _) = Make(client, new FakeGuard { IsOnlyInstance = false, UpdateRunningElsewhere = true });
        Assert.Equal(StartupAction.Exit, (await gate.DecideAsync(TestContext.Current.CancellationToken)).Action);
    }

    [Fact]
    public async Task A_portable_copy_is_never_sent_away_by_an_update_that_runs_elsewhere()
    {
        var client = new FakeUpdateClient { IsInstalled = false };
        var (gate, _) = Make(client, new FakeGuard { IsOnlyInstance = false, UpdateRunningElsewhere = true });
        Assert.Equal(StartupAction.StartNormally, (await gate.DecideAsync(TestContext.Current.CancellationToken)).Action);
    }

    [Fact]
    public async Task Offline_the_program_starts_normally_and_logs_it()
    {
        var client = new FakeUpdateClient { CheckFails = new HttpRequestException("No such host is known") };
        var (gate, log) = Make(client);
        var d = await gate.DecideAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StartupAction.StartNormally, d.Action);
        Assert.Contains(log, l => l.Contains("HttpRequestException") && l.Contains("again next time"));
    }

    [Fact]
    public async Task A_server_that_does_not_answer_in_time_never_keeps_the_program_from_starting()
    {
        var client = new FakeUpdateClient { CheckGate = new TaskCompletionSource<UpdateOffer?>() };
        var (gate, _) = Make(client, timeout: TimeSpan.FromMilliseconds(60));
        var d = await gate.DecideAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StartupAction.StartNormally, d.Action);
        Assert.Contains("did not answer", d.Reason);
        client.CheckGate.SetException(new IOException("late failure"));   // arrives after the program started: must not blow up
        await Task.Delay(20, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_unexpected_error_inside_the_client_also_starts_normally()
    {
        var client = new FakeUpdateClient { CheckFails = new InvalidOperationException("boom") };
        var (gate, log) = Make(client);
        Assert.Equal(StartupAction.StartNormally, (await gate.DecideAsync(TestContext.Current.CancellationToken)).Action);
        Assert.Single(log);
    }
}

/// <summary>What the "Update required" window does: download, wait for the person, install; failures never throw.</summary>
public class UpdateFlowTests
{
    private static readonly string[] Args = ["C:\\Designs\\sign.dsm"];

    private static UpdateFlow Make(FakeUpdateClient client, bool downloaded, List<string>? log = null) =>
        new(client, new GateDecision(StartupAction.RequireUpdate, "test", new UpdateOffer("2.0.0"), downloaded), Args, (log ?? []).Add);

    [Fact]
    public async Task An_update_that_is_already_downloaded_is_ready_at_once_and_applying_hands_the_arguments_on()
    {
        var client = new FakeUpdateClient();
        var flow = Make(client, downloaded: true);
        await flow.PrepareAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateStep.Ready, flow.Step);
        Assert.Equal(0, client.Downloads);
        flow.Apply();
        var applied = Assert.Single(client.Applied);
        Assert.Equal("2.0.0", applied.Offer.Version);
        Assert.Equal(Args, applied.Args);
        Assert.Equal(UpdateStep.Applying, flow.Step);
    }

    [Fact]
    public async Task An_update_that_is_not_downloaded_yet_is_downloaded_with_progress_before_it_is_ready()
    {
        var client = new FakeUpdateClient();
        var flow = Make(client, downloaded: false);
        var seen = new List<(UpdateStep, int)>();
        flow.Changed += () => seen.Add((flow.Step, flow.Percent));
        await flow.PrepareAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, client.Downloads);
        Assert.Equal(UpdateStep.Ready, flow.Step);
        Assert.Equal(100, flow.Percent);
        Assert.Contains(seen, s => s == (UpdateStep.Downloading, 55));
        Assert.Empty(client.Applied);   // nothing installs until the person presses the button
    }

    [Fact]
    public async Task A_failed_download_is_logged_and_ends_in_Failed_so_the_program_can_start_normally()
    {
        var client = new FakeUpdateClient { DownloadFails = new HttpRequestException("connection reset") };
        var log = new List<string>();
        var flow = Make(client, downloaded: false, log);
        await flow.PrepareAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateStep.Failed, flow.Step);
        Assert.Contains("try again", flow.FailureText);
        Assert.Contains(log, l => l.Contains("connection reset"));
        flow.Apply();   // does nothing when there is nothing to apply
        Assert.Empty(client.Applied);
    }

    [Fact]
    public async Task A_failed_installation_is_logged_and_ends_in_Failed()
    {
        var client = new FakeUpdateClient { ApplyFails = new IOException("Update.exe is missing") };
        var log = new List<string>();
        var flow = Make(client, downloaded: true, log);
        await flow.PrepareAsync(TestContext.Current.CancellationToken);
        flow.Apply();
        Assert.Equal(UpdateStep.Failed, flow.Step);
        Assert.Contains(log, l => l.Contains("Update.exe is missing"));
        Assert.Contains("try again", flow.FailureText);
    }

    [Fact]
    public async Task A_download_that_is_cancelled_by_the_stall_limit_counts_as_failed()
    {
        var client = new FakeUpdateClient { DownloadFails = new OperationCanceledException() };
        var flow = Make(client, downloaded: false);
        await flow.PrepareAsync(TestContext.Current.CancellationToken);   // the token itself is not cancelled here, so this is a failure, not a quiet exit
        Assert.Equal(UpdateStep.Failed, flow.Step);
    }
}

/// <summary>While the program runs: quiet checks and downloads, never an install.</summary>
public class BackgroundUpdaterTests
{
    private static BackgroundUpdater Make(FakeUpdateClient client, List<string>? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(client, (log ?? []).Add, delay);

    [Fact]
    public async Task A_portable_copy_reports_that_it_does_not_update_and_asks_nothing()
    {
        var client = new FakeUpdateClient { IsInstalled = false, Available = new UpdateOffer("2.0.0") };
        var updater = Make(client);
        await updater.CheckOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.NotInstalled, updater.Status.State);
        Assert.Equal(0, client.Checks);
        Assert.Contains("does not update itself", updater.Status.Describe("1.0.0"));
    }

    [Fact]
    public async Task A_newer_version_is_downloaded_quietly_and_never_installed()
    {
        var client = new FakeUpdateClient { Available = new UpdateOffer("2.0.0") };
        var updater = Make(client);
        await updater.CheckOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.Downloaded, updater.Status.State);
        Assert.Equal("2.0.0", updater.Status.Version);
        Assert.Equal(1, client.Downloads);
        Assert.Empty(client.Applied);
        Assert.Contains("next time you start", updater.Status.Describe("1.0.0"));
    }

    [Fact]
    public async Task An_update_that_is_already_on_the_computer_is_not_downloaded_again()
    {
        var client = new FakeUpdateClient { Downloaded = new UpdateOffer("2.0.0") };
        var updater = Make(client);
        await updater.CheckOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.Downloaded, updater.Status.State);
        Assert.Equal(0, client.Checks);
        Assert.Equal(0, client.Downloads);
    }

    [Fact]
    public async Task Up_to_date_is_reported()
    {
        var updater = Make(new FakeUpdateClient());
        await updater.CheckOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.UpToDate, updater.Status.State);
    }

    [Fact]
    public async Task Offline_is_logged_and_never_throws()
    {
        var client = new FakeUpdateClient { CheckFails = new HttpRequestException("offline") };
        var log = new List<string>();
        var updater = Make(client, log);
        await updater.CheckOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.CheckFailed, updater.Status.State);
        Assert.Contains(log, l => l.Contains("offline"));
    }

    [Fact]
    public async Task A_failed_download_is_reported_as_a_failed_check_and_leaves_nothing_to_install()
    {
        var client = new FakeUpdateClient { Available = new UpdateOffer("2.0.0"), DownloadFails = new IOException("disk full") };
        var updater = Make(client);
        await updater.CheckOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.CheckFailed, updater.Status.State);
        Assert.Null(client.Downloaded);
    }

    [Fact]
    public async Task The_loop_waits_first_then_checks_every_few_hours_until_cancelled()
    {
        var client = new FakeUpdateClient();
        var waits = new List<TimeSpan>();
        using var cts = new CancellationTokenSource();
        var updater = Make(client, delay: (t, ct) =>
        {
            waits.Add(t);
            if (waits.Count == 3) cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        await updater.RunAsync(cts.Token);
        Assert.Equal([BackgroundUpdater.FirstCheckDelay, BackgroundUpdater.CheckInterval, BackgroundUpdater.CheckInterval], waits);
        Assert.Equal(2, client.Checks);
    }

    [Fact]
    public async Task Changes_of_the_status_are_announced()
    {
        var updater = Make(new FakeUpdateClient { Available = new UpdateOffer("2.0.0") });
        var states = new List<UpdateState>();
        updater.Changed += () => states.Add(updater.Status.State);
        await updater.CheckOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal([UpdateState.Checking, UpdateState.Downloading, UpdateState.Downloaded], states);
    }
}
