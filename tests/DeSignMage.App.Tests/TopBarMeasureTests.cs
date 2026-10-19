using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>
/// How much room the drawing area gets at the window sizes that matter (R37, R38), and the pictures of the top of the window at 150% display
/// scaling. Docs only: it does nothing unless DSM_SCREENSHOTS names a folder; DSM_PREFIX (default "measure") starts the file names.
/// </summary>
[Collection("app-settings")]   // dark mode and the text size are program-wide: one class at a time
public class TopBarMeasureTests
{
    private static readonly (string Name, double Width, double Height, double Text)[] Sizes =
    [
        ("1280x720 effective (1920x1080 at 150%)", 1280, 720, 1.0),
        ("1280x642 effective (the same laptop, window without title bar and taskbar)", 1280, 642, 1.0),
        ("1366x768 at 100%", 1366, 768, 1.0),
        ("1920x1080 at 100%", 1920, 1080, 1.0),
        ("1280x720 effective, Interface Text Size 150%", 1280, 720, 1.5),
    ];

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static MainWindow Open(double width, double height, bool demo = false)
    {
        var w = new MainWindow(new StartupOptions(Demo: demo)) { Width = width, Height = height };
        w.Show();
        Flush();
        w.CaptureRenderedFrame();
        return w;
    }

