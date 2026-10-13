using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
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

/// <summary>Dark mode (R19): Dark Menus and Dark Canvas, two independent switches that are remembered and never reach an export.</summary>
[Collection("app-settings")]   // these swap the program-wide settings store and the theme: one class at a time
public class DarkModeTests
{
    /// <summary>A private settings store and both switches off for the test; everything is put back afterwards (all of it is program-wide state).</summary>
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

    private static MainWindow Open(bool demo = false, bool guides = false)
    {
        var w = new MainWindow(new StartupOptions(Demo: demo, Guides: guides)) { Width = 1400, Height = 860 };
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

    /// <summary>The window as pixels.</summary>
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

    private static SKColor At(SKBitmap bmp, MainWindow w, Control c, double dx, double dy)
    {
        var p = c.TranslatePoint(new Point(dx, dy), w)!.Value;
        double scale = w.RenderScaling;
        return bmp.GetPixel((int)(p.X * scale), (int)(p.Y * scale));
    }

    private static Color Rgb(string hex) => Color.Parse(hex);

    private static bool Neutral(Color c) => c.R == c.G && c.G == c.B;

    private static double Luminance(Color c)
    {
        static double Lin(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static Color ToColor(SKColor c) => Color.FromRgb(c.Red, c.Green, c.Blue);

    // ---------------------------------------------------------------- the two switches

    [AvaloniaFact]
    public async Task Both_switches_start_off_and_light_mode_keeps_the_classic_look()
    {
        await WithTheme(store =>
        {
            AppTheme.Load(store);
            Assert.False(AppTheme.DarkMenus);
            Assert.False(AppTheme.DarkCanvas);
            Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
            Assert.Equal(Rgb("#F0F0F0"), AppTheme.ColorOf("Ui.Chrome"));
            Assert.Equal(Rgb("#C8C8C8"), AppTheme.ColorOf("Ui.ChromeBorder"));
            Assert.Equal(Rgb("#F7F7F7"), AppTheme.ColorOf("Ui.Panel"));
            Assert.Equal(Rgb("#E6E6E6"), AppTheme.ColorOf("Ui.TabBack"));
            Assert.Same(CanvasTheme.Light, AppTheme.Canvas);
            Assert.Equal(SKColors.White, CanvasTheme.Light.Pasteboard);   // the real white page
            Assert.Null(CanvasTheme.Light.Page);
            Assert.Equal(SKColors.Black, CanvasTheme.Light.Open);
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task The_switches_are_independent_and_remembered_between_runs()
    {
        await WithTheme(store =>
        {
            AppTheme.SetDarkMenus(true);
            Assert.True(AppTheme.DarkMenus);
            Assert.False(AppTheme.DarkCanvas);                                  // the other one is untouched
            Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
            Assert.Equal(Rgb("#333333"), AppTheme.ColorOf("Ui.Chrome"));
            Assert.Same(CanvasTheme.Light, AppTheme.Canvas);
            Assert.True(store.Get("ui.darkMenus", false));
            Assert.False(store.Get("ui.darkCanvas", false));

            AppTheme.SetDarkCanvas(true);
            Assert.True(AppTheme.DarkCanvas);
            Assert.Same(CanvasTheme.Dark, AppTheme.Canvas);
            AppTheme.SetDarkMenus(false);                                       // menus back to light, the canvas stays dark
            Assert.False(AppTheme.DarkMenus);
            Assert.True(AppTheme.DarkCanvas);
            Assert.Equal(ThemeVariant.Light, Application.Current.RequestedThemeVariant);
            Assert.Equal(Rgb("#F0F0F0"), AppTheme.ColorOf("Ui.Chrome"));
            Assert.False(store.Get("ui.darkMenus", true));
            Assert.True(store.Get("ui.darkCanvas", false));

            // the next run reads them back
            AppTheme.Reset();
            Assert.False(AppTheme.DarkCanvas);
            AppTheme.Load(store);
            Assert.False(AppTheme.DarkMenus);
            Assert.True(AppTheme.DarkCanvas);
            AppTheme.SetDarkMenus(true);
            AppTheme.Reset();
            AppTheme.Load(store);
            Assert.True(AppTheme.DarkMenus);
            Assert.True(AppTheme.DarkCanvas);

            // a development run can switch without writing it down
            AppTheme.SetDarkMenus(false, remember: false);
            Assert.True(store.Get("ui.darkMenus", false));
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public void The_switches_are_stored_in_the_settings_file_and_an_empty_file_means_both_off()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dsm-theme-{Guid.NewGuid():N}.json");
        var oldStore = AppSettings.Store;
        try
        {
            AppSettings.Store = new SettingsStore(path);
            AppTheme.Reset();
            AppTheme.SetDarkCanvas(true);
            AppTheme.Reset();
            AppTheme.Load(new SettingsStore(path));                             // a new run of the program
            Assert.True(AppTheme.DarkCanvas);
            Assert.False(AppTheme.DarkMenus);
            AppTheme.Reset();
            AppTheme.Load(new SettingsStore(Path.Combine(Path.GetTempPath(), $"dsm-theme-none-{Guid.NewGuid():N}.json")));
            Assert.False(AppTheme.DarkCanvas);
            Assert.False(AppTheme.DarkMenus);
        }
        finally
        {
            AppTheme.Reset();
            AppSettings.Store = oldStore;
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- the greys

    [Fact]
    public void Every_dark_grey_is_neutral_and_the_UI_greys_sit_in_the_pro_app_range()
    {
        // colors that are on purpose not grey: accent blue, error red, the amber notice, the blue ruler marker
        string[] colored = ["Ui.Error", "Ui.IconAccent", "Ui.RulerMarker", "Ui.NoticeBack", "Ui.NoticeBorder"];
        foreach (var key in AppTheme.Keys.Where(k => !colored.Contains(k)))
            Assert.True(Neutral(AppTheme.ColorIn(key, dark: true)), $"{key} is {AppTheme.ColorIn(key, dark: true)} in dark mode: R, G and B must be equal");

        // the surfaces of the chrome: #2B to #3C; text about #E0 to #E8
        foreach (var key in new[] { "Ui.WindowBack", "Ui.Chrome", "Ui.Panel", "Ui.TabBack" })
            Assert.InRange(AppTheme.ColorIn(key, dark: true).R, 0x2B, 0x3C);
        Assert.True(Neutral(AppTheme.DarkText));
        Assert.InRange(AppTheme.DarkText.R, 0xE0, 0xE8);
        Assert.True(Neutral(AppTheme.DarkField));                       // the focused text box too

        // the canvas: neutral too, and the page a bit lighter than the workspace around it
        var d = CanvasTheme.Dark;
        foreach (var c in new[] { d.Pasteboard, d.Page!.Value, d.PageEdge, d.Open, d.Marquee, d.Caret, d.Label, d.LabelHalo })
            Assert.True(c.Red == c.Green && c.Green == c.Blue, $"{c} is not neutral");
        Assert.True(d.Page!.Value.Red > d.Pasteboard.Red);
        Assert.True(d.Page!.Value.Red - d.Pasteboard.Red < 0x30, "only a bit lighter");
    }

    [Fact]
    public void Everything_the_canvas_draws_stays_visible_on_the_dark_page_and_around_it()
    {
        var d = CanvasTheme.Dark;
        var page = ToColor(d.Page!.Value);
        var around = ToColor(d.Pasteboard);
        var drawn = new Dictionary<string, SKColor>
        {
            ["plain outlines (open paths)"] = d.Open,
            ["outer outlines (blue)"] = d.Border,
            ["holes (red)"] = d.Interior,
            ["guides"] = d.Guide,
            ["the marquee"] = d.Marquee,
            ["the text caret"] = d.Caret,
            ["loop numbers"] = d.Label,
            ["the page edge"] = d.PageEdge,
        };
        foreach (var (what, color) in drawn)
        {
            Assert.True(Contrast(ToColor(color), page) >= 3, $"{what} on the dark page: {Contrast(ToColor(color), page):0.0}:1");
            Assert.True(Contrast(ToColor(color), around) >= 3, $"{what} on the workspace: {Contrast(ToColor(color), around):0.0}:1");
        }
        // selection handles invert what is under them (Difference with white): light on the dark page, so they are visible as well
        Assert.True(Contrast(Color.FromRgb((byte)(255 - page.R), (byte)(255 - page.G), (byte)(255 - page.B)), page) >= 3);
    }

    // ---------------------------------------------------------------- the window

    [AvaloniaFact]
    public async Task The_top_row_has_the_two_switches_and_the_View_menu_the_two_checkable_items_without_shortcuts()
    {
        await WithTheme(store =>
        {
            var w = Open();
            var menus = w.FindControl<ToggleButton>("DarkMenusButton")!;
            var canvas = w.FindControl<ToggleButton>("DarkCanvasButton")!;
            var feedback = w.FindControl<Button>("FeedbackButton")!;
            Assert.Equal("Dark Menus", ToolTip.GetTip(menus));
            Assert.Equal("Dark Canvas", ToolTip.GetTip(canvas));
            Assert.NotNull(menus.Content);
            Assert.NotNull(canvas.Content);
            // at the right end of the top row, next to Feedback / Bug
            var top = w.FindControl<Border>("TopRowBorder")!;
            Assert.Contains(top, menus.GetVisualAncestors());
            Assert.True(menus.TranslatePoint(default, w)!.Value.X < canvas.TranslatePoint(default, w)!.Value.X);
            Assert.True(canvas.TranslatePoint(default, w)!.Value.X < feedback.TranslatePoint(default, w)!.Value.X);
            Assert.False(menus.IsChecked);
            Assert.False(canvas.IsChecked);

            Press(w, menus);
            Assert.True(AppTheme.DarkMenus);
            Assert.False(AppTheme.DarkCanvas);
            Assert.True(menus.IsChecked);
            Assert.False(canvas.IsChecked);
            Press(w, canvas);
            Assert.True(AppTheme.DarkCanvas);
            Assert.True(canvas.IsChecked);
            Press(w, menus);                                                     // each one turns off by itself
            Assert.False(AppTheme.DarkMenus);
            Assert.False(menus.IsChecked);
            Assert.True(canvas.IsChecked);

            // View menu: two checkable items that agree with the switches, and no shortcut on either
            var view = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == "_View");
            MenuItem Item(string header) => view.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == header);
            var darkMenus = Item("Dark _Menus");
            var darkCanvas = Item("Dark _Canvas");
            Assert.Equal(MenuItemToggleType.CheckBox, darkMenus.ToggleType);
            Assert.Equal(MenuItemToggleType.CheckBox, darkCanvas.ToggleType);
            Assert.All(new[] { darkMenus, darkCanvas }, m =>
            {
                var cmd = Assert.IsType<AppCommand>(m.Tag);
                Assert.Empty(cmd.Gestures);
                Assert.Null(m.InputGesture);
            });
            Assert.False(((AppCommand)darkMenus.Tag!).IsChecked!());
            Assert.True(((AppCommand)darkCanvas.Tag!).IsChecked!());
            darkMenus.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(AppTheme.DarkMenus);
            Assert.True(menus.IsChecked);                                        // the button follows the menu
            darkCanvas.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(AppTheme.DarkCanvas);
            Assert.False(canvas.IsChecked);
            Assert.True(store.Get("ui.darkMenus", false));
            Assert.False(store.Get("ui.darkCanvas", true));
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Dark_Menus_darkens_the_chrome_and_leaves_the_canvas_alone()
    {
        await WithTheme(store =>
        {
            var w = Open(demo: true);
            var toolbox = w.FindControl<StackPanel>("ToolboxHost")!.Parent!;
            var canvas = w.FindControl<DesignCanvas>("Surface")!;
            Control toolboxBorder = (Control)toolbox;

            using (var light = Pixels(w))
            {
                Assert.Equal(new SKColor(0xF0, 0xF0, 0xF0), At(light, w, toolboxBorder, 10, toolboxBorder.Bounds.Height - 20));
                Assert.Equal(SKColors.White, At(light, w, canvas, 5, 5));
            }
            AppTheme.SetDarkMenus(true);
            using (var dark = Pixels(w))
            {
                Assert.Equal(new SKColor(0x33, 0x33, 0x33), At(dark, w, toolboxBorder, 10, toolboxBorder.Bounds.Height - 20));   // the Toolbox is dark
                Assert.Equal(SKColors.White, At(dark, w, canvas, 5, 5));                                                         // the canvas is not
                var window = At(dark, w, w.FindControl<Border>("PaletteStripBorder")!, 2, 200);
                Assert.Equal(window.Red, window.Green);                                                                           // neutral
            }
            // the rest of the chrome follows through the same brushes
            Assert.Equal(AppTheme.ColorOf("Ui.Chrome"), ((ISolidColorBrush)w.FindControl<Border>("TopRowBorder")!.Background!).Color);
            Assert.Equal(Rgb("#333333"), ((ISolidColorBrush)w.FindControl<Border>("TopRowBorder")!.Background!).Color);
            Press(w, w.FindControl<ToggleButton>("DockTabLayers")!);
            Assert.Equal(Rgb("#3C3C3C"), ((ISolidColorBrush)w.FindControl<Grid>("DockGrid")!.Background!).Color);
            // text and drop-downs: standard controls use the dark theme's soft white
            Assert.Equal(AppTheme.DarkText, ((ISolidColorBrush)w.FindControl<Button>("FeedbackButton")!.Foreground!).Color);
            Assert.Equal(ThemeVariant.Dark, w.ActualThemeVariant);

            AppTheme.SetDarkMenus(false);                                        // and back, live
            Assert.Equal(Rgb("#F0F0F0"), ((ISolidColorBrush)w.FindControl<Border>("TopRowBorder")!.Background!).Color);
            Assert.Equal(ThemeVariant.Light, w.ActualThemeVariant);
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Dark_Canvas_darkens_the_canvas_and_the_page_and_leaves_the_chrome_alone()
    {
        await WithTheme(store =>
        {
            var w = Open();
            var canvas = w.FindControl<DesignCanvas>("Surface")!;
            var toolbox = (Control)w.FindControl<StackPanel>("ToolboxHost")!.Parent!;
            var page = w.Session.Document.Page.Bounds;
            var center = w.Session.Viewport.ToScreen(page.Center);

            using (var light = Pixels(w))
            {
                Assert.Equal(SKColors.White, At(light, w, canvas, 5, 5));
                Assert.Equal(SKColors.White, At(light, w, canvas, center.X, center.Y));                // the real white page
            }
            AppTheme.SetDarkCanvas(true);
            using (var dark = Pixels(w))
            {
                Assert.Equal(CanvasTheme.Dark.Pasteboard, At(dark, w, canvas, 5, 5));                  // around the page
                Assert.Equal(CanvasTheme.Dark.Page, At(dark, w, canvas, center.X, center.Y));          // the page itself, lighter
                Assert.Equal(new SKColor(0xF0, 0xF0, 0xF0), At(dark, w, toolbox, 10, toolbox.Bounds.Height - 20));   // the chrome is not touched
            }
            Assert.Equal(ThemeVariant.Light, w.ActualThemeVariant);

            AppTheme.SetDarkCanvas(false);                                       // flip back: the design on its real white page
            using (var back = Pixels(w))
                Assert.Equal(SKColors.White, At(back, w, canvas, center.X, center.Y));
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task The_menu_font_size_setting_keeps_working_in_both_modes()
    {
        await WithTheme(store =>
        {
            var w = Open();
            MenuItem File() => w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == "_File");
            double at100 = File().FontSize;
            foreach (bool dark in new[] { false, true })
            {
                AppTheme.SetDarkMenus(dark);
                UiScale.SetScale(1.5);
                Flush();
                Assert.Equal(at100 * 1.5, File().FontSize, 3);
                UiScale.SetScale(2.0);
                Flush();
                Assert.Equal(at100 * 2, File().FontSize, 3);
                UiScale.SetScale(1.0);
                Flush();
                Assert.Equal(at100, File().FontSize, 3);
            }
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task The_Dock_starts_closed_in_dark_mode_too_and_the_switches_do_not_change_it()
    {
        await WithTheme(store =>
        {
            store.Set("ui.darkMenus", true);
            store.Set("ui.darkCanvas", true);
            AppTheme.Load(store);
            var w = Open();
            Assert.Null(w.OpenDockPanel);                                        // only the Dock panels start closed; the switches were remembered
            Assert.True(AppTheme.DarkMenus && AppTheme.DarkCanvas);
            Press(w, w.FindControl<ToggleButton>("DockTabHistory")!);
            Assert.Equal("history", w.OpenDockPanel);
            AppTheme.SetDarkMenus(false);
            Assert.Equal("history", w.OpenDockPanel);
            w.Close();
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- swatches and exports

    [AvaloniaFact]
    public async Task Swatches_always_show_their_true_color_with_a_neutral_border_that_shows_black_and_white()
    {
        await WithTheme(store =>
        {
            foreach (bool dark in new[] { false, true })
            {
                AppTheme.SetDarkMenus(dark);
                foreach (var color in new[] { DocColor.Black, DocColor.White, DocColor.FromHex("#E53935") })
                {
                    var swatch = ColorUi.Swatch(new SolidPaint(color), 20);
                    var box = swatch.GetLogicalChildren().OfType<Border>().First();
                    var fill = ((ISolidColorBrush)box.Background!).Color;
                    Assert.Equal(Color.FromArgb(color.A, color.R, color.G, color.B), fill);          // never recolored by the mode
                    var border = ((ISolidColorBrush)box.BorderBrush!).Color;
                    Assert.True(Neutral(border), "a neutral border");
                    Assert.Equal(1, box.BorderThickness.Left);                                       // thin
                    // the border shows against the panel it sits on, and against the swatch itself when that is black (dark) or white (light)
                    Assert.True(Contrast(border, AppTheme.ColorOf("Ui.Panel")) >= 2.5 || Contrast(border, AppTheme.ColorOf("Ui.Chrome")) >= 2.5);
                    if (dark) Assert.True(Contrast(border, Color.FromRgb(0, 0, 0)) >= 3, "a black swatch shows in dark mode");
                    else Assert.True(Contrast(border, Color.FromRgb(255, 255, 255)) >= 3, "a white swatch shows in light mode");
                }
            }
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Neither_switch_ever_reaches_an_export_or_the_picture_drawn_for_files()
    {
        await WithTheme(store =>
        {
            var doc = new Document(PageSetup.FromInches(3, 2));
            var session = new DeSignMage.Core.Editing.EditorSession(doc);
            var rect = new RectangleObject(100, 60) { Transform = Affine2.Translation(30, 30), Style = ObjectStyle.FillOnly(DocColor.FromHex("#E53935")) };
            session.History.Execute(new AddObjectCommand(doc.ActiveLayer, rect));
            var line = new PathObject(new PathBuilder().MoveTo(10, 150).LineTo(150, 170).LineTo(200, 120).Build());   // an uncolored open path
            session.History.Execute(new AddObjectCommand(doc.ActiveLayer, line));

            var options = new ExportOptions { Dpi = 72 };
            byte[] png = DocumentExporter.RenderPng(doc, options);
            byte[] svg = DocumentExporter.Render(doc, ExportFormat.Svg, options);
            byte[] pdf = DocumentExporter.RenderPdf(doc, options);

            AppTheme.SetDarkMenus(true);
            AppTheme.SetDarkCanvas(true);
            Assert.Equal(png, DocumentExporter.RenderPng(doc, options));
            Assert.Equal(svg, DocumentExporter.Render(doc, ExportFormat.Svg, options));
            Assert.Equal(pdf.Length, DocumentExporter.RenderPdf(doc, options).Length);

            using var bmp = SKBitmap.Decode(png);
            Assert.Equal(0, bmp.GetPixel(2, 2).Alpha);                // no page color in a file (R27): transparent, whatever the canvas looks like
            Assert.Equal(new SKColor(0xE5, 0x39, 0x35), bmp.GetPixel(60, 50));  // the design's own color

            // the renderer itself only turns dark when the canvas asks for it
            using var renderer = new SceneRenderer(doc);
            Assert.Same(CanvasTheme.Light, renderer.Theme);
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- the canvas drawing

    private static SKBitmap Draw(SceneRenderer renderer, CanvasTheme theme, int size = 220)
    {
        renderer.Theme = theme;
        var pic = renderer.GetDocumentPicture(null);
        var bmp = new SKBitmap(size, size);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(theme.Page ?? theme.Pasteboard);
            canvas.DrawPicture(pic.Picture);
        }
        pic.Release();
        return bmp;
    }

    [Fact]
    public void Outline_view_turns_plain_outlines_light_on_the_dark_canvas_and_back()
    {
        var doc = new Document(PageSetup.FromInches(3, 3));
        var session = new DeSignMage.Core.Editing.EditorSession(doc);
        // an open path (plain black outline) and an uncolored closed shape (blue outer edge)
        var open = new PathObject(new PathBuilder().MoveTo(10.5, 100.5).LineTo(110.5, 100.5).Build()) { Style = ObjectStyle.None };
        var box = new RectangleObject(50, 50) { Transform = Affine2.Translation(10.5, 10.5), Style = ObjectStyle.None };
        session.History.Execute(new AddObjectCommand(doc.ActiveLayer, open));
        session.History.Execute(new AddObjectCommand(doc.ActiveLayer, box));

        using var renderer = new SceneRenderer(doc) { Wireframe = true, ViewScale = 1 };
        using (var dark = Draw(renderer, CanvasTheme.Dark))
        {
            Assert.Equal(CanvasTheme.Dark.Open, dark.GetPixel(60, 100));         // light, not black, on the dark page
            Assert.Equal(CanvasTheme.Dark.Border, dark.GetPixel(10, 30));        // the blue stays (lifted so it does not sink into the grey)
        }
        using (var light = Draw(renderer, CanvasTheme.Light))                    // the same renderer, the document picture is redrawn
        {
            Assert.Equal(SKColors.Black, light.GetPixel(60, 100));
            Assert.Equal(SceneRenderer.BorderColor, light.GetPixel(10, 30));
        }
    }

    [Fact]
    public void A_frame_for_the_screen_fills_the_workspace_and_a_lighter_page_only_in_the_dark_theme()
    {
        var doc = new Document(PageSetup.FromInches(2, 1));
        var session = new DeSignMage.Core.Editing.EditorSession(doc);
        session.Viewport.SetViewSize(new Vec2(400, 300));
        session.ZoomToPage();
        using var frames = new CanvasFrameBuilder(session);

        SKColor Pixel(CanvasFrame frame, float x, float y)
        {
            using var bmp = new SKBitmap(400, 300);
            using (var c = new SKCanvas(bmp)) frame.Draw(c);
            return bmp.GetPixel((int)x, (int)y);
        }

        using (var light = frames.Build(400, 300))
        {
            var page = light.PageScreenRect;
            Assert.Equal(SKColors.White, Pixel(light, 2, 2));
            Assert.Equal(SKColors.White, Pixel(light, page.MidX, page.MidY));
        }
        frames.Theme = CanvasTheme.Dark;
        using (var dark = frames.Build(400, 300))
        {
            var page = dark.PageScreenRect;
            Assert.Equal(CanvasTheme.Dark.Pasteboard, Pixel(dark, 2, 2));
            Assert.Equal(CanvasTheme.Dark.Page, Pixel(dark, page.MidX, page.MidY));
        }
    }

    // ---------------------------------------------------------------- pictures for the notes

    /// <summary>Saves the pictures of the three combinations when DSM_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public async Task Screenshots_of_dark_mode_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        await WithTheme(store =>
        {
            Directory.CreateDirectory(dir);
            var w = new MainWindow(new StartupOptions(Demo: true, Guides: true)) { Width = 1500, Height = 900 };
            w.Show();
            Flush();
            var some = w.Session.Document.AllObjects().First(o => o is not GroupObject);
            // an open path with no color (plain outline) on the page, to show the outline view
            var open = new PathObject(new PathBuilder().MoveTo(40, 40).LineTo(140, 90).LineTo(220, 40).Build()) { Style = ObjectStyle.None };
            w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, open));
            w.Session.Selection.Set(some);
            Press(w, w.FindControl<ToggleButton>("DockTabColorMixer")!);
            void Save(string name)
            {
                Flush();
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
                w.CaptureRenderedFrame()!.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
            }
            AppTheme.SetDarkMenus(true); AppTheme.SetDarkCanvas(true);   Save("dark-both");
            AppTheme.SetDarkCanvas(false);                               Save("dark-menus");
            AppTheme.SetDarkMenus(false); AppTheme.SetDarkCanvas(true);  Save("dark-canvas");
            AppTheme.SetDarkMenus(true);
            w.Session.ShowFillColors = false;                            Save("dark-both-outlines");
            Press(w, w.FindControl<ToggleButton>("DockTabLayers")!);     Save("dark-both-outlines-layers");
            w.Close();
            return Task.CompletedTask;
        });
    }
}
