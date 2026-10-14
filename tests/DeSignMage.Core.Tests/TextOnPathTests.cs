using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Text on a path (TASKS C.28): the flow geometry, the options, and that the text stays live.</summary>
public class TextOnPathFlowTests
{
    private static PathGeometry Line(double x0, double y0, double x1, double y1) => new PathBuilder().MoveTo(x0, y0).LineTo(x1, y1).Build();

    private static TextObject Text(string s = "HELLO", double size = 100) => new(s, "Arial", size) { Style = ObjectStyle.FillOnly(DocColor.Black) };

    private static TextObject OnPath(TextOnPath p, string s = "HELLO", double size = 100)
    {
        var t = Text(s, size);
        t.OnPath = p;
        return t;
    }

    /// <summary>The point of the straight text that sits on the path for a letter: the middle of its advance box, on the baseline.</summary>
    private static Vec2 Anchor(TextObject t, int letter)
    {
        var l = t.Layout.Letters[letter];
        return t.Layout.Place(letter, new Vec2(l.X + l.Advance / 2, 0));
    }

    private static Vec2 Heading(TextObject t, int letter)
    {
        var l = t.Layout.Letters[letter];
        var a = t.Layout.Place(letter, new Vec2(l.X + l.Advance / 2, 0));
        var b = t.Layout.Place(letter, new Vec2(l.X + l.Advance / 2 + 1, 0));
        return b - a;
    }

    [Fact]
    public void Natural_spacing_on_a_straight_horizontal_line_changes_nothing()
    {
        var straight = Text();
        var onPath = OnPath(new TextOnPath(Line(0, 0, 2000, 0)) { Spacing = TextPathSpacing.Natural });
        Assert.True(onPath.IsOnPath);
        Assert.True(onPath.Layout.IsOnPath);
        var a = straight.Layout.Outline.GetBounds();
        var b = onPath.Layout.Outline.GetBounds();
        Assert.Equal(a.Left, b.Left, 2);
        Assert.Equal(a.Right, b.Right, 2);
        Assert.Equal(a.Top, b.Top, 2);
        Assert.Equal(a.Bottom, b.Bottom, 2);
        Assert.Equal(straight.Layout.Letters.Count, onPath.Layout.Letters.Count);
    }

    [Fact]
    public void Fit_spreads_the_first_to_the_last_letter_over_the_whole_path()
    {
        var t = OnPath(new TextOnPath(Line(0, 0, 1000, 0)) { Spacing = TextPathSpacing.Fit });
        Assert.Equal(0, Anchor(t, 0).X, 3);
        Assert.Equal(1000, Anchor(t, 4).X, 3);
        Assert.All(Enumerable.Range(0, 5), i => Assert.Equal(0, Anchor(t, i).Y, 3));
        // The gaps are stretched in proportion: the middle letter sits in the middle of the straight text's own spread.
        var straight = Text();
        double s0 = straight.Layout.Letters[0].X + straight.Layout.Letters[0].Advance / 2, s4 = straight.Layout.Letters[4].X + straight.Layout.Letters[4].Advance / 2;
        double s2 = straight.Layout.Letters[2].X + straight.Layout.Letters[2].Advance / 2;
        Assert.Equal(1000 * (s2 - s0) / (s4 - s0), Anchor(t, 2).X, 2);
        // Glyph sizes are unchanged: each letter is only moved.
        Assert.Equal(straight.Layout.Letters[1].Outline.GetBounds().Width, t.Layout.Letters[1].Outline.GetBounds().Width, 3);
    }

    [Fact]
    public void Letters_follow_a_vertical_path_downward_rotated_a_quarter_turn()
    {
        var t = OnPath(new TextOnPath(Line(300, 0, 300, 1000)) { Spacing = TextPathSpacing.Natural });
        for (int i = 0; i < 5; i++)
        {
            var a = Anchor(t, i);
            Assert.Equal(300, a.X, 3);
            var h = Heading(t, i);
            Assert.Equal(0, h.X, 6);
            Assert.Equal(1, h.Y, 6);   // reading direction = the path's direction
        }
        Assert.True(Anchor(t, 4).Y > Anchor(t, 0).Y + 200);
        // Standing on the path: the letters' tops point to the path's left, which for a path running down the page is the page's right-hand side... in y-down terms, +x here.
        var up = t.Layout.Place(0, new Vec2(0, -100)) - t.Layout.Place(0, Vec2.Zero);
        Assert.True(up.X > 90 || up.X < -90);
        Assert.Equal(0, up.Y, 3);
    }

