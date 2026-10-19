using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Effects ▸ Text on Path through the real window: the menu entry, the tool, its Context Bar, the canvas drag.</summary>
public class TextPathShellTests
{
    private static (MainWindow Window, DesignCanvas Canvas) Open(int width = 1600)
    {
        var w = new MainWindow(new StartupOptions()) { Width = width, Height = 900 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        return (w, w.FindControl<DesignCanvas>("Surface")!);
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static TextPathContextBar Bar(MainWindow w) => Assert.IsType<TextPathContextBar>(w.FindControl<ContentControl>("ContextBarHost")!.Content);

    private static TextObject AddText(MainWindow w, string text = "SIGN", double inchesX = 4, double inchesY = 6)
    {
        var t = new TextObject(text, "Arial", Units.FromInches(1)) { Transform = Affine2.Translation(Units.FromInches(inchesX), Units.FromInches(inchesY)) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, t));
        Flush();
        return t;
    }

    private static MenuItem MenuEntry(MainWindow w, string menu, string commandId)
    {
        var top = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().First(m => ((string)m.Header!).Replace("_", "") == menu);
        return top.Items.OfType<MenuItem>().First(i => i.Tag is AppCommand c && c.Id == commandId);
    }

    private static void Press(MainWindow w, Control c)
    {
        var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w).GetValueOrDefault();
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        Flush();
    }

    private static void Type(MainWindow w, TextBox box, string text)
    {
        box.Focus();
        box.Text = text;
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Flush();
    }

    // ---- The command ----

