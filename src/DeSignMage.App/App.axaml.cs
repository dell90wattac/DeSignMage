using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DeSignMage.App.Reporting;
using DeSignMage.App.Shell;
using DeSignMage.App.Updates;
using DeSignMage.Feedback;

namespace DeSignMage.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        AppTheme.Register(this);   // the themed brushes (Ui.*) that styles and windows refer to
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Program.Mark("avalonia-up");
        // An exception that escapes on the UI thread: write the crash report (and an emergency save of the design), keep running.
        // Only in a real run: tests must see their exceptions.
        if (FeedbackHost.IsReal)
            Dispatcher.UIThread.UnhandledException += (_, e) => e.Handled = FeedbackHost.HandleUiException(e.Exception);

        // Interface text size (R12): remembered between sessions; applied before any window exists.
        AppSettings.Store = FeedbackHost.Get().Settings;
        UiScale.Load(AppSettings.Store);
        AppTheme.Load(AppSettings.Store);   // Dark Menus / Dark Canvas (R19), remembered between runs
        VectorEditSettings.Load(AppSettings.Store);   // the small-vector threshold
        PerformanceSettings.Load(AppSettings.Store);   // undo memory limit, before any document exists
        if (Program.Options.TextScale is { } devScale) UiScale.SetScale(devScale, remember: false);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = new MainWindow(Program.Options);
            Program.Mark("window-built");
            if (UpdateHost.StartupDecision is { } decision)
                _ = StartAfterUpdateCheckAsync(desktop, main, decision);   // fresh start of an installed copy: an update may come first
            else
                desktop.MainWindow = main;
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The window is built; before it opens, the answer of the update check decides: start as usual, or show "Update required" first
    /// (which installs the update and restarts, or, when that fails, lets the program start as it is).
    /// </summary>
    private static async Task StartAfterUpdateCheckAsync(IClassicDesktopStyleApplicationLifetime desktop, MainWindow main, Task<GateDecision> decisionTask)
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;   // no window yet, and the update window may close before the main one opens
        // An answer that is already there must not be acted on before the main loop runs: Shutdown() in here would kill the dispatcher first.
        await Task.Yield();
        GateDecision decision;
        try { decision = await decisionTask; }
        catch (Exception ex)
        {
            AppLog.Error("update", "The update check ended unexpectedly; starting normally", ex);
            decision = new GateDecision(StartupAction.StartNormally, "the update check failed");
        }
        AppLog.Info("update", $"start: {decision.Action} - {decision.Reason}");

        if (decision.Action == StartupAction.Exit) { desktop.Shutdown(); return; }

        if (decision.Action == StartupAction.RequireUpdate && UpdateHost.Client is { } client)
        {
            var flow = new UpdateFlow(client, decision, UpdateHost.RestartArgs, m => AppLog.Warn("update", m));
            UpdateHost.Guard?.SetUpdating(true);
            var window = new UpdateRequiredWindow(flow, client.CurrentVersion);
            window.Show();
            await window.StartNormally;
            UpdateHost.Guard?.SetUpdating(false);
        }

        desktop.MainWindow = main;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
        UpdateHost.StartBackground();
    }
}
