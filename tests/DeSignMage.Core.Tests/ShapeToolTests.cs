using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>The polygon and arrow objects, Layout ▸ Shapes (Rounded Rectangle, Square, Circle, Polygon/Star, Arrow) and Convert to Rounded Rectangle (TASKS C.37).</summary>
public class ShapeObjectTests
{
    [Fact]
    public void A_polygon_has_its_corners_on_the_circle_starting_at_the_start_angle()
    {
        var g = new PolygonObject(5, 100, 0, -Math.PI / 2).GetLocalGeometry();
        var loop = Assert.Single(PathEditing.ToLoops(g));
        Assert.True(loop.Closed);
        Assert.Equal(5, loop.Segments.Count);
        Assert.All(Enumerable.Range(0, 5), k => Assert.Equal(100, loop.Node(k).Length, 6));
        Assert.Equal(0, loop.Node(0).X, 6);
        Assert.Equal(-100, loop.Node(0).Y, 6);                        // pointing up on screen
        Assert.All(loop.Segments, s => Assert.Equal(SegmentKind.Line, s.Kind));
        Assert.True(loop.IsClockwise);                                // successive corners turn clockwise on screen
    }

    [Fact]
    public void A_star_alternates_the_outer_and_inner_radius_and_has_twice_the_corners()
    {
        var star = new PolygonObject(5, 100, 0.4, 0);
        var loop = Assert.Single(PathEditing.ToLoops(star.GetLocalGeometry()));
        Assert.Equal(10, loop.Segments.Count);
        for (int k = 0; k < 10; k++) Assert.Equal(k % 2 == 0 ? 100 : 40, loop.Node(k).Length, 6);
        Assert.Equal("Star", star.KindName);
        Assert.True(star.IsStar);
        Assert.Equal("Polygon", new PolygonObject(3, 10).KindName);
    }

    [Fact]
    public void Polygon_settings_are_kept_in_range_and_survive_a_copy()
    {
        var p = new PolygonObject(2, 50, 2);
        Assert.Equal(3, p.Sides);
        Assert.Equal(PolygonObject.MaxInnerRatio, p.InnerRatio);
        p.Sides = 500;
        Assert.Equal(100, p.Sides);
        p.Radius = -4;
        Assert.Equal(0, p.Radius);

        var q = (PolygonObject)new PolygonObject(7, 33, 0.25, 1.5) { Name = "Seven" }.Clone();
        Assert.Equal((7, 33.0, 0.25, 1.5, "Seven"), (q.Sides, q.Radius, q.InnerRatio, q.StartAngle, q.Name));
    }

    [Fact]
    public void An_arrow_points_along_x_with_a_head_and_a_shaft()
    {
        var arrow = new ArrowObject(100, 40, 0.4, 0.5);
        var loop = Assert.Single(PathEditing.ToLoops(arrow.GetLocalGeometry()));
        Assert.Equal(7, loop.Segments.Count);
        Assert.Equal(new Vec2(0, -10), loop.Node(0));                 // shaft half-width: 40 * 0.5 / 2
        Assert.Equal(new Vec2(60, -10), loop.Node(1));                // the head starts 40% from the tip
        Assert.Equal(new Vec2(60, -20), loop.Node(2));
        Assert.Equal(new Vec2(100, 0), loop.Node(3));                 // the tip
        Assert.Equal(new Vec2(60, 20), loop.Node(4));
        Assert.Equal(new Rect2(0, -20, 100, 20), arrow.GetLocalGeometry().GetBounds());
        Assert.Equal("Arrow", arrow.KindName);
    }

    [Fact]
    public void Arrow_proportions_are_kept_in_range_and_survive_a_copy()
    {
        var a = new ArrowObject(10, 4, 5, 0);
        Assert.Equal(1, a.HeadFraction);
        Assert.Equal(ArrowObject.MinFraction, a.ShaftFraction);
        var b = (ArrowObject)new ArrowObject(120, 30, 0.3, 0.6) { Transform = Affine2.Translation(5, 6) }.Clone();
        Assert.Equal((120.0, 30.0, 0.3, 0.6), (b.Length, b.HeadWidth, b.HeadFraction, b.ShaftFraction));
        Assert.Equal(Affine2.Translation(5, 6), b.Transform);
    }

