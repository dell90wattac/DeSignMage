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

/// <summary>The Image, Effects and Tools tabs in the top row and their strips of buttons that drop down over the canvas (R25).</summary>
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

    /// <summary>The text on a tab (its name; a small down arrow sits beside it).</summary>
    private static string TabText(ToggleButton tab) => ((StackPanel)tab.Content!).Children.OfType<TextBlock>().Single().Text!;

    /// <summary>Opens a strip unless it is open already (a button closes its strip once it has run).</summary>
    private static void OpenStrip(MainWindow w, string name)
    {
        if (w.OpenMenuStrip != name) Press(w, TabOf(w, name));
        w.CaptureRenderedFrame();
    }

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
    public async Task The_tabs_are_Image_Effects_and_Tools_in_the_top_row_and_every_strip_starts_closed()
    {
        await WithTheme(() =>
        {
            var w = Open();
            var strip = StripOf(w);
            var top = w.FindControl<Border>("TopRowBorder")!;
            Assert.Equal(["Image", "Effects", "Tools"], strip.TabNames);
            Assert.Equal(["Image", "Effects", "Tools"], strip.Tabs.Children.OfType<ToggleButton>().Select(TabText));
            Assert.All(strip.TabNames, n => Assert.False(strip.Tab(n).IsChecked));
            Assert.Null(w.OpenMenuStrip);
            Assert.False(strip.Panel.IsVisible);
            Assert.All(strip.TabNames, n => Assert.All(strip.ButtonsOf(n), b => Assert.False(b.GetVisualAncestors().OfType<BarRow>().First().IsVisible)));   // every strip's row is hidden

            // in the top row, after the Main Toolbar and before Feedback / Bug; the Context Bar keeps its whole width for the tool controls
            Point At(Control c) => c.TranslatePoint(new Point(0, 0), w)!.Value;
            Assert.True(At(strip).Y >= At(top).Y - 0.5 && At(strip).Y + strip.Bounds.Height <= At(top).Y + top.Bounds.Height + 0.5);
            var toolbar = w.FindControl<ScrollViewer>("MainToolbarScroll")!;
            Assert.True(At(strip).X >= At(toolbar).X + toolbar.Bounds.Width - 0.5);
            Assert.True(At(strip).X + strip.Bounds.Width <= At(w.FindControl<Button>("FeedbackButton")!).X);
            Assert.Empty(w.FindControl<Border>("ContextBarBorder")!.GetVisualDescendants().OfType<MenuStrip>());
            // the Dock is a different thing and stays closed
            Assert.Null(w.OpenDockPanel);
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Picking_a_command_or_pressing_on_the_canvas_closes_the_open_strip()
    {
        await WithTheme(() =>
        {
            var w = Open();
            var strip = StripOf(w);
            OpenStrip(w, "Tools");
            var tools = strip.CommandsOf("Tools").ToList();
            Press(w, strip.ButtonsOf("Tools")[tools.FindIndex(c => c.Id == "tool.rectangle")]);
            Assert.Equal("rectangle", w.Session.ActiveTool.Id);   // the command ran ...
            Assert.Null(w.OpenMenuStrip);                         // ... and the strip went away, like a menu
            Assert.False(strip.Panel.IsVisible);

            w.Session.SetTool("select");
            OpenStrip(w, "Effects");
            var canvas = w.FindControl<DesignCanvas>("Surface")!;
            var p = canvas.TranslatePoint(new Point(canvas.Bounds.Width - 40, canvas.Bounds.Height - 40), w)!.Value;   // away from the strip
            w.MouseDown(p, Avalonia.Input.MouseButton.Left);
            w.MouseUp(p, Avalonia.Input.MouseButton.Left);
            Flush();
            Assert.Null(w.OpenMenuStrip);
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
                var stripIds = strip.CommandsOf(name).Select(c => c.Id).ToArray();
                if (name == "Tools")
                {
                    // the Tools strip follows the Toolbox (R29, see ToolsStripParityTests) and keeps everything else the menu has
                    Assert.All(ids, id => Assert.Contains(id, stripIds));
                }
                else
                {
                    Assert.Equal(ids, stripIds);                                         // the same commands, in the menu's order
                    Assert.Equal(ids.Length, strip.ButtonsOf(name).Count);
                }
                foreach (var id in stripIds)
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
            OpenStrip(w, "Effects");                                // (the strip closed once the command ran)
            Assert.Contains("on", Knife().Classes);                 // and shows as pressed while its tool is active
            w.Session.SetTool("select");
            Flush();

            OpenStrip(w, "Effects");
            Press(w, Weld());                                       // Weld runs in the background; the two shapes become one
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (w.Session.Document.ActiveLayer.Children.Count > 1 && watch.ElapsedMilliseconds < 15000) { Flush(); await Task.Delay(10); }
            Assert.Single(w.Session.Document.ActiveLayer.Children);
            Assert.Equal("Weld", w.Session.History.UndoDescription);

            // Tools: a button changes the tool just as the menu item does
            OpenStrip(w, "Tools");
            var tools = strip.CommandsOf("Tools").ToList();
            Press(w, strip.ButtonsOf("Tools")[tools.FindIndex(c => c.Id == "tool.vector")]);
            Assert.IsType<VectorEditTool>(w.Session.ActiveTool);
            OpenStrip(w, "Tools");
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
            OpenStrip(w, "Image");
            Press(w, B("image.flipV"));
            Assert.Equal("Flip Vertical", w.Session.History.UndoDescription);
            OpenStrip(w, "Image");
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
            Assert.InRange(barH, ContextBars.OneLineHeight - 1, ContextBars.OneLineHeight + 1);

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
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaTheory]
    [InlineData(1600, 900)]
    [InlineData(1280, 640)]
    public async Task An_open_strip_drops_down_under_its_tab_over_the_canvas_and_stays_inside_the_window(double width, double height)
    {
        await WithTheme(() =>
        {
            var w = Open(width, height);
            var strip = StripOf(w);
            var top = w.FindControl<Border>("TopRowBorder")!;
            var canvas = w.FindControl<DesignCanvas>("Surface")!;
            Point At(Control c) => c.TranslatePoint(default, w)!.Value;
            double topBottom = At(top).Y + top.Bounds.Height;
            foreach (var name in strip.TabNames)
            {
                OpenStrip(w, name);
                var panel = strip.Panel;
                var tab = TabOf(w, name);
                Assert.True(panel.IsEffectivelyVisible);
                // right under the top row, over the Context Bar and the top of the canvas
                Assert.InRange(At(panel).Y, topBottom - 0.5, topBottom + 4);
                Assert.True(At(panel).Y + panel.Bounds.Height < At(canvas).Y + canvas.Bounds.Height, "it hangs over the canvas, not past it");
                // under its tab, or moved left just enough to stay inside the window
                Assert.True(At(panel).X <= At(tab).X + 0.5);
                Assert.True(At(panel).X >= 0 && At(panel).X + panel.Bounds.Width <= w.Bounds.Width + 0.5);
                if (At(tab).X + panel.Bounds.Width < w.Bounds.Width - 10) Assert.Equal(At(tab).X, At(panel).X, 0.5);
                // every button is inside the panel
                foreach (var b in strip.ButtonsOf(name))
                {
                    var p = b.TranslatePoint(default, panel)!.Value;
                    Assert.True(p.X >= -0.5 && p.X + b.Bounds.Width <= panel.Bounds.Width + 0.5, $"{name}: a button sticks out sideways");
                }
                Press(w, tab);
            }
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Every_button_has_a_short_visible_label_that_scales_with_the_text_size()
    {
        await WithTheme(() =>
        {
            var w = Open(1600, 860);
            var strip = StripOf(w);
            foreach (var name in strip.TabNames)
            {
                var labels = strip.LabelsOf(name);
                var commands = strip.CommandsOf(name);
                Assert.Equal(commands.Count, labels.Count);
                Assert.All(labels, l => Assert.InRange(l.Length, 1, 12));                         // short enough to sit under an icon
                Assert.Equal(labels.Count, labels.Distinct().Count());                              // no two buttons with the same word
                foreach (var c in commands) Assert.Contains(c.Id, MenuStripIcons.LabeledIds);       // every command has its own short word, not a guess
                // the tooltip keeps the full command name, so the label can be short
                for (int i = 0; i < commands.Count; i++) Assert.Equal(commands[i].ToolTip, ToolTip.GetTip(strip.ButtonsOf(name)[i]));
            }
            Assert.Contains("Crop", strip.LabelsOf("Image"));
            Assert.Contains("Resample", strip.LabelsOf("Image"));
            Assert.Contains("Weld", strip.LabelsOf("Effects"));
            Assert.Contains("Options", strip.LabelsOf("Tools"));

            Press(w, TabOf(w, "Image"));
            w.CaptureRenderedFrame();
            foreach (var scale in new[] { 1.0, 1.5, 2.0 })
            {
                UiScale.SetScale(scale, remember: false);
                Flush();
                w.CaptureRenderedFrame();
                var texts = strip.ButtonsOf("Image").Select(b => b.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("striplabel"))).ToArray();
                Assert.All(texts, t => { Assert.True(t.IsEffectivelyVisible); Assert.Equal(MenuStrip.LabelSize * scale, t.FontSize, 3); });
                Assert.All(texts, t => Assert.True(t.Bounds.Width > 3 && t.Bounds.Height > 3));
            }
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaTheory]
    [InlineData(1600, 1.0)]
    [InlineData(1920, 1.0)]
    [InlineData(1920, 1.25)]
    public async Task Each_strip_fits_in_its_panel_with_every_label_at_normal_window_sizes(double window, double text)
    {
        await WithTheme(() =>
        {
            UiScale.SetScale(text, remember: false);
            var w = Open(window, 900);
            var strip = StripOf(w);
            var scroll = strip.Panel.Child as ScrollViewer;
            foreach (var name in strip.TabNames)
            {
                Press(w, TabOf(w, name));
                w.CaptureRenderedFrame();
                Assert.NotNull(scroll);
                // everything is visible at once: no scrolling, on a few lines
                Assert.True(scroll!.Extent.Height <= scroll.Viewport.Height + 1, $"{name} at {window} px and {text * 100}%: {scroll.Extent.Height} high needed, {scroll.Viewport.Height} there");
                Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
                Assert.InRange(strip.ButtonsOf(name).Select(b => Math.Round(b.TranslatePoint(default, strip.Panel)!.Value.Y)).Distinct().Count(), 1, 4);
                // no button is clipped by the panel
                var panel = strip.Panel;
                foreach (var b in strip.ButtonsOf(name))
                {
                    var p = b.TranslatePoint(default, panel)!.Value;
                    Assert.True(p.X >= -0.5 && p.X + b.Bounds.Width <= panel.Bounds.Width + 0.5, $"{name}: a button sticks out sideways");
                    Assert.True(p.Y >= -0.5 && p.Y + b.Bounds.Height <= panel.Bounds.Height + 0.5, $"{name}: a button sticks out vertically ({p.Y} + {b.Bounds.Height} in {panel.Bounds.Height})");
                }
                Press(w, TabOf(w, name));
            }
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task A_strip_too_tall_for_a_small_window_scrolls_and_never_hides_buttons()
    {
        await WithTheme(() =>
        {
            var w = Open(420, 300);                                         // a tiny window: the Image strip needs more lines than the panel may have
            var strip = StripOf(w);
            double barH = w.FindControl<Border>("ContextBarBorder")!.Bounds.Height;
            OpenStrip(w, "Image");
            var scroll = (ScrollViewer)strip.Panel.Child!;
            Assert.True(strip.Panel.Bounds.Height <= w.Bounds.Height * 0.6 + 1 || strip.Panel.Bounds.Height <= 121, "the panel never takes over the window");
            Assert.Equal(0, scroll.Extent.Width - Math.Min(scroll.Extent.Width, scroll.Viewport.Width + 1), 1);   // it wraps; it never runs sideways out of the panel
            Assert.Equal(strip.CommandsOf("Image").Count, strip.ButtonsOf("Image").Count);   // every button is still there ...
            Assert.All(strip.ButtonsOf("Image"), b => Assert.True(b.IsVisible));
            if (scroll.Extent.Height > scroll.Viewport.Height + 1)
            {
                scroll.Offset = new Vector(0, 12);                          // ... and reachable by scrolling down
                Flush();
                Assert.True(scroll.Offset.Y > 0);
            }
            Assert.Equal(barH, w.FindControl<Border>("ContextBarBorder")!.Bounds.Height, 1.0);   // the strip floats: no height jump
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
            var firstLabel = first.GetVisualDescendants().OfType<TextBlock>().First(x => x.Classes.Contains("striplabel"));
            Assert.Equal(MenuStrip.LabelSize * text, firstLabel.FontSize, 3);                           // the label follows the text size
            var bar = w.FindControl<Border>("ContextBarBorder")!;
            var panel = strip.Panel;
            double panelBottom = panel.TranslatePoint(new Point(0, panel.Bounds.Height), w)!.Value.Y;
            Assert.All(strip.ButtonsOf("Image"), b =>
                Assert.True(b.TranslatePoint(new Point(0, b.Bounds.Height), w)!.Value.Y <= panelBottom + 0.5, $"at {text}: the buttons fit inside the strip"));
            Assert.Equal(MenuStrip.LabelSize * text, firstLabel.FontSize, 3);
            Assert.True(panel.Bounds.Width <= MainWindow.MenuStripWidth * text + 1);   // the strip widens with the text, so its words fit
            Assert.InRange(bar.Bounds.Height, ContextBars.OneLineHeight - 1, ContextBars.OneLineHeight + 1);
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Dark_Menus_darkens_the_tabs_the_strip_and_its_icons_and_the_open_tab_looks_pressed()
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
            foreach (bool dark in new[] { false, true })
            {
                AppTheme.SetDarkMenus(dark);
                Flush();
                w.CaptureRenderedFrame();
                Assert.Equal(AppTheme.ColorIn("Ui.Panel", dark), PanelColor());
                Assert.Equal(AppTheme.ColorIn("Ui.OnBack", dark), OpenTabColor());       // the open tab looks pressed, like a switched-on button
                Assert.Equal(AppTheme.ColorIn("Ui.TabBack", dark), ClosedTabColor());
            }
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
            var w = new MainWindow(new StartupOptions(Demo: true)) { Width = 1600, Height = 900 };
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
                w.Session.Selection.Set(w.Session.Document.AllObjects().OfType<ImageObject>().First());
                Save($"menu-strip-v4-closed-{(dark ? "dark" : "light")}");                 // no strip open: only the tabs in the top row
                foreach (var name in new[] { "Image", "Effects", "Tools" })
                {
                    if (name == "Image") w.Session.Selection.Set(w.Session.Document.AllObjects().OfType<ImageObject>().First());
                    else if (name == "Effects") w.Session.Selection.Set([a, b]);
                    Flush();
                    Press(w, TabOf(w, name));
                    Save($"menu-strip-v4-{name.ToLowerInvariant()}-{(dark ? "dark" : "light")}");
                    Press(w, TabOf(w, name));
                }
            }
            w.Close();
        });
    }
}
