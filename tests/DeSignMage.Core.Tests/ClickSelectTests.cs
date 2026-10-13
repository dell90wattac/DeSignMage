using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Click-to-select (requirement R10): outline hit wins from any depth, then the topmost enclosed area, then the top-level group.</summary>
public class ClickSelectTests
{
    private static readonly ObjectStyle Filled = ObjectStyle.FillOnly(DocColor.Black);
    private static readonly ObjectStyle NoFill = new(null, StrokeStyle.Solid(DocColor.Black, 0));   // outline only, hairline
    private const double Tol = 1;

    private readonly Document _doc = NewDoc();

    private T Add<T>(T o) where T : DocObject
    {
        _doc.Insert(_doc.ActiveLayer, -1, o);
        return o;
    }

    private DocObject? Hit(double x, double y, double tol = Tol) => HitTesting.HitTest(_doc, new Vec2(x, y), tol);

    private static TextObject Text(double x, double y, double size = 100)
    {
        var t = new TextObject("HIH", "Arial", size) { Transform = Affine2.Translation(x, y) };
        return t;
    }

    /// <summary>A point exactly on a glyph outline, in document coordinates.</summary>
    private static Vec2 OnGlyphOutline(TextObject t) => t.GetWorldTransform().Transform(t.Layout.Outline.Points[0]);

    /// <summary>The point inside the text's line box that is farthest from every glyph outline, in document coordinates.</summary>
    private static Vec2 DeadSpaceInTextBox(TextObject t)
    {
        var f = t.GetPickFrame();
        var outline = t.Layout.Outline;
        Vec2 best = f.Center;
        double bestDist = -1;
        for (int i = 1; i < 40; i++)
            for (int j = 1; j < 20; j++)
            {
                var p = new Vec2(f.Left + f.Width * i / 40, f.Top + f.Height * j / 20);
                if (outline.Contains(p)) continue;
                double d = outline.DistanceToOutline(p);
                if (d > bestDist) { bestDist = d; best = p; }
            }
        Assert.True(bestDist > 3 * Tol, $"no dead space in the text box ({bestDist})");
        return t.GetWorldTransform().Transform(best);
    }

    [Fact]
    public void Box_with_text_in_front_click_the_empty_part_selects_the_box_and_click_on_the_text_selects_the_text()
    {
        var box = Add(Rect(0, 0, 600, 400, Filled));
        var text = Add(Text(150, 250));
        Assert.Same(box, Hit(20, 20));                          // dead space of the box
        Assert.Same(text, Hit(OnGlyphOutline(text).X, OnGlyphOutline(text).Y));   // on a letter's outline
        var dead = DeadSpaceInTextBox(text);
        Assert.Same(text, Hit(dead.X, dead.Y));                 // inside the text's line box: the topmost area is the text
    }

    [Fact]
    public void Text_behind_the_box_click_on_the_text_area_selects_the_box_but_click_on_a_glyph_outline_selects_the_text()
    {
        var text = Add(Text(150, 250));
        var box = Add(Rect(0, 0, 600, 400, Filled));
        var dead = DeadSpaceInTextBox(text);
        Assert.Same(box, Hit(dead.X, dead.Y));                  // where the text is, but the box is on top
        var g = OnGlyphOutline(text);
        Assert.Same(text, Hit(g.X, g.Y));                       // exactly on a letter's outline wins from behind
        Assert.Same(box, Hit(20, 20));
    }

    [Fact]
    public void An_unfilled_shape_is_selected_by_its_empty_middle()
    {
        var r = Add(Rect(0, 0, 100, 100, NoFill));
        var e = Add(new EllipseObject(100, 100) { Style = NoFill, Transform = Affine2.Translation(300, 0) });
        Assert.Same(r, Hit(50, 50));
        Assert.Same(e, Hit(350, 50));
        Assert.Null(Hit(200, 50));                              // between them: nothing
        Assert.Null(Hit(305, 5));                               // ellipse corner of its bounds is outside the ellipse
    }

    [Fact]
    public void A_shape_with_no_fill_and_no_stroke_is_still_selectable()
    {
        var r = Add(Rect(0, 0, 100, 100, ObjectStyle.None));
        Assert.Same(r, Hit(50, 50));
    }