    [Fact]
    public void Polygons_and_arrows_open_in_vector_edit_and_become_plain_paths_on_the_first_edit()
    {
        var s = new EditorSession(NewDoc());
        s.Viewport.SetViewSize(new Vec2(1000, 800));
        s.Viewport.Set(1, Vec2.Zero);
        var poly = new PolygonObject(4, 100, 0, 0) { Transform = Affine2.Translation(300, 300), Style = ObjectStyle.FillOnly(DocColor.Black) };
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, poly));
        Assert.True(OutlineView.IsEditable(poly));
        Assert.True(OutlineView.IsEditable(new ArrowObject(10, 4)));
        s.EditVectors(poly);
        var node = new Vec2(400, 300);                                // the first corner
        s.PointerDown(new PointerInput(node, node, PointerButton.Left, ModifierKeys.None));
        s.PointerUp(new PointerInput(node, node, PointerButton.Left, ModifierKeys.None));
        Assert.True(s.KeyDown(EditorKey.Right, ModifierKeys.None));   // nudge it
        var path = Assert.IsType<PathObject>(Assert.Single(s.Document.ActiveLayer.Children));
        Assert.Equal(poly.Transform, path.Transform);
        Assert.Equal(poly.Style, path.Style);
        s.Undo();
        Assert.Same(poly, Assert.Single(s.Document.ActiveLayer.Children));
    }
}

/// <summary>The shape tools driven with pointer events, like the shell does.</summary>
public class ShapeToolTests
{
    private readonly EditorSession _s;
    private Layer L => _s.Document.ActiveLayer;