    private static RectangleObject AddRect(MainWindow w)
    {
        var r = new RectangleObject(100, 80) { Transform = Affine2.Translation(100, 100), Style = ObjectStyle.FillOnly(DocColor.FromHex("#F2B705")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        return r;
    }

    private static TextObject AddText(MainWindow w)
    {
        var t = new TextObject("Hello", "Arial", 40) { Transform = Affine2.Translation(100, 300), Style = ObjectStyle.FillOnly(DocColor.Black) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, t));
        return t;
    }

    /// <summary>A click on the canvas at a document point (the Vector Draw tool places a node).</summary>
    private static void CanvasClick(MainWindow w, double x, double y)
    {
        var doc = new Vec2(x, y);
        var screen = w.Session.Viewport.ToScreen(doc);
        w.Session.PointerDown(new PointerInput(screen, doc, PointerButton.Left, ModifierKeys.None));
        w.Session.PointerUp(new PointerInput(screen, doc, PointerButton.Left, ModifierKeys.None));
        Flush();
    }

    /// <summary>Every tool that is a mode (the bars that end in Done and Cancel), and how to open it on a window with a shape and a text.</summary>
    internal static (string Name, Action<MainWindow, RectangleObject, TextObject> Open)[] DoneCancelModes() =>
    [
        ("Soft Shadow", (w, r, _) => { w.Session.Selection.Set(r); w.Session.BeginSoftEffect(SoftKind.SoftShadow); }),
        ("Glow", (w, r, _) => { w.Session.Selection.Set(r); w.Session.BeginSoftEffect(SoftKind.Glow); }),
        ("Feather", (w, r, _) => { w.Session.Selection.Set(r); w.Session.BeginFeather(); }),
        ("Easy Bevel", (w, r, _) => { w.Session.Selection.Set(r); w.Session.BeginBevel(); }),
        ("Gradient Blend", (w, r, _) => { w.Session.Selection.Set(r); w.Session.BeginGradient(); }),
        ("Outline", (w, r, _) => { w.Session.Selection.Set(r); w.Session.BeginOutline(OutlineKind.Outline); w.Session.GetTool<OutlineTool>().Update(o => o with { First = 10 }); }),
        ("Shadow", (w, r, _) => { w.Session.Selection.Set(r); w.Session.BeginShadow(ShadowForm.Flat); }),
        ("Text on Path", (w, _, t) => { w.Session.Selection.Set(t); w.Session.BeginTextOnPath(); }),
        ("Convert to Rounded Rectangle", (w, r, _) => { w.Session.Selection.Set(r); w.Session.BeginConvertToRoundedRectangle(); }),
        ("Vector Edit", (w, r, _) => { w.Session.Selection.Set(r); w.Session.SetTool("vector"); }),
        ("Loop Edit", (w, r, _) => { w.Session.Selection.Set(r); w.Session.SetTool("loop"); }),
        ("Vector Draw", (w, _, _) => { w.Session.SetTool("vectordraw"); CanvasClick(w, 600, 500); CanvasClick(w, 700, 520); }),
        ("Text", (w, _, _) => { w.Session.SetTool("text"); w.Session.GetTool<TextTool>().BeginNew(new Vec2(600, 600)); w.Session.TextInput("Hi"); }),
    ];

    private static bool Stacked(MainWindow w)
    {
        var bar = (Control)w.FindControl<ContentControl>("ContextBarHost")!.Content!;
        return bar.GetSelfAndVisualDescendants().OfType<TwoLinePanel>().Any(p => p.Stacked);
    }

    private static string Row(MainWindow w, string state)
    {
        Flush();
        w.CaptureRenderedFrame();
        Flush();
        w.CaptureRenderedFrame();
        var surface = w.FindControl<DesignCanvas>("Surface")!;
        double windowArea = w.Bounds.Width * w.Bounds.Height;
        var sb = surface.Bounds;
        string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        // what the Context Bar needs side by side (its two rows) against the room it has
        var content = (Control)w.FindControl<ContentControl>("ContextBarHost")!.Content!;
        var rowWidths = content.GetVisualChildren().OfType<BarRow>().Select(r => F(r.DesiredSize.Width)).ToList();
        var scroll = w.FindControl<ScrollViewer>("ContextBarScroll")!;
        string need = $"rows {string.Join(" + ", rowWidths)} in {F(scroll.Viewport.Width)}";
        return $"{state,-22} {need,-30} drawing area {F(sb.Width)} x {F(sb.Height)} = {sb.Width * sb.Height / windowArea * 100:0.0}% of the window | top row {F(w.FindControl<Border>("TopRowBorder")!.Bounds.Height)}"
             + $" | Context Bar {F(w.FindControl<Border>("ContextBarBorder")!.Bounds.Height)}{(Stacked(w) ? " (2 lines)" : "")} | bottom bar {F(w.FindControl<Border>("BottomBar")!.Bounds.Height)}";
    }

    private static void Save(MainWindow w, string path, double displayScale)
    {
        Flush();
        w.CaptureRenderedFrame();
        Flush();
        var size = new PixelSize((int)Math.Round(w.Bounds.Width * displayScale), (int)Math.Round(w.Bounds.Height * displayScale));
        using var rtb = new RenderTargetBitmap(size, new Vector(96 * displayScale, 96 * displayScale));
        rtb.Render(w);
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
        rtb.Save(path);
#pragma warning restore CS0618
    }

    /// <summary>The parts of the new top bar that open or flash (pictures): the Align list, the zoom presets, a message on the Info Line, Interface Text Size 150% (docs only).</summary>
    [AvaloniaFact]
    public void Save_pictures_of_the_zoom_presets_the_Align_list_a_message_and_a_bigger_text_size()
    {
        var dir = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        string prefix = Environment.GetEnvironmentVariable("DSM_PREFIX") is { Length: > 0 } p ? p : "measure";
        Directory.CreateDirectory(dir);
        var oldStore = AppSettings.Store;
        AppSettings.Store = new SettingsStore(null);
        UiScale.Store = AppSettings.Store;
        UiScale.Reset();
        AppTheme.Reset();
        try
        {
            foreach (bool dark in new[] { false, true })
            {
                AppTheme.SetDarkMenus(dark, remember: false);
                AppTheme.SetDarkCanvas(dark, remember: false);
                string theme = dark ? "dark" : "light";
                var w = Open(1280, 720, demo: true);
                var shapes = w.Session.Document.ActiveLayer.Children.Where(o => o is not TextObject).Take(2).ToArray();
                w.Session.Selection.Set(shapes);
                Flush();
                w.ZoomControl.PresetsButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Save(w, Path.Combine(dir, $"{prefix}-150-zoom-presets-{theme}.png"), 1.5);
                w.ZoomControl.PresetMenu.Close();
                var bar = (SelectionContextBar)w.FindControl<ContentControl>("ContextBarHost")!.Content!;
                bar.AlignDropDownButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Save(w, Path.Combine(dir, $"{prefix}-150-align-open-{theme}.png"), 1.5);
                bar.AlignMenu.Close();
                w.Flash("Saved sign-for-the-bakery.dsm");
                Save(w, Path.Combine(dir, $"{prefix}-150-message-{theme}.png"), 1.5);
                w.EndFlash();
                w.Close();
            }
            AppTheme.Reset();
            UiScale.SetScale(1.5, remember: false);
            var big = Open(1280, 720, demo: true);
            big.Session.Selection.Set(big.Session.Document.ActiveLayer.Children.First(o => o is not TextObject));
            Save(big, Path.Combine(dir, $"{prefix}-150-text150-light.png"), 1.5);
            big.Close();
        }
        finally
        {
            AppTheme.Reset();
            UiScale.Reset();
            UiScale.Store = null;
            AppSettings.Store = oldStore;
        }
    }

    [AvaloniaFact]
    public async Task Measure_the_drawing_area_and_save_the_top_of_the_window_at_150_percent()
    {
        var dir = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        string prefix = Environment.GetEnvironmentVariable("DSM_PREFIX") is { Length: > 0 } p ? p : "measure";
        Directory.CreateDirectory(dir);

        var oldStore = AppSettings.Store;
        AppSettings.Store = new SettingsStore(null);
        UiScale.Store = AppSettings.Store;
        UiScale.Reset();
        AppTheme.Reset();
        var report = new StringBuilder();
        try
        {
            foreach (var (name, width, height, text) in Sizes)
            {
                UiScale.Reset();
                UiScale.SetScale(text, remember: false);
                report.AppendLine().AppendLine(name);
                var w = Open(width, height);
                var rect = AddRect(w);
                var t = AddText(w);
                w.Session.Selection.Clear();
                report.AppendLine("  " + Row(w, "nothing selected"));
                w.Session.Selection.Set(rect);
                report.AppendLine("  " + Row(w, "a shape selected"));
                w.Session.Selection.Set([rect, t]);
                report.AppendLine("  " + Row(w, "a shape and a text"));
                w.Session.Selection.Set(t);
                report.AppendLine("  " + Row(w, "a text selected"));
                w.Session.Selection.Set(rect);
                w.Session.BeginSoftEffect(SoftKind.SoftShadow);
                report.AppendLine("  " + Row(w, "Soft Shadow bar"));
                w.Session.CancelMode();
                w.Session.Selection.Set(rect);
                w.Session.BeginBevel();
                report.AppendLine("  " + Row(w, "Easy Bevel bar"));
                w.Session.CancelMode();
                w.Session.Selection.Set(rect);
                w.Session.SetTool("vector");
                report.AppendLine("  " + Row(w, "Vector Edit bar"));
                w.Session.SetTool("select");
                var dir2 = Directory.CreateTempSubdirectory("dsm-measure-").FullName;
                try
                {
                    var pixels = new DeSignMage.Imaging.PixelBuffer(60, 30);
                    for (int y = 0; y < 30; y++)
                        for (int x = 0; x < 60; x++) pixels.Set(x, y, (byte)(x * 4), 80, 200, 255);
                    var pic = Path.Combine(dir2, "p.png");
                    File.WriteAllBytes(pic, DeSignMage.Imaging.ImageCodec.EncodePng(pixels));
                    await w.ImportImagesAsync([pic], null);
                    Flush();
                    report.AppendLine("  " + Row(w, "a picture selected"));
                    w.Session.SetTool("crop");
                    report.AppendLine("  " + Row(w, "Crop bar"));
                }
                finally { Directory.Delete(dir2, true); }
                w.Close();
            }
            UiScale.Reset();

            // every bar that ends in Done / Cancel, at 1280 x 720 effective
            report.AppendLine().AppendLine("Done / Cancel bars at 1280x720 effective");
            foreach (var (name, open) in DoneCancelModes())
            {
                var w = Open(1280, 720);
                var rect = AddRect(w);
                var t = AddText(w);
                open(w, rect, t);
                report.AppendLine("  " + Row(w, name));
                w.Close();
            }

            // Pictures at 150% display scaling: the window is 1280 x 720 effective, saved at 1920 x 1080 pixels.
            foreach (bool dark in new[] { false, true })
            {
                AppTheme.SetDarkMenus(dark);
                AppTheme.SetDarkCanvas(dark);
                var w = Open(1280, 720, demo: true);
                var text = w.Session.Document.ActiveLayer.Children.OfType<TextObject>().First();
                w.Session.Selection.Set(text);
                Save(w, Path.Combine(dir, $"{prefix}-150-{(dark ? "dark" : "light")}.png"), 1.5);
                if (!dark)
                {
                    w.Session.Selection.Set(w.Session.Document.ActiveLayer.Children.First(o => o is not TextObject));
                    Save(w, Path.Combine(dir, $"{prefix}-150-shape-light.png"), 1.5);
                    w.Session.SetTool("select");
                    w.Session.Selection.Clear();
                    Save(w, Path.Combine(dir, $"{prefix}-150-nothing-light.png"), 1.5);
                }
                w.Close();
            }
            AppTheme.Reset();
            foreach (bool dark in new[] { false, true })
            {
                AppTheme.SetDarkMenus(dark);
                AppTheme.SetDarkCanvas(dark);
                var w = Open(1366, 768, demo: true);
                w.Session.Selection.Set(w.Session.Document.ActiveLayer.Children.OfType<TextObject>().First());
                Save(w, Path.Combine(dir, $"{prefix}-100-{(dark ? "dark" : "light")}.png"), 1.0);
                w.Close();
            }
        }
        finally
        {
            AppTheme.Reset();
            UiScale.Reset();
            UiScale.Store = null;
            AppSettings.Store = oldStore;
            File.WriteAllText(Path.Combine(dir, $"{prefix}-measure.txt"), report.ToString());
        }
        await Task.CompletedTask;
    }
}
