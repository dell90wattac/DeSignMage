using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>The Gradient Blend control-point tool: creating, dragging, adding and removing colors, one undo step per action.</summary>
public class GradientToolTests
{
    private readonly EditorSession _s;
    private readonly RectangleObject _r;   // 200 x 100 at (100, 100)

    private static readonly DocColor Gold = new(240, 190, 20, 255, "Golden Yellow", "Oracal 651");

    public GradientToolTests()
    {
        _s = new EditorSession(NewDoc());
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);
        _r = Rect(100, 100, 200, 100, ObjectStyle.FillOnly(Gold));
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, _r));
        _s.Selection.Set(_r);
    }

    private GradientTool Tool => _s.GetTool<GradientTool>();
    private GradientPaint G => Assert.IsType<GradientPaint>(_r.Style.Fill);
    private int Steps => _s.History.UndoList.Count;

    private PointerInput At(double x, double y, PointerButton b = PointerButton.None, ModifierKeys m = ModifierKeys.None) => new(new Vec2(x, y), new Vec2(x, y), b, m);

    private void Press(double x, double y, ModifierKeys m = ModifierKeys.None) => _s.PointerDown(At(x, y, PointerButton.Left, m));
    private void Move(double x, double y, ModifierKeys m = ModifierKeys.None) => _s.PointerMove(At(x, y, PointerButton.None, m));
    private void Release(double x, double y) => _s.PointerUp(At(x, y, PointerButton.Left));

    private void Drag(double fromX, double fromY, double toX, double toY, ModifierKeys m = ModifierKeys.None)
    {
        Press(fromX, fromY, m);
        Move((fromX + toX) / 2, (fromY + toY) / 2, m);
        Move(toX, toY, m);
        Release(toX, toY);
    }

    private void Begin(GradientKind kind)
    {
        Assert.True(_s.BeginGradient());
        Tool.SetKind(kind);
    }

    // ---- entering the tool ----

    [Fact]
    public void The_tool_needs_one_shape_or_text_selected()
    {
        Assert.True(_s.CanGradient);
        _s.Selection.Clear();
        Assert.False(_s.CanGradient);
        Assert.False(_s.BeginGradient());

        var other = Rect(400, 100, 50, 50);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, other));
        _s.Selection.Set([_r, other]);
        Assert.False(_s.CanGradient);                                                        // two objects have no one fill

        var group = _s.GroupObjects([_r, other])!;
        _s.Selection.Set(group);
        Assert.False(_s.CanGradient);

        var text = new TextObject("Hi", "Arial", 24);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, text));
        _s.Selection.Set(text);
        Assert.True(_s.CanGradient);                                                         // live text takes a gradient
    }

    [Fact]
    public void Picking_a_type_makes_a_gradient_from_the_fill_color_in_one_undo_step()
    {
        _s.BeginGradient();
        int steps = Steps;
        Tool.SetKind(GradientKind.Linear);

        Assert.Equal(steps + 1, Steps);
        Assert.Equal(Gold, G.Stops[0].Color);                                                // identity (name, vendor) travels
        Assert.NotEqual(Gold, G.Stops[1].Color);
        Assert.Equal(GradientKind.Linear, G.Kind);

        _s.Undo();
        Assert.Equal(new SolidPaint(Gold), _r.Style.Fill);
        _s.Redo();
        Assert.IsType<GradientPaint>(_r.Style.Fill);
    }

    [Fact]
    public void Changing_the_type_keeps_the_colors_and_remove_goes_back_to_a_plain_fill()
    {
        Begin(GradientKind.Linear);
        var colors = G.Stops.Select(s => s.Color).ToArray();
        Tool.SetKind(GradientKind.Circle);
        Assert.Equal(GradientKind.Circle, G.Kind);
        Assert.Equal(colors, G.Stops.Select(s => s.Color));

        int steps = Steps;
        Tool.Remove();
        Assert.Equal(new SolidPaint(Gold), _r.Style.Fill);
        Assert.Equal(steps + 1, Steps);
        Assert.Null(Tool.Gradient);
    }

    [Fact]
    public void Alternate_colors_and_reflect_are_one_undo_step_each()
    {
        Begin(GradientKind.Linear);
        int steps = Steps;
        Tool.SetAlternateColors(true);
        Tool.SetReflect(true);
        Assert.True(G.AlternateColors && G.Reflect);
        Assert.Equal(steps + 2, Steps);
        _s.Undo();
        Assert.False(G.Reflect);
        Assert.True(G.AlternateColors);
    }

    // ---- handles ----

    [Fact]
    public void Handles_sit_on_the_control_line_in_the_page()
    {
        Begin(GradientKind.Linear);   // (0,0) to (1,1) of a 200 x 100 rectangle at (100,100)
        var handles = Tool.Handles();
        Assert.Equal(2, handles.Count);
        Assert.Equal(new Vec2(100, 100), handles[0].Position);
        Assert.Equal(new Vec2(300, 200), handles[1].Position);
    }

    [Fact]
    public void Dragging_the_end_handle_moves_the_end_in_one_undo_step()
    {
        Begin(GradientKind.Linear);
        int steps = Steps;
        Drag(300, 200, 250, 150);

        Assert.Equal(new Vec2(0.75, 0.5), G.Points[1]);
        Assert.Equal(new Vec2(0, 0), G.Points[0]);
        Assert.Equal(steps + 1, Steps);                                                      // the whole drag is one step
        _s.Undo();
        Assert.Equal(new Vec2(1, 1), G.Points[1]);
    }

    [Fact]
    public void The_gradient_follows_the_object_when_it_is_moved_or_scaled()
    {
        Begin(GradientKind.Linear);
        var before = G;
        _s.TransformSelection("Move", Affine2.Translation(500, 40));
        _s.TransformSelection("Scale", Affine2.Scale(2, 3));
        Assert.Equal(before, G);                                                             // stored relative to the object: nothing to update
        var h = Tool.Handles();
        Assert.Equal(_r.GetWorldBounds().BottomRight, h[1].Position);
    }

    [Fact]
    public void Vertical_and_horizontal_ends_slide_along_their_axis_only()
    {
        Begin(GradientKind.Vertical);                                                        // (0.5, 0) to (0.5, 1): x = 200, y 100..200
        Drag(200, 200, 260, 150);
        Assert.Equal(new Vec2(0.5, 0.5), G.Points[1]);                                       // x stayed, y moved

        _s.Undo(); _s.Undo();
        Tool.SetKind(GradientKind.Horizontal);                                               // (0, 0.5) to (1, 0.5)
        Drag(100, 150, 140, 190);
        Assert.Equal(new Vec2(0.2, 0.5), G.Points[0]);                                       // y stayed, x moved
    }

    [Fact]
    public void Shift_snaps_a_linear_line_to_45_degrees()
    {
        Begin(GradientKind.Linear);
        _s.Selection.Set(_r);
        Drag(300, 200, 300, 150, ModifierKeys.Shift);                                        // from the start (100,100): dx 200, dy 50, snaps to 0 degrees
        var f = G.Frame(_r.GetLocalGeometry().GetBounds());
        Assert.Equal(0, f.Axis.Y, 6);
        Assert.True(f.Axis.X > 0);
    }

    [Fact]
    public void A_circle_center_carries_the_whole_circle_and_its_radius_handle_sets_the_radius()
    {
        Begin(GradientKind.Circle);                                                          // center (0.5, 0.5), radius point (1, 0.5)
        Drag(200, 150, 160, 130);                                                            // the center handle
        Assert.Equal(new Vec2(0.3, 0.3), G.Points[0]);
        Assert.Equal(new Vec2(0.8, 0.3), G.Points[1]);                                       // the radius point moved along
        Drag(260, 130, 300, 130);                                                            // the radius handle (1,0.5 -> 0.8,0.3 is at x=260)
        Assert.Equal(new Vec2(1, 0.3), G.Points[1]);
        Assert.Equal(new Vec2(0.3, 0.3), G.Points[0]);
    }

    [Fact]
    public void An_ellipses_axes_stay_at_right_angles()
    {
        Begin(GradientKind.Ellipse);                                                         // center (200,150), axis ends (300,150) and (200,200)
        Assert.Equal(3, Tool.Handles().Count);

        Drag(300, 150, 250, 100);                                                            // turn and shorten the first axis
        var f = G.Frame(_r.GetLocalGeometry().GetBounds());
        Assert.Equal(0, Vec2.Dot(f.Axis1, f.Axis2), 6);
        Assert.Equal(50, f.Axis2.Length, 6);                                                 // the second axis kept its length

        var before = f.Axis1;
        var axis2 = Tool.Handles()[^1].Position;                                             // the second axis' handle
        Drag(axis2.X, axis2.Y, axis2.X + 30, axis2.Y + 30);
        var after = G.Frame(_r.GetLocalGeometry().GetBounds());
        Assert.Equal(before, after.Axis1);                                                   // the first axis did not change
        Assert.Equal(0, Vec2.Dot(after.Axis1, after.Axis2), 6);
        Assert.NotEqual(f.Axis2.Length, after.Axis2.Length, 3);
    }

    // ---- colors ----

    [Fact]
    public void Ctrl_click_on_the_line_adds_a_color_with_the_blend_color_there()
    {
        Begin(GradientKind.Linear);
        int steps = Steps;
        Press(200, 150, ModifierKeys.Control);                                               // the middle of the diagonal
        Release(200, 150);

        Assert.Equal(3, G.Stops.Count);
        Assert.Equal(0.5, G.Stops[1].Offset, 3);
        Assert.Equal(GradientRamp.ColorAt(G.WithStops([G.Stops[0], G.Stops[2]]), 0.5), G.Stops[1].Color);   // looks the same as before
        Assert.Equal(1, Tool.CurrentNode);
        Assert.Equal(steps + 1, Steps);
        Assert.Equal(3, Tool.Handles().Count);
    }

    [Fact]
    public void A_click_that_misses_the_line_or_has_no_modifier_adds_nothing()
    {
        Begin(GradientKind.Linear);
        int steps = Steps;
        Press(200, 150); Release(200, 150);
        Press(250, 120, ModifierKeys.Control); Release(250, 120);                            // far from the diagonal
        Assert.Equal(2, G.Stops.Count);
        Assert.Equal(steps, Steps);
    }

    [Fact]
    public void Shift_click_removes_a_color_but_never_an_end_or_the_last_two()
    {
        Begin(GradientKind.Linear);
        Press(200, 150, ModifierKeys.Control); Release(200, 150);
        Assert.Equal(3, G.Stops.Count);

        Press(200, 150, ModifierKeys.Shift); Release(200, 150);
        Assert.Equal(2, G.Stops.Count);

        Press(100, 100, ModifierKeys.Shift); Release(100, 100);                              // an end: nothing happens
        Press(300, 200, ModifierKeys.Shift); Release(300, 200);
        Assert.Equal(2, G.Stops.Count);
        Assert.False(Tool.RemoveNode(0));
        Assert.False(Tool.RemoveNode(1));
    }

    [Fact]
    public void Alt_click_adds_a_highlight_between_two_copies_of_the_base_color_and_drags_as_one()
    {
        Begin(GradientKind.Linear);
        Press(200, 150, ModifierKeys.Alt); Release(200, 150);

        Assert.Equal(5, G.Stops.Count);
        Assert.Equal([false, false, true, false, false], G.Stops.Select(s => s.Highlight));
        Assert.Equal(DocColor.White, G.Stops[2].Color);
        Assert.Equal(G.Stops[1].Color, G.Stops[3].Color);                                    // the flanks are the base color
        Assert.Equal(2, Tool.CurrentNode);

        double spread = G.Stops[3].Offset - G.Stops[1].Offset;
        var h = Tool.Handles()[2].Position;
        Drag(h.X, h.Y, h.X + 40, h.Y + 40);                                                  // along the diagonal: +0.2 of the line
        Assert.Equal(spread, G.Stops[3].Offset - G.Stops[1].Offset, 6);                      // the neighbors came along
        Assert.True(G.Stops[2].Offset > 0.6);

        // shift-click the highlight takes its two neighbors with it
        var moved = Tool.Handles()[2].Position;
        Press(moved.X, moved.Y, ModifierKeys.Shift); Release(moved.X, moved.Y);
        Assert.Equal(2, G.Stops.Count);
    }

    [Fact]
    public void A_color_slides_between_its_neighbors_and_never_past_them()
    {
        Begin(GradientKind.Linear);
        Press(200, 150, ModifierKeys.Control); Release(200, 150);                            // a color at 0.5
        var mid = Tool.Handles()[1].Position;
        Drag(mid.X, mid.Y, 900, 900);                                                        // way beyond the end
        Assert.InRange(G.Stops[1].Offset, 0.9, 1);
        Assert.Equal(3, G.Stops.Count);
        Assert.Equal([0.0, 1.0], new[] { G.Stops[0].Offset, G.Stops[^1].Offset });
        Assert.True(G.Stops[1].Offset < 1);
    }

    [Fact]
    public void There_are_at_most_twenty_colors()
    {
        Begin(GradientKind.Linear);
        for (int i = 1; i <= 18; i++)   // 18 more colors fit between the ends, each far enough from the others to be a new one
        {
            double t = i / 19.0;
            Press(100 + 200 * t, 100 + 100 * t, ModifierKeys.Control); Release(100 + 200 * t, 100 + 100 * t);
        }
        Assert.Equal(GradientPaint.MaxStops, G.Stops.Count);
        double between = 0.5 / 19.0;
        Press(100 + 200 * between, 100 + 100 * between, ModifierKeys.Control); Release(100 + 200 * between, 100 + 100 * between);
        Press(100 + 200 * between, 100 + 100 * between, ModifierKeys.Alt); Release(100 + 200 * between, 100 + 100 * between);
        Assert.Equal(GradientPaint.MaxStops, G.Stops.Count);                                 // no 21st color, no highlight
    }

    [Fact]
    public void Tab_cycles_the_picked_color()
    {
        Begin(GradientKind.Linear);
        Press(200, 150, ModifierKeys.Control); Release(200, 150);
        Assert.Equal(1, Tool.CurrentNode);
        Assert.True(_s.KeyDown(EditorKey.Tab, ModifierKeys.None));
        Assert.Equal(2, Tool.CurrentNode);
        _s.KeyDown(EditorKey.Tab, ModifierKeys.None);
        Assert.Equal(0, Tool.CurrentNode);                                                   // wraps round
        _s.KeyDown(EditorKey.Tab, ModifierKeys.Shift);
        Assert.Equal(2, Tool.CurrentNode);
    }

    [Fact]
    public void Clicking_a_handle_picks_that_color()
    {
        Begin(GradientKind.Linear);
        Assert.Equal(0, Tool.CurrentNode);
        Press(300, 200); Release(300, 200);
        Assert.Equal(1, Tool.CurrentNode);
        Assert.Equal(Steps, Steps);
    }

    // ---- colors from the Color Mixer, the Palette Strip and the Project Color Bar ----

    [Fact]
    public void While_the_tool_is_open_a_color_goes_to_the_picked_color_of_the_gradient()
    {
        Begin(GradientKind.Linear);
        var last = G.Stops[1].Color;
        Tool.SelectNode(0);
        int steps = Steps;

        var navy = new DocColor(10, 20, 80, 255, "Navy", "Oracal 651");
        _s.ApplyFill(new SolidPaint(navy));

        Assert.IsType<GradientPaint>(_r.Style.Fill);                                         // still a gradient
        Assert.Equal(navy, G.Stops[0].Color);
        Assert.Equal(last, G.Stops[1].Color);
        Assert.Equal(steps + 1, Steps);
        Assert.Equal(navy, Tool.CurrentNodeColor);
    }

    [Fact]
    public void A_slider_drag_on_a_gradient_color_is_one_undo_step()
    {
        Begin(GradientKind.Linear);
        Tool.SelectNode(1);
        int steps = Steps;
        _s.BeginStyleGesture();
        for (int v = 10; v < 200; v += 20) _s.ApplyFill(new SolidPaint(new DocColor((byte)v, 0, 0)));
        _s.EndStyleGesture();
        Assert.Equal(steps + 1, Steps);
        Assert.Equal(new DocColor(190, 0, 0), G.Stops[1].Color);
        _s.Undo();
        Assert.NotEqual(new DocColor(190, 0, 0), G.Stops[1].Color);
    }

    [Fact]
    public void A_picked_color_keeps_its_transparency()
    {
        Begin(GradientKind.Linear);
        var faded = G.WithStops([G.Stops[0], G.Stops[1] with { Color = G.Stops[1].Color with { A = 40 } }]);
        Tool.SetGradient(faded);
        Tool.SelectNode(1);
        _s.ApplyFill(new SolidPaint(new DocColor(1, 2, 3)));
        Assert.Equal(new DocColor(1, 2, 3, 40), G.Stops[1].Color);
    }

    [Fact]
    public void Without_the_tool_a_color_replaces_the_whole_fill_again()
    {
        Begin(GradientKind.Linear);
        _s.SetTool("select");
        _s.ApplyFill(new SolidPaint(new DocColor(9, 9, 9)));
        Assert.Equal(new SolidPaint(new DocColor(9, 9, 9)), _r.Style.Fill);
    }

    [Fact]
    public void A_gradient_keeps_the_stroke_and_a_preset_fits_another_object()
    {
        _r.Style = new ObjectStyle(_r.Style.Fill, StrokeStyle.Solid(DocColor.Black, 3));
        Begin(GradientKind.Ellipse);
        Assert.Equal(3, _r.Style.Stroke!.Width);

        var preset = G;
        var other = Rect(500, 300, 40, 400, ObjectStyle.FillOnly(DocColor.White));
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, other));
        _s.Selection.Set(other);
        Tool.SetGradient(preset);
        Assert.Equal(preset, other.Style.Fill);
        var f = ((GradientPaint)other.Style.Fill!).Frame(other.GetLocalGeometry().GetBounds());
        Assert.Equal(new Vec2(20, 200), f.P0);                                               // the center of the new, tall shape
    }

    [Fact]
    public void Clicking_another_object_with_the_tool_open_picks_it()
    {
        Begin(GradientKind.Linear);
        var other = Rect(500, 100, 100, 100, ObjectStyle.FillOnly(DocColor.White));
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, other));
        Press(550, 150); Release(550, 150);
        Assert.Same(other, Tool.Target);
        Assert.Null(Tool.Gradient);
        Assert.Empty(Tool.Handles());
    }

    [Fact]
    public void The_overlay_shows_the_control_line_with_a_handle_per_color()
    {
        Begin(GradientKind.Ellipse);
        var items = new List<OverlayItem>();
        Tool.BuildOverlay(items);
        var overlay = Assert.Single(items.OfType<GradientOverlay>());
        Assert.NotNull(overlay.Shape);
        Assert.NotNull(overlay.Axis2End);
        Assert.Equal(3, overlay.Marks.Count);                                                // two colors and the second axis
        Assert.Equal(Gold, overlay.Marks[0].Color);
        Assert.True(overlay.Marks[0].Current);
        Assert.Null(overlay.Marks[2].Color);
    }
}