    [Fact]
    public void Letters_sit_on_a_circular_arc_with_their_heading_along_the_tangent()
    {
        var center = new Vec2(0, 0);
        const double r = 400;
        var arc = new PathBuilder().MoveTo(r, 0).ArcTo(new Vec2(r * Math.Cos(Math.PI / 4), r * Math.Sin(Math.PI / 4)), new Vec2(0, r)).Build();   // a quarter circle, clockwise on screen
        var t = OnPath(new TextOnPath(arc) { Spacing = TextPathSpacing.Fit, CurveCompensation = 0 });
        Assert.True(new TextOnPath(arc).PathIsSingleArc);
        for (int i = 0; i < 5; i++)
        {
            var a = Anchor(t, i);
            Assert.True(Math.Abs(Vec2.Distance(center, a) - r) < 0.2);   // on the circle (a quarter arc becomes one cubic: that is exact to 0.03 %)
            var radial = (a - center) / r;
            var heading = Heading(t, i);
            Assert.True(Math.Abs(Vec2.Dot(radial, heading)) < 0.02);   // perpendicular to the radius = along the tangent (to a third of a degree)
        }
        // First and last letter at the two ends of the arc.
        Assert.True(Vec2.Distance(Anchor(t, 0), new Vec2(r, 0)) < 1);
        Assert.True(Vec2.Distance(Anchor(t, 4), new Vec2(0, r)) < 1);
    }

    [Fact]
    public void Letters_sit_on_a_bezier_curve()
    {
        var curve = new PathBuilder().MoveTo(0, 0).CubicTo(300, -400, 700, 400, 1000, 0).Build();
        var t = OnPath(new TextOnPath(curve) { CurveCompensation = 0 }, "WAVES ON A CURVE");
        for (int i = 0; i < t.Layout.Letters.Count; i++)
            Assert.True(curve.DistanceToOutline(Anchor(t, i), 0.01) < 0.1, $"letter {i}");
        // The heading follows the curve: it changes smoothly from letter to letter.
        double previous = Math.Atan2(Heading(t, 0).Y, Heading(t, 0).X);
        for (int i = 1; i < t.Layout.Letters.Count; i++)
        {
            double angle = Math.Atan2(Heading(t, i).Y, Heading(t, i).X);
            Assert.True(Math.Abs(angle - previous) < 0.6, $"letter {i} turned by {angle - previous}");
            previous = angle;
        }
    }

    [Fact]
    public void Natural_spacing_starts_where_the_alignment_says_and_the_offset_moves_it()
    {
        var left = OnPath(new TextOnPath(Line(0, 0, 2000, 0)) { Spacing = TextPathSpacing.Natural });
        var center = OnPath(new TextOnPath(Line(0, 0, 2000, 0)) { Spacing = TextPathSpacing.Natural });
        center.Alignment = TextAlignment.Center;
        var right = OnPath(new TextOnPath(Line(0, 0, 2000, 0)) { Spacing = TextPathSpacing.Natural });
        right.Alignment = TextAlignment.Right;
        double w = Text().Layout.Frame.Width;
        Assert.Equal(0, left.Layout.Frame.Left, 0);
        Assert.Equal(1000 - w / 2, center.Layout.Frame.Left, 0);
        Assert.Equal(2000 - w, right.Layout.Frame.Left, 0);

        var shifted = OnPath(new TextOnPath(Line(0, 0, 2000, 0)) { Spacing = TextPathSpacing.Natural, StartOffset = 150 });
        Assert.Equal(150, Anchor(shifted, 0).X - Anchor(left, 0).X, 3);
    }

    [Fact]
    public void Side_below_raise_and_flip()
    {
        var path = Line(0, 0, 2000, 0);
        var above = OnPath(new TextOnPath(path) { Spacing = TextPathSpacing.Natural });
        var below = OnPath(new TextOnPath(path) { Spacing = TextPathSpacing.Natural, Side = TextPathSide.Below });
        var raised = OnPath(new TextOnPath(path) { Spacing = TextPathSpacing.Natural, Raise = 40 });
        Assert.True(above.GetWorldBounds().Bottom <= 3);                 // standing on the path (round letters overshoot the baseline a little)
        Assert.True(below.GetWorldBounds().Top >= -1);                   // hanging from it
        Assert.Equal(above.GetWorldBounds().Top - 40, raised.GetWorldBounds().Top, 2);   // lifted off it by 40

        var flipped = OnPath(new TextOnPath(path) { Spacing = TextPathSpacing.Fit, Flip = true });
        Assert.Equal(2000, Anchor(flipped, 0).X, 3);                     // the text starts at the path's far end...
        Assert.Equal(0, Anchor(flipped, 4).X, 3);                        // ...and runs back to its beginning
        var h = Heading(flipped, 0);
        Assert.Equal(-1, h.X, 6);                                        // reading right to left
        Assert.Equal(0, h.Y, 6);
    }

