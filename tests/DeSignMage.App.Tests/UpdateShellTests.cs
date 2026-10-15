using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Shell;
using DeSignMage.App.Updates;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>The "Update required" window and Help ▸ About.</summary>
public class UpdateShellTests
{
    private static GateDecision Required(bool downloaded) =>
        new(StartupAction.RequireUpdate, "test", new UpdateOffer("2.0.0"), downloaded);

    private static (UpdateRequiredWindow Window, UpdateFlow Flow, FakeUpdateClient Client) Open(bool downloaded, FakeUpdateClient? client = null)
    {
        client ??= new FakeUpdateClient();
        var flow = new UpdateFlow(client, Required(downloaded), ["x.dsm"], _ => { });
        var w = new UpdateRequiredWindow(flow, "1.0.0");
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (w, flow, client);
    }

    private static async Task Settle(UpdateFlow flow, UpdateStep step)
    {
        for (int i = 0; i < 200 && flow.Step != step; i++) { await Task.Delay(10, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs(); }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public async Task An_update_that_is_already_downloaded_shows_one_button_and_no_later_choice()
    {
        var (w, flow, _) = Open(downloaded: true);
        await Settle(flow, UpdateStep.Ready);
        Assert.Contains("2.0.0", w.HeadingText.Text);
        Assert.Contains("required", w.HeadingText.Text);
        Assert.Equal("Update and restart", w.ActionButton.Content);
        Assert.True(w.ActionButton.IsEnabled);
        Assert.False(w.Progress.IsVisible);
        Assert.Single(Buttons(w));
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_window_cannot_be_closed_around_the_button()
    {
        var (w, flow, client) = Open(downloaded: true);
        await Settle(flow, UpdateStep.Ready);
        w.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.IsVisible);
        Assert.False(w.StartNormally.IsCompleted);
        Assert.Empty(client.Applied);
        Click(w.ActionButton);   // the way out: install (the fake only records it)
        Assert.Single(client.Applied);
        w.Close();
    }

    [AvaloniaFact]
    public async Task An_update_that_is_not_downloaded_downloads_with_a_progress_bar_and_waits_for_the_button()
    {
        var (w, flow, client) = Open(downloaded: false);
        await Settle(flow, UpdateStep.Ready);
        Assert.Equal(1, client.Downloads);
        Assert.True(w.ActionButton.IsEnabled);
        Assert.Empty(client.Applied);   // never installs without the person pressing the button
        Click(w.ActionButton);
        Assert.Equal("2.0.0", Assert.Single(client.Applied).Offer.Version);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_failed_download_says_so_and_the_one_button_starts_the_program_as_it_is()
    {
        var (w, flow, client) = Open(downloaded: false, new FakeUpdateClient { DownloadFails = new HttpRequestException("reset") });
        await Settle(flow, UpdateStep.Failed);
        Assert.Contains("could not update", w.HeadingText.Text);
        Assert.Contains("try again", w.ProgressText.Text);
        Assert.Equal("Start DeSign Mage", w.ActionButton.Content);
        Assert.False(w.StartNormally.IsCompleted);
        Click(w.ActionButton);
        Assert.True(await w.StartNormally);
        Assert.Empty(client.Applied);
    }

    [AvaloniaFact]
    public async Task A_failed_installation_returns_to_the_message_and_the_start_button()
    {
        var (w, flow, _) = Open(downloaded: true, new FakeUpdateClient { ApplyFails = new IOException("nope") });
        await Settle(flow, UpdateStep.Ready);
        Click(w.ActionButton);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(UpdateStep.Failed, flow.Step);
        Assert.Equal("Start DeSign Mage", w.ActionButton.Content);
        Assert.True(w.ActionButton.IsEnabled);
        w.Close();
    }

    [AvaloniaFact]
    public void About_says_a_portable_copy_does_not_update_itself()
    {
        var dialog = new AboutDialog("1.2.3", () => new UpdateStatus(UpdateState.NotInstalled), _ => { }, _ => { });
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Version 1.2.3", dialog.VersionText.Text);
        Assert.Contains("does not update itself", dialog.UpdateText.Text);
        dialog.Close();
    }

    [AvaloniaFact]
    public void About_follows_the_updater_status_and_stops_listening_when_closed()
    {
        var status = new UpdateStatus(UpdateState.UpToDate, "1.2.3", new DateTime(2026, 10, 15, 9, 30, 0));
        Action? handler = null;
        var dialog = new AboutDialog("1.2.3", () => status, h => handler = h, h => { if (handler == h) handler = null; });
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("newest version", dialog.UpdateText.Text);
        status = new UpdateStatus(UpdateState.Downloaded, "1.3.0");
        handler!();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("1.3.0 is downloaded", dialog.UpdateText.Text);
        dialog.Close();
        Assert.Null(handler);
    }

    [AvaloniaFact]
    public void Help_About_opens_with_the_running_version_and_says_a_test_run_is_not_an_installed_copy()
    {
        var dialog = new AboutDialog();   // what the menu command opens: the real version and the updater's status
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("Version ", dialog.VersionText.Text);
        Assert.False(UpdateHost.IsInstalled);
        Assert.Contains("does not update itself", dialog.UpdateText.Text);
        dialog.Close();
    }
    private static IEnumerable<Button> Buttons(Window w) =>
        w.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible && b.GetType() == typeof(Button));
}

/// <summary>Developer tool, not a test: with DSM_SHOTS set to a folder it writes screenshots of the Update required window in each state.</summary>
public class UpdateWindowScreenshots
{
    [AvaloniaFact]
    public async Task Write_screenshots_when_asked()
    {
        var folder = Environment.GetEnvironmentVariable("DSM_SHOTS");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);

        async Task Shot(string name, bool dark, double scale, bool downloaded, FakeUpdateClient? client = null, Action<UpdateFlow>? before = null)
        {
            AppTheme.SetDarkMenus(dark, remember: false);
            UiScale.SetScale(scale, remember: false);
            Dispatcher.UIThread.RunJobs();
            client ??= new FakeUpdateClient();
            var flow = new UpdateFlow(client, new GateDecision(StartupAction.RequireUpdate, "demo", new UpdateOffer("0.3.7"), downloaded), [], _ => { });
            var w = new UpdateRequiredWindow(flow, "0.3.6");
            w.Show();
            Dispatcher.UIThread.RunJobs();
            for (int i = 0; i < 200 && flow.Step is UpdateStep.Idle or UpdateStep.Downloading; i++) { await Task.Delay(10, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs(); }
            before?.Invoke(flow);
            Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618
            w.CaptureRenderedFrame()!.Save(Path.Combine(folder, name));
#pragma warning restore CS0618
            w.Close();
        }

        await Shot("update-required-ready.png", dark: false, 1.0, downloaded: true);
        await Shot("update-required-ready-dark.png", dark: true, 1.0, downloaded: true);
        await Shot("update-required-ready-150.png", dark: false, 1.5, downloaded: true);
        await Shot("update-required-failed.png", dark: false, 1.0, downloaded: false, new FakeUpdateClient { DownloadFails = new HttpRequestException("demo") });
        AppTheme.SetDarkMenus(false, remember: false);
        UiScale.Reset();
    }
}