    [Fact]
    public void An_unfilled_shape_on_top_of_a_filled_one_wins_the_area_click_because_it_is_topmost()
    {
        var below = Add(Rect(0, 0, 200, 200, Filled));
        var above = Add(Rect(50, 50, 100, 100, NoFill));
        Assert.Same(above, Hit(100, 100));
        Assert.Same(below, Hit(20, 20));
    }

    [Fact]
    public void The_outline_of_a_deeply_buried_object_is_selected()
    {
        var buried = Add(Rect(100, 100, 100, 100, Filled));
        for (int i = 0; i < 6; i++) Add(Rect(0, 0, 400, 400, Filled));          // six big plates on top
        Assert.Same(_doc.ActiveLayer.Children[^1], Hit(300, 300));             // plain area: topmost plate
        Assert.Same(buried, Hit(100, 150));                                    // on its left edge
        Assert.Same(buried, Hit(199.5, 150));                                  // within tolerance of its right edge
        Assert.Same(buried, Hit(150, 100.9));
        Assert.Same(_doc.ActiveLayer.Children[^1], Hit(150, 150));             // its middle is covered
    }

    [Fact]
    public void The_nearest_outline_wins_and_a_tie_goes_to_the_topmost()
    {
        var far = Add(Rect(0, 0, 100, 100, Filled));        // right edge x = 100
        var near = Add(Rect(104, 0, 100, 100, Filled));     // left edge x = 104
        Assert.Same(far, Hit(101.5, 50, tol: 4));
        Assert.Same(near, Hit(102.7, 50, tol: 4));
        Assert.Same(near, Hit(102, 50, tol: 4));            // exactly between the two outlines: topmost

        var lower = Add(Rect(300, 0, 50, 50, Filled));
        var upper = Add(Rect(300, 0, 50, 50, Filled));      // identical outline
        Assert.Same(upper, Hit(300, 25));
        Assert.NotSame(lower, Hit(300, 25));
    }

    [Fact]
    public void A_stroke_counts_as_part_of_the_outline()
    {
        var thick = Add(Rect(0, 0, 100, 100, new ObjectStyle(null, StrokeStyle.Solid(DocColor.Black, 20))));
        Add(Rect(-50, -50, 400, 400, Filled));
        Assert.Same(thick, Hit(-8, 50));                    // 8 outside the edge, inside the 10-wide half stroke
        Assert.NotSame(thick, Hit(-40, 50));
    }

    [Fact]
    public void A_hit_member_selects_its_whole_top_level_group_by_area_or_by_outline()
    {
        var g = Add(new GroupObject());
        var inner = new GroupObject();
        _doc.Insert(g, -1, inner);
        _doc.Insert(inner, -1, Rect(0, 0, 100, 100, Filled));
        _doc.Insert(g, -1, Rect(300, 0, 100, 100, NoFill));
        var top = Add(Rect(-50, -50, 600, 600, Filled));   // covers everything

        Assert.Same(top, Hit(200, 200));
        Assert.Same(g, Hit(0, 50));                         // outline of a nested member from under the plate
        Assert.Same(g, Hit(300, 50));                       // outline of the unfilled member
        top.Visible = false;
        Assert.Same(g, Hit(50, 50));                        // area of the nested member
        Assert.Same(g, Hit(350, 50));                       // empty middle of the unfilled member
        Assert.Null(Hit(200, 50));                          // between members: the group has no area of its own
    }

    [Fact]
    public void Combined_pieces_select_by_outline_or_area()
    {
        var back = Add(Rect(0, 0, 300, 300, Filled));
        var combined = Add(new CombinedObject([Rect(0, 0, 100, 100, NoFill), Rect(200, 0, 100, 100, NoFill)]));
        Add(Rect(-10, -10, 400, 400, Filled));              // plate over both
        var plate = _doc.ActiveLayer.Children[^1];
        Assert.Same(plate, Hit(50, 50));
        Assert.Same(combined, Hit(100, 50));               // a part's edge from under the plate
        plate.Visible = false;
        Assert.Same(combined, Hit(50, 50));                // a part's unfilled middle
        Assert.Same(back, Hit(150, 150));
    }

