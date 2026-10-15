using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>The "no color by default" start and the compact Context Bar that flows in one row.</summary>
[Collection("app-settings")]   // these swap the program-wide settings store and text size: one class at a time
public class ContextBarLayoutTests
{
    private static MainWindow Open(double width = 1400, bool demo = false)
    {
        var w = new MainWindow(new StartupOptions(Demo: demo)) { Width = width, Height = 860 };
        w.Show();
        Flush();
        w.CaptureRenderedFrame();
        return w;
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    /// <summary>How many lines a BarRow has laid its visible children on.</summary>
    private static int Lines(BarRow row) =>
        row.Children.Where(c => c.IsVisible && c.Bounds.Width > 0.5).Select(c => Math.Round((c.Bounds.Y + c.Bounds.Height / 2) / 8)).Distinct().Count();

    /// <summary>The rows of the Context Bar: every bar is two of them (the text tool's font panel brings its own two).</summary>
    private static List<BarRow> ContextRows(MainWindow w)
    {
        var bar = (Control)w.FindControl<ContentControl>("ContextBarHost")!.Content!;
        return bar.GetVisualDescendants().OfType<BarRow>().Where(r => r.IsEffectivelyVisible && r.Parent is StackPanel sp && (ReferenceEquals(sp, bar) || sp is TextFormatPanel)).ToList();
    }

    // ---------------------------------------------------------------- no color by default

    [AvaloniaFact]
    public void A_fresh_session_has_no_color_in_the_style_the_Color_Mixer_or_the_Project_Color_Bar()
    {
        var w = Open();
        Assert.Equal(ObjectStyle.None, w.Session.DefaultStyle);
        Assert.Equal(ObjectStyle.None, w.Session.CurrentStyle);

        // Project Color Bar: both swatches are the crossed box
        foreach (var button in new[] { w.ProjectColorBar.FillButton, w.ProjectColorBar.StrokeButton })
        {
            var swatch = (Control)button.Content!;
            Assert.Null(swatch.Tag);
            Assert.Single(swatch.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(), p => Equals(p.Stroke, Brushes.Red));
        }

        // Color Mixer: "no color", not a color
        var big = w.ColorMixer.BigSwatch;
        Assert.NotNull(big.Child);                                  // the red cross
        Assert.Equal("No color", ToolTip.GetTip(big));
        Assert.False(w.ColorMixer.NameBox.IsVisible);               // no color, so no name to show
        Assert.Equal(string.Empty, w.ColorMixer.NameBox.Text ?? "");
        w.Close();
    }

    [AvaloniaFact]
    public void Shapes_drawn_in_a_fresh_session_have_no_fill_and_no_stroke_and_are_picked_by_their_outline()
    {
        var w = Open();
        var c = w.FindControl<DesignCanvas>("Surface")!;
        Point At(double x, double y) => c.TranslatePoint(new Point(x, y), w).GetValueOrDefault();

        w.Session.SetTool("rectangle");
        w.MouseDown(At(200, 200), MouseButton.Left);
        w.MouseMove(At(260, 240));
        w.MouseMove(At(360, 300));
        w.MouseUp(At(360, 300), MouseButton.Left);
        var rect = Assert.IsType<RectangleObject>(Assert.Single(w.Session.Document.ActiveLayer.Children));
        Assert.Equal(ObjectStyle.None, rect.Style);

        w.Session.SetTool("ellipse");
        w.MouseDown(At(500, 200), MouseButton.Left);
        w.MouseMove(At(560, 240));
        w.MouseMove(At(640, 300));
        w.MouseUp(At(640, 300), MouseButton.Left);
        Assert.Equal(2, w.Session.Document.ActiveLayer.Children.Count);
        Assert.All(w.Session.Document.ActiveLayer.Children, o => Assert.Equal(ObjectStyle.None, o.Style));

        // nothing is painted inside, but the outline can be clicked
        w.Session.SetTool("select");
        w.Session.Selection.Clear();
        var edge = w.Session.Viewport.ToScreen(new DeSignMage.Core.Geometry.Vec2(rect.GetWorldBounds().Left, rect.GetWorldBounds().Center.Y));
        w.MouseDown(At(edge.X, edge.Y), MouseButton.Left);
        w.MouseUp(At(edge.X, edge.Y), MouseButton.Left);
        Assert.Same(rect, Assert.Single(w.Session.Selection.Items));
        w.Close();
    }

    [AvaloniaFact]
    public void Choosing_a_color_with_nothing_selected_sets_what_new_shapes_get_and_shows_in_the_bar_and_the_mixer()
    {
        var w = Open();
        w.Session.Selection.Clear();
        var red = new DocColor(200, 30, 30);
        w.Session.ApplyFill(new SolidPaint(red));
        Flush();
        Assert.Equal(red, ((SolidPaint)w.Session.DefaultStyle.Fill!).Color);
        Assert.Equal(new SolidPaint(red), ((Control)w.ProjectColorBar.FillButton.Content!).Tag);
        Assert.Null(((Control)w.ProjectColorBar.StrokeButton.Content!).Tag);
        Assert.False(w.ColorMixer.NameBox.IsVisible);               // an unnamed color shows no name
        Assert.Equal(200, w.ColorMixer.Rows[0].Value);
        w.Close();
    }

    // ---------------------------------------------------------------- Main Toolbar

    [AvaloniaFact]
    public void The_top_row_holds_menus_toolbar_and_feedback_at_one_fixed_height_and_the_toolbar_scrolls_when_narrow()
    {
        var wide = Open(1400);
        Assert.Equal(1, Lines(wide.FindControl<BarRow>("MainToolbar")!));
        double height = wide.FindControl<Border>("TopRowBorder")!.Bounds.Height;
        var menu = wide.FindControl<Menu>("MainMenu")!;
        var toolbar = wide.FindControl<BarRow>("MainToolbar")!;
        var feedback = wide.FindControl<Button>("FeedbackButton")!;
        Assert.Same(wide.FindControl<Border>("TopRowBorder"), menu.GetVisualAncestors().OfType<Border>().First(b => b.Name == "TopRowBorder"));
        Assert.True(toolbar.TranslatePoint(new Point(0, 0), wide)!.Value.X > menu.Bounds.Right - 1);                 // menus, then the toolbar ...
        Assert.True(feedback.TranslatePoint(new Point(0, 0), wide)!.Value.X > toolbar.TranslatePoint(new Point(toolbar.Bounds.Width, 0), wide)!.Value.X - 1);   // ... then Feedback / Bug
        wide.Close();

        var narrow = Open(520);
        Assert.Equal(1, Lines(narrow.FindControl<BarRow>("MainToolbar")!));          // no second line
        Assert.Equal(height, narrow.FindControl<Border>("TopRowBorder")!.Bounds.Height, 1);
        var scroll = narrow.FindControl<ScrollViewer>("MainToolbarScroll")!;
        Assert.True(scroll.Extent.Width > scroll.Viewport.Width + 20, "the icons scroll sideways");
        narrow.Close();
    }

    // ---------------------------------------------------------------- Context Bar

    private static (double Bar, Point Canvas) Metrics(MainWindow w)
    {
        w.CaptureRenderedFrame();
        var canvas = w.FindControl<DesignCanvas>("Surface")!;
        return (w.FindControl<Border>("ContextBarBorder")!.Bounds.Height, canvas.TranslatePoint(new Point(0, 0), w).GetValueOrDefault());
    }

    [AvaloniaFact]
    public void The_Context_Bar_of_every_tool_is_two_rows_tall_whatever_is_in_it()
    {
        var w = Open();
        var r = new RectangleObject(100, 80) { Style = ObjectStyle.FillOnly(DocColor.Black) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        w.Session.Selection.Set(r);
        double rowHeight = ContextBars.RowHeightBase * UiScale.Scale;
        foreach (var tool in new[] { "select", "rectangle", "ellipse", "text", "vector", "loop", "outline", "knife", "cookie", "eyedropper" })
        {
            w.Session.SetTool(tool);
            Flush();
            w.CaptureRenderedFrame();
            Assert.Equal(tool, w.Session.ActiveTool.Id);
            var rows = ContextRows(w);
            Assert.True(rows.Count == 2, $"{tool}: {rows.Count} rows");
            foreach (var row in rows)
            {
                Assert.True(Lines(row) <= 1, $"{tool}: a row has {Lines(row)} lines");   // never two; a row with nothing valid to show is empty (R26)
                Assert.Equal(rowHeight, row.Bounds.Height, 1);
                foreach (var child in row.Children.Where(c => c.IsVisible && c.Bounds.Width > 0.5))
                    Assert.True(child.Bounds.Height <= rowHeight + 0.5, $"{tool}: {child.GetType().Name} is {child.Bounds.Height} px tall in a {rowHeight} px row");
            }
            Assert.Equal(ContextBars.BarHeight, w.FindControl<Border>("ContextBarBorder")!.Bounds.Height, 1);
        }
        w.Close();
    }

    [AvaloniaFact]
    public void A_Context_Bar_row_that_is_too_long_scrolls_sideways_instead_of_wrapping_and_the_height_stays()
    {
        var wide = Open(1400);
        wide.Session.SetTool("vector");
        Flush();
        var (barWide, canvasWide) = Metrics(wide);
        Assert.All(ContextRows(wide), row => Assert.InRange(Lines(row), 0, 1));   // an empty row has no line at all (R26)
        wide.Close();

        var narrow = Open(700);
        narrow.Session.SetTool("vector");
        Flush();
        var (barNarrow, canvasNarrow) = Metrics(narrow);
        Assert.All(ContextRows(narrow), row => Assert.InRange(Lines(row), 0, 1));   // no third row
        Assert.Equal(barWide, barNarrow, 1);
        Assert.Equal(canvasWide.Y, canvasNarrow.Y, 1);
        var scroll = narrow.FindControl<ScrollViewer>("ContextBarScroll")!;
        Assert.True(scroll.Extent.Width > scroll.Viewport.Width + 50, "the row is longer than the window");
        scroll.Offset = new Vector(60, 0);
        Flush();
        Assert.Equal(60, scroll.Offset.X, 1);
        narrow.Close();
    }

    [AvaloniaFact]
    public async Task The_Context_Bar_never_changes_height_and_the_canvas_never_moves_across_tools_and_selections()
    {
        var w = Open();
        var dir = Directory.CreateTempSubdirectory("dsm-bar-").FullName;
        try
        {
            var (bar0, canvas0) = Metrics(w);
            int checks = 0;
            void Check(string state)
            {
                Flush();
                var (bar, canvas) = Metrics(w);
                Assert.True(Math.Abs(bar - bar0) < 0.01, $"{state}: bar {bar} instead of {bar0}");
                Assert.True(Math.Abs(canvas.X - canvas0.X) < 0.01 && Math.Abs(canvas.Y - canvas0.Y) < 0.01, $"{state}: canvas at {canvas} instead of {canvas0}");
                checks++;
            }

            Check("nothing");
            var rect = new RectangleObject(100, 80) { Transform = Affine2.Translation(100, 100), Style = ObjectStyle.FillOnly(DocColor.Black) };
            w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, rect));
            w.Session.Selection.Set(rect);
            Check("a shape");
            var text = new TextObject("Hello", "Arial", 40) { Transform = Affine2.Translation(100, 300) };
            w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, text));
            w.Session.Selection.Set(text);
            Check("live text");
            w.Session.Selection.Set([rect, text]);
            Check("two objects");

