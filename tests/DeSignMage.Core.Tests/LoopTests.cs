using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Arrange ▸ Break Into Loops: a graphic with several loops becomes one object per loop.</summary>
public class LoopTests
{
    private readonly EditorSession _s = new(NewDoc());
    private Document Doc => _s.Document;
    private Layer L => _s.Document.ActiveLayer;

    private static PathGeometry Square(double x, double y, double size, bool clockwise = true)
    {
        var b = new PathBuilder().MoveTo(x, y);
        if (clockwise) b.LineTo(x + size, y).LineTo(x + size, y + size).LineTo(x, y + size);
        else b.LineTo(x, y + size).LineTo(x + size, y + size).LineTo(x + size, y);
        return b.Close().Build();
    }

    /// <summary>Several loops in one path, like a paw print: a pad and some toes.</summary>
    private static PathGeometry Loops(params PathGeometry[] parts)
    {
        var verbs = new List<PathVerb>();
        var points = new List<Vec2>();
        foreach (var g in parts)
        {
            verbs.AddRange(g.Verbs);
            points.AddRange(g.Points);
        }
        return new PathGeometry(verbs.ToArray(), points.ToArray());
    }

    private PathObject AddPath(PathGeometry g, string color = "#FFBC11", Affine2? transform = null)
    {
        var p = new PathObject(g) { Style = ObjectStyle.FillOnly(DocColor.FromHex(color)), Transform = transform ?? Affine2.Identity, Name = "Loop 1" };
        _s.History.Execute(new AddObjectCommand(L, p));
        return p;
    }

    private RectangleObject AddRect(double x)
    {
        var r = Rect(x, 0, 10, 10, ObjectStyle.FillOnly(DocColor.Black));
        _s.History.Execute(new AddObjectCommand(L, r));
        return r;
    }

    // ---- The command ----

    [Fact]
    public void A_path_with_three_loops_becomes_three_objects_in_its_place()
    {
        var below = AddRect(0);
        var paw = AddPath(Loops(Square(0, 0, 10), Square(20, 0, 10), Square(40, 0, 10)), transform: Affine2.Translation(100, 50));
        var above = AddRect(200);
        var before = paw.GetWorldBounds();

        var cmd = new BreakIntoLoopsCommand(paw);
        _s.History.Execute(cmd);

        Assert.Equal(3, cmd.Pieces.Count);
        Assert.Equal([below, .. cmd.Pieces, above], L.Children);              // same place in the stack
        Assert.False(Doc.Contains(paw));
        Assert.All(cmd.Pieces, p => Assert.True(Doc.Contains(p)));
        Assert.All(cmd.Pieces, p => Assert.IsType<PathObject>(p));
        Assert.Equal(["Loop 1", "Loop 2", "Loop 3"], cmd.Pieces.Select(p => p.Name));

        // Together they cover exactly what the paw did, and each keeps the color and the transform.
        var union = cmd.Pieces.Aggregate(Rect2.Empty, (r, p) => r.Union(p.GetWorldBounds()));
        Assert.Equal(before, union);
        Assert.All(cmd.Pieces, p =>
        {
            Assert.Equal(paw.Style, p.Style);
            Assert.Equal(paw.Transform, p.Transform);
            Assert.Single(PathEditing.ToLoops(((PathObject)p).Geometry));
        });
        Assert.Equal([100, 120, 140], cmd.Pieces.Select(p => p.GetWorldBounds().Left));
    }

    [Fact]
    public void Undo_puts_the_original_back_where_it_was_and_redo_splits_it_again()
    {
        var below = AddRect(0);
        var paw = AddPath(Loops(Square(0, 0, 10), Square(20, 0, 10)));
        var above = AddRect(200);
        var cmd = new BreakIntoLoopsCommand(paw);
        _s.History.Execute(cmd);
        var ids = cmd.Pieces.Select(p => p.Id).ToList();

        _s.History.Undo();
        Assert.Equal([below, paw, above], L.Children);
        Assert.True(Doc.Contains(paw));
        Assert.All(cmd.Pieces, p => Assert.False(Doc.Contains(p)));

        _s.History.Redo();
        Assert.Equal([below, .. cmd.Pieces, above], L.Children);
        Assert.Equal(ids, cmd.Pieces.Select(p => p.Id));                      // the same objects, same ids
        Assert.Equal(Doc.AllObjects().Count(), Doc.ObjectCount);
    }

