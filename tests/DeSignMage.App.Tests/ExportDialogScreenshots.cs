using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DeSignMage.App.Shell;
using DeSignMage.Core.Model;
using DeSignMage.Rendering.Export;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>
/// Developer tool, not a test: with the environment variable DSM_SHOTS set to a folder it writes screenshots of the Export dialog
/// (light and dark, at 100% and 150% text size, and the other formats in light). Without it the test does nothing.
/// </summary>
public class ExportDialogScreenshots
{
    [AvaloniaFact]
    public void Write_screenshots_when_asked()
    {
        var folder = Environment.GetEnvironmentVariable("DSM_SHOTS");
        if (string.IsNullOrEmpty(folder)) return;
        var label = Environment.GetEnvironmentVariable("DSM_SHOT_LABEL") ?? "shot";
        Directory.CreateDirectory(folder);

        var window = new MainWindow(new StartupOptions(Demo: true)) { Width = 1400, Height = 860 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var doc = window.Session.Document;
        var selection = doc.Layers[0].Children.Take(2).ToArray();

        void Shot(string name, bool dark, double scale, ExportSettings settings, DocObject[]? chosen = null)
        {
            AppTheme.SetDarkMenus(dark, remember: false);
            UiScale.SetScale(scale, remember: false);
            Dispatcher.UIThread.RunJobs();
            var dialog = new ExportDialog(doc, chosen ?? selection, settings);
            dialog.Show();
            for (int i = 0; i < 100 && dialog.PreviewsDrawn == 0; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(20);
            }
            Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618 // the replacement overload is not needed for a plain PNG
            dialog.CaptureRenderedFrame()!.Save(Path.Combine(folder, $"export-dialog-v2-{label}-{name}.png"));
#pragma warning restore CS0618
            dialog.Close();
        }

        foreach (var (dark, scale) in new[] { (false, 1.0), (true, 1.0), (false, 1.5), (true, 1.5) })
            Shot($"{(dark ? "dark" : "light")}-{(int)(scale * 100)}", dark, scale, new ExportSettings());
        if (label != "before")
        {
            Shot("jpeg-light-100", false, 1.0, new ExportSettings(ExportFormat.Jpeg));
            Shot("jpeg-background-selection-dark-100", true, 1.0, new ExportSettings(ExportFormat.Jpeg, SelectionOnly: true, JpegBackground: DocColor.FromHex("#1A2B4C")),
                doc.AllObjects().OfType<EllipseObject>().Take(2).ToArray());   // round shapes: the box around them has empty corners
            Shot("pdf-light-100", false, 1.0, new ExportSettings(ExportFormat.Pdf, SelectionOnly: true));
            Shot("svg-light-100", false, 1.0, new ExportSettings(ExportFormat.Svg, IncludePasteboard: true));
            Shot("eps-light-100", false, 1.0, new ExportSettings(ExportFormat.Eps));
            Shot("lyt-light-100", false, 1.0, new ExportSettings(ExportFormat.Lyt));
            Shot("selection-dark-150", true, 1.5, new ExportSettings(ExportFormat.Png, SelectionOnly: true));
        }
        AppTheme.SetDarkMenus(false, remember: false);
        if (label != "before")
        {
            var picker = new ColorChoiceDialog("Background color", DocColor.FromHex("#1A2B4C"), JobColors.Collect(doc));
            picker.Show();
            Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618 // the replacement overload is not needed for a plain PNG
            picker.CaptureRenderedFrame()!.Save(Path.Combine(folder, $"export-dialog-v2-{label}-background-color-choice.png"));
#pragma warning restore CS0618
            picker.Close();
        }
        UiScale.Reset();
        window.Close();
    }
}
