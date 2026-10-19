using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
using DeSignMage.Rendering;
using DeSignMage.Rendering.Export;
using SkiaSharp;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Show Transparency (R27): the page as a checkerboard on screen, and exports that are always transparent where nothing is drawn.</summary>
[Collection("app-settings")]   // the toggle, dark mode and the settings store are program-wide: one class at a time
public class TransparencyShellTests
{
    private static async Task WithTheme(Func<SettingsStore, Task> body)
    {
        var oldStore = AppSettings.Store;
        var store = AppSettings.Store = new SettingsStore(null);
        UiScale.Store = store;
        UiScale.Reset();
        AppTheme.Reset();
        try { await body(store); }
        finally
        {
            AppTheme.Reset();
            UiScale.Reset();
            UiScale.Store = null;
            AppSettings.Store = oldStore;
        }
    }

    private static MainWindow Open(double width = 1600)
    {
        var w = new MainWindow(new StartupOptions()) { Width = width, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        return w;
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static void Press(MainWindow w, Control c)
    {
        var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w).GetValueOrDefault();
        w.MouseDown(p, Avalonia.Input.MouseButton.Left);
        w.MouseUp(p, Avalonia.Input.MouseButton.Left);
        Flush();
    }

    private static SKBitmap Pixels(MainWindow w)
    {
        Flush();
        using var ms = new MemoryStream();
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
        w.CaptureRenderedFrame()!.Save(ms);
#pragma warning restore CS0618
        ms.Position = 0;
        return SKBitmap.Decode(ms);
    }

    private static MenuItem ToolsMenu(MainWindow w) => w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == "_Tools");

    // ---------------------------------------------------------------- the toggle

    [AvaloniaFact]
    public async Task Show_Transparency_is_off_by_default_remembered_and_independent_of_dark_mode()
    {
        await WithTheme(store =>
        {
            AppTheme.Load(store);
            Assert.False(AppTheme.ShowTransparency);
            Assert.Null(store.Get<bool?>("ui.showTransparency", null));          // nothing written until it is changed

            AppTheme.SetShowTransparency(true);
            Assert.True(AppTheme.ShowTransparency);
            Assert.True(store.Get("ui.showTransparency", false));
            Assert.False(AppTheme.DarkMenus);                                    // the other switches are untouched
            Assert.False(AppTheme.DarkCanvas);
            AppTheme.SetDarkCanvas(true);
            AppTheme.SetDarkMenus(true);
            Assert.True(AppTheme.ShowTransparency);                              // and it keeps its state when they change
            AppTheme.SetDarkMenus(false);
            AppTheme.SetDarkCanvas(false);
            Assert.True(AppTheme.ShowTransparency);

            AppTheme.Reset();                                                    // the next run reads it back
            Assert.False(AppTheme.ShowTransparency);
            AppTheme.Load(store);
            Assert.True(AppTheme.ShowTransparency);
            AppTheme.SetShowTransparency(false);
            AppTheme.Reset();
            AppTheme.Load(store);
            Assert.False(AppTheme.ShowTransparency);

            AppTheme.SetShowTransparency(true, remember: false);                 // a development run need not write it down
            Assert.False(store.Get("ui.showTransparency", true));
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task The_Tools_menu_has_a_checkable_Show_Transparency_after_Loops_and_before_Options_without_a_shortcut()
    {
        await WithTheme(store =>
        {
            var w = Open();
            var tools = ToolsMenu(w);
            var items = tools.Items.ToList();
            int Index(string header) => items.FindIndex(i => i is MenuItem m && m.Header?.ToString() == header);
            int loop = Index("_Loop Edit Tool"), transparency = Index("Show _Transparency"), options = Index("_Options...");
            Assert.True(loop >= 0 && transparency >= 0 && options >= 0);
            Assert.Equal(loop + 1, transparency);                                // right after Loops
            Assert.True(transparency < options);                                 // before Options
            var item = Assert.IsType<MenuItem>(items[transparency]);
            Assert.Equal(Avalonia.Controls.MenuItemToggleType.CheckBox, item.ToggleType);
            var command = Assert.IsType<AppCommand>(item.Tag);
            Assert.Equal("tools.showTransparency", command.Id);
            Assert.Empty(command.Gestures);                                      // no shortcut
            Assert.Null(item.InputGesture);
            Assert.False(command.IsChecked!());

            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(AppTheme.ShowTransparency);
            Assert.True(command.IsChecked!());
            Assert.True(store.Get("ui.showTransparency", false));
            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(AppTheme.ShowTransparency);
            w.Close();
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- on screen

    [AvaloniaFact]
    public async Task The_page_turns_into_a_checkerboard_on_screen_in_light_and_dark_canvas_and_the_pasteboard_does_not()
    {
        await WithTheme(store =>
        {
            var w = Open();
            var canvas = w.FindControl<DesignCanvas>("Surface")!;
            var page = w.Session.PageBounds.Transform(w.Session.Viewport.DocToScreen);
            var topLeft = canvas.TranslatePoint(new Point(page.Left, page.Top), w)!.Value;
            SKColor At(SKBitmap bmp, double dx, double dy) => bmp.GetPixel((int)Math.Round(topLeft.X + dx), (int)Math.Round(topLeft.Y + dy));
            SKColor Outside(SKBitmap bmp) { var p = canvas.TranslatePoint(new Point(4, 4), w)!.Value; return bmp.GetPixel((int)p.X, (int)p.Y); }

            foreach (bool darkCanvas in new[] { false, true })
            {
                AppTheme.SetDarkCanvas(darkCanvas);
                var theme = AppTheme.Canvas;
                AppTheme.SetShowTransparency(false);
                using (var plain = Pixels(w))
                {
                    Assert.Equal(theme.Page ?? theme.Pasteboard, At(plain, 20, 20));        // the page color as before
                    Assert.Equal(theme.Page ?? theme.Pasteboard, At(plain, 28, 20));
                }
                AppTheme.SetShowTransparency(true);
                using var checkered = Pixels(w);
                Assert.Equal(theme.CheckerA, At(checkered, 4, 4));
                Assert.Equal(theme.CheckerB, At(checkered, 12, 4));
                Assert.Equal(theme.CheckerB, At(checkered, 4, 12));
                Assert.Equal(theme.CheckerA, At(checkered, 12, 12));
                Assert.Equal(theme.Pasteboard, Outside(checkered));                           // the area around the page looks as before
            }
            w.Close();
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- exports

    private static Document DocWithOneShape()
    {
        var doc = new Document(PageSetup.FromInches(3, 2));
        var s = new DeSignMage.Core.Editing.EditorSession(doc);
        s.History.Execute(new AddObjectCommand(doc.ActiveLayer, new RectangleObject(60, 40) { Transform = Affine2.Translation(30, 30), Style = ObjectStyle.FillOnly(DocColor.FromHex("#E53935")) }));
        return doc;
    }

    [AvaloniaFact]
    public async Task A_PNG_export_has_real_alpha_where_nothing_is_drawn_whatever_the_toggle_or_dark_canvas_say()
    {
        await WithTheme(store =>
        {
            var doc = DocWithOneShape();
            var options = new ExportOptions { Dpi = 72 };
            byte[]? first = null;
            foreach (var (transparency, darkCanvas, darkMenus) in new[] { (false, false, false), (true, false, false), (false, true, false), (true, true, false), (true, true, true) })
            {
                AppTheme.SetShowTransparency(transparency);
                AppTheme.SetDarkCanvas(darkCanvas);
                AppTheme.SetDarkMenus(darkMenus);
                var png = DocumentExporter.RenderPng(doc, options);
                first ??= png;
                Assert.Equal(first, png);                                        // not one byte depends on the switches
                using var bmp = SKBitmap.Decode(png);
                Assert.Equal(0, bmp.GetPixel(2, 2).Alpha);                       // nothing drawn: fully transparent
                Assert.Equal(0, bmp.GetPixel(200, 130).Alpha);
                Assert.Equal(0, bmp.GetPixel(100, 5).Alpha);
                Assert.Equal(new SKColor(0xE5, 0x39, 0x35, 255), bmp.GetPixel(60, 50));      // what is drawn keeps its color
                Assert.NotEqual(SKAlphaType.Opaque, bmp.AlphaType);               // the file has an alpha channel
                Assert.DoesNotContain(Enumerable.Range(0, bmp.Width).SelectMany(x => Enumerable.Range(0, bmp.Height).Select(y => bmp.GetPixel(x, y))),
                    c => c.Alpha == 255 && (c == CanvasTheme.Dark.Page || c == new SKColor(0x4C, 0x4C, 0x4C) || c == new SKColor(0xCC, 0xCC, 0xCC)));   // no display color leaked
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>The text of every compressed stream of a PDF (its page content), joined.</summary>
    private static string PdfContents(byte[] pdf)
    {
        var raw = Encoding.Latin1.GetString(pdf);
        var all = new StringBuilder();
        int at = 0;
        while ((at = raw.IndexOf("stream", at, StringComparison.Ordinal)) >= 0)
        {
            int start = at + "stream".Length;
            if (raw[start] == '\r') start++;
            if (raw[start] == '\n') start++;
            int end = raw.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) break;
            if (end - start <= 2) { at = end + "endstream".Length; continue; }
            try
            {
                using var input = new MemoryStream(pdf, start + 2, end - start - 2);   // skip the two zlib header bytes
                using var inflate = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                inflate.CopyTo(output);
                all.Append(Encoding.Latin1.GetString(output.ToArray())).Append('\n');
            }
            catch (InvalidDataException) { }   // not a compressed stream (a font, an image)
            at = end + "endstream".Length;
        }
        return all.ToString();
    }

    [AvaloniaFact]
    public async Task A_PDF_and_an_SVG_have_no_page_background()
    {
        await WithTheme(store =>
        {
            AppTheme.SetShowTransparency(true);
            AppTheme.SetDarkCanvas(true);
            var empty = new Document(PageSetup.FromInches(3, 2));
            var options = new ExportOptions { Dpi = 72 };

            var svg = Encoding.UTF8.GetString(DocumentExporter.Render(empty, ExportFormat.Svg, options));
            Assert.DoesNotContain("<rect", svg);                                // an empty page is just the empty drawing
            Assert.DoesNotContain("<path", svg);
            Assert.DoesNotContain("#424242", svg, StringComparison.OrdinalIgnoreCase);

            var content = PdfContents(DocumentExporter.RenderPdf(empty, options));
            Assert.DoesNotContain(" re", content);                              // no rectangle was filled
            Assert.DoesNotMatch(@"(^|\s)f\*?(\s|$)", content);                  // and nothing was filled at all

            // (the check can see a fill: a page with a shape in it has one)
            Assert.Matches(@"(^|\s)f\*?(\s|$)", PdfContents(DocumentExporter.RenderPdf(DocWithOneShape(), options)));

            // with a shape on the page there is drawing, but still no rectangle for the page behind it
            var doc = DocWithOneShape();
            var svgShape = Encoding.UTF8.GetString(DocumentExporter.Render(doc, ExportFormat.Svg, options));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(svgShape, "<(rect|path)"));
            Assert.DoesNotContain("#4C4C4C", svgShape, StringComparison.OrdinalIgnoreCase);
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task The_export_dialog_no_longer_offers_a_transparent_background_checkbox()
    {
        await WithTheme(store =>
        {
            var w = Open();
            var dialog = new ExportDialog(w.Session.Document, [], new ExportSettings());
            dialog.Show(w);
            Flush();
            Assert.DoesNotContain(dialog.GetLogicalDescendants().OfType<CheckBox>(), c => c.Content?.ToString()?.Contains("ransparen", StringComparison.OrdinalIgnoreCase) == true);
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Transparent background") == true);
            dialog.Close();
            w.Close();
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- pictures for the notes

    /// <summary>Saves pictures of Show Transparency with a soft shadow and a holed shape, light and dark, when DSM_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public async Task Screenshots_of_Show_Transparency_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        await WithTheme(store =>
        {
            Directory.CreateDirectory(dir);
            var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 800 };
            w.Show();
            Flush();
            var s = w.Session;
            // a red shape with a soft shadow, and a black ring-like shape with a hole in it, on the page
            var box = new RectangleObject(160, 100) { Transform = Affine2.Translation(60, 60), Style = ObjectStyle.FillOnly(DocColor.FromHex("#CC0000")) };
            s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, box));
            s.Selection.Set(box);
            s.ApplySoftEffect(new DeSignMage.Core.Effects.SoftSettings { Opacity = 1, Softness = 0.6, ShiftX = 22, ShiftY = 22, Color = DocColor.Black });
            var outer = new EllipseObject(180, 180) { Transform = Affine2.Translation(380, 40), Style = ObjectStyle.FillOnly(DocColor.FromHex("#1E88E5")) };
            var hole = new EllipseObject(90, 90) { Transform = Affine2.Translation(425, 85), Style = ObjectStyle.FillOnly(DocColor.FromHex("#1E88E5")) };
            s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, outer));
            s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, hole));
            s.Selection.Set([outer, hole]);
            s.CombineSelection();
            s.Selection.Clear();
            s.ZoomToContent();
            Flush();
            void Save(string name)
            {
                Flush();
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
                w.CaptureRenderedFrame()!.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
            }
            AppTheme.SetShowTransparency(true, remember: false);
            AppTheme.SetDarkCanvas(false, remember: false); AppTheme.SetDarkMenus(false, remember: false);
            Save("transparency-light");
            AppTheme.SetDarkCanvas(true, remember: false); AppTheme.SetDarkMenus(true, remember: false);
            Save("transparency-dark");
            w.Close();
            return Task.CompletedTask;
        });
    }
}