    public ShapeToolTests()
    {
        _s = new EditorSession(NewDoc());
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);
    }

    private PointerInput At(double x, double y, PointerButton b = PointerButton.None, ModifierKeys m = ModifierKeys.None) =>
        new(new Vec2(x, y), new Vec2(x, y), b, m);

    private void Drag(double x0, double y0, double x1, double y1, ModifierKeys m = ModifierKeys.None)
    {
        _s.PointerDown(At(x0, y0, PointerButton.Left, m));
        _s.PointerMove(At((x0 + x1) / 2, (y0 + y1) / 2, PointerButton.None, m));
        _s.PointerMove(At(x1, y1, PointerButton.None, m));
        _s.PointerUp(At(x1, y1, PointerButton.Left, m));
    }

    private T Only<T>() where T : DocObject => Assert.IsType<T>(Assert.Single(L.Children));

    // ---- Every shape: bottom of the stack, uncolored, selected, one undo step ----

    [Theory]
    [InlineData("roundedRectangle", typeof(RectangleObject))]
    [InlineData("square", typeof(RectangleObject))]
    [InlineData("circle", typeof(EllipseObject))]
    [InlineData("polygon", typeof(PolygonObject))]
    [InlineData("arrow", typeof(ArrowObject))]
    public void A_new_shape_goes_to_the_bottom_with_no_color_and_is_selected(string tool, Type kind)
    {
        var existing = Rect(100, 100, 50, 50, ObjectStyle.FillOnly(DocColor.Black));
        _s.History.Execute(new AddObjectCommand(L, existing));
        int steps = _s.History.UndoList.Count;

        _s.SetTool(tool);
        Drag(400, 400, 500, 450);
        Assert.Equal(2, L.Children.Count);
        var obj = L.Children[0];                                       // below the old one
        Assert.IsType(kind, obj);
        Assert.Same(existing, L.Children[1]);
        Assert.Equal(ObjectStyle.None, obj.Style);                     // no fill, no stroke: drawn as an outline
        Assert.Same(obj, Assert.Single(_s.Selection.Items));
        Assert.Equal("select", _s.ActiveTool.Id);
        Assert.Equal(steps + 1, _s.History.UndoList.Count);

        _s.Undo();
        Assert.Same(existing, Assert.Single(L.Children));
    }

    [Fact]
    public void Alt_keeps_the_tool_and_a_tiny_drag_makes_nothing()
    {
        _s.SetTool("polygon");
        Drag(400, 400, 401, 400);
        Assert.Empty(L.Children);
        Assert.Equal("polygon", _s.ActiveTool.Id);

        _s.PointerDown(At(400, 400, PointerButton.Left));
        _s.PointerMove(At(450, 400));
        _s.PointerUp(At(450, 400, PointerButton.Left, ModifierKeys.Alt));
        Assert.Single(L.Children);
        Assert.Equal("polygon", _s.ActiveTool.Id);
    }

    [Fact]
    public void The_outline_follows_the_pointer_while_dragging_and_escape_cancels()
    {
        _s.SetTool("arrow");
        _s.PointerDown(At(100, 100, PointerButton.Left));
        _s.PointerMove(At(300, 100));
        Assert.Single(_s.BuildOverlay().OfType<OutlineOverlay>());
        Assert.True(_s.KeyDown(EditorKey.Escape, ModifierKeys.None));
        Assert.Empty(_s.BuildOverlay().OfType<OutlineOverlay>());
        _s.PointerUp(At(300, 100, PointerButton.Left));
        Assert.Empty(L.Children);
    }

    // ---- Each shape ----

    [Fact]
    public void A_rounded_rectangle_starts_with_a_quarter_inch_radius_that_the_bar_can_change()
    {
        _s.SetTool("roundedRectangle");
        Drag(100, 100, 300, 200);
        var r = Only<RectangleObject>();
        Assert.Equal(200, r.Width);
        Assert.Equal(100, r.Height);
        Assert.Equal(Units.FromInches(0.25), r.CornerRadius, 6);
        Assert.Equal(Affine2.Translation(100, 100), r.Transform);

        _s.SetTool("roundedRectangle");
        _s.GetTool<RoundedRectangleTool>().CornerRadius = 10;
        Drag(400, 400, 500, 500, ModifierKeys.Control);               // Ctrl: a square
        var second = L.Children.OfType<RectangleObject>().First(o => o != r);
        Assert.Equal((100, 100, 10), (second.Width, second.Height, second.CornerRadius));
    }

    [Fact]
    public void A_square_is_drawn_from_its_center_following_the_larger_distance()
    {
        _s.SetTool("square");
        Drag(400, 300, 450, 320);
        var sq = Only<RectangleObject>();
        Assert.Equal((100, 100, 0), (sq.Width, sq.Height, sq.CornerRadius));
        Assert.Equal(new Rect2(350, 250, 450, 350), sq.GetWorldBounds());   // centered on the press point
    }

    [Fact]
    public void A_circle_is_drawn_from_its_center_out_to_its_edge()
    {
        _s.SetTool("circle");
        Drag(400, 300, 430, 340);                                      // 50 away
        var c = Only<EllipseObject>();
        Assert.Equal((100, 100), (c.Width, c.Height));
        Assert.Equal(new Vec2(400, 300), c.GetWorldBounds().Center);
    }

    [Fact]
    public void A_polygon_has_the_set_number_of_sides_and_its_first_corner_follows_the_pointer()
    {
        _s.SetTool("polygon");
        _s.GetTool<PolygonTool>().Sides = 6;
        Drag(400, 300, 480, 300);
        var p = Only<PolygonObject>();
        Assert.Equal((6, 80.0, 0.0), (p.Sides, p.Radius, p.InnerRatio));
        Assert.Equal(0, p.StartAngle, 6);                              // pointing east
        Assert.Equal(new Vec2(400, 300), p.Transform.Offset);
        Assert.Equal(new Vec2(480, 300), p.Transform.Transform(PathEditing.ToLoops(p.GetLocalGeometry())[0].Node(0)));
        Assert.Equal("Polygon", p.KindName);
    }

    [Fact]
    public void A_star_has_the_set_inner_radius()
    {
        var tool = _s.GetTool<PolygonTool>();
        tool.Sides = 5; tool.Star = true; tool.InnerRatio = 0.4;
        _s.SetTool("polygon");
        Drag(400, 300, 400, 200);
        var star = Only<PolygonObject>();
        Assert.Equal(0.4, star.InnerRatio, 6);
        Assert.Equal(-Math.PI / 2, star.StartAngle, 6);                // dragged straight up
        Assert.Equal("Star", star.KindName);
        Assert.Equal(10, PathEditing.ToLoops(star.GetLocalGeometry())[0].Segments.Count);
    }

    [Fact]
    public void Ctrl_snaps_the_polygon_angle_to_15_degrees()
    {
        _s.SetTool("polygon");
        double a = 20 * Math.PI / 180;
        Drag(400, 300, 400 + 100 * Math.Cos(a), 300 + 100 * Math.Sin(a), ModifierKeys.Control);
        Assert.Equal(15 * Math.PI / 180, Only<PolygonObject>().StartAngle, 6);
    }

    [Fact]
    public void An_arrow_runs_from_the_tail_to_the_tip_with_the_set_proportions()
    {
        var tool = _s.GetTool<ArrowTool>();
        tool.HeadFraction = 0.25; tool.ShaftFraction = 0.5; tool.WidthRatio = 0.5;
        _s.SetTool("arrow");
        Drag(100, 100, 300, 100);
        var a = Only<ArrowObject>();
        Assert.Equal((200.0, 100.0, 0.25, 0.5), (a.Length, a.HeadWidth, a.HeadFraction, a.ShaftFraction));
        var tip = a.Transform.Transform(new Vec2(a.Length, 0));
        Assert.Equal(300, tip.X, 6);
        Assert.Equal(100, tip.Y, 6);
        Assert.Equal(new Vec2(100, 100), a.Transform.Transform(Vec2.Zero));   // the tail stays where it was pressed
    }

    [Fact]
    public void An_arrow_can_point_any_way_and_ctrl_snaps_it_to_45_degrees()
    {
        _s.SetTool("arrow");
        Drag(100, 100, 100, 300);                                       // straight down
        var down = Only<ArrowObject>();
        var tip = down.Transform.Transform(new Vec2(down.Length, 0));
        Assert.Equal(100, tip.X, 6);
        Assert.Equal(300, tip.Y, 6);

        _s.SetTool("arrow");
        Drag(500, 500, 600, 540, ModifierKeys.Control);                 // about 22°: snaps to 0° (east)
        var east = L.Children.OfType<ArrowObject>().First(o => o != down);
        var tip2 = east.Transform.Transform(new Vec2(east.Length, 0));
        Assert.Equal(500 + Math.Sqrt(100 * 100 + 40 * 40), tip2.X, 4);
        Assert.Equal(500, tip2.Y, 4);
    }

    [Fact]
    public void The_help_line_names_each_shape()
    {
        foreach (var id in new[] { "roundedRectangle", "square", "circle", "polygon", "arrow" })
        {
            _s.SetTool(id);
            Assert.StartsWith("To DRAW", _s.Hint);
        }
    }
}

