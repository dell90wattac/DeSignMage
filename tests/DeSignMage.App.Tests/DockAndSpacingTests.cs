using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Closable dock panels (View ▸ Panels), what was trimmed from the color UI, and the spacing that grows at a third of the text's rate.</summary>
[Collection("app-settings")]   // these swap the program-wide settings store and text size: one class at a time
public class DockAndSpacingTests
{
    /// <summary>A private settings store and 100% text for the test; everything is put back afterwards (both are program-wide state).</summary>
    private static async Task WithSettings(Func<Task> body)
    {
        var oldStore = AppSettings.Store;
        AppSettings.Store = new SettingsStore(null);
        UiScale.Store = AppSettings.Store;
        UiScale.Reset();
        try { await body(); }
        finally
        {
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

    // ---------------------------------------------------------------- the Dock (R17)

    private static ToggleButton Tab(MainWindow w, string id) => w.FindControl<ToggleButton>(id switch
    {
        "colorMixer" => "DockTabColorMixer",
        "layers" => "DockTabLayers",
        _ => "DockTabHistory",
    })!;

    private static Control PanelOf(MainWindow w, string id) => w.FindControl<Control>(id switch
    {
        "colorMixer" => "ColorMixerPanel",
        "layers" => "LayersPanel",
        _ => "HistoryPanel",
    })!;

    [AvaloniaFact]
    public async Task The_Dock_is_a_full_height_column_with_its_Dock_Tabs_in_the_Context_Bar_band_and_the_Context_Bar_and_ruler_stop_at_its_edge()
    {
        await WithSettings(() =>
        {
            var w = Open();
            Press(w, Tab(w, "colorMixer"));
            w.CaptureRenderedFrame();
            var bar = w.FindControl<Border>("ContextBarBorder")!;
            var band = w.FindControl<Grid>("DockBand")!;
            var tabs = w.FindControl<StackPanel>("DockTabs")!;
            var dock = w.FindControl<Grid>("DockGrid")!;
            var top = w.FindControl<Border>("TopRowBorder")!;
            double height = bar.Bounds.Height;
            Assert.InRange(height, ContextBars.BarHeight - 1, ContextBars.BarHeight + 1);

            Point At(Control c) => c.TranslatePoint(new Point(0, 0), w)!.Value;
            // the band holds the tabs and is exactly as tall as the Context Bar, right beside it
            Assert.InRange(At(band).Y, At(bar).Y - 0.5, At(bar).Y + 0.5);
            Assert.InRange(band.Bounds.Height, height - 0.5, height + 0.5);
            Assert.True(At(bar).X + bar.Bounds.Width <= At(band).X + 0.5, "the Context Bar ends where the Dock begins");
            Assert.True(At(tabs).X >= At(band).X && At(tabs).Y >= At(band).Y && At(tabs).Y + tabs.Bounds.Height <= At(band).Y + band.Bounds.Height + 0.5);
            Assert.Equal(["Color Mixer", "Layers", "History"], tabs.Children.OfType<ToggleButton>().Select(b => (string)b.Content!));
            // the Dock column runs from just under the top row to the bottom of the work area
            Assert.True(At(band).Y >= At(top).Y + top.Bounds.Height - 0.5);
            Assert.InRange(dock.Bounds.Height + band.Bounds.Height, w.FindControl<DesignCanvas>("Surface")!.Bounds.Height - 200, 5000);
            // the ruler and the canvas stop at the Dock's edge as well
            var ruler = w.FindControl<Ruler>("TopRuler")!;
            Assert.True(At(ruler).X + ruler.Bounds.Width <= At(dock).X + 0.5);

            foreach (var id in DockLayout.AllPanels) { Press(w, Tab(w, id)); w.CaptureRenderedFrame(); Assert.InRange(bar.Bounds.Height, height - 0.5, height + 0.5); }
            foreach (var tool in new[] { "vector", "text", "select" })
            {
                w.Session.SetTool(tool);
                w.CaptureRenderedFrame();
                Assert.InRange(bar.Bounds.Height, height - 0.5, height + 0.5);
                Assert.InRange(band.Bounds.Height, height - 0.5, height + 0.5);
            }
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task An_open_Dock_Tab_melts_into_its_panel()
    {
        await WithSettings(() =>
        {
            var w = Open();
            Press(w, Tab(w, "colorMixer"));
            w.CaptureRenderedFrame();
            var open = Tab(w, "colorMixer");
            var closed = Tab(w, "history");
            Assert.True(open.IsChecked);
            Assert.False(closed.IsChecked);
            // no line under an open tab; a closed one is an ordinary tab with a border all around the top
            Assert.Equal(0, open.BorderThickness.Bottom);
            Assert.Equal(0, closed.BorderThickness.Bottom);
            Assert.Equal(1, closed.BorderThickness.Top);
            var openPresenter = open.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            var closedPresenter = closed.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            var panelBackground = ((ISolidColorBrush)w.FindControl<Grid>("DockGrid")!.Background!).Color;
            Assert.Equal(panelBackground, ((ISolidColorBrush)openPresenter.Background!).Color);          // same color as the panel: one piece
            Assert.NotEqual(panelBackground, ((ISolidColorBrush)closedPresenter.Background!).Color);
            // the tab is the panel's header: there is no separate title any more
            Assert.Null(w.FindControl<TextBlock>("ColorMixerTitle"));
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task The_program_starts_with_every_Dock_panel_closed_and_the_Palette_Strip_there()
    {
        await WithSettings(() =>
        {
            // an old settings file that still says which panels were open must not bring them back
            AppSettings.Store.Set("ui.dock.open", "colorMixer,layers");
            var w = Open();
            var canvas = w.FindControl<DesignCanvas>("Surface")!;
            Assert.Null(w.OpenDockPanel);
            Assert.False(w.DockVisible);
            Assert.All(DockLayout.AllPanels, id =>
            {
                Assert.False(Tab(w, id).IsChecked);
                Assert.False(PanelOf(w, id).IsVisible);
                Assert.True(Tab(w, id).IsVisible);                                   // the tabs are there, to open a panel
            });
            Assert.False(w.FindControl<Grid>("DockGrid")!.IsVisible);
            Assert.False(w.FindControl<GridSplitter>("DockSplitter")!.IsVisible);

            // with the Dock closed the ruler and canvas run the full width below the tab strip
            var ruler = w.FindControl<Ruler>("TopRuler")!;
            var band = w.FindControl<Grid>("DockBand")!;
            Point At(Control c) => c.TranslatePoint(new Point(0, 0), w)!.Value;
            Assert.True(At(ruler).X + ruler.Bounds.Width > At(band).X + 50, "the ruler is not stopped by the Dock");
            Assert.True(canvas.Bounds.Width > w.Bounds.Width - 150, $"canvas {canvas.Bounds.Width} of {w.Bounds.Width}");

            var strip = w.FindControl<Border>("PaletteStripBorder")!;                 // the Palette Strip is not a Dock panel
            Assert.True(strip.IsVisible);
            Assert.True(strip.Bounds.Width > 10);
            Assert.NotNull(w.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "PaletteStripNoColor"));
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task One_Dock_panel_is_open_at_a_time_and_fills_the_whole_Dock()
    {
        await WithSettings(() =>
        {
            var w = Open();
            var canvas = w.FindControl<DesignCanvas>("Surface")!;
            double closedWidth = canvas.Bounds.Width;
            double TabX(string id) => Tab(w, id).TranslatePoint(new Point(0, 0), w)!.Value.X;
            var closedTabX = DockLayout.AllPanels.Select(TabX).ToArray();

            Press(w, Tab(w, "colorMixer"));                                            // a closed tab opens its panel across the Dock
            w.CaptureRenderedFrame();
            Assert.Equal(closedTabX, DockLayout.AllPanels.Select(TabX).ToArray());     // the tabs stay where they were: no jumping under the mouse
            var dockHeight = w.FindControl<Grid>("DockGrid")!.Bounds.Height;
            Assert.Equal("colorMixer", w.OpenDockPanel);
            Assert.True(Tab(w, "colorMixer").IsChecked);
            Assert.True(PanelOf(w, "colorMixer").IsVisible);
            Assert.InRange(PanelOf(w, "colorMixer").Bounds.Height, dockHeight - 3, dockHeight + 3);
            Assert.True(canvas.Bounds.Width < closedWidth - 200, "the canvas gives up the Dock's width");

            Press(w, Tab(w, "layers"));                                                // another tab: that panel opens, the first one closes
            w.CaptureRenderedFrame();
            Assert.Equal("layers", w.OpenDockPanel);
            Assert.False(Tab(w, "colorMixer").IsChecked);
            Assert.False(PanelOf(w, "colorMixer").IsVisible);
            Assert.True(Tab(w, "layers").IsChecked);
            Assert.InRange(PanelOf(w, "layers").Bounds.Height, dockHeight - 3, dockHeight + 3);
            Assert.Single(DockLayout.AllPanels, id => PanelOf(w, id).IsVisible);

            Press(w, Tab(w, "history"));
            Assert.Equal("history", w.OpenDockPanel);
            Assert.Single(DockLayout.AllPanels, id => Tab(w, id).IsChecked == true);

            Press(w, Tab(w, "history"));                                               // the open tab closes it: only the tab strip is left
            w.CaptureRenderedFrame();
            Assert.Null(w.OpenDockPanel);
            Assert.False(w.DockVisible);
            Assert.False(w.FindControl<Grid>("DockGrid")!.IsVisible);
            Assert.All(DockLayout.AllPanels, id => Assert.True(Tab(w, id).IsVisible));
            Assert.InRange(canvas.Bounds.Width, closedWidth - 1, closedWidth + 1);
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Open_Dock_panels_are_not_remembered_between_sessions()
    {
        await WithSettings(() =>
        {
            var first = Open();
            Press(first, Tab(first, "history"));
            Assert.Equal("history", first.OpenDockPanel);
            Assert.Null(AppSettings.Store.Get<string?>("ui.dock.open", null));         // nothing is written
            first.Close();

            var second = Open();                                                       // a new window starts closed again
            Assert.Null(second.OpenDockPanel);
            Assert.False(Tab(second, "history").IsChecked);
            second.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void The_Dock_rules_are_one_open_panel_at_a_time_starting_closed()
    {
        var d = new DockLayout();
        Assert.Null(d.Open);                                                           // starts with every panel closed
        Assert.All(DockLayout.AllPanels, id => Assert.False(d.IsOpen(id)));

        d.Toggle("colorMixer");                                                        // a click on a closed tab opens that panel
        Assert.Equal("colorMixer", d.Open);
        d.Toggle("history");                                                           // another one: it replaces the open panel
        Assert.Equal("history", d.Open);
        Assert.False(d.IsOpen("colorMixer"));
        Assert.True(d.IsOpen("history"));
        d.Toggle("history");                                                           // the open tab closes it
        Assert.Null(d.Open);

        int changes = 0;
        d.Changed += (_, _) => changes++;
        d.Toggle("layers");
        d.Toggle("layers");
        Assert.Equal(2, changes);
        Assert.Throws<ArgumentException>(() => d.Toggle("nonsense"));
    }
    // ---------------------------------------------------------------- trimmed UI

    [AvaloniaFact]
    public async Task The_stroke_width_controls_and_the_extra_red_X_buttons_are_gone_from_the_color_UI()
    {
        await WithSettings(() =>
        {
            var w = Open();
            var buttons = w.GetVisualDescendants().OfType<Button>().ToList();
            Assert.DoesNotContain(w.GetVisualDescendants().OfType<TextBox>(), t => t.Name == "StrokeWidthBox");
            Assert.DoesNotContain(w.GetVisualDescendants().OfType<ComboBox>(), c => c.Name == "WidthUnitCombo");
            Assert.DoesNotContain(buttons, b => ToolTip.GetTip(b) is string tip && tip.Contains("common widths"));
            Assert.DoesNotContain(buttons, b => ToolTip.GetTip(b) is string tip && tip.StartsWith("Common stroke widths"));
            Assert.DoesNotContain(w.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Stroke width" || t.Text == "Width");
            Assert.DoesNotContain(buttons, b => b.Name is "NoColorButton" or "BarNoColorButton");   // only the Palette Strip X is left
            Assert.Single(buttons, b => b.Name == "PaletteStripNoColor");
            // what stays: the corner and line-end buttons and the Fill / Stroke targets
            Assert.Equal(3, w.ProjectColorBar.JoinButtons.Length);
            Assert.NotNull(w.ColorMixer.StrokeRadio);
            w.Close();
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- spacing scale

    [AvaloniaFact]
    public void Spacing_grows_at_a_third_of_the_text_rate()
    {
        var oldStore = AppSettings.Store;
        AppSettings.Store = new SettingsStore(null);
        UiScale.Store = AppSettings.Store;
        try
        {
            UiScale.Reset();
            Assert.Equal(1.0, UiScale.SpaceScale, 6);
            UiScale.SetScale(1.5, remember: false);
            Assert.Equal(1 + 0.5 / 3, UiScale.SpaceScale, 6);
            UiScale.SetScale(2.0, remember: false);
            Assert.Equal(4.0 / 3, UiScale.SpaceScale, 6);
            Assert.Equal(16 * 4.0 / 3, UiScale.Sp(16), 6);
            Assert.Equal(new Thickness(8 * 4.0 / 3, 6 * 4.0 / 3), UiScale.Space(8, 6));
            Assert.Equal(2.0, UiScale.Scale);                               // the text scale itself is unchanged
        }
        finally
        {
            UiScale.Reset();
            UiScale.Store = null;
            AppSettings.Store = oldStore;
        }
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public async Task Paddings_gaps_and_bars_follow_the_spacing_scale_while_text_and_controls_follow_the_text(double text)
    {
        await WithSettings(() =>
        {
            UiScale.SetScale(text, remember: false);
            var w = Open(2400, 1200);
            double sp = 1 + (text - 1) / 3;
            var rect = new RectangleObject(100, 80) { Style = ObjectStyle.FillOnly(DocColor.Black) };
            w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, rect));
            w.Session.Selection.Set(rect);
            Flush();
            w.CaptureRenderedFrame();

            // gaps: one third of the text's growth
            Assert.Equal(new Thickness(8 * sp, 6 * sp), w.FindControl<Border>("ContextBarBorder")!.Padding);
            Assert.Equal(8 * sp, w.FindControl<BarRow>("ProjectColorBarHost")!.Spacing, 6);
            Assert.Equal(6 * sp, w.FindControl<StackPanel>("ColorMixerHost")!.Spacing, 6);
            Assert.Equal(2 * sp, w.FindControl<StackPanel>("ToolboxHost")!.Spacing, 6);
            var bar = Assert.IsType<SelectionContextBar>(w.FindControl<ContentControl>("ContextBarHost")!.Content);
            var row = bar.GetVisualDescendants().OfType<BarRow>().First();
            Assert.Equal(8 * sp, row.Spacing, 6);

            // text and controls: the full text scale
            Assert.Equal(12 * text, bar.X.FontSize, 3);
            Assert.Equal(70 * text, bar.X.Bounds.Width, 1);
            Assert.Equal(30 * text, row.Bounds.Height, 1);

            // the fixed heights hold: the Context Bar is two rows plus the (slowly growing) gaps, whatever the tool
            double top = w.FindControl<Border>("TopRowBorder")!.Bounds.Height;
            Assert.InRange(w.FindControl<Border>("ContextBarBorder")!.Bounds.Height, ContextBars.BarHeight - 1, ContextBars.BarHeight + 1);   // layout rounds to whole pixels
            Assert.Equal(2 * 30 * text + 16 * sp, ContextBars.BarHeight, 3);
            foreach (var tool in new[] { "vector", "text", "outline", "select" })
            {
                w.Session.SetTool(tool);
                Flush();
                w.CaptureRenderedFrame();
                Assert.InRange(w.FindControl<Border>("ContextBarBorder")!.Bounds.Height, ContextBars.BarHeight - 1, ContextBars.BarHeight + 1);   // layout rounds to whole pixels
                Assert.Equal(top, w.FindControl<Border>("TopRowBorder")!.Bounds.Height, 1);
            }

            // the top row's single line and its toolbar are all there, and the dock never takes more than 40% of the window
            Press(w, Tab(w, "colorMixer"));   // the Dock starts closed: open a panel to measure it
            w.CaptureRenderedFrame();
            Assert.True(w.FindControl<Grid>("DockGrid")!.Bounds.Width <= w.Bounds.Width * 0.4 + 1);
            w.Close();
            return Task.CompletedTask;
        });
    }
}
