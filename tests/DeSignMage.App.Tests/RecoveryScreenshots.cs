using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DeSignMage.App.Recovery;
using DeSignMage.App.Shell;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>
/// Developer tool, not a test: with the environment variable DSM_SHOTS set to a folder it writes screenshots of Options ▸ Auto-save and of the
/// restore question with two designs. Without it the test does nothing.
/// </summary>
public class RecoveryScreenshots
{
    [AvaloniaFact]
    public void Write_screenshots_when_asked()
    {
        var folder = Environment.GetEnvironmentVariable("DSM_SHOTS");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);

        void Shoot(Avalonia.Controls.Window window, string name)
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618 // the replacement overload is not needed for a plain PNG
            window.CaptureRenderedFrame()!.Save(Path.Combine(folder, name));
#pragma warning restore CS0618
            window.Close();
        }

        var old = AppSettings.Store;
        AppSettings.Store = new DeSignMage.Feedback.SettingsStore(null);
        try
        {
            var options = new OptionsWindow(OptionsPages.AutoSaveId) { Width = 780, Height = 460 };
            Shoot(options, "autosave-options.png");
        }
        finally { AppSettings.Store = old; }

        var now = DateTime.UtcNow;
        RecoverableDesign Make(string run, string title, string? path, double minutesAgo, bool crash = false)
        {
            var meta = new RecoveryMeta { Title = title, DocumentPath = path, SavedUtc = now.AddMinutes(-minutesAgo), RunId = run, Key = "design" };
            return TestDesigns.Create(run, meta, crash);
        }
        var two = new[]
        {
            Make("run-b", "Shop window.dsm", @"C:\Jobs\Shop window.dsm", 3, crash: true),
            Make("run-a", "Untitled", null, 26 * 60),
        };
        Shoot(new RestorePromptDialog(two, _ => { }), "autosave-restore-prompt-two.png");
    }
}

/// <summary>Builds a <see cref="RecoverableDesign"/> without any files, for a picture of the prompt.</summary>
internal static class TestDesigns
{
    public static RecoverableDesign Create(string run, RecoveryMeta meta, bool crash) =>
        new(run, "design", meta, @"C:\none.dsm", @"C:\none", crash ? @"C:\none.dsm.recovery.json" : null, null, null);
}