    [Fact]
    public void Curves_survive_the_split_unchanged()
    {
        var loop1 = new PathBuilder().MoveTo(0, 0).CubicTo(0, -10, 20, -10, 20, 0).ArcTo(new Vec2(10, 12), new Vec2(0, 0)).Close().Build();
        var loop2 = new PathBuilder().MoveTo(50, 0).CubicTo(50, -10, 70, -10, 70, 0).LineTo(50, 0).Close().Build();
        var paw = AddPath(Loops(loop1, loop2));

        var cmd = new BreakIntoLoopsCommand(paw);
        _s.History.Execute(cmd);

        var first = ((PathObject)cmd.Pieces[0]).Geometry;
        Assert.Contains(PathVerb.CubicTo, first.Verbs);
        Assert.Contains(PathVerb.ArcTo, first.Verbs);                          // arcs stay arcs, not chords
        Assert.DoesNotContain(PathVerb.LineTo, first.Verbs.Where((v, i) => i < 3));
        Assert.Contains(PathVerb.CubicTo, ((PathObject)cmd.Pieces[1]).Geometry.Verbs);
    }

    [Fact]
    public void A_hole_becomes_a_separate_filled_shape()
    {
        // Outer clockwise square with a counter-clockwise hole: holes are loops too.
        var ring = AddPath(Loops(Square(0, 0, 100), Square(25, 25, 50, clockwise: false)));
        Assert.False(ring.Geometry.Contains(new Vec2(50, 50)));               // a hole before

        var cmd = new BreakIntoLoopsCommand(ring);
        _s.History.Execute(cmd);

        Assert.Equal(2, cmd.Pieces.Count);
        Assert.All(cmd.Pieces, p => Assert.IsType<PathObject>(p));
        Assert.True(((PathObject)cmd.Pieces[1]).Geometry.Contains(new Vec2(50, 50)));   // the former hole is now solid
    }

    [Fact]
    public void Open_loops_are_loops_too()
    {
        var open = new PathBuilder().MoveTo(0, 0).LineTo(10, 0).LineTo(10, 10).Build();
        var path = AddPath(Loops(open, Square(30, 0, 10)));

        var cmd = new BreakIntoLoopsCommand(path);
        _s.History.Execute(cmd);

        Assert.Equal(2, cmd.Pieces.Count);
        Assert.False(PathEditing.ToLoops(((PathObject)cmd.Pieces[0]).Geometry).Single().Closed);
        Assert.True(PathEditing.ToLoops(((PathObject)cmd.Pieces[1]).Geometry).Single().Closed);
    }

    [Fact]
    public void Single_loop_shapes_and_other_objects_are_not_eligible()
    {
        var one = AddPath(Square(0, 0, 10));
        var rect = AddRect(50);
        var ellipse = new EllipseObject(10, 10);
        var group = _s.GroupObjects([one, rect])!;

        Assert.False(BreakIntoLoopsCommand.CanBreak(one));
        Assert.False(BreakIntoLoopsCommand.CanBreak(rect));
        Assert.False(BreakIntoLoopsCommand.CanBreak(ellipse));
        Assert.False(BreakIntoLoopsCommand.CanBreak(group));
        Assert.False(BreakIntoLoopsCommand.CanBreak(new TextObject("hello", "Arial", 12)));
        Assert.Throws<ArgumentException>(() => new BreakIntoLoopsCommand(one));
    }

    // ---- Combined objects ----

