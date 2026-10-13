using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
using DeSignMage.Imaging;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>The Image, Effects and Tools tabs in the Context Bar and their strips of icon buttons (R25).</summary>
[Collection("app-settings")]   // dark mode and the text size are program-wide: one class at a time
public class MenuStripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dsm-strip-tests-" + Guid.NewGuid().ToString("N"));

    public MenuStripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static async Task WithTheme(Func<Task> body)
    {
        var oldStore = AppSettings.Store;
        AppSettings.Store = new SettingsStore(null);
        UiScale.Store = AppSettings.Store;
        UiScale.Reset();
        AppTheme.Reset();
        try { await body(); }
        finally
        {
            AppTheme.Reset();
            UiScale.Reset();
            UiScale.Store = null;
            AppSettings.Store = oldStore;
        }
    }

    private static MainWindow Open(double width = 1400, double height = 860)
    {
        var w = new MainWindow(new StartupOptions()) { Width = width, Height = height };
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

    private static MenuStrip StripOf(MainWindow w) => w.FindControl<MenuStrip>("MenuStrip")!;

    private static ToggleButton TabOf(MainWindow w, string name) => StripOf(w).Tab(name);

    private static RectangleObject AddRect(MainWindow w, double x, double y)
    {
        var r = new RectangleObject(100, 60) { Transform = DeSignMage.Core.Geometry.Affine2.Translation(x, y), Style = ObjectStyle.FillOnly(DocColor.FromHex("#E53935")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        return r;
    }

    /// <summary>The commands of the menu bar's menu with this name, in order (menu items that are placeholders or development helpers left out).</summary>
    private static string[] MenuCommandIds(MainWindow w, string name) =>
    [
        .. w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => m.Header?.ToString()!.Replace("_", "") == name)
            .Items.OfType<MenuItem>().Where(m => m.Tag is AppCommand { IsPlaceholder: false } c && !c.Id.StartsWith("debug.")).Select(m => ((AppCommand)m.Tag!).Id),
    ];

    // ---------------------------------------------------------------- the rule

    [Fact]
    public void One_strip_is_open_at_a_time_and_the_open_tab_closes_it()
    {
        var d = new DockLayout(["Image", "Effects", "Tools"]);
        Assert.Null(d.Open);                                   // starts closed
        d.Toggle("Image");
        Assert.Equal("Image", d.Open);
        d.Toggle("Tools");                                     // another tab swaps
        Assert.Equal("Tools", d.Open);
        Assert.False(d.IsOpen("Image"));
        d.Toggle("Tools");                                     // the open tab closes it
        Assert.Null(d.Open);
        Assert.Throws<ArgumentException>(() => d.Toggle("Layers"));   // a tab this strip does not have
        Assert.Equal(["colorMixer", "layers", "history"], new DockLayout().Panels);   // the Dock keeps its own three
    }

    // ---------------------------------------------------------------- in the window

    [AvaloniaFact]
    public async Task The_tabs_are_Image_Effects_and_Tools_in_the_right_part_of_the_Context_Bar_and_every_strip_starts_closed()
    {
        await WithTheme(() =>
        {
            var w = Open();
            var strip = StripOf(w);
            var bar = w.FindControl<Border>("ContextBarBorder")!;
            Assert.Equal(["Image", "Effects", "Tools"], strip.TabNames);
            Assert.Equal(["Image", "Effects", "Tools"], strip.Tabs.Children.OfType<ToggleButton>().Select(t => (string)t.Content!));
            Assert.All(strip.TabNames, n => Assert.False(strip.Tab(n).IsChecked));
            Assert.Null(w.OpenMenuStrip);
            Assert.False(strip.Panel.IsVisible);
            Assert.All(strip.TabNames, n => Assert.All(strip.ButtonsOf(n), b => Assert.False(b.GetVisualAncestors().OfType<BarRow>().First().IsVisible)));   // every strip's row is hidden

            // inside the Context Bar, at its right end, much narrower than the half it takes when open
            var topLeft = strip.TranslatePoint(new Point(0, 0), w)!.Value;
            var barLeft = bar.TranslatePoint(new Point(0, 0), w)!.Value;
            Assert.True(topLeft.X > barLeft.X + bar.Bounds.Width / 2);
            Assert.InRange(topLeft.X + strip.Bounds.Width, barLeft.X + bar.Bounds.Width - bar.Padding.Right - 1.5, barLeft.X + bar.Bounds.Width - bar.Padding.Right + 1.5);   // at the bar's right end
            Assert.True(topLeft.Y >= barLeft.Y && topLeft.Y + strip.Bounds.Height <= barLeft.Y + bar.Bounds.Height + 0.5);
            // the Dock is a different thing and stays closed
            Assert.Null(w.OpenDockPanel);
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Clicking_tabs_opens_one_strip_at_a_time_and_clicking_the_open_tab_closes_it()
    {
        await WithTheme(() =>
        {
            var w = Open();
            var strip = StripOf(w);
            Press(w, TabOf(w, "Image"));
            Assert.Equal("Image", w.OpenMenuStrip);
            Assert.True(TabOf(w, "Image").IsChecked);
            Assert.True(strip.Panel.IsVisible);
            w.CaptureRenderedFrame();
            Assert.All(strip.ButtonsOf("Image"), b => Assert.True(b.IsEffectivelyVisible));
            Assert.All(strip.ButtonsOf("Effects"), b => Assert.False(b.IsEffectivelyVisible));

            Press(w, TabOf(w, "Effects"));                       // another tab: that strip, not both
            w.CaptureRenderedFrame();
            Assert.Equal("Effects", w.OpenMenuStrip);
            Assert.False(TabOf(w, "Image").IsChecked);
            Assert.True(TabOf(w, "Effects").IsChecked);
            Assert.All(strip.ButtonsOf("Image"), b => Assert.False(b.IsEffectivelyVisible));
            Assert.All(strip.ButtonsOf("Effects"), b => Assert.True(b.IsEffectivelyVisible));

            Press(w, TabOf(w, "Effects"));                       // the open tab closes the strip
            w.CaptureRenderedFrame();
            Assert.Null(w.OpenMenuStrip);
            Assert.False(strip.Panel.IsVisible);
            Assert.All(strip.TabNames, n => Assert.False(strip.Tab(n).IsChecked));

            w.Close();
            Assert.Null(Open().OpenMenuStrip);                    // a new window starts closed again
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task The_dropdown_menus_stay_and_the_strips_list_what_the_menus_list_with_a_drawn_icon_each()
    {
        await WithTheme(() =>
        {
            var w = Open();
            var strip = StripOf(w);
            foreach (var name in strip.TabNames)
            {
                var ids = MenuCommandIds(w, name);
                Assert.NotEmpty(ids);
                Assert.Equal(ids, strip.CommandsOf(name).Select(c => c.Id));         // the same commands, in the menu's order
                Assert.Equal(ids.Length, strip.ButtonsOf(name).Count);
                foreach (var id in ids)
                    Assert.NotNull(MenuStripIcons.For(id));                           // every one has its own drawn icon (no initials fallback)
            }
            Assert.Contains("image.resample", strip.CommandsOf("Image").Select(c => c.Id));   // the newest Image command is in
            Assert.Contains("tool.vectorDraw", strip.CommandsOf("Tools").Select(c => c.Id));
            Assert.DoesNotContain(strip.CommandsOf("Tools"), c => c.Id.StartsWith("debug."));
            // the menu bar still has all of its own menus, unchanged
            var menus = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Select(m => m.Header!.ToString()!.Replace("_", "")).ToArray();
            Assert.Contains("Image", menus);
            Assert.Contains("Effects", menus);
            Assert.Contains("Tools", menus);
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Each_button_has_its_command_name_as_tooltip_and_the_icons_are_drawn_from_the_themed_brushes()
    {
        await WithTheme(() =>
        {
            var w = Open();
            var strip = StripOf(w);
            foreach (var name in strip.TabNames)
            {
                var commands = strip.CommandsOf(name);
                var buttons = strip.ButtonsOf(name);
                for (int i = 0; i < commands.Count; i++)
                {
                    string tip = Assert.IsType<string>(ToolTip.GetTip(buttons[i]));
                    Assert.Equal(commands[i].ToolTip, tip);
                    Assert.False(string.IsNullOrWhiteSpace(tip));
                    Assert.DoesNotContain("_", tip);
                }
            }
            Assert.Equal("Vectorize...", ToolTip.GetTip(strip.ButtonsOf("Image")[0]));
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task A_button_runs_its_menu_items_command_and_has_its_enabled_state()
    {
        await WithTheme(async () =>
        {
            var w = Open();
            var strip = StripOf(w);
            Press(w, TabOf(w, "Effects"));
            w.CaptureRenderedFrame();
            Button Weld() => strip.ButtonsOf("Effects")[strip.CommandsOf("Effects").ToList().FindIndex(c => c.Id == "effects.weld")];
            Button Knife() => strip.ButtonsOf("Effects")[strip.CommandsOf("Effects").ToList().FindIndex(c => c.Id == "effects.knife")];

            // nothing selected: what needs a selection is greyed, like the menu items
            Assert.False(Weld().IsEnabled);
            Assert.False(Knife().IsEnabled);
            void AllMatchTheirCommands()
            {
                foreach (var n in strip.TabNames)
                    for (int i = 0; i < strip.CommandsOf(n).Count; i++)
                        Assert.Equal(strip.CommandsOf(n)[i].CanExecute(), strip.ButtonsOf(n)[i].IsEnabled);
            }
            Press(w, TabOf(w, "Effects"));                         // close and look at the other strips too
            foreach (var n in strip.TabNames) { Press(w, TabOf(w, n)); AllMatchTheirCommands(); Press(w, TabOf(w, n)); }

            var a = AddRect(w, 100, 100);
            var b = AddRect(w, 140, 120);
            w.Session.Selection.Set([a, b]);
            Press(w, TabOf(w, "Effects"));
            Flush();
            w.CaptureRenderedFrame();
            Assert.True(Weld().IsEnabled);                          // two shapes: Weld is possible now
            Assert.True(Knife().IsEnabled);

            Press(w, Knife());                                      // runs Effects > Knife: the same command
            Assert.IsType<KnifeTool>(w.Session.ActiveTool);
            Assert.Contains("on", Knife().Classes);                 // and shows as pressed while its tool is active
            w.Session.SetTool("select");
            Flush();

            Press(w, Weld());                                       // Weld runs in the background; the two shapes become one
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (w.Session.Document.ActiveLayer.Children.Count > 1 && watch.ElapsedMilliseconds < 15000) { Flush(); await Task.Delay(10); }
            Assert.Single(w.Session.Document.ActiveLayer.Children);
            Assert.Equal("Weld", w.Session.History.UndoDescription);

            // Tools: a button changes the tool just as the menu item does
            Press(w, TabOf(w, "Tools"));
            w.CaptureRenderedFrame();
            var tools = strip.CommandsOf("Tools").ToList();
            Press(w, strip.ButtonsOf("Tools")[tools.FindIndex(c => c.Id == "tool.vector")]);
            Assert.IsType<VectorEditTool>(w.Session.ActiveTool);
            Press(w, strip.ButtonsOf("Tools")[tools.FindIndex(c => c.Id == "tool.select")]);
            Assert.IsType<SelectTool>(w.Session.ActiveTool);
            w.Close();
        });
    }

    [AvaloniaFact]
    public async Task Image_buttons_wait_for_a_picture_and_then_do_what_the_Image_menu_does()
    {
        await WithTheme(async () =>
        {
            var w = Open();
            var strip = StripOf(w);
            Press(w, TabOf(w, "Image"));
            w.CaptureRenderedFrame();
            var cmds = strip.CommandsOf("Image").ToList();
            Button B(string id) => strip.ButtonsOf("Image")[cmds.FindIndex(c => c.Id == id)];

            Assert.False(B("image.rotateCW").IsEnabled);            // no picture
            Assert.False(B("image.flipH").IsEnabled);
            Assert.False(B("image.resample").IsEnabled);
            Assert.True(B("image.vectorize").IsEnabled == cmds.Single(c => c.Id == "image.vectorize").CanExecute());

            var pixels = new PixelBuffer(60, 30);
            for (int y = 0; y < 30; y++) for (int x = 0; x < 60; x++) pixels.Set(x, y, (byte)(x * 4), (byte)(y * 8), 128, 255);
            string path = Path.Combine(_dir, "p.png");
            File.WriteAllBytes(path, ImageCodec.EncodePng(pixels));
            Assert.Equal(1, await w.ImportImagesAsync([path], null));
            Flush();
            w.CaptureRenderedFrame();
            var img = Assert.IsType<ImageObject>(Assert.Single(w.Session.Selection.Items));

            Assert.True(B("image.rotateCW").IsEnabled);              // a picture is selected: they come alive
            Assert.True(B("image.flipH").IsEnabled);
            Assert.True(B("image.resample").IsEnabled);
            Assert.True(B("image.crop").IsEnabled);
            Press(w, B("image.rotateCW"));
            Assert.Equal("Rotate 90 Degrees Clockwise", w.Session.History.UndoDescription);
            Press(w, B("image.flipV"));
            Assert.Equal("Flip Vertical", w.Session.History.UndoDescription);
            Press(w, B("image.crop"));
            Assert.IsType<CropTool>(w.Session.ActiveTool);
            Assert.NotNull(img);
            w.Close();
        });
    }

    // ---------------------------------------------------------------- layout

    [AvaloniaFact]
    public async Task Opening_and_closing_a_strip_never_changes_the_height_of_anything()
    {
        await WithTheme(() =>
        {
            var w = Open();
            var bar = w.FindControl<Border>("ContextBarBorder")!;
            var top = w.FindControl<Border>("TopRowBorder")!;
            var canvas = w.FindControl<DesignCanvas>("Surface")!;
            var band = w.FindControl<Grid>("DockBand")!;
            double barH = bar.Bounds.Height, topH = top.Bounds.Height, canvasH = canvas.Bounds.Height, canvasW = canvas.Bounds.Width;
            var canvasAt = canvas.TranslatePoint(default, w)!.Value;
            Assert.InRange(barH, ContextBars.BarHeight - 1, ContextBars.BarHeight + 1);

            void Check()
            {
                w.CaptureRenderedFrame();
                Assert.Equal(barH, bar.Bounds.Height, 0.01);
                Assert.Equal(topH, top.Bounds.Height, 0.01);
                Assert.Equal(canvasH, canvas.Bounds.Height, 0.01);
                Assert.Equal(canvasW, canvas.Bounds.Width, 0.01);
                Assert.Equal(canvasAt, canvas.TranslatePoint(default, w)!.Value);
                Assert.Equal(barH, band.Bounds.Height, 0.01);
            }
            foreach (var tool in new[] { "select", "vector", "text", "rectangle" })
            {
                w.Session.SetTool(tool);
                Flush();
                foreach (var name in StripOf(w).TabNames)
                {
                    Press(w, TabOf(w, name)); Check();            // open
                    Press(w, TabOf(w, name)); Check();            // close
                }
            }
            // and the strip's own content stays inside the bar
            Press(w, TabOf(w, "Image"));
            w.CaptureRenderedFrame();
            var panel = StripOf(w).Panel;
            double panelBottom = panel.TranslatePoint(new Point(0, panel.Bounds.Height), w)!.Value.Y;
            double barBottom = bar.TranslatePoint(new Point(0, bar.Bounds.Height), w)!.Value.Y;
            Assert.True(panelBottom <= barBottom + 0.5, $"strip bottom {panelBottom} vs bar bottom {barBottom}");
            foreach (var b in StripOf(w).ButtonsOf("Image"))
            {
                double bottom = b.TranslatePoint(new Point(0, b.Bounds.Height), w)!.Value.Y;
                Assert.True(bottom <= panelBottom + 0.5);
            }
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task An_open_strip_takes_the_right_half_and_the_tool_controls_keep_the_left_half()
    {
        await WithTheme(() =>
        {
            var w = Open();
            var strip = StripOf(w);
            var bar = w.FindControl<Border>("ContextBarBorder")!;
            var left = w.FindControl<ScrollViewer>("ContextBarScroll")!;
            double closedWidth = strip.Bounds.Width;
            Press(w, TabOf(w, "Image"));
            w.CaptureRenderedFrame();

            double barW = bar.Bounds.Width - bar.Padding.Left - bar.Padding.Right;
            Assert.InRange(strip.Bounds.Width, barW / 2 - 12, barW / 2 + 12);        // about the right half
            Assert.True(strip.Bounds.Width > closedWidth + 100);
            Assert.True(left.Bounds.Right <= strip.Bounds.Left + 0.5);                 // the tool controls end where the strip begins
            Assert.InRange(left.Bounds.Width, barW / 2 - 24, barW / 2 + 24);           // and keep the left half
            Assert.Equal(ContextBars.BarHeight, bar.Bounds.Height, 1.0);

            Press(w, TabOf(w, "Image"));                                               // closed: only the tabs again
            w.CaptureRenderedFrame();
            Assert.InRange(strip.Bounds.Width, closedWidth - 1, closedWidth + 1);
            Assert.True(left.Bounds.Width > barW - closedWidth - 40);
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public async Task The_interface_text_size_scales_the_tabs_and_buttons_and_the_strip_still_fits(double text)
    {
        await WithTheme(() =>
        {
            UiScale.SetScale(text, remember: false);
            var w = Open(2400, 1200);
            var strip = StripOf(w);
            Press(w, TabOf(w, "Image"));
            w.CaptureRenderedFrame();
            Assert.Equal(12 * text, TabOf(w, "Image").FontSize, 3);                    // tab text follows the text size
            var first = strip.ButtonsOf("Image")[0];
            Assert.Equal(26 * text, first.MinWidth, 3);                                // buttons follow the icon scale (icons scale with text)
            var bar = w.FindControl<Border>("ContextBarBorder")!;
            var panel = strip.Panel;
            double panelBottom = panel.TranslatePoint(new Point(0, panel.Bounds.Height), w)!.Value.Y;
            double barBottom = bar.TranslatePoint(new Point(0, bar.Bounds.Height), w)!.Value.Y;
            Assert.True(panelBottom <= barBottom + 0.5, $"at {text}: strip bottom {panelBottom} vs bar bottom {barBottom}");
            double buttonBottom = first.TranslatePoint(new Point(0, first.Bounds.Height), w)!.Value.Y;
            Assert.True(buttonBottom <= panelBottom + 0.5, $"at {text}: the buttons fit inside the strip");
            Assert.InRange(bar.Bounds.Height, ContextBars.BarHeight - 1, ContextBars.BarHeight + 1);
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Dark_Menus_darkens_the_tabs_the_strip_and_its_icons_and_the_open_tab_melts_into_the_strip()
    {
        await WithTheme(() =>
        {
            var w = Open();
            var strip = StripOf(w);
            Press(w, TabOf(w, "Effects"));
            w.CaptureRenderedFrame();

            Color PanelColor() => ((ISolidColorBrush)strip.Panel.Background!).Color;
            Color OpenTabColor() => ((ISolidColorBrush)TabOf(w, "Effects").GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter").Background!).Color;
            Color ClosedTabColor() => ((ISolidColorBrush)TabOf(w, "Image").GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter").Background!).Color;
            Assert.Equal(AppTheme.ColorIn("Ui.Panel", dark: false), PanelColor());
            Assert.Equal(PanelColor(), OpenTabColor());                                // the open tab and the strip are one color
            Assert.NotEqual(PanelColor(), ClosedTabColor());
            Assert.Equal(0, TabOf(w, "Effects").BorderThickness.Bottom);                // no line between the open tab and its strip

            AppTheme.SetDarkMenus(true);
            Flush();
            w.CaptureRenderedFrame();
            Assert.Equal(AppTheme.ColorIn("Ui.Panel", dark: true), PanelColor());
            Assert.Equal(PanelColor(), OpenTabColor());
            Assert.NotEqual(PanelColor(), ClosedTabColor());
            // the icons are drawn with the themed brush, which is now the light one: visible on the dark strip
            var icon = strip.ButtonsOf("Effects")[0].GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().First(p => p.Stroke is not null || p.Fill is not null);
            var ink = ((ISolidColorBrush)(icon.Fill ?? icon.Stroke)!).Color;
            Assert.True(ink.R > 0xC0 && ink.G > 0xC0 && ink.B > 0xC0, $"icon color {ink}");
            w.Close();
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- pictures for the notes

    /// <summary>Saves a picture of each strip, light and dark, when DSM_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public async Task Screenshots_of_the_menu_strips_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        await WithTheme(async () =>
        {
            Directory.CreateDirectory(dir);
            var w = new MainWindow(new StartupOptions(Demo: true)) { Width = 1500, Height = 900 };
            w.Show();
            Flush();
            // a picture and two shapes in the selection, so the buttons of all three strips are alive
            var pixels = new PixelBuffer(120, 60);
            for (int y = 0; y < 60; y++) for (int x = 0; x < 120; x++) pixels.Set(x, y, (byte)(x * 2), (byte)(y * 4), 140, 255);
            string path = Path.Combine(_dir, "p.png");
            File.WriteAllBytes(path, ImageCodec.EncodePng(pixels));
            await w.ImportImagesAsync([path], null);
            Flush();
            var a = AddRect(w, 100, 100);
            var b = AddRect(w, 140, 120);
            w.Session.Selection.Set([a, b]);
            Flush();
            void Save(string name)
            {
                Flush();
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
                w.CaptureRenderedFrame()!.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
            }
            foreach (bool dark in new[] { false, true })
            {
                AppTheme.SetDarkMenus(dark, remember: false);
                foreach (var name in new[] { "Image", "Effects", "Tools" })
                {
                    if (name == "Image") w.Session.Selection.Set(w.Session.Document.AllObjects().OfType<ImageObject>().First());
                    else if (name == "Effects") w.Session.Selection.Set([a, b]);
                    Flush();
                    Press(w, TabOf(w, name));
                    Save($"menu-strip-{name.ToLowerInvariant()}-{(dark ? "dark" : "light")}");
                    Press(w, TabOf(w, name));
                }
            }
            w.Close();
        });
    }
}