    [Fact]
    public void Curve_compensation_pushes_letters_forward_past_a_bend_toward_the_tops_and_back_past_a_bend_away_from_them()
    {
        // 500 straight, then a 20 degree bend. Toward the tops = upward on the page for a path running right.
        double a = 20 * Math.PI / 180;
        var towardTops = new PathBuilder().MoveTo(0, 0).LineTo(500, 0).LineTo(500 + 500 * Math.Cos(a), -500 * Math.Sin(a)).Build();
        var awayFromTops = new PathBuilder().MoveTo(0, 0).LineTo(500, 0).LineTo(500 + 500 * Math.Cos(a), 500 * Math.Sin(a)).Build();
        double pathLength = 1000;
        double shift = TextPathPlacer.BendFactor * pathLength * Math.Tan(a) * 0.5;   // the spec's factor · Lp · tan(turn) · c with c = 0.5

        foreach (var (path, sign) in new[] { (towardTops, 1.0), (awayFromTops, -1.0) })
        {
            var plain = OnPath(new TextOnPath(path) { Spacing = TextPathSpacing.Natural, CurveCompensation = 0 }, "MMMMMMMMMMMMMMMM", 100);
            var comp = OnPath(new TextOnPath(path) { Spacing = TextPathSpacing.Natural, CurveCompensation = 0.5 }, "MMMMMMMMMMMMMMMM", 100);
            // A letter well past the bend: its distance along the second segment grows by the shift (a letter before the bend does not move).
            var last = plain.Layout.Letters.Count - 1;
            Assert.True(Vec2.Distance(Anchor(plain, 0), Anchor(comp, 0)) < 1e-6);
            var p = Anchor(plain, last);
            var q = Anchor(comp, last);
            var dir = path.Points[2] - path.Points[1];
            dir /= dir.Length;
            Assert.Equal(sign * shift, Vec2.Dot(q - p, dir), 1);
        }
    }

    [Fact]
    public void A_single_arc_gets_no_compensation_and_the_default_is_the_legacy_one()
    {
        var arc = new PathBuilder().MoveTo(0, 0).ArcTo(new Vec2(200, -100), new Vec2(400, 0)).Build();
        var settings = new TextOnPath(arc);
        Assert.True(settings.PathIsSingleArc);
        Assert.Equal(0, settings.EffectiveCompensation);
        Assert.False(new TextOnPath(Line(0, 0, 100, 0)).PathIsSingleArc);
        Assert.Equal(0.5, TextOnPath.DefaultCompensation);

        var t = Text("Default path", 80);
        var def = TextOnPath.DefaultFor(t);
        var loops = PathEditing.ToLoops(def.Path);
        var l = Assert.Single(loops);
        Assert.False(l.Closed);
        var s = Assert.Single(l.Segments);
        Assert.Equal(SegmentKind.Cubic, s.Kind);
        double chord = t.Layout.Frame.Width;
        Assert.Equal(chord, s.End.X - l.Start.X, 3);
        Assert.Equal(-0.3 * chord, s.C1.Y, 3);       // the control points lie 0.3 x the chord above (first) and below (second) the baseline: a gentle S that rises first
        Assert.Equal(0.3 * chord, s.C2.Y, 3);
        Assert.Equal(l.Start.X + 0.4 * chord, s.C1.X, 3);
        Assert.Equal(l.Start.X + 0.6 * chord, s.C2.X, 3);
    }

    [Fact]
    public void Changing_the_text_or_the_font_re_flows_it_and_removing_the_path_gives_the_straight_text_back()
    {
        var t = OnPath(new TextOnPath(Line(0, 0, 1000, 0)) { Spacing = TextPathSpacing.Fit }, "AB");
        double end = Anchor(t, 1).X;
        t.Text = "ABCD";
        Assert.Equal(4, t.Layout.Letters.Count);
        Assert.Equal(1000, Anchor(t, 3).X, 3);          // still fills the path
        Assert.True(Anchor(t, 1).X < end);              // the same letter moved: everything re-flowed
        t.FontSize = 50;
        Assert.Equal(1000, Anchor(t, 3).X, 3);
        Assert.True(t.Layout.Letters[1].Outline.GetBounds().Width < 50);   // smaller glyphs
        t.FontFamily = "Times New Roman";
        Assert.Equal(1000, Anchor(t, 3).X, 3);

        var straight = Text("ABCD", 50);
        straight.FontFamily = "Times New Roman";
        var before = straight.Layout.Outline.GetBounds();
        t.OnPath = null;
        Assert.False(t.Layout.IsOnPath);
        Assert.Equal(before, t.Layout.Outline.GetBounds());
    }