/// <summary>Layout ▸ Shapes ▸ Convert to Rounded Rectangle.</summary>
public class RoundCornersTests
{
    private readonly EditorSession _s;
    private Layer L => _s.Document.ActiveLayer;

    public RoundCornersTests()
    {
        _s = new EditorSession(NewDoc());
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);
    }

    private PathObject BoxPath(double x, double y, double w, double h, Affine2? transform = null)
    {
        var p = new PathObject(PathGeometry.Rectangle(Rect2.FromXYWH(x, y, w, h))) { Transform = transform ?? Affine2.Identity, Style = ObjectStyle.FillOnly(DocColor.FromHex("#336699")), Name = "box" };
        _s.History.Execute(new AddObjectCommand(L, p));
        return p;
    }

    [Fact]
    public void A_rectangle_object_gets_the_radius_and_undo_takes_it_back()
    {
        var r = Rect(100, 100, 200, 100, ObjectStyle.FillOnly(DocColor.Black));
        _s.History.Execute(new AddObjectCommand(L, r));
        _s.Selection.Set(r);
        Assert.True(_s.CanConvertToRoundedRectangle);
        _s.BeginConvertToRoundedRectangle();
        var tool = _s.GetTool<RoundCornersTool>();
        Assert.Same(tool, _s.ActiveTool);
        Assert.Equal(RoundedRectangleTool.DefaultRadius, tool.Radius, 6);

        tool.Radius = 20;
        int steps = _s.History.UndoList.Count;
        tool.Apply();
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Convert to Rounded Rectangle", _s.History.UndoDescription);
        Assert.Equal(20, r.CornerRadius);
        Assert.Equal("select", _s.ActiveTool.Id);
        Assert.Same(r, Assert.Single(_s.Selection.Items));

        _s.Undo();
        Assert.Equal(0, r.CornerRadius);
        _s.Redo();
        Assert.Equal(20, r.CornerRadius);
    }

    [Fact]
    public void A_plain_box_path_becomes_a_rectangle_object_in_the_same_place()
    {
        var below = Rect(0, 0, 10, 10, ObjectStyle.None);
        _s.History.Execute(new AddObjectCommand(L, below));
        var path = BoxPath(20, 30, 200, 100, Affine2.Rotation(0.3) * Affine2.Translation(50, 60));
        var above = Rect(0, 500, 10, 10, ObjectStyle.None);
        _s.History.Execute(new AddObjectCommand(L, above));
        var before = path.GetWorldBounds();
        _s.Selection.Set(path);
        Assert.True(_s.CanConvertToRoundedRectangle);

        _s.BeginConvertToRoundedRectangle();
        var tool = _s.GetTool<RoundCornersTool>();
        tool.Radius = 15;
        tool.Apply();

        Assert.Equal(3, L.Children.Count);
        Assert.Same(below, L.Children[0]);
        Assert.Same(above, L.Children[2]);                                  // the stacking position did not change
        var rect = Assert.IsType<RectangleObject>(L.Children[1]);
        Assert.Equal((200, 100, 15), (rect.Width, rect.Height, rect.CornerRadius));
        Assert.Equal(path.Style, rect.Style);
        Assert.Equal("box", rect.Name);
        // The sharp corners are shaved off, so the bounds of the rotated shape shrink only a little; its center stays.
        var after = rect.GetWorldBounds();
        Assert.Equal(before.Center.X, after.Center.X, 3);
        Assert.Equal(before.Center.Y, after.Center.Y, 3);
        Assert.Same(rect, Assert.Single(_s.Selection.Items));

        _s.Undo();
        Assert.Same(path, L.Children[1]);
        Assert.Equal(before, path.GetWorldBounds());
        _s.Redo();
        Assert.IsType<RectangleObject>(L.Children[1]);
    }

    [Fact]
    public void Only_rectangles_can_be_converted()
    {
        var tri = new PathObject(new PathBuilder().MoveTo(0, 0).LineTo(100, 0).LineTo(50, 80).Close().Build());
        _s.History.Execute(new AddObjectCommand(L, tri));
        var ellipse = new EllipseObject(50, 50);
        _s.History.Execute(new AddObjectCommand(L, ellipse));
        var skew = new PathObject(new PathBuilder().MoveTo(0, 0).LineTo(100, 10).LineTo(100, 100).LineTo(0, 90).Close().Build());   // not axis aligned
        _s.History.Execute(new AddObjectCommand(L, skew));
        var withHole = new PathObject(PathEditing.FromLoops([PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(0, 0, 100, 100)))[0], PathEditing.ToLoops(PathGeometry.Rectangle(Rect2.FromXYWH(20, 20, 20, 20)))[0]], FillRule.EvenOdd));
        _s.History.Execute(new AddObjectCommand(L, withHole));
        foreach (var o in new DocObject[] { tri, ellipse, skew, withHole })
        {
            _s.Selection.Set(o);
            Assert.False(_s.CanConvertToRoundedRectangle);
        }
        _s.BeginConvertToRoundedRectangle();
        Assert.Equal("select", _s.ActiveTool.Id);                           // nothing to do: the tool does not open
        _s.Selection.Clear();
        Assert.False(_s.CanConvertToRoundedRectangle);
    }

    [Fact]
    public void The_preview_shows_the_rounded_outline_of_each_rectangle_and_follows_the_radius_without_changing_anything()
    {
        var r = Rect(100, 100, 200, 100, ObjectStyle.FillOnly(DocColor.Black));
        var other = BoxPath(400, 100, 100, 100);
        _s.History.Execute(new AddObjectCommand(L, r));
        _s.Selection.Set([r, other]);
        _s.BeginConvertToRoundedRectangle();
        var tool = _s.GetTool<RoundCornersTool>();
        tool.Radius = 30;
        int steps = _s.History.UndoList.Count;

        var previews = _s.BuildOverlay().OfType<OutlineOverlay>().ToList();
        Assert.Equal(2, previews.Count);
        Assert.All(previews, p => Assert.True(p.Path.Verbs.Contains(PathVerb.CubicTo) || p.Path.HasArcs));   // rounded: more than straight lines
        Assert.Equal(new Rect2(100, 100, 300, 200), previews.Single(p => p.Path.GetBounds().Left == 100).Path.GetBounds());
        tool.Radius = 0;
        Assert.All(_s.BuildOverlay().OfType<OutlineOverlay>(), p => Assert.DoesNotContain(PathVerb.CubicTo, p.Path.Verbs));
        Assert.Equal(steps, _s.History.UndoList.Count);                     // a preview is not a change
        Assert.Equal(0, r.CornerRadius);
    }

    [Fact]
    public void The_radius_is_limited_to_half_the_shortest_side()
    {
        var r = Rect(0, 0, 200, 100, ObjectStyle.None);
        _s.History.Execute(new AddObjectCommand(L, r));
        _s.Selection.Set(r);
        _s.BeginConvertToRoundedRectangle();
        var tool = _s.GetTool<RoundCornersTool>();
        Assert.Equal(50, tool.MaxRadius);
        tool.Radius = 500;
        tool.Apply();
        Assert.Equal(50, r.CornerRadius);
    }

    [Fact]
    public void Enter_applies_escape_cancels_and_the_radius_starts_from_the_existing_one()
    {
        var r = new RectangleObject(200, 100, 12) { Style = ObjectStyle.None };
        _s.History.Execute(new AddObjectCommand(L, r));
        _s.Selection.Set(r);
        _s.BeginConvertToRoundedRectangle();
        var tool = _s.GetTool<RoundCornersTool>();
        Assert.Equal(12, tool.Radius);

        tool.Radius = 30;
        int steps = _s.History.UndoList.Count;
        Assert.True(_s.KeyDown(EditorKey.Escape, ModifierKeys.None));
        Assert.Equal("select", _s.ActiveTool.Id);
        Assert.Equal(steps, _s.History.UndoList.Count);
        Assert.Equal(12, r.CornerRadius);

        _s.BeginConvertToRoundedRectangle();
        _s.GetTool<RoundCornersTool>().Radius = 30;
        Assert.True(_s.KeyDown(EditorKey.Enter, ModifierKeys.None));
        Assert.Equal(30, r.CornerRadius);
    }

    [Fact]
    public void Rectangles_inside_a_group_are_converted_too_and_stay_in_the_group()
    {
        var a = Rect(0, 0, 100, 100, ObjectStyle.None);
        var b = BoxPath(300, 0, 100, 100);
        _s.History.Execute(new AddObjectCommand(L, a));
        _s.Selection.Set([a, b]);
        var group = _s.GroupSelection()!;
        Assert.True(_s.CanConvertToRoundedRectangle);
        _s.BeginConvertToRoundedRectangle();
        _s.GetTool<RoundCornersTool>().Apply();
        Assert.Equal(2, group.Children.Count);
        Assert.All(group.Children, c => Assert.IsType<RectangleObject>(c));
        Assert.All(group.Children.Cast<RectangleObject>(), r => Assert.True(r.CornerRadius > 0));
        Assert.Same(group, Assert.Single(_s.Selection.Items));
    }
}
