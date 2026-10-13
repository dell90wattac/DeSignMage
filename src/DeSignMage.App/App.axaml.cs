using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DeSignMage.App.Reporting;
using DeSignMage.App.Shell;

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
        PerformanceSettings.Load(AppSettings.Store);   // undo memory limit, before any document exists
        if (Program.Options.TextScale is { } devScale) UiScale.SetScale(devScale, remember: false);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(Program.Options);
            Program.Mark("window-built");
        }
        base.OnFrameworkInitializationCompleted();
    }
}