    [Fact]
    public void A_click_between_letters_on_a_curve_picks_the_text_and_one_far_from_it_does_not()
    {
        var doc = NewDoc();
        var curve = new PathBuilder().MoveTo(0, 400).CubicTo(300, 0, 700, 800, 1000, 400).Build();
        var t = OnPath(new TextOnPath(curve) { CurveCompensation = 0 }, "CURVY TEXT");
        doc.Insert(doc.ActiveLayer, -1, t);
        var between = Anchor(t, 3) + new Vec2(0, -20);
        Assert.Same(t, HitTesting.HitTest(doc, between, 1));
        Assert.Null(HitTesting.HitTest(doc, new Vec2(500, 2000), 1));
        Assert.Null(HitTesting.HitTest(doc, new Vec2(-500, 400), 1));
    }

    [Fact]
    public void Convert_to_curves_keeps_the_letters_where_they_flowed()
    {
        var doc = NewDoc();
        var curve = new PathBuilder().MoveTo(0, 400).CubicTo(300, 0, 700, 800, 1000, 400).Build();
        var t = OnPath(new TextOnPath(curve), "Flow");
        var h = new CommandHistory(doc);
        h.Execute(new AddObjectCommand(doc.ActiveLayer, t));
        var before = t.GetWorldBounds();
        var cmd = new ConvertTextToCurvesCommand(doc, [t]);
        h.Execute(cmd);
        var group = Assert.IsType<GroupObject>(Assert.Single(cmd.TopLevelResults));
        Assert.Equal(4, group.Children.Count);
        var after = group.GetWorldBounds();
        Assert.Equal(before.Left, after.Left, 2);
        Assert.Equal(before.Right, after.Right, 2);
        Assert.Equal(before.Top, after.Top, 2);
        Assert.Equal(before.Bottom, after.Bottom, 2);
        h.Undo();
        Assert.True(doc.Contains(t));
        Assert.True(t.IsOnPath);   // undo gives the live text back, still on its path
    }

    [Fact]
    public void Caret_dots_and_the_tracking_handle_follow_the_letters_onto_the_path()
    {
        var t = OnPath(new TextOnPath(Line(0, 0, 1000, 0)) { Spacing = TextPathSpacing.Fit }, "ABCD");
        var layout = t.Layout;
        // The dot between B and C sits on the path where the next letter starts.
        var dots = layout.PlacedGapPoints().ToList();
        Assert.Equal(3, dots.Count);
        Assert.Equal(0, dots[1].Point.Y, 3);
        Assert.True(dots[0].Point.X < dots[1].Point.X && dots[1].Point.X < dots[2].Point.X);
        // The caret is a short vertical line standing on the path at the letter's left edge.
        var (top, bottom) = layout.CaretLine(2);
        Assert.Equal(top.X, bottom.X, 3);
        Assert.True(top.Y < bottom.Y);
        // Clicking near a letter finds its index: before the middle = that letter, after = the next.
        var c = Anchor(t, 2);
        Assert.Equal(2, layout.IndexAt(c + new Vec2(-5, -30)));
        Assert.Equal(3, layout.IndexAt(c + new Vec2(5, -30)));
    }
}

/// <summary>The command, the session entry points and the path editing tool.</summary>
public class TextOnPathEditorTests
{
    private readonly EditorSession _s;

    public TextOnPathEditorTests()
    {
        _s = new EditorSession(NewDoc());
        _s.Viewport.SetViewSize(new Vec2(1400, 900));
        _s.Viewport.Set(1, Vec2.Zero);   // screen == document coordinates
    }

    private PointerInput At(double x, double y, PointerButton b = PointerButton.None, int clicks = 1) =>
        new(new Vec2(x, y), _s.Viewport.ToDoc(new Vec2(x, y)), b, ModifierKeys.None, clicks);