            var pixels = new DeSignMage.Imaging.PixelBuffer(60, 30);
            for (int y = 0; y < 30; y++)
                for (int x = 0; x < 60; x++) pixels.Set(x, y, (byte)(x * 4), 80, 200, 255);
            var path = Path.Combine(dir, "p.png");
            File.WriteAllBytes(path, DeSignMage.Imaging.ImageCodec.EncodePng(pixels));
            Assert.Equal(1, await w.ImportImagesAsync([path], null));
            Check("a picture");
            w.Session.SetTool("crop");
            Assert.IsType<CropTool>(w.Session.ActiveTool);
            Check("crop");
            w.Session.SetTool("select");

            var guide = new Guide(new DeSignMage.Core.Geometry.Vec2(300, 200), 0);
            w.Session.History.Execute(new AddGuideCommand(guide));
            w.Session.SelectedGuide = guide;
            Check("a guide");
            w.Session.Selection.Set(rect);
            Check("back to a shape");

            foreach (var tool in new[] { "vector", "loop", "outline", "knife", "cookie", "rectangle", "ellipse", "text", "eyedropper", "select" })
            {
                w.Session.SetTool(tool);
                Check($"the {tool} tool");
            }
            Assert.True(checks >= 17);
        }
        finally { Directory.Delete(dir, true); }
        w.Close();
    }
    [AvaloniaFact]
    public void The_Context_Bar_scales_with_the_interface_text_size()
    {
        var oldStore = AppSettings.Store;
        AppSettings.Store = new DeSignMage.Feedback.SettingsStore(null);
        UiScale.Store = AppSettings.Store;
        UiScale.Reset();
        try
        {
            var w = Open(1900);
            var r = new RectangleObject(100, 80) { Style = ObjectStyle.FillOnly(DocColor.Black) };
            w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
            w.Session.Selection.Set(r);
            Flush();
            w.CaptureRenderedFrame();
            var border = w.FindControl<Border>("ContextBarBorder")!;
            var bar = Assert.IsType<SelectionContextBar>(w.FindControl<ContentControl>("ContextBarHost")!.Content);
            double h100 = border.Bounds.Height, box100 = bar.X.Bounds.Width, font100 = bar.X.FontSize;

            UiScale.SetScale(1.5);
            Flush();
            w.CaptureRenderedFrame();
            Assert.Equal(box100 * 1.5, bar.X.Bounds.Width, 1);
            Assert.Equal(font100 * 1.5, bar.X.FontSize, 3);
            Assert.True(border.Bounds.Height > h100 * 1.3, $"{border.Bounds.Height} vs {h100}");
            Assert.True(border.Bounds.Height < h100 * 1.9);
            w.Close();
        }
        finally
        {
            UiScale.Reset();
            UiScale.Store = null;
            AppSettings.Store = oldStore;
        }
    }

    // ---------------------------------------------------------------- the flowing row itself

    private static Control Box(double width, bool separator = false) => separator ? new BarSeparator() : new Border { Width = width, Height = 20 };

    [AvaloniaFact]
    public void A_BarRow_wraps_by_width_and_never_starts_or_ends_a_line_with_a_separator()
    {
        var row = new BarRow { Spacing = 10, LineSpacing = 5 };
        var items = new List<Control> { Box(100), Box(1, true), Box(100), Box(1, true), Box(100), Box(1, true), Box(100) };
        foreach (var i in items) row.Children.Add(i);
        var host = new Window { Width = 400, Height = 200, Content = new Border { Child = row, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top } };
        host.Show();
        Flush();

        // 330 wide: 100 | 100 | 100 fit on one line (3 x 100 + separators + gaps = 340 > 330 once the 3rd is added)
        row.Measure(new Size(330, double.PositiveInfinity));
        row.Arrange(new Rect(0, 0, 330, row.DesiredSize.Height));
        var lines = items.Where(b => b.Bounds.Width > 0.5).GroupBy(b => Math.Round(b.Bounds.Center.Y / 8)).OrderBy(g => g.Key).ToList();
        Assert.True(lines.Count >= 2);
        foreach (var line in lines)
        {
            var ordered = line.OrderBy(b => b.Bounds.X).ToList();
            Assert.False(ordered[0].Classes.Contains("sep"), "line starts with a separator");
            Assert.False(ordered[^1].Classes.Contains("sep"), "line ends with a separator");
        }
        Assert.All(items.Where(b => b.Bounds.Width > 0.5), b => Assert.True(b.Bounds.Right <= 330.5));

        // wide enough: one line, separators kept
        row.Measure(new Size(2000, double.PositiveInfinity));
        row.Arrange(new Rect(0, 0, 2000, row.DesiredSize.Height));
        Assert.Single(items.Select(b => Math.Round(b.Bounds.Center.Y)).Distinct());
        Assert.Equal(3, items.Count(b => b.Classes.Contains("sep") && b.Bounds.Width > 0.5));
        host.Close();
    }

    [AvaloniaFact]
    public void A_hidden_item_between_two_separators_leaves_only_one_of_them()
    {
        var row = new BarRow { Spacing = 10 };
        var a = Box(50); var s1 = Box(1, true); var hidden = Box(50); hidden.IsVisible = false; var s2 = Box(1, true); var b = Box(50);
        foreach (var i in new[] { a, s1, hidden, s2, b }) row.Children.Add(i);
        row.Measure(new Size(1000, double.PositiveInfinity));
        row.Arrange(new Rect(0, 0, 1000, row.DesiredSize.Height));
        Assert.Equal(1, new[] { s1, s2 }.Count(s => s.Bounds.Width > 0.5));
        Assert.Equal(50 + 10 + 9 + 10 + 50, row.DesiredSize.Width, 3);
    }

    /// <summary>Saves pictures of the bars when DSM_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public void Screenshots_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        void Save(MainWindow w, string name)
        {
            Flush();
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
            w.CaptureRenderedFrame()!.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
        }

        var w = Open(demo: true);
        Save(w, "bar-select-text");
        w.Session.Selection.Clear();
        Save(w, "bar-select-empty");
        var r = new RectangleObject(100, 80) { Style = ObjectStyle.FillOnly(DocColor.FromHex("#336699")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        w.Session.Selection.Set(r);
        w.Session.SetTool("vector");
        Save(w, "bar-vector");
        w.Session.SetTool("loop");
        Save(w, "bar-loop");
        w.Session.SetTool("outline");
        Save(w, "bar-outline");
        w.Session.SetTool("rectangle");
        Save(w, "bar-rectangle");
        w.Session.SetTool("text");
        Save(w, "bar-text");
        w.Close();

        var narrow = Open(820, demo: true);
        Save(narrow, "bar-narrow");
        narrow.Close();

        var oldStore = AppSettings.Store;
        AppSettings.Store = new DeSignMage.Feedback.SettingsStore(null);
        UiScale.Store = AppSettings.Store;
        try
        {
            UiScale.Reset();
            var big = Open(1600, demo: true);
            UiScale.SetScale(1.5);
            Flush();
            Save(big, "bar-scale150");
            big.Close();
        }
        finally { UiScale.Reset(); UiScale.Store = null; AppSettings.Store = oldStore; }

        var fresh = Open();
        Save(fresh, "fresh");
        fresh.Close();
    }
}