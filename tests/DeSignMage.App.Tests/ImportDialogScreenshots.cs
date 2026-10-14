using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DeSignMage.App.Shell;
using DeSignMage.Core.Model;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>
/// Developer tool, not a test: with the environment variable DSM_SHOTS set to a folder it writes screenshots of the Import dialog
/// (light and dark, at 100% and 150% text size, a multi-page file with notes, a file that cannot be read). Without it the test does nothing.
/// </summary>
public class ImportDialogScreenshots
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
        var sign = window.Session.Document;

        void Shot(string name, bool dark, double scale, FakeVectorImporter importer, string file)
        {
            VectorFiles.Importer = importer;
            AppTheme.SetDarkMenus(dark, remember: false);
            UiScale.SetScale(scale, remember: false);
            Dispatcher.UIThread.RunJobs();
            var dialog = new ImportDialog(Path.Combine(Path.GetTempPath(), file), importer, new ImportSettings());
            dialog.Show();
            for (int i = 0; i < 300 && !(dialog.ReadTask.IsCompleted && dialog.PreviewTask.IsCompleted && (dialog.PreviewsDrawn > 0 || dialog.ErrorText.Text is { Length: > 0 })); i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618 // the replacement overload is not needed for a plain PNG
            dialog.CaptureRenderedFrame()!.Save(Path.Combine(folder, $"import-dialog-{name}.png"));
#pragma warning restore CS0618
            dialog.Close();
        }

        var simple = new FakeVectorImporter { Make = _ => sign };
        Shot("light-100", false, 1.0, simple, "sign.pdf");
        Shot("dark-100", true, 1.0, simple, "sign.pdf");
        Shot("light-150", false, 1.5, simple, "sign.pdf");
        Shot("dark-150", true, 1.5, simple, "sign.pdf");
        var catalog = new FakeVectorImporter
        {
            Pages = 12,
            Make = _ => sign,
            Warnings = ["Two fonts were not embedded in the file; their text was drawn as curves.", "A soft mask was ignored.", "A pattern fill was drawn as a flat color."],
        };
        Shot("pages-and-notes-light-100", false, 1.0, catalog, "catalog.pdf");
        Shot("unreadable-light-100", false, 1.0, new FakeVectorImporter(), "broken.ai");
        AppTheme.SetDarkMenus(false, remember: false);
        UiScale.Reset();
        VectorFiles.Importer = null;
        window.Close();
    }
}