    [Fact]
    public void An_open_path_is_selected_only_at_its_outline_not_in_the_middle()
    {
        var c = Add(new PathObject(new PathBuilder().MoveTo(100, 0).LineTo(0, 0).LineTo(0, 100).LineTo(100, 100).Build())
        {
            Style = new ObjectStyle(null, StrokeStyle.Solid(DocColor.Black, 1)),
        });
        Assert.Same(c, Hit(0, 50));                         // on the "C" spine
        Assert.Same(c, Hit(50, 0.5));
        Assert.Null(Hit(50, 50));                           // the open middle encloses nothing
        Assert.Null(Hit(50, 20));
    }

    [Fact]
    public void A_closed_path_with_no_fill_has_an_area()
    {
        var tri = Add(new PathObject(new PathBuilder().MoveTo(0, 0).LineTo(100, 0).LineTo(50, 100).Close().Build()) { Style = NoFill });
        Assert.Same(tri, Hit(50, 30));
        Assert.Null(Hit(5, 90));
    }

    [Fact]
    public void A_filled_open_path_still_selects_by_its_painted_area()
    {
        var c = Add(new PathObject(new PathBuilder().MoveTo(100, 0).LineTo(0, 0).LineTo(0, 100).LineTo(100, 100).Build()) { Style = Filled });
        Assert.Same(c, Hit(30, 50));                        // a fill paints the implicitly closed shape
    }

    [Fact]
    public void Hidden_and_locked_objects_cannot_be_hit_by_their_outline_either()
    {
        var a = Add(Rect(0, 0, 100, 100, NoFill));
        a.Locked = true;
        Assert.Null(Hit(0, 50));
        a.Locked = false;
        a.Visible = false;
        Assert.Null(Hit(0, 50));
    }

    [Fact]
    public void Images_are_selected_by_frame_and_by_area()
    {
        var img = Add(new ImageObject("a", 100, 80) { Transform = Affine2.Translation(50, 50) });
        Add(Rect(0, 0, 400, 400, Filled));
        Assert.Same(img, Hit(50, 90));                      // frame edge, from behind
        Assert.NotSame(img, Hit(100, 90));                  // interior is covered
        _doc.ActiveLayer.Children[^1].Visible = false;
        Assert.Same(img, Hit(100, 90));
    }

    [Fact]
    public void Rotated_and_scaled_objects_hit_in_document_space()
    {
        var r = Add(Rect(0, 0, 100, 10, NoFill));
        r.Transform = Affine2.Rotation(Math.PI / 2, Vec2.Zero) * Affine2.Translation(200, 0);   // now a vertical bar x in [190,200]
        Assert.Same(r, Hit(195, 50));
        Assert.Same(r, Hit(189.5, 50));
        Assert.Null(Hit(150, 5));
    }

    // ---- Tolerance in screen pixels ----

    private EditorSession Session(double zoom)
    {
        var s = new EditorSession(NewDoc());
        s.Viewport.SetViewSize(new Vec2(1000, 800));
        s.Viewport.Set(zoom, Vec2.Zero);
        s.Document.Insert(s.Document.ActiveLayer, -1, Rect(100, 100, 100, 100, ObjectStyle.None));
        return s;
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(4.0)]
    [InlineData(0.25)]
    public void The_tolerance_is_eight_screen_pixels_at_every_zoom(double zoom)
    {
        var s = Session(zoom);
        Assert.Equal(8, s.HitTolerancePixels);
        var box = s.Document.ActiveLayer.Children[0];
        double edgeX = 100;   // left edge in document space
        // Just inside the tolerance: 7 screen px outside the left edge. Just outside: 9 px. Both far from the box's area.
        Assert.Same(box, s.HitTestAt(new Vec2(edgeX - 7 / zoom, 150)));
        Assert.Null(s.HitTestAt(new Vec2(edgeX - 9 / zoom, 150)));
    }

    [Fact]
    public void The_tolerance_is_a_setting()
    {
        var s = Session(1);
        s.HitTolerancePixels = 3;
        Assert.Null(s.HitTestAt(new Vec2(94, 150)));
        s.HitTolerancePixels = 8;
        Assert.NotNull(s.HitTestAt(new Vec2(94, 150)));
    }

    // ---- Through the Select tool ----