    [AvaloniaFact]
    public void Text_on_Path_is_in_the_Effects_and_Text_menus_and_follows_the_selection()
    {
        var (w, _) = Open();
        var s = w.Session;
        foreach (var menu in new[] { "Effects", "Text" })
        {
            var item = MenuEntry(w, menu, "effects.textOnPath");
            var command = Assert.IsType<AppCommand>(item.Tag);
            Assert.Null(command.Gesture);                                         // no shortcut
            Assert.False(command.CanExecute());                                   // nothing selected
        }
        var t = AddText(w);
        s.Selection.Set(t);
        var cmd = (AppCommand)MenuEntry(w, "Effects", "effects.textOnPath").Tag!;
        Assert.True(cmd.CanExecute());
        Assert.Contains("Text on Path", cmd.Header.Replace("_", ""));

        var rect = new RectangleObject(100, 50) { Style = ObjectStyle.FillOnly(DocColor.Black) };
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, rect));
        s.Selection.Set(rect);
        Assert.False(cmd.CanExecute());                                           // a shape alone is not text
        s.Selection.Set([t, rect]);
        Assert.True(cmd.CanExecute());                                            // text plus a shape to follow is
        w.Close();
    }

    [AvaloniaFact]
    public void Running_it_opens_the_tool_and_a_Context_Bar_that_shows_only_valid_choices()
    {
        var (w, _) = Open();
        var s = w.Session;
        var t = AddText(w);
        s.Selection.Set(t);
        ((AppCommand)MenuEntry(w, "Effects", "effects.textOnPath").Tag!).Execute();
        Flush();

        Assert.IsType<TextPathTool>(s.ActiveTool);
        var bar = Bar(w);
        Assert.True(t.IsOnPath);
        // Fit (the default): the text always fills the path, so there is no start to choose; one vector: nothing to delete; a Bezier already.
        Assert.False(bar.StartGroup.IsVisible);
        Assert.True(bar.CompensationGroup.IsVisible);
        Assert.True(bar.AddCurve.IsVisible);
        Assert.False(bar.DeleteCurve.IsVisible);
        Assert.False(bar.Bezier.IsVisible);
        Assert.True(bar.Arc.IsVisible);
        Assert.True(bar.Done.IsVisible && bar.Remove.IsVisible);
        Assert.Equal(0, bar.SpacingBox.SelectedIndex);
        Assert.Equal(0, bar.SideBox.SelectedIndex);

        bar.SpacingBox.SelectedIndex = 1;                                        // natural spacing
        Flush();
        Assert.Equal(TextPathSpacing.Natural, t.OnPath!.Spacing);
        Assert.True(bar.StartGroup.IsVisible);                                    // now the start applies
        Assert.Equal("Text on Path Spacing", s.History.UndoDescription);

        bar.StartBox.SelectedIndex = 1;                                           // middle of the path
        Flush();
        Assert.Equal(TextAlignment.Center, t.Alignment);

        bar.SideBox.SelectedIndex = 1;
        bar.FlipBox.IsChecked = true;
        Flush();
        Assert.Equal(TextPathSide.Below, t.OnPath.Side);
        Assert.True(t.OnPath.Flip);

        Type(w, bar.OffsetBox, "0.5");
        Assert.Equal(Units.FromInches(0.5), t.OnPath.StartOffset, 6);
        Type(w, bar.RaiseBox, "-0.25");
        Assert.Equal(Units.FromInches(-0.25), t.OnPath.Raise, 6);
        Type(w, bar.CompensationBox, "0.8");
        Assert.Equal(0.8, t.OnPath.CurveCompensation, 6);

        w.CaptureRenderedFrame();                                                 // lay the bar out again before pressing a button in it
        bar.AddCurve.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Flush();
        Assert.True(bar.DeleteCurve.IsVisible);                                   // two vectors now
        var tool = (TextPathTool)s.ActiveTool;
        Assert.True(tool.CanToArc, $"selected segment {tool.SelectedSegment}, node {tool.SelectedNode}, segments {tool.SegmentCount}, kind {PathEditing.ToLoops(t.OnPath!.Path)[0].Segments[tool.SelectedSegment].Kind}");
        Assert.True(bar.Arc.IsVisible);                                           // the new vector is the selected one and a Bezier: it can become an arc...
        Assert.False(bar.Bezier.IsVisible);                                       // ...but not a Bezier again
        w.Close();
    }

    [AvaloniaFact]
    public void A_single_arc_path_hides_the_compensation_and_the_bar_offers_only_what_applies_to_it()
    {
        var (w, _) = Open();
        var s = w.Session;
        var t = AddText(w);
        var arc = new PathBuilder().MoveTo(0, 0).ArcTo(new Vec2(150, -90), new Vec2(300, 0)).Build();
        s.ApplyTextOnPath(t, new TextOnPath(arc));
        s.Selection.Set(t);
        s.BeginTextOnPath();
        Flush();
        var bar = Bar(w);
        Assert.False(bar.CompensationGroup.IsVisible);                            // forced to 0 on one arc
        Assert.False(bar.Arc.IsVisible);                                          // it is one already
        Assert.True(bar.Bezier.IsVisible);
        Press(w, bar.Bezier);
        Assert.True(bar.CompensationGroup.IsVisible);                             // a Bezier path has it again
        w.Close();
    }

    [AvaloniaFact]
    public void Done_and_Remove_buttons()
    {
        var (w, _) = Open();
        var s = w.Session;
        var t = AddText(w);
        s.Selection.Set(t);
        s.BeginTextOnPath();
        Flush();
        Press(w, Bar(w).Done);
        Assert.IsType<SelectTool>(s.ActiveTool);
        Assert.True(t.IsOnPath);
        Assert.Equal([t], s.Selection.Items);

        s.BeginTextOnPath();
        Flush();
        Press(w, Bar(w).Remove);
        Assert.False(t.IsOnPath);
        Assert.IsType<SelectTool>(s.ActiveTool);
        s.Undo();
        Assert.True(t.IsOnPath);
        w.Close();
    }

    [AvaloniaFact]
    public void Dragging_a_path_node_on_the_canvas_re_flows_the_text()
    {
        var (w, c) = Open();
        var s = w.Session;
        var t = AddText(w, "SIGN", 3, 6);
        s.ZoomToContent();
        s.Selection.Set(t);
        s.BeginTextOnPath();
        Flush();
        w.CaptureRenderedFrame();

        var loop = PathEditing.ToLoops(t.OnPath!.Path)[0];
        var node = s.Viewport.ToScreen(t.GetWorldTransform().Transform(loop.Node(1)));
        Point P(Vec2 screen) => c.TranslatePoint(new Point(screen.X, screen.Y), TopLevel.GetTopLevel(c)!).GetValueOrDefault();
        var before = t.GetWorldBounds();
        int steps = s.History.UndoList.Count;

        w.MouseDown(P(node), MouseButton.Left);
        w.MouseMove(P(node) + new Point(0, 40));
        w.MouseMove(P(node) + new Point(0, 90));
        w.MouseUp(P(node) + new Point(0, 90), MouseButton.Left);
        Flush();

        Assert.NotEqual(before, t.GetWorldBounds());
        Assert.Equal(steps + 1, s.History.UndoList.Count);                        // one undo step for the whole drag
        s.Undo();
        Assert.Equal(before, t.GetWorldBounds());
        w.Close();
    }

    [AvaloniaFact]
    public void The_bar_is_two_one_line_rows_with_nothing_cut_off_even_with_every_option_showing()
    {
        var (w, _) = Open(1600);
        var s = w.Session;
        var t = AddText(w);
        s.Selection.Set(t);
        s.BeginTextOnPath();
        s.GetTool<TextPathTool>().Update(x => x with { Spacing = TextPathSpacing.Natural });   // natural spacing shows the From choice too
        s.GetTool<TextPathTool>().AddCurve();                                                  // and a second vector shows Delete Curve
        Flush();
        w.CaptureRenderedFrame();
        var bar = Bar(w);
        var rows = bar.GetVisualDescendants().OfType<BarRow>().Take(2).ToList();
        Assert.Equal(2, rows.Count);
        double edge = w.FindControl<Border>("ContextBarBorder")!.Bounds.Right;   // the Context Bar ends at the Dock's edge
        foreach (var row in rows)
        {
            var visible = row.Children.Where(c => c.IsVisible && c.Bounds.Width > 0.5).ToList();
            Assert.NotEmpty(visible);
            Assert.Single(visible.Select(c => Math.Round((c.Bounds.Y + c.Bounds.Height / 2) / 8)).Distinct());        // one line
            Assert.All(visible, c => Assert.True(c.TranslatePoint(new Point(c.Bounds.Width, 0), w)!.Value.X <= edge + 0.5, $"{c.GetType().Name} reaches past the Context Bar"));
        }
        w.Close();
    }
}