    private TextObject AddText(string text = "SIGN", double size = 100, double x = 100, double y = 400)
    {
        var t = new TextObject(text, "Arial", size) { Transform = Affine2.Translation(x, y) };
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, t));
        return t;
    }

    private PathObject AddLine(double x0, double y0, double x1, double y1)
    {
        var p = new PathObject(new PathBuilder().MoveTo(x0, y0).LineTo(x1, y1).Build()) { Style = new ObjectStyle(null, StrokeStyle.Solid(DocColor.Black, 1)) };
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, p));
        return p;
    }

    private void Drag(Vec2 from, Vec2 to)
    {
        _s.PointerDown(At(from.X, from.Y, PointerButton.Left));
        _s.PointerMove(At((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        _s.PointerMove(At(to.X, to.Y));
        _s.PointerUp(At(to.X, to.Y, PointerButton.Left));
    }

    // ---- Starting it ----

    [Fact]
    public void One_selected_text_gets_the_default_path_in_one_undo_step_and_the_tool_opens()
    {
        var t = AddText();
        _s.Selection.Set(t);
        Assert.True(_s.CanTextOnPath);
        Assert.False(_s.HasTextOnPath);
        int steps = _s.History.UndoList.Count;

        Assert.True(_s.BeginTextOnPath());

        Assert.True(t.IsOnPath);
        Assert.IsType<TextPathTool>(_s.ActiveTool);
        Assert.Same(t, _s.GetTool<TextPathTool>().Target);
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Text on Path", _s.History.UndoDescription);
        Assert.Equal([t], _s.Selection.Items);
        Assert.True(_s.HasTextOnPath);

        _s.Undo();
        Assert.False(t.IsOnPath);
        Assert.IsType<SelectTool>(_s.ActiveTool);   // nothing left to edit: the tool closed itself
        _s.Redo();
        Assert.True(t.IsOnPath);
    }

    [Fact]
    public void A_text_and_a_shape_selected_together_start_the_text_on_the_shape_and_the_shape_stays_an_ordinary_object()
    {
        var t = AddText("FOLLOW", 80, x: 50, y: 50);                     // not at the origin: the path has to be taken into the text's own space
        var line = AddLine(200, 300, 1000, 300);
        _s.Selection.Set([t, line]);
        Assert.True(_s.CanTextOnPath);
        Assert.True(_s.BeginTextOnPath());

        Assert.True(t.IsOnPath);
        for (int i = 0; i < t.Layout.Letters.Count; i++)
        {
            var l = t.Layout.Letters[i];
            var anchor = t.GetWorldTransform().Transform(t.Layout.Place(i, new Vec2(l.X + l.Advance / 2, 0)));
            Assert.Equal(300, anchor.Y, 2);                               // on the line, in the page's coordinates
            Assert.InRange(anchor.X, 199, 1001);
        }
        Assert.True(_s.Document.Contains(line));                          // the line is still there, untouched, not grouped, not linked
        Assert.Same(_s.Document.ActiveLayer, line.Parent);
        Assert.Same(_s.Document.ActiveLayer, t.Parent);
        Assert.DoesNotContain(_s.Document.AllObjects(), o => o is GroupObject);
        Assert.Single(_s.History.UndoList.Skip(2));                       // the two Adds, then ONE step
        line.Transform = Affine2.Translation(0, 200);                     // moving the line later does not move the text (no link)
        Assert.Equal(300, t.GetWorldTransform().Transform(t.Layout.Place(0, new Vec2(t.Layout.Letters[0].X, 0))).Y, 1);
    }

    [Fact]
    public void A_closed_shape_gives_a_looping_path_and_other_selections_are_refused()
    {
        var t = AddText("ROUND AND ROUND", 60);
        var ellipse = new EllipseObject(600, 300) { Transform = Affine2.Translation(300, 300), Style = new ObjectStyle(null, StrokeStyle.Solid(DocColor.Black, 1)) };
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, ellipse));
        _s.Selection.Set([t, ellipse]);
        Assert.True(_s.BeginTextOnPath());
        Assert.True(_s.GetTool<TextPathTool>().PathIsClosed);
        Assert.False(_s.GetTool<TextPathTool>().CanAddCurve);             // a closed path has no end to add to
        _s.SetTool("select");

        var other = AddText("two");
        _s.Selection.Set([t, other]);
        Assert.False(_s.CanTextOnPath);                                   // two texts: no
        _s.Selection.Clear();
        Assert.False(_s.CanTextOnPath);
        _s.Selection.Set(ellipse);
        Assert.False(_s.CanTextOnPath);                                   // a shape alone: no
        var group = new GroupObject();
        _s.Selection.Set([t, group]);
        Assert.False(_s.CanTextOnPath);
    }

    [Fact]
    public void A_text_that_already_follows_a_path_just_opens_it()
    {
        var t = AddText();
        _s.ApplyTextOnPath(t, TextOnPath.DefaultFor(t) with { CurveCompensation = 0.25 });
        _s.Selection.Set(t);
        int steps = _s.History.UndoList.Count;
        Assert.True(_s.BeginTextOnPath());
        Assert.Equal(steps, _s.History.UndoList.Count);                   // nothing changed, nothing recorded
        Assert.Equal(0.25, _s.GetTool<TextPathTool>().Settings!.CurveCompensation);
    }

    // ---- Editing the path ----

    private (TextObject Text, TextPathTool Tool) Open()
    {
        var t = AddText("SIGN", 100, 100, 400);
        _s.Selection.Set(t);
        _s.BeginTextOnPath();
        return (t, _s.GetTool<TextPathTool>());
    }

    private static Vec2 NodeDoc(TextObject t, int k)
    {
        var loop = PathEditing.ToLoops(t.OnPath!.Path)[0];
        return t.GetWorldTransform().Transform(loop.Node(k));
    }

    [Fact]
    public void Dragging_a_node_moves_it_with_live_preview_and_is_one_undo_step()
    {
        var (t, tool) = Open();
        var before = t.OnPath!.Path;
        var layoutBefore = t.Layout.Outline.GetBounds();
        int steps = _s.History.UndoList.Count;
        var from = NodeDoc(t, 1);
        var to = from + new Vec2(0, 120);

        _s.PointerDown(At(from.X, from.Y, PointerButton.Left));
        _s.PointerMove(At(from.X, from.Y + 60));
        _s.PointerMove(At(to.X, to.Y));
        Assert.NotEqual(layoutBefore, t.Layout.Outline.GetBounds());         // the text follows while the node is dragged
        Assert.Equal(steps, _s.History.UndoList.Count);                      // but nothing is recorded yet
        _s.PointerUp(At(to.X, to.Y, PointerButton.Left));

        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Move Path Node", _s.History.UndoDescription);
        Assert.Equal(to.Y, NodeDoc(t, 1).Y, 2);
        _s.Undo();
        Assert.Same(before, t.OnPath!.Path);
        Assert.Equal(layoutBefore, t.Layout.Outline.GetBounds());
        _s.Redo();
        Assert.Equal(to.Y, NodeDoc(t, 1).Y, 2);
        Assert.Same(t, tool.Target);
    }

    [Fact]
    public void Dragging_a_bezier_handle_bends_the_path_and_escape_cancels_a_drag()
    {
        var (t, tool) = Open();
        var loop = PathEditing.ToLoops(t.OnPath!.Path)[0];
        // Select the start node so its handle shows, then drag that handle.
        var start = NodeDoc(t, 0);
        _s.PointerDown(At(start.X, start.Y, PointerButton.Left));
        _s.PointerUp(At(start.X, start.Y, PointerButton.Left));
        Assert.Equal(0, tool.SelectedNode);
        var handle = t.GetWorldTransform().Transform(loop.Segments[0].C1);
        int steps = _s.History.UndoList.Count;
        Drag(handle, handle + new Vec2(40, -200));
        Assert.Equal("Move Path Handle", _s.History.UndoDescription);
        var moved = PathEditing.ToLoops(t.OnPath!.Path)[0].Segments[0].C1;
        Assert.Equal(loop.Segments[0].C1.Y - 200, moved.Y, 2);
        Assert.Equal(steps + 1, _s.History.UndoList.Count);

        var node = NodeDoc(t, 1);
        var path = t.OnPath!.Path;
        _s.PointerDown(At(node.X, node.Y, PointerButton.Left));
        _s.PointerMove(At(node.X + 90, node.Y + 90));
        Assert.True(_s.KeyDown(EditorKey.Escape, ModifierKeys.None));   // Esc during a drag cancels the drag, it does not leave the tool
        Assert.Same(path, t.OnPath!.Path);
        Assert.IsType<TextPathTool>(_s.ActiveTool);
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
    }

    [Fact]
    public void Add_curve_delete_curve_and_the_conversions()
    {
        var (t, tool) = Open();
        Assert.Equal(1, tool.SegmentCount);
        Assert.False(tool.CanDeleteCurve);                      // the path keeps at least one vector
        Assert.True(tool.CanAddCurve);
        var first = PathEditing.ToLoops(t.OnPath!.Path)[0];
        var lastEnd = first.Segments[0].End;

        Assert.True(tool.AddCurve());
        var loop = PathEditing.ToLoops(t.OnPath!.Path)[0];
        Assert.Equal(2, loop.Segments.Count);
        var added = loop.Segments[1];
        Assert.Equal(SegmentKind.Cubic, added.Kind);
        double len = loop.SegmentLength(0);
        Assert.Equal(lastEnd.X + len, added.End.X, 1);            // horizontally by the last vector's length...
        Assert.Equal(loop.Start.Y, added.End.Y, 3);               // ...at the first node's height
        Assert.Equal("Add Curve", _s.History.UndoDescription);
        Assert.True(tool.CanDeleteCurve);

        Assert.True(tool.ToArc());                                // the selected vector (the one just added) becomes an arc
        Assert.Equal(SegmentKind.Arc, PathEditing.ToLoops(t.OnPath!.Path)[0].Segments[1].Kind);
        Assert.False(tool.CanToArc);
        Assert.True(tool.ToBezier());
        Assert.Equal(SegmentKind.Cubic, PathEditing.ToLoops(t.OnPath!.Path)[0].Segments[1].Kind);
        Assert.False(tool.CanToBezier);

        Assert.True(tool.DeleteCurve());
        Assert.Equal(1, tool.SegmentCount);
        Assert.False(tool.DeleteCurve());
        _s.Undo();
        Assert.Equal(2, tool.SegmentCount);
    }

    [Fact]
    public void Settings_changes_are_single_undo_steps_and_re_flow_the_text()
    {
        var (t, tool) = Open();
        var natural = t.Layout.Outline.GetBounds();
        Assert.True(tool.Update(s => s with { Spacing = TextPathSpacing.Natural }, "Spacing"));
        Assert.Equal("Spacing", _s.History.UndoDescription);
        Assert.NotEqual(natural, t.Layout.Outline.GetBounds());
        Assert.False(tool.Update(s => s with { Spacing = TextPathSpacing.Natural }));   // nothing changed: nothing recorded
        _s.Undo();
        Assert.Equal(TextPathSpacing.Fit, t.OnPath!.Spacing);
        Assert.Equal(natural, t.Layout.Outline.GetBounds());
    }

    [Fact]
    public void Remove_takes_the_path_off_closes_the_tool_and_undo_brings_it_back()
    {
        var (t, tool) = Open();
        var straight = new TextObject("SIGN", "Arial", 100).Layout.Outline.GetBounds();
        tool.Remove();
        Assert.False(t.IsOnPath);
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.Equal([t], _s.Selection.Items);
        Assert.Equal("Remove Text on Path", _s.History.UndoDescription);
        Assert.Equal(straight.Width, t.Layout.Outline.GetBounds().Width, 2);
        _s.Undo();
        Assert.True(t.IsOnPath);
    }

    [Fact]
    public void Enter_and_escape_leave_the_tool_with_the_text_still_selected_and_on_its_path()
    {
        var (t, _) = Open();
        Assert.True(_s.KeyDown(EditorKey.Enter, ModifierKeys.None));
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.True(t.IsOnPath);
        Assert.Equal([t], _s.Selection.Items);
        Assert.True(_s.BeginTextOnPath());
        Assert.True(_s.KeyDown(EditorKey.Escape, ModifierKeys.None));
        Assert.IsType<SelectTool>(_s.ActiveTool);
    }

    [Fact]
    public void The_overlay_shows_the_path_its_nodes_and_the_selected_nodes_handles()
    {
        var (t, tool) = Open();
        var overlay = _s.BuildOverlay().OfType<VectorEditOverlay>().Single();
        Assert.Single(overlay.Loops);
        Assert.Equal(2, overlay.Nodes.Count);
        Assert.NotEmpty(overlay.Handles);                         // the single Bezier vector shows its two handles
        var n = NodeDoc(t, 0);
        _s.PointerDown(At(n.X, n.Y, PointerButton.Left));
        _s.PointerUp(At(n.X, n.Y, PointerButton.Left));
        Assert.Contains(_s.BuildOverlay().OfType<VectorEditOverlay>().Single().Nodes, m => m.Selected);
    }

    // ---- Staying live ----

    [Fact]
    public void Typing_and_changing_the_font_re_flow_the_text_on_its_path()
    {
        var t = AddText("AB", 100, 100, 400);
        _s.ApplyTextOnPath(t, TextOnPath.DefaultFor(t) with { Spacing = TextPathSpacing.Fit, CurveCompensation = 0 });
        var path = t.OnPath!.Path;
        _s.EditText(t);
        _s.TextInput("CD");
        _s.KeyDown(EditorKey.Escape, ModifierKeys.None);
        Assert.Equal("ABCD", t.Text);
        Assert.True(t.IsOnPath);
        Assert.Same(path, t.OnPath!.Path);
        var last = t.Layout.Letters[3];
        var end = t.GetWorldTransform().Transform(t.Layout.Place(3, new Vec2(last.X + last.Advance / 2, 0)));
        var pathEnd = t.GetWorldTransform().Transform(PathEditing.ToLoops(path)[0].Segments[0].End);
        Assert.True(Vec2.Distance(end, pathEnd) < 1);             // four letters now fill the path

        _s.Selection.Set(t);
        _s.ApplyTextFormat(new TextFormatChange(Family: "Times New Roman", Size: 60));
        Assert.True(t.IsOnPath);
        var after = t.Layout.Letters[3];
        Assert.True(Vec2.Distance(t.GetWorldTransform().Transform(t.Layout.Place(3, new Vec2(after.X + after.Advance / 2, 0))), pathEnd) < 1);   // still on the same path, still filling it
    }

    [Fact]
    public void Edit_mode_shows_the_caret_on_the_path()
    {
        var t = AddText("ABC", 100, 100, 400);
        _s.ApplyTextOnPath(t, TextOnPath.DefaultFor(t));
        _s.EditText(t);
        _s.GetTool<TextTool>().MoveCaret(1, false);
        var caret = _s.BuildOverlay().OfType<TextEditOverlay>().Single();
        var (top, bottom) = t.Layout.CaretLine(1);
        var w = t.GetWorldTransform();
        Assert.Equal(w.Transform(top), caret.CaretTop);
        Assert.Equal(w.Transform(bottom), caret.CaretBottom);
        _s.GetTool<TextTool>().MoveCaret(0, false);
        _s.GetTool<TextTool>().MoveCaret(2, true);
        Assert.Equal(2, _s.BuildOverlay().OfType<TextEditOverlay>().Single().Highlights.Count);   // one tilted box per selected letter
    }

    [Fact]
    public void Letter_dots_and_the_tracking_handle_work_on_text_on_a_path()
    {
        var t = AddText("ABCD", 80, 100, 400);
        _s.ApplyTextOnPath(t, TextOnPath.DefaultFor(t) with { Spacing = TextPathSpacing.Natural, CurveCompensation = 0 });
        _s.Selection.Set(t);
        var dots = _s.BuildOverlay().OfType<LetterDotsOverlay>().Single().Dots;
        Assert.Equal(3, dots.Count);
        var onPath = t.Layout.PlacedGapPoints().Select(g => t.GetWorldTransform().Transform(g.Point)).ToArray();
        Assert.Equal(onPath, dots);                                // the dots sit where the letters went, not on the straight text

        var grab = dots[1];
        _s.PointerDown(At(grab.X, grab.Y, PointerButton.Left));
        _s.PointerMove(At(grab.X + 15, grab.Y));
        _s.PointerMove(At(grab.X + 30, grab.Y));
        _s.PointerUp(At(grab.X + 30, grab.Y, PointerButton.Left));
        Assert.Equal("Letter Spacing", _s.History.UndoDescription);
        Assert.True(t.KerningAt(1) > 0);                            // dragged along the text, so the gap after B grew
        _s.Undo();
        Assert.Empty(t.Kerning);

        var handle = _s.BuildOverlay().OfType<TrackingHandleOverlay>().Single().Position;
        _s.PointerDown(At(handle.X, handle.Y, PointerButton.Left));
        _s.PointerMove(At(handle.X + 20, handle.Y));
        _s.PointerUp(At(handle.X + 20, handle.Y, PointerButton.Left));
        Assert.Equal("Tracking", _s.History.UndoDescription);
        Assert.NotEqual(0, t.Tracking);
    }

    [Fact]
    public void Text_on_a_path_can_be_copied_and_still_follows_its_path()
    {
        var t = AddText("COPY");
        _s.ApplyTextOnPath(t, TextOnPath.DefaultFor(t) with { Raise = 12, Flip = true, Side = TextPathSide.Below, StartOffset = 7, CurveCompensation = 0.3, Spacing = TextPathSpacing.Natural });
        var copy = Assert.IsType<TextObject>(t.Clone());
        Assert.Equal(t.OnPath, copy.OnPath);
        Assert.Equal(t.Layout.Outline.GetBounds(), copy.Layout.Outline.GetBounds());
    }
}
