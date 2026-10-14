using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Geometry;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Effects ▸ Outline, Inline, Weld, Common Weld, Cookie Cutter, Knife (effects step 2; requirement R13).</summary>
public class EffectCommandTests
{
    private readonly EditorSession _s;
    private Document Doc => _s.Document;
    private Layer L => _s.Document.ActiveLayer;
    private static readonly ObjectStyle Red = ObjectStyle.FillOnly(DocColor.FromHex("#CC0000"));
    private static readonly ObjectStyle Blue = ObjectStyle.FillOnly(DocColor.FromHex("#0000CC"));

    public EffectCommandTests()
    {
        _s = new EditorSession(NewDoc())
        {
            BooleanService = GeometryEngines.Booleans,
            OffsetService = GeometryEngines.Offsets,
        };
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);
    }

    private RectangleObject AddRect(double x, double y, double w, double h, ObjectStyle? style = null)
    {
        var r = Rect(x, y, w, h, style);
        _s.History.Execute(new AddObjectCommand(L, r));
        return r;
    }

    private EllipseObject AddCircle(double cx, double cy, double radius, ObjectStyle? style = null)
    {
        var c = new EllipseObject(radius * 2, radius * 2) { Transform = Affine2.Translation(cx - radius, cy - radius), Style = style ?? Red };
        _s.History.Execute(new AddObjectCommand(L, c));
        return c;
    }

    private T Add<T>(T o) where T : DocObject
    {
        _s.History.Execute(new AddObjectCommand(L, o));
        return o;
    }

    private static PointerInput At(double x, double y, PointerButton b = PointerButton.None, ModifierKeys m = ModifierKeys.None) =>
        new(new Vec2(x, y), new Vec2(x, y), b, m);

    private int UndoSteps => _s.History.UndoList.Count;

    private static Rect2 Bounds(DocObject o) => o.GetWorldBounds();

    private static void AssertRect(Rect2 expected, Rect2 actual, double tol = 0.05)
    {
        Assert.InRange(actual.Left, expected.Left - tol, expected.Left + tol);
        Assert.InRange(actual.Top, expected.Top - tol, expected.Top + tol);
        Assert.InRange(actual.Right, expected.Right - tol, expected.Right + tol);
        Assert.InRange(actual.Bottom, expected.Bottom - tol, expected.Bottom + tol);
    }

    // ---- Outline / Inline ----

    [Fact]
    public void Outline_makes_a_standalone_object_just_below_the_source()
    {
        var a = AddRect(200, 200, 100, 100, Blue);
        var other = AddRect(0, 0, 10, 10);
        _s.Selection.Set(a);
        int steps = UndoSteps;
        var outcome = _s.RunEffect(OutlineEffect.Plan(_s, OutlineKind.Outline, new OutlineSettings { First = 7.2 })!);

        Assert.True(outcome.Applied);
        Assert.Equal(steps + 1, UndoSteps);
        Assert.Equal(3, L.Children.Count);
        var result = Assert.IsType<PathObject>(L.Children[0]);   // directly below `a`, which moved up one
        Assert.Same(a, L.Children[1]);
        Assert.Same(other, L.Children[2]);
        Assert.Equal("Outline", result.Name);
        Assert.Same(L, result.Parent);                            // not grouped with the source (R13)
        Assert.Equal(Blue, result.Style);
        AssertRect(new Rect2(192.8, 192.8, 307.2, 307.2), Bounds(result));
        Assert.Equal([result], _s.Selection.Items);

        _s.History.Undo();
        Assert.Equal([a, other], L.Children);
        Assert.Equal(new Rect2(200, 200, 300, 300), Bounds(a));
    }

    [Fact]
    public void Inline_goes_just_above_the_source_and_shrinks_it()
    {
        var a = AddRect(200, 200, 100, 100);
        _s.Selection.Set(a);
        _s.RunEffect(OutlineEffect.Plan(_s, OutlineKind.Inline, new OutlineSettings { First = 7.2 })!);

        Assert.Equal(2, L.Children.Count);
        Assert.Same(a, L.Children[0]);
        var result = Assert.IsType<PathObject>(L.Children[1]);
        Assert.Equal("Inline", result.Name);
        AssertRect(new Rect2(207.2, 207.2, 292.8, 292.8), Bounds(result));
    }

    [Fact]
    public void An_inline_deeper_than_the_shape_leaves_nothing_and_changes_nothing()
    {
        var a = AddRect(200, 200, 10, 10);
        _s.Selection.Set(a);
        int steps = UndoSteps;
        var outcome = _s.RunEffect(OutlineEffect.Plan(_s, OutlineKind.Inline, new OutlineSettings { First = 20 })!);
        Assert.False(outcome.Applied);
        Assert.Equal(steps, UndoSteps);
        Assert.Equal([a], L.Children);
        Assert.NotNull(_s.LastEffectMessage);
    }

    [Fact]
    public void Outline_of_live_text_is_its_own_object_and_the_text_stays_text()
    {
        var text = new TextObject("Hi", "Arial", 100) { Transform = Affine2.Translation(100, 300) };
        Add(text);
        _s.Selection.Set(text);
        var before = Bounds(text);
        _s.RunEffect(OutlineEffect.Plan(_s, OutlineKind.Outline, new OutlineSettings { First = 5 })!);

        Assert.Equal(2, L.Children.Count);
        var kept = Assert.IsType<TextObject>(L.Children[1]);
        Assert.Same(text, kept);
        Assert.Equal("Hi", kept.Text);
        Assert.IsNotType<GroupObject>(kept.Parent);              // R13: the outline is not grouped with the text
        var outline = Assert.IsType<PathObject>(L.Children[0]);
        Assert.Equal("Outline", outline.Name);
        Assert.True(Bounds(outline).Width > before.Width + 9);
        Assert.Equal(before, Bounds(text));                       // the text itself did not change
    }

    [Fact]
    public void Outline_of_an_open_path_is_an_open_parallel_line()
    {
        var line = Add(new PathObject(new PathBuilder().MoveTo(100, 100).LineTo(300, 100).Build())
        {
            Style = new ObjectStyle(null, StrokeStyle.Solid(DocColor.Black, 2)),
        });
        _s.Selection.Set(line);
        _s.RunEffect(OutlineEffect.Plan(_s, OutlineKind.Outline, new OutlineSettings { First = 10 })!);

        var result = Assert.IsType<PathObject>(L.Children[0]);
        var (closed, open) = EffectGeometry.CountLoops(result.Geometry);
        Assert.Equal(0, closed);
        Assert.Equal(1, open);
        var b = Bounds(result);
        Assert.InRange(b.Height, 0, 0.01);                        // a straight line, one side only
        Assert.InRange(b.Top, 89.9, 90.1);                        // positive = the left of the direction of travel
        Assert.InRange(b.Width, 199.9, 200.1);                    // no end caps
        Assert.Same(line, L.Children[1]);                         // the original is untouched

        // A negative distance goes to the other side.
        _s.Selection.Set(line);
        _s.RunEffect(OutlineEffect.Plan(_s, OutlineKind.Outline, new OutlineSettings { First = -10 })!);
        Assert.InRange(Bounds(_s.Selection.Items[0]).Top, 109.9, 110.1);
    }

    [Fact]
    public void Leave_original_off_replaces_the_source_in_place()
    {
        var a = AddRect(200, 200, 100, 100);
        var top = AddRect(0, 0, 10, 10);
        _s.Selection.Set(a);
        _s.RunEffect(OutlineEffect.Plan(_s, OutlineKind.Outline, new OutlineSettings { First = 5, LeaveOriginal = false })!);
        Assert.Equal(2, L.Children.Count);
        Assert.IsType<PathObject>(L.Children[0]);
        Assert.Same(top, L.Children[1]);
        _s.History.Undo();
        Assert.Equal([a, top], L.Children);
    }

    [Fact]
    public void Second_amount_makes_the_band_between_the_two_rings()
    {
        var a = AddRect(200, 200, 100, 100);
        _s.Selection.Set(a);
        _s.RunEffect(OutlineEffect.Plan(_s, OutlineKind.Outline, new OutlineSettings { First = 10, Second = 5 })!);
        var band = Assert.IsType<PathObject>(L.Children[0]);
        AssertRect(new Rect2(185, 185, 315, 315), Bounds(band));
        Assert.True(band.Geometry.Contains(new Vec2(187, 250)));     // inside the band
        Assert.False(band.Geometry.Contains(new Vec2(250, 250)));    // the middle is empty
        Assert.False(band.Geometry.Contains(new Vec2(205, 250)));    // so is the gap next to the source
    }

    [Fact]
    public void Contour_drops_the_holes_and_corners_follow_the_chosen_type()
    {
        var ring = Add(new PathObject(PathGeometry.Rectangle(new Rect2(0, 0, 100, 100)).Transform(Affine2.Identity))
        {
            Style = Red,
        });
        // make a hole: outer clockwise + inner counter-clockwise
        var b = new PathBuilder();
        b.MoveTo(0, 0).LineTo(100, 0).LineTo(100, 100).LineTo(0, 100).Close();
        b.MoveTo(30, 30).LineTo(30, 70).LineTo(70, 70).LineTo(70, 30).Close();
        ring.Geometry = b.Build();
        _s.Selection.Set(ring);
        _s.RunEffect(OutlineEffect.Plan(_s, OutlineKind.Outline, new OutlineSettings { First = 5, ContourOnly = true })!);
        var contour = Assert.IsType<PathObject>(L.Children[0]);
        Assert.True(contour.Geometry.Contains(new Vec2(50, 50)));      // the hole is gone

        _s.Selection.Set(ring);
        _s.RunEffect(OutlineEffect.Plan(_s, OutlineKind.Outline, new OutlineSettings { First = 5, Corners = CornerStyle.Round })!);
        var round = Assert.IsType<PathObject>(_s.Selection.Items[0]);
        Assert.False(round.Geometry.Contains(new Vec2(-4.9, -4.9)));   // a round corner is cut off at the diagonal
        _s.Selection.Set(ring);
        _s.RunEffect(OutlineEffect.Plan(_s, OutlineKind.Outline, new OutlineSettings { First = 5, Corners = CornerStyle.Miter })!);
        var miter = Assert.IsType<PathObject>(_s.Selection.Items[0]);
        Assert.True(miter.Geometry.Contains(new Vec2(-4.9, -4.9)));
    }

    [Fact]
    public async Task Outline_tool_drag_sets_the_distance_and_done_applies_it()
    {
        var a = AddRect(200, 200, 100, 100);
        _s.Selection.Set(a);
        var tool = _s.GetTool<OutlineTool>();
        Assert.True(_s.BeginOutline(OutlineKind.Outline));
        Assert.Equal("outline", _s.ActiveTool.Id);
        Assert.False(tool.CanApply);                                  // Done is disabled while the distance is 0

        _s.PointerDown(At(310, 250, PointerButton.Left));
        Assert.Equal(10, tool.Settings.First, 6);                     // 10 points outside the right edge
        _s.PointerMove(At(290, 250));
        Assert.Equal(-10, tool.Settings.First, 6);                    // inside = negative
        _s.PointerMove(At(305, 250));
        _s.PointerUp(At(305, 250, PointerButton.Left));
        Assert.Equal(5, tool.Settings.First, 6);

        await tool.PreviewSettled;
        Assert.Single(tool.PreviewPaths);
        AssertRect(new Rect2(195, 195, 305, 305), tool.PreviewPaths[0].GetBounds());
        Assert.Single(L.Children);                                     // nothing is made until Done

        Assert.True(await tool.Apply());
        Assert.Equal("select", _s.ActiveTool.Id);
        Assert.Equal(2, L.Children.Count);
        Assert.Equal("Outline", L.Children[0].Name);
    }

    [Fact]
    public void Inline_tool_counts_inward_as_positive_and_percent_is_of_the_selection_height()
    {
        var a = AddRect(200, 200, 100, 50);
        _s.Selection.Set(a);
        var tool = _s.GetTool<OutlineTool>();
        _s.BeginOutline(OutlineKind.Inline);
        _s.PointerDown(At(250, 215, PointerButton.Left));           // 15 inside the top edge
        _s.PointerUp(At(250, 215, PointerButton.Left));
        Assert.Equal(15, tool.Settings.First, 6);
        Assert.Equal(30, tool.FirstPercent, 6);                       // 15 of a 50 pt high selection
        tool.Update(s => s with { First = 25 * 0.1 });
        Assert.Equal(5, tool.FirstPercent, 6);
    }

    // ---- Weld ----

    [Fact]
    public void Weld_unites_the_selection_into_one_object_and_removes_the_originals()
    {
        var a = AddRect(0, 0, 100, 100, Red);
        var b = AddRect(50, 50, 100, 100, Blue);
        var c = AddRect(400, 400, 10, 10);                              // not selected
        _s.Selection.Set([a, b]);
        Assert.True(_s.CanWeld);
        int steps = UndoSteps;

        var outcome = _s.Weld();
        Assert.True(outcome.Applied);
        Assert.Equal(steps + 1, UndoSteps);
        Assert.Equal(2, L.Children.Count);
        var weld = Assert.IsType<PathObject>(L.Children[0]);
        Assert.Same(c, L.Children[1]);
        Assert.Equal("Weld", weld.Name);
        Assert.Equal(ObjectStyle.None, weld.Style);                     // a weld removes all color (user, 2026-10-12)
        Assert.Equal(new Rect2(0, 0, 150, 150), Bounds(weld));
        Assert.True(weld.Geometry.Contains(new Vec2(10, 140)) is false);   // the notch corners of the union are empty
        Assert.True(weld.Geometry.Contains(new Vec2(75, 75)));
        Assert.Equal([weld], _s.Selection.Items);

        _s.History.Undo();
        Assert.Equal([a, b, c], L.Children);                             // the very same objects, same order
        Assert.True(Doc.Contains(a) && Doc.Contains(b));
        _s.History.Redo();
        Assert.Single(L.Children.OfType<PathObject>());
    }

    [Fact]
    public void Weld_of_three_circles_is_the_outer_outline()
    {
        var a = AddCircle(200, 200, 50);
        var b = AddCircle(260, 200, 50);
        var c = AddCircle(230, 250, 50);
        _s.Selection.Set([a, b, c]);
        _s.Weld();
        var weld = Assert.IsType<PathObject>(Assert.Single(L.Children));
        Assert.Equal(1, EffectGeometry.CountLoops(weld.Geometry).Closed);   // one merged loop
        Assert.True(weld.Geometry.Contains(new Vec2(230, 216)));
    }

    [Fact]
    public void Weld_needs_two_closed_shapes_and_no_pictures()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        Assert.False(_s.CanWeld);                                      // one shape: nothing to merge
        var line = Add(new PathObject(new PathBuilder().MoveTo(0, 0).LineTo(50, 50).Build()));
        _s.Selection.Set([a, line]);
        Assert.False(_s.CanWeld);                                      // a line has no area
        var b = AddRect(50, 50, 100, 100);
        _s.Selection.Set([a, b, line]);
        Assert.True(_s.CanWeld);
        _s.Weld();
        Assert.Contains(line, L.Children);                             // the stray line was not touched
        var asset = Doc.Assets.Add([1, 2, 3], "image/png");
        var img = Add(new ImageObject(asset, 50, 50));
        _s.Selection.Set([a, b, img]);
        Assert.False(_s.CanWeld);
        _s.BooleanService = null;
        _s.Selection.Set([a, b]);
        Assert.False(_s.CanWeld);                                      // no engine plugged in
    }

    [Fact]
    public void Weld_turns_live_text_into_curves_in_the_same_undo_step()
    {
        var text = new TextObject("Hi", "Arial", 100) { Transform = Affine2.Translation(100, 300) };
        Add(text);
        var box = AddRect(90, 200, 200, 120);
        _s.Selection.Set([text, box]);
        int steps = UndoSteps;
        _s.Weld();
        Assert.Equal(steps + 1, UndoSteps);
        Assert.Empty(L.Children.OfType<TextObject>());
        Assert.Single(L.Children.OfType<PathObject>());
        _s.History.Undo();
        Assert.Same(text, L.Children[0]);                              // the live text comes back live
        Assert.Equal("Hi", text.Text);
    }

    [Fact]
    public void Weld_looks_inside_groups()
    {
        var a = AddRect(0, 0, 100, 100);
        var b = AddRect(50, 50, 100, 100);
        var g = _s.GroupObjects([a, b])!;
        _s.Selection.Set(g);
        Assert.True(_s.CanWeld);
        _s.Weld();
        Assert.IsType<PathObject>(Assert.Single(L.Children));
        _s.History.Undo();
        Assert.Equal([a, b], L.Children);                               // the group is only membership: the two rectangles are back where they were
        Assert.Same(g, a.TopLevel);
        Assert.Equal([a, b], g.Children);
    }

    // ---- Weld and combined objects ----

    private static PathGeometry RingGeometry()
    {
        var b = new PathBuilder();
        b.MoveTo(0, 0).LineTo(100, 0).LineTo(100, 100).LineTo(0, 100).Close();          // outer, clockwise on screen
        b.MoveTo(30, 30).LineTo(30, 70).LineTo(70, 70).LineTo(70, 30).Close();          // the hole, the other way round
        return b.Build();
    }

    [Fact]
    public void Weld_of_a_combined_object_merges_the_overlap_its_parts_had_knocked_out()
    {
        var a = AddRect(0, 0, 100, 100, Red);
        var b = AddRect(50, 50, 100, 100, Blue);
        _s.Selection.Set([a, b]);
        var combined = _s.CombineSelection()!;
        Assert.False(combined.GetLocalGeometry().Contains(new Vec2(75, 75)));            // as drawn, the overlap is a hole
        Assert.True(_s.CanWeld);                                                          // one selected object, but it holds two shapes
        int steps = UndoSteps;

        var outcome = _s.Weld();

        Assert.True(outcome.Applied);
        Assert.Equal(steps + 1, UndoSteps);
        var weld = Assert.IsType<PathObject>(Assert.Single(L.Children));
        Assert.Equal("Weld", weld.Name);
        Assert.Equal(new Rect2(0, 0, 150, 150), Bounds(weld));
        Assert.True(weld.Geometry.Contains(new Vec2(75, 75)));                            // the overlap is filled again
        Assert.True(weld.Geometry.Contains(new Vec2(10, 10)) && weld.Geometry.Contains(new Vec2(140, 140)));
        Assert.False(weld.Geometry.Contains(new Vec2(140, 10)));
        Assert.Equal(1, EffectGeometry.CountLoops(weld.Geometry).Closed);                 // one merged outline
        Assert.Equal(ObjectStyle.None, weld.Style);                                       // no color (R15)

        _s.History.Undo();
        Assert.Same(combined, Assert.Single(L.Children));                                 // the exact combined object is back
        Assert.Equal(2, combined.Parts.Count);
        _s.History.Redo();
        Assert.IsType<PathObject>(Assert.Single(L.Children));
    }

    [Fact]
    public void Weld_of_a_combined_object_keeps_a_real_hole_and_fills_only_what_the_other_part_covers()
    {
        var ring = Add(new PathObject(RingGeometry()) { Style = Red });
        var cover = AddRect(50, 50, 100, 100, Blue);                       // covers the lower right half of the ring's hole
        _s.Selection.Set([ring, cover]);
        _s.CombineSelection();
        Assert.True(_s.CanWeld);

        _s.Weld();

        var weld = Assert.IsType<PathObject>(Assert.Single(L.Children));
        Assert.False(weld.Geometry.Contains(new Vec2(40, 40)));                           // the part of the hole nobody covers stays a hole
        Assert.True(weld.Geometry.Contains(new Vec2(60, 60)));                            // the covered part of the hole is solid
        Assert.True(weld.Geometry.Contains(new Vec2(10, 10)));                            // the ring itself
        Assert.True(weld.Geometry.Contains(new Vec2(140, 140)));                          // the other part
        Assert.Equal(new Rect2(0, 0, 150, 150), Bounds(weld));
    }

    [Fact]
    public void Weld_of_a_combined_object_together_with_another_shape_welds_all_of_them()
    {
        var a = AddRect(0, 0, 100, 100);
        var b = AddRect(50, 50, 100, 100);
        _s.Selection.Set([a, b]);
        var combined = _s.CombineSelection()!;
        var c = AddRect(120, 0, 100, 60);                                  // overlaps b, away from a
        _s.Selection.Set([combined, c]);
        var other = AddRect(500, 500, 10, 10);                             // not selected
        int steps = UndoSteps;

        _s.Weld();

        Assert.Equal(steps + 1, UndoSteps);
        Assert.Equal(2, L.Children.Count);
        var weld = Assert.IsType<PathObject>(L.Children[0]);
        Assert.Same(other, L.Children[1]);
        Assert.Equal(new Rect2(0, 0, 220, 150), Bounds(weld));
        Assert.True(weld.Geometry.Contains(new Vec2(75, 75)));                            // overlap inside the combined object
        Assert.True(weld.Geometry.Contains(new Vec2(130, 55)));                           // overlap of b and c
        Assert.Equal(1, EffectGeometry.CountLoops(weld.Geometry).Closed);

        _s.History.Undo();
        Assert.Equal([combined, c, other], L.Children);
    }

    [Fact]
    public void Weld_of_one_shape_is_only_available_when_its_own_loops_overlap()
    {
        var rect = AddRect(0, 0, 100, 100);
        _s.Selection.Set(rect);
        Assert.False(_s.CanWeld);                                                         // one plain shape: nothing to weld
        var ring = Add(new PathObject(RingGeometry()));
        _s.Selection.Set(ring);
        Assert.False(_s.CanWeld);                                                         // a hole is not an overlap

        var apart = new PathBuilder();
        apart.MoveTo(300, 0).LineTo(340, 0).LineTo(340, 40).LineTo(300, 40).Close();
        apart.MoveTo(400, 0).LineTo(440, 0).LineTo(440, 40).LineTo(400, 40).Close();
        var twoLoops = Add(new PathObject(apart.Build()));
        _s.Selection.Set(twoLoops);
        Assert.False(_s.CanWeld);                                                         // two loops, but they do not touch

        var overlapping = new PathBuilder();
        overlapping.MoveTo(0, 200).LineTo(100, 200).LineTo(100, 300).LineTo(0, 300).Close();
        overlapping.MoveTo(60, 240).LineTo(160, 240).LineTo(160, 340).LineTo(60, 340).Close();
        var script = Add(new PathObject(overlapping.Build()));
        _s.Selection.Set(script);
        Assert.True(_s.CanWeld);                                                          // loops that cross: weld merges them
        _s.Weld();
        var weld = Assert.IsType<PathObject>(_s.Selection.Items[0]);
        Assert.Equal(1, EffectGeometry.CountLoops(weld.Geometry).Closed);
        Assert.Equal(new Rect2(0, 200, 160, 340), Bounds(weld));
    }

    [Fact]
    public void Common_weld_of_a_combined_object_is_the_overlap_of_its_parts()
    {
        var a = AddRect(0, 0, 100, 100);
        var b = AddRect(60, 40, 100, 100);
        _s.Selection.Set([a, b]);
        var combined = _s.CombineSelection()!;
        Assert.True(_s.CanCommonWeld);                                                     // two shapes inside the one object

        _s.CommonWeld();
        var result = Assert.IsType<PathObject>(Assert.Single(L.Children));
        Assert.Equal("Common Weld", result.Name);
        Assert.Equal(new Rect2(60, 40, 100, 100), Bounds(result));
        _s.History.Undo();
        Assert.Same(combined, Assert.Single(L.Children));

        // With another shape: every pairwise overlap, A∩B, A∩C and B∩C.
        var c = AddRect(80, 20, 100, 100);
        _s.Selection.Set([combined, c]);
        _s.CommonWeld();
        var lenses = Assert.IsType<PathObject>(Assert.Single(L.Children));
        Assert.Equal(3, EffectGeometry.CountLoops(lenses.Geometry).Closed);
        Assert.Equal(FillRule.NonZero, lenses.Geometry.FillRule);
    }
    // ---- Common Weld ----

    [Fact]
    public void Common_weld_of_two_shapes_is_their_overlap()
    {
        var a = AddRect(0, 0, 100, 100, Red);
        var b = AddRect(60, 40, 100, 100, Blue);
        _s.Selection.Set([a, b]);
        Assert.True(_s.CanCommonWeld);
        int steps = UndoSteps;
        var outcome = _s.CommonWeld();
        Assert.True(outcome.Applied);
        Assert.Equal(steps + 1, UndoSteps);
        var result = Assert.IsType<PathObject>(Assert.Single(L.Children));
        Assert.Equal("Common Weld", result.Name);
        Assert.Equal(new Rect2(60, 40, 100, 100), Bounds(result));
        _s.History.Undo();
        Assert.Equal([a, b], L.Children);
    }

    [Fact]
    public void Common_weld_of_three_circles_keeps_every_pairwise_overlap_as_a_separate_lens()
    {
        var a = AddCircle(200, 200, 50);
        var b = AddCircle(260, 200, 50);
        var c = AddCircle(230, 250, 50);
        _s.Selection.Set([a, b, c]);
        _s.CommonWeld();
        var result = Assert.IsType<PathObject>(Assert.Single(L.Children));     // originals removed, one result object
        Assert.Equal(3, EffectGeometry.CountLoops(result.Geometry).Closed);    // A∩B, A∩C, B∩C
        Assert.Equal(FillRule.NonZero, result.Geometry.FillRule);
        Assert.True(result.Geometry.Contains(new Vec2(230, 216)));             // where all three overlap: still solid
        Assert.True(result.Geometry.Contains(new Vec2(230, 190)));             // inside A∩B only
        Assert.False(result.Geometry.Contains(new Vec2(160, 200)));            // inside A only: gone
        // The lens edges cross in the middle: the loops overlap, they are not merged into one.
        var lenses = result.Geometry.Flatten(0.1);
        Assert.Equal(3, lenses.Count);
    }

    [Fact]
    public void Common_weld_of_shapes_that_do_not_overlap_changes_nothing()
    {
        var a = AddRect(0, 0, 50, 50);
        var b = AddRect(200, 200, 50, 50);
        _s.Selection.Set([a, b]);
        int steps = UndoSteps;
        var outcome = _s.CommonWeld();
        Assert.False(outcome.Applied);
        Assert.Equal(steps, UndoSteps);
        Assert.Equal([a, b], L.Children);
        Assert.NotNull(_s.LastEffectMessage);
    }

    // ---- Cookie Cutter ----

    [Fact]
    public void Cookie_cutter_cuts_what_is_below_it_and_keeps_the_cutter()
    {
        var dough = AddRect(0, 0, 200, 200, Blue);
        var above = AddRect(80, 80, 20, 20, Red);                         // above the cutter: not cut
        var cutter = AddCircle(100, 100, 30, Red);
        // restack: dough, cutter, above
        _s.History.Execute(new ReorderObjectCommand(above, 2));
        Assert.Equal([dough, cutter, above], L.Children);
        _s.Selection.Set(cutter);
        Assert.True(_s.CanCookieCut);
        int steps = UndoSteps;

        var outcome = _s.CookieCut();
        Assert.True(outcome.Applied);
        Assert.Equal(steps + 1, UndoSteps);
        Assert.Equal(3, L.Children.Count);
        var cut = Assert.IsType<PathObject>(L.Children[0]);
        Assert.Equal("Cookie Cut", cut.Name);
        Assert.Equal(Blue, cut.Style);
        Assert.False(cut.Geometry.Contains(new Vec2(100, 100)));                // a hole where the cutter was
        Assert.True(cut.Geometry.Contains(new Vec2(10, 10)));
        Assert.Same(cutter, L.Children[1]);                                      // the cutter stays
        Assert.Same(above, L.Children[2]);
        Assert.IsType<RectangleObject>(above);

        _s.History.Undo();
        Assert.Equal([dough, cutter, above], L.Children);                        // exactly the original dough
    }

    [Fact]
    public void Cookie_cutter_outline_grows_the_cut()
    {
        var dough = AddRect(0, 0, 200, 200);
        var cutter = AddCircle(100, 100, 30);
        _s.Selection.Set(cutter);
        _s.CookieCut(outline: 10);
        var cut = Assert.IsType<PathObject>(L.Children[0]);
        Assert.False(cut.Geometry.Contains(new Vec2(100 + 38, 100)));           // within 30 + 10
        Assert.True(cut.Geometry.Contains(new Vec2(100 + 42, 100)));
        Assert.Equal(1, EffectGeometry.CountLoops(cut.Geometry).Closed - 1);     // outer loop + one hole
    }

    [Fact]
    public void Cookie_cutter_cuts_inside_groups_and_removes_what_it_covers_completely()
    {
        var a = AddRect(0, 0, 100, 100, Blue);
        var b = AddRect(300, 0, 20, 20, Red);                                    // will be covered completely
        var g = _s.GroupObjects([a, b])!;
        var cutter = AddRect(-10, -10, 130, 130, Red);
        // another cutter-sized shape covering b
        var cover = AddRect(290, -10, 50, 50, Red);
        _s.Selection.Set([cutter, cover]);
        _s.CookieCut();
        Assert.Equal([cutter, cover], L.Children);                                // both members were swallowed, so the empty group went too
        _s.History.Undo();
        Assert.Equal([a, b, cutter, cover], L.Children);
        Assert.True(Doc.Contains(g));
        Assert.Equal([a, b], g.Children);
    }

    [Fact]
    public void Cookie_cutter_replaces_a_swallowed_group_member_and_keeps_a_partly_cut_one()
    {
        var a = AddRect(0, 0, 100, 100, Blue);
        var b = AddRect(300, 0, 20, 20, Red);
        var g = _s.GroupObjects([a, b])!;
        var cutter = AddRect(40, 40, 100, 100, Red);                               // overlaps a only
        _s.Selection.Set(cutter);
        _s.CookieCut();
        var group = Assert.IsType<GroupObject>(L.Children[0].Parent);
        Assert.Equal(2, group.Children.Count);
        Assert.IsType<PathObject>(group.Children[0]);                              // a, cut, in place in the group
        Assert.Same(b, group.Children[1]);                                          // b was not touched
        Assert.False(((PathObject)group.Children[0]).Geometry.Contains(new Vec2(60, 60)));
        _s.History.Undo();
        Assert.Same(a, group.Children[0]);
    }

    [Fact]
    public void Cookie_cutter_stamp_moves_the_cutter_and_cuts_in_one_undo_step()
    {
        var dough = AddRect(0, 0, 400, 100);
        var cutter = AddCircle(20, 50, 15);
        _s.Selection.Set(cutter);
        int steps = UndoSteps;
        _s.CookieCut(moveBy: new Vec2(200, 0));
        Assert.Equal(steps + 1, UndoSteps);
        Assert.InRange(Bounds(cutter).Center.X, 219.9, 220.1);
        var cut = Assert.IsType<PathObject>(L.Children[0]);
        Assert.False(cut.Geometry.Contains(new Vec2(220, 50)));
        Assert.True(cut.Geometry.Contains(new Vec2(20, 50)));
        _s.History.Undo();
        Assert.InRange(Bounds(cutter).Center.X, 19.9, 20.1);
        Assert.Same(dough, L.Children[0]);
    }

    [Fact]
    public void Cookie_cutter_turns_cut_text_into_curves()
    {
        var text = Add(new TextObject("Hi", "Arial", 100) { Transform = Affine2.Translation(100, 300) });
        var cutter = AddCircle(140, 260, 25);
        _s.Selection.Set(cutter);
        _s.CookieCut();
        Assert.Empty(L.Children.OfType<TextObject>());
        Assert.IsType<PathObject>(L.Children[0]);
        _s.History.Undo();
        Assert.Same(text, L.Children[0]);
    }

    // ---- Knife ----

    [Fact]
    public void Knife_cuts_a_shape_into_named_pieces_and_undoes_in_one_step()
    {
        var a = AddRect(100, 100, 200, 100, Blue);
        _s.Selection.Set(a);
        int steps = UndoSteps;
        var plan = KnifeEffect.Plan(_s, new Vec2(50, 150), new Vec2(350, 150), new KnifeSettings(), selectedOnly: true)!;
        var outcome = _s.RunEffect(plan);

        Assert.True(outcome.Applied);
        Assert.Equal(steps + 1, UndoSteps);
        Assert.Equal(2, L.Children.Count);
        var pieces = L.Children.Cast<PathObject>().ToList();
        Assert.Equal(["Knife piece 1", "Knife piece 2"], pieces.Select(p => p.Name));
        Assert.All(pieces, p => Assert.Equal(Blue, p.Style));
        var byTop = pieces.OrderBy(p => Bounds(p).Top).ToList();
        Assert.InRange(Bounds(byTop[0]).Bottom, 149.92, 149.94);                          // the 0.002 in blade: 0.072 pt either side of the line
        Assert.InRange(Bounds(byTop[1]).Top, 150.06, 150.08);
        Assert.Equal(pieces.Count, _s.Selection.Count);                                  // the pieces are selected afterwards

        _s.History.Undo();
        Assert.Same(a, Assert.Single(L.Children));
    }

    [Fact]
    public void Knife_gap_removes_a_strip_and_overlap_mode_makes_the_pieces_overlap()
    {
        var a = AddRect(100, 100, 200, 100);
        _s.Selection.Set(a);
        var gap = new KnifeSettings { Gap = 10 };
        _s.RunEffect(KnifeEffect.Plan(_s, new Vec2(50, 150), new Vec2(350, 150), gap, true)!);
        var heights = L.Children.Select(c => Bounds(c).Height).OrderBy(h => h).ToList();
        Assert.Equal(2, heights.Count);
        Assert.InRange(heights[0] + heights[1], 89.9, 90.1);                              // 100 minus the 10 point strip

        _s.History.Undo();
        _s.Selection.Set(a);
        var overlap = new KnifeSettings { OverlapMode = true, Overlap = 20 };
        _s.RunEffect(KnifeEffect.Plan(_s, new Vec2(50, 150), new Vec2(60, 150), overlap, true)!);   // a short line: overlap mode always extends it
        var b = L.Children.Select(Bounds).OrderBy(r => r.Top).ToList();
        Assert.Equal(2, b.Count);
        Assert.InRange(b[0].Bottom - b[1].Top, 19.9, 20.1);                                // the two pieces overlap by 20
    }

    [Fact]
    public void Knife_simple_cut_follows_the_dragged_segment_only()
    {
        var a = AddRect(100, 100, 200, 100);
        _s.Selection.Set(a);
        // stops halfway: with Simple Cut a notch, no new piece
        _s.RunEffect(KnifeEffect.Plan(_s, new Vec2(50, 150), new Vec2(200, 150), new KnifeSettings { Gap = 6 }, true)!);
        var notched = Assert.IsType<PathObject>(Assert.Single(L.Children));
        Assert.False(notched.Geometry.Contains(new Vec2(150, 150)));
        Assert.True(notched.Geometry.Contains(new Vec2(250, 150)));

        _s.History.Undo();
        _s.Selection.Set(a);
        _s.RunEffect(KnifeEffect.Plan(_s, new Vec2(50, 150), new Vec2(200, 150), new KnifeSettings { Gap = 6, SimpleCut = false }, true)!);
        Assert.Equal(2, L.Children.Count);                                                 // extended across the whole shape
    }

    [Fact]
    public void Knife_cuts_what_the_line_crosses_when_selected_only_is_off_and_leaves_the_rest()
    {
        var a = AddRect(100, 100, 100, 100);
        var b = AddRect(300, 100, 100, 100);
        var far = AddRect(100, 400, 100, 100);
        _s.Selection.Set(a);
        var all = new KnifeSettings { SelectedOnly = false };
        _s.RunEffect(KnifeEffect.Plan(_s, new Vec2(50, 150), new Vec2(450, 150), all, all.SelectedOnly)!);
        Assert.Equal(5, L.Children.Count);
        Assert.Contains(far, L.Children);
        Assert.DoesNotContain(a, L.Children);
        Assert.DoesNotContain(b, L.Children);
        Assert.Equal(["Knife piece 1", "Knife piece 2", "Knife piece 3", "Knife piece 4"],
            L.Children.OfType<PathObject>().Select(p => p.Name));

        _s.History.Undo();
        Assert.Equal([a, b, far], L.Children);
        _s.Selection.Set(a);
        _s.RunEffect(KnifeEffect.Plan(_s, new Vec2(50, 150), new Vec2(450, 150), new KnifeSettings(), true)!);   // Selected Only: just a
        Assert.Contains(b, L.Children);
        Assert.Equal(4, L.Children.Count);
    }

    [Fact]
    public void Knife_turns_cut_text_into_curves_and_cuts_group_members_in_place()
    {
        var text = Add(new TextObject("Hi", "Arial", 100) { Transform = Affine2.Translation(100, 300) });
        var a = AddRect(0, 0, 50, 50);
        var b = AddRect(60, 0, 50, 50);
        var g = _s.GroupObjects([a, b])!;
        var all = new KnifeSettings { SelectedOnly = false, Gap = 4 };
        _s.Selection.Clear();
        _s.RunEffect(KnifeEffect.Plan(_s, new Vec2(10, 265), new Vec2(300, 265), all, false)!);
        Assert.Empty(L.Children.OfType<TextObject>());
        Assert.True(L.Children.OfType<PathObject>().Count() >= 2);
        _s.History.Undo();
        Assert.Same(text, L.Children.OfType<TextObject>().Single());

        _s.RunEffect(KnifeEffect.Plan(_s, new Vec2(30, -10), new Vec2(30, 60), all, false)!);   // down through `a` only
        var group = Assert.Single(_s.Document.Groups);
        Assert.Equal(3, group.Children.Count);                                                     // a became two pieces, b stayed
        Assert.Same(b, group.Children[2]);
    }

    [Fact]
    public void Knife_ctrl_snaps_a_nearly_horizontal_line_flat()
    {
        Assert.Equal(new Vec2(300, 100), KnifeTool.Constrain(new Vec2(0, 100), new Vec2(300, 103), true));
        Assert.Equal(new Vec2(0, 300), KnifeTool.Constrain(new Vec2(0, 100), new Vec2(2, 300), true));
        Assert.Equal(new Vec2(300, 200), KnifeTool.Constrain(new Vec2(0, 100), new Vec2(300, 200), true));
        Assert.Equal(new Vec2(300, 103), KnifeTool.Constrain(new Vec2(0, 100), new Vec2(300, 103), false));
    }

    [Fact]
    public async Task Knife_tool_drag_cuts_on_release_and_stays_active()
    {
        var a = AddRect(100, 100, 200, 100);
        _s.Selection.Set(a);
        Assert.True(_s.BeginKnife());
        _s.PointerDown(At(50, 150, PointerButton.Left));
        _s.PointerMove(At(200, 151));
        Assert.NotNull(_s.GetTool<KnifeTool>().Line);
        _s.PointerUp(At(350, 150, PointerButton.Left));
        await _s.GetTool<KnifeTool>().Idle;
        Assert.Equal("knife", _s.ActiveTool.Id);
        Assert.Equal(2, L.Children.Count);
    }

    // ---- Tool state follows the drawing ----

    [Fact]
    public async Task Outline_tool_notices_when_the_object_is_moved_while_it_is_open()
    {
        var a = AddRect(200, 200, 100, 100);
        _s.Selection.Set(a);
        var tool = _s.GetTool<OutlineTool>();
        _s.BeginOutline(OutlineKind.Outline);
        tool.Update(s => s with { First = 10 });
        _s.NudgeSelection(new Vec2(50, 0));                       // arrow key, with the tool still open
        Assert.True(await tool.Apply());
        AssertRect(new Rect2(240, 190, 360, 310), Bounds(_s.Selection.Items[0]));   // around the moved square, not where it was
    }

    [Fact]
    public async Task Escape_lets_go_of_a_long_effect_at_once_and_drops_its_result()
    {
        var a = AddRect(0, 0, 100, 100);
        var b = AddRect(50, 50, 100, 100);
        _s.Selection.Set([a, b]);
        using var release = new ManualResetEventSlim();
        var plan = new EffectPlan("Slow", "Working...", ct =>
        {
            release.Wait(TimeSpan.FromSeconds(20));
            return EffectOutcome.Change(s => s.History.Execute(new DeleteObjectsCommand([a])));
        });
        var task = _s.RunEffectAsync(plan);
        Assert.True(_s.EffectBusy);
        Assert.False(_s.CanWeld);                                  // other effects wait their turn
        Assert.True(_s.CancelEffect());
        var outcome = await task;
        Assert.False(outcome.Applied);
        Assert.False(_s.EffectBusy);
        Assert.Equal([a, b], L.Children);                          // nothing was changed
        release.Set();
    }
    // ---- Shared ----

    [Fact]
    public async Task Async_run_gives_the_same_result_and_reports_busy_state()
    {
        var a = AddRect(0, 0, 100, 100);
        var b = AddRect(50, 50, 100, 100);
        _s.Selection.Set([a, b]);
        var states = new List<bool>();
        _s.EffectBusyChanged += (_, _) => states.Add(_s.EffectBusy);
        var outcome = await _s.WeldAsync();
        Assert.True(outcome.Applied);
        Assert.Equal([true, false], states);
        Assert.False(_s.EffectBusy);
        Assert.IsType<PathObject>(Assert.Single(L.Children));
    }

    [Fact]
    public void Menu_state_follows_the_selection()
    {
        Assert.False(_s.CanOutline);
        Assert.False(_s.CanWeld);
        Assert.False(_s.CanCookieCut);
        Assert.False(_s.CanKnife);                                                   // an empty drawing: nothing to cut
        var a = AddRect(0, 0, 100, 100);
        Assert.True(_s.CanKnife);
        _s.Selection.Set(a);
        Assert.True(_s.CanOutline);
        Assert.True(_s.CanCookieCut);
        Assert.False(_s.CanWeld);
        Assert.False(_s.CanCommonWeld);
        var b = AddRect(50, 50, 100, 100);
        _s.Selection.Set([a, b]);
        Assert.True(_s.CanWeld);
        Assert.True(_s.CanCommonWeld);
        _s.OffsetService = null;
        Assert.False(_s.CanOutline);
        Assert.False(_s.CanCookieCut);
    }
}


