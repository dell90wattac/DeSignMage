using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DeSignMage.App.Shell;
using DeSignMage.Core.Model;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>
/// Developer tool, not a test: with the environment variable DSM_SHOTS set to a folder it writes screenshots of the Layout Properties dialog
/// (light and dark, at 100% and 150% text size). Without it the test does nothing.
/// </summary>
public class LayoutDialogScreenshots
{
    [AvaloniaFact]
    public void Write_screenshots_when_asked()
    {
        var folder = Environment.GetEnvironmentVariable("DSM_SHOTS");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);

        var window = new MainWindow(new StartupOptions(Demo: true)) { Width = 1400, Height = 860 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        void Shot(string name, bool dark, double scale)
        {
            AppTheme.SetDarkMenus(dark, remember: false);
            UiScale.SetScale(scale, remember: false);
            Dispatcher.UIThread.RunJobs();
            var dialog = new LayoutPropertiesDialog(PageSetup.FromInches(24, 12), showGuides: true, snapToGuides: true, isDefaultSize: false);
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618 // the replacement overload is not needed for a plain PNG
            dialog.CaptureRenderedFrame()!.Save(Path.Combine(folder, name));
#pragma warning restore CS0618
            dialog.Close();
        }

        Shot("layout-properties.png", dark: false, 1.0);
        Shot("layout-properties-dark.png", dark: true, 1.0);
        Shot("layout-properties-150.png", dark: false, 1.5);
        AppTheme.SetDarkMenus(false, remember: false);
        UiScale.Reset();
        window.Close();
    }
}