    private static PointerInput At(EditorSession s, double x, double y, PointerButton b = PointerButton.None) =>
        new(new Vec2(x, y), s.Viewport.ToDoc(new Vec2(x, y)), b, ModifierKeys.None);

    private static void Click(EditorSession s, double x, double y)
    {
        s.PointerDown(At(s, x, y, PointerButton.Left));
        s.PointerUp(At(s, x, y, PointerButton.Left));
    }

    [Fact]
    public void The_hover_cursor_is_the_select_hit_cursor_exactly_where_a_click_would_select_something()
    {
        var s = Session(1);                                   // an unfilled box at (100,100)-(200,200)
        Assert.Equal(CursorKind.Arrow, s.CurrentCursor);

        s.PointerMove(At(s, 100, 150));                       // on its outline
        Assert.Equal(CursorKind.SelectHit, s.CurrentCursor);
        s.PointerMove(At(s, 95, 150));                        // within the 8 px tolerance of the outline
        Assert.Equal(CursorKind.SelectHit, s.CurrentCursor);
        s.PointerMove(At(s, 150, 150));                       // inside the no-fill shape
        Assert.Equal(CursorKind.SelectHit, s.CurrentCursor);
        s.PointerMove(At(s, 80, 150));                        // 20 px away: empty canvas
        Assert.Equal(CursorKind.Arrow, s.CurrentCursor);
        s.PointerMove(At(s, 600, 600));
        Assert.Equal(CursorKind.Arrow, s.CurrentCursor);
    }

    [Fact]
    public void The_hover_cursor_follows_the_zoom_because_the_tolerance_is_in_screen_pixels()
    {
        var s = Session(4);                                   // box edge at screen x = 400
        s.PointerMove(At(s, 393, 600));                       // 7 px left of the left edge, level with the box middle
        Assert.Equal(CursorKind.SelectHit, s.CurrentCursor);
        s.PointerMove(At(s, 390, 600));                       // 10 px
        Assert.Equal(CursorKind.Arrow, s.CurrentCursor);
    }

    [Fact]
    public void Handles_and_tools_keep_their_own_cursors_over_the_select_hit_cursor()
    {
        var s = Session(1);
        s.Selection.Set(s.Document.ActiveLayer.Children[0]);
        s.PointerMove(At(s, 100, 100));                       // the top-left scale handle sits on the box corner
        Assert.Equal(CursorKind.SizeNWSE, s.CurrentCursor);
        s.SetTool("rectangle");
        s.PointerMove(At(s, 150, 150));
        Assert.Equal(CursorKind.Cross, s.CurrentCursor);      // other tools are untouched
    }

    [Fact]
    public void Clicking_empty_canvas_deselects_and_a_drag_from_empty_canvas_still_rubber_bands()
    {
        var s = Session(1);
        var box = s.Document.ActiveLayer.Children[0];
        Click(s, 150, 150);                                  // empty middle of the unfilled box
        Assert.Equal([box], s.Selection.Items);
        Click(s, 600, 600);
        Assert.True(s.Selection.IsEmpty);

        s.PointerDown(At(s, 50, 50, PointerButton.Left));
        s.PointerMove(At(s, 150, 150));
        s.PointerMove(At(s, 300, 300));
        s.PointerUp(At(s, 300, 300, PointerButton.Left));
        Assert.Equal([box], s.Selection.Items);              // the box was fully caught by the band
    }

    [Fact]
    public void Clicking_a_member_of_a_rubber_band_group_selects_the_whole_group()
    {
        var s = Session(1);
        var other = Rect(400, 100, 100, 100, ObjectStyle.None);
        s.Document.Insert(s.Document.ActiveLayer, -1, other);
        s.PointerDown(At(s, 50, 50, PointerButton.Left));
        s.PointerMove(At(s, 300, 300));
        s.PointerMove(At(s, 600, 300));
        s.PointerUp(At(s, 600, 300, PointerButton.Left));
        var group = Assert.IsType<GroupObject>(Assert.Single(s.Selection.Items));
        Click(s, 700, 700);
        Assert.True(s.Selection.IsEmpty);
        Click(s, 450, 150);                                  // the empty middle of one member
        Assert.Equal([group], s.Selection.Items);
    }
}