    [Fact]
    public void A_combined_object_breaks_into_the_loops_of_all_its_parts_keeping_each_parts_style()
    {
        var a = AddPath(Loops(Square(0, 0, 10), Square(20, 0, 10)), "#FF0000");
        var b = AddRect(100);
        b.Style = ObjectStyle.FillOnly(DocColor.FromHex("#0000FF"));
        var combine = new CombineCommand(Doc, [a, b]);
        _s.History.Execute(combine);
        var combinedBounds = combine.Combined.GetWorldBounds();

        var cmd = new BreakIntoLoopsCommand(combine.Combined);
        _s.History.Execute(cmd);

        Assert.Equal(3, cmd.Pieces.Count);                                      // two loops of the path + the rectangle
        Assert.Equal([DocColor.FromHex("#FF0000"), DocColor.FromHex("#FF0000"), DocColor.FromHex("#0000FF")],
            cmd.Pieces.Select(p => ((SolidPaint)p.Style.Fill!).Color));
        Assert.Equal(combinedBounds, cmd.Pieces.Aggregate(Rect2.Empty, (r, p) => r.Union(p.GetWorldBounds())));

        _s.History.Undo();
        Assert.Equal([combine.Combined], L.Children);
    }

    [Fact]
    public void Text_inside_a_combined_object_is_kept_whole()
    {
        var path = AddPath(Loops(Square(0, 0, 10), Square(20, 0, 10)));
        var text = new TextObject("hi", "Arial", 12) { Transform = Affine2.Translation(100, 100) };
        _s.History.Execute(new AddObjectCommand(L, text));
        var combine = new CombineCommand(Doc, [path, text]);
        _s.History.Execute(combine);

        var cmd = new BreakIntoLoopsCommand(combine.Combined);
        _s.History.Execute(cmd);

        Assert.Equal(3, cmd.Pieces.Count);
        var kept = Assert.IsType<TextObject>(cmd.Pieces[2]);
        Assert.Equal("hi", kept.Text);
        Assert.Equal(Affine2.Translation(100, 100), kept.Transform);
    }

    // ---- The session command (what the menu runs) ----

    [Fact]
    public void Session_breaks_every_eligible_selected_object_in_one_undo_step_and_selects_the_loops()
    {
        var paw = AddPath(Loops(Square(0, 0, 10), Square(20, 0, 10), Square(40, 0, 10)));
        var logo = AddPath(Loops(Square(0, 50, 10), Square(20, 50, 10)));
        var plain = AddRect(300);
        _s.Selection.Set([paw, logo, plain]);
        Assert.True(_s.CanBreakIntoLoops);
        int undoSteps = _s.History.UndoList.Count;

        _s.BreakIntoLoopsSelection();

        Assert.Equal(undoSteps + 1, _s.History.UndoList.Count);                 // one step for the whole selection
        Assert.Equal("Break Into Loops", _s.History.UndoDescription);
        Assert.Equal(6, L.Children.Count);                                      // 3 + 2 loops + the untouched rectangle
        Assert.Empty(_s.Selection.Items);                                       // splitting deselects everything, like Ungroup

        _s.Undo();
        Assert.Equal([paw, logo, plain], L.Children);
    }

    [Fact]
    public void Break_Apart_K_splits_a_multi_loop_shape_that_is_not_combined()
    {
        // An imported paw print is one path with pad + toe loops, not a CombinedObject; K must still split it.
        var paw = AddPath(Loops(Square(0, 0, 10), Square(20, 0, 10), Square(40, 0, 10), Square(60, 0, 10), Square(80, 0, 10)));
        _s.Selection.Set(paw);
        Assert.True(_s.CanBreakApart);

        _s.BreakApartSelection();

        Assert.Equal(5, L.Children.Count);
        Assert.Empty(_s.Selection.Items);                                       // K deselects everything, like U
        Assert.Equal("Break Apart", _s.History.UndoDescription);
        _s.Undo();
        Assert.Equal([paw], L.Children);
    }

    [Fact]
    public void Session_does_nothing_without_an_eligible_selection()
    {
        var rect = AddRect(0);
        _s.Selection.Set(rect);
        int steps = _s.History.UndoList.Count;

        Assert.False(_s.CanBreakIntoLoops);
        _s.BreakIntoLoopsSelection();

        Assert.Equal(steps, _s.History.UndoList.Count);
        Assert.Equal([rect], L.Children);
    }
}
