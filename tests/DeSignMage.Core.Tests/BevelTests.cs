using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Geometry;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Effects ▸ Easy Bevel (effects step 7; R7, R8, R13, R22).</summary>
public class BevelTests
{
    private readonly EditorSession _s;
    private Document Doc => _s.Document;
    private Layer L => _s.Document.ActiveLayer;
    private static readonly ObjectStyle Red = ObjectStyle.FillOnly(DocColor.FromHex("#CC0000"));

    public BevelTests()
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
        var r = Rect(x, y, w, h, style ?? Red);
        _s.History.Execute(new AddObjectCommand(L, r));
        return r;
    }

    private T Add<T>(T o) where T : DocObject
    {
        _s.History.Execute(new AddObjectCommand(L, o));
        return o;
    }

    private int UndoSteps => _s.History.UndoList.Count;

    private static PointerInput At(double x, double y, PointerButton b = PointerButton.None, ModifierKeys m = ModifierKeys.None) =>
        new(new Vec2(x, y), new Vec2(x, y), b, m);

    private static PathObject Highlight(DocObject source) => Crescent(source, BevelEffect.HighlightName);
    private static PathObject Shadow(DocObject source) => Crescent(source, BevelEffect.ShadowName);

    private static PathObject Crescent(DocObject source, string name) =>
        ((GroupObject)source.Parent!).Children.OfType<PathObject>().Single(p => p.Name == name);

    private static SolidPaint Fill(DocObject o) => Assert.IsType<SolidPaint>(o.Style.Fill);

    // ---- Geometry ----

    [Fact]
    public void A_bevel_is_a_highlight_crescent_on_the_upper_left_edges_and_a_shadow_crescent_on_the_lower_right()
    {
        var a = AddRect(0, 0, 200, 100);
        _s.Selection.Set(a);

        _s.ApplyBevel(new BevelSettings());   // inset 1 % and width 2 % of the 100 pt height: the outline is pulled in 1, the crescents are 2 wide

        var hl = Highlight(a);
        var sh = Shadow(a);
        AssertRect(new Rect2(1, 1, 199, 99), hl.GetWorldBounds());
        AssertRect(new Rect2(1, 1, 199, 99), sh.GetWorldBounds());
        Assert.True(hl.Geometry.Contains(new Vec2(2, 50)));                        // along the left edge
        Assert.True(hl.Geometry.Contains(new Vec2(100, 2)));                       // along the top edge
        Assert.False(hl.Geometry.Contains(new Vec2(100, 50)));                     // not in the middle
        Assert.False(hl.Geometry.Contains(new Vec2(198, 50)));                     // not on the far edges
        Assert.False(hl.Geometry.Contains(new Vec2(100, 98)));
        Assert.True(sh.Geometry.Contains(new Vec2(198, 50)));                      // the shadow is the opposite: right and bottom
        Assert.True(sh.Geometry.Contains(new Vec2(100, 98)));
        Assert.False(sh.Geometry.Contains(new Vec2(2, 50)));
        Assert.False(sh.Geometry.Contains(new Vec2(100, 2)));
        // the crescent is 2 points wide along an edge that faces the light head on
        Assert.True(hl.Geometry.Contains(new Vec2(2.9, 50)));
        Assert.False(hl.Geometry.Contains(new Vec2(3.2, 50)));
    }

    private static void AssertRect(Rect2 expected, Rect2 actual, double tol = 0.05)
    {
        Assert.InRange(actual.Left, expected.Left - tol, expected.Left + tol);
        Assert.InRange(actual.Top, expected.Top - tol, expected.Top + tol);
        Assert.InRange(actual.Right, expected.Right - tol, expected.Right + tol);
        Assert.InRange(actual.Bottom, expected.Bottom - tol, expected.Bottom + tol);
    }

    [Fact]
    public void The_crescents_are_see_through_colors_with_a_feather_so_what_is_under_them_shows()
    {
        var a = AddRect(0, 0, 200, 100);
        _s.Selection.Set(a);
        _s.ApplyBevel(new BevelSettings());

        var hl = Highlight(a);
        var sh = Shadow(a);
        Assert.Equal(new DocColor(255, 255, 255, 204), Fill(hl).Color);            // white at the highlight's 0.8
        Assert.Equal(new DocColor(0, 0, 0, 204), Fill(sh).Color);                  // black at the shadow's 0.8
        Assert.Equal(new FeatherStyle(0.10, 0), hl.Style.Feather);                  // the softness is the crescent's feather (R8: drawn soft, kept vector)
        Assert.Equal(new FeatherStyle(0.10, 0), sh.Style.Feather);
        Assert.Null(hl.Style.Stroke);
        Assert.Equal(Red, a.Style);                                                 // the object keeps its own paint
    }

    [Fact]
    public void Neutral_forces_white_and_black_the_shadow_can_be_left_out_and_softness_zero_is_a_hard_edge()
    {
        var a = AddRect(0, 0, 200, 100);
        _s.Selection.Set(a);
        _s.ApplyBevel(new BevelSettings
        {
            Neutral = true, HighlightColor = DocColor.FromHex("#FFD400"), ShadowColor = DocColor.FromHex("#2060E0"),
            Highlight = 0.5, Softness = 0,
        });
        Assert.Equal(new DocColor(255, 255, 255, 128), Fill(Highlight(a)).Color);
        Assert.Equal(new DocColor(0, 0, 0, 204), Fill(Shadow(a)).Color);
        Assert.Null(Highlight(a).Style.Feather);

        _s.History.Undo();
        _s.Selection.Set(a);
        _s.ApplyBevel(new BevelSettings { HighlightColor = DocColor.FromHex("#FFD400"), ShadowColor = DocColor.FromHex("#2060E0"), IncludeShadow = false });
        Assert.Equal(DocColor.FromHex("#FFD400") with { A = 204 }, Fill(Highlight(a)).Color);   // the chosen color, at the highlight's opacity
        Assert.DoesNotContain(((GroupObject)a.Parent!).Children, c => c.Name == BevelEffect.ShadowName);
        _s.History.Undo();
        _s.Selection.Set(a);
        _s.ApplyBevel(new BevelSettings { Shadow = 0 });                              // a shadow of strength 0 is no crescent either
        Assert.DoesNotContain(((GroupObject)a.Parent!).Children, c => c.Name == BevelEffect.ShadowName);
    }

    [Fact]
    public void A_curved_shape_gets_a_thinner_rim_where_its_edge_turns_away_from_the_light()
    {
        var c = Add(new EllipseObject(200, 200) { Style = Red });
        _s.Selection.Set(c);
        _s.ApplyBevel(new BevelSettings { Inset = 0, Width = 0.05 });                // d = 10
        var hl = Highlight(c);
        Assert.True(hl.Geometry.Contains(new Vec2(100 - 0.7071 * 99, 100 - 0.7071 * 99)));   // upper left of the rim: at its fattest
        Assert.False(hl.Geometry.Contains(new Vec2(100 + 0.7071 * 99, 100 + 0.7071 * 99)));  // lower right: no highlight
        Assert.False(hl.Geometry.Contains(new Vec2(100, 100)));
        var sh = Shadow(c);
        Assert.True(sh.Geometry.Contains(new Vec2(100 + 0.7071 * 99, 100 + 0.7071 * 99)));
        Assert.False(sh.Geometry.Contains(new Vec2(100 - 0.7071 * 99, 100 - 0.7071 * 99)));
    }

    [Fact]
    public void A_shape_with_a_hole_is_lit_on_the_opposite_side_of_the_hole()
    {
        var b = new PathBuilder();
        b.MoveTo(0, 0).LineTo(200, 0).LineTo(200, 200).LineTo(0, 200).Close();
        b.MoveTo(60, 60).LineTo(60, 140).LineTo(140, 140).LineTo(140, 60).Close();   // the hole
        var ring = Add(new PathObject(b.Build()) { Style = Red });
        _s.Selection.Set(ring);
        _s.ApplyBevel(new BevelSettings { Inset = 0, Width = 0.02 });                // d = 4
        var hl = Highlight(ring);
        Assert.True(hl.Geometry.Contains(new Vec2(2, 100)));                          // outer left edge
        Assert.True(hl.Geometry.Contains(new Vec2(142, 100)));                        // the material at the hole's right wall faces the light
        Assert.False(hl.Geometry.Contains(new Vec2(58, 100)));                        // at its left wall it does not
        Assert.True(Shadow(ring).Geometry.Contains(new Vec2(58, 100)));               // that side is in the shadow
    }

    [Fact]
    public void The_light_turns_with_the_object()
    {
        var r = AddRect(0, 0, 100, 50);
        r.Transform = Affine2.Rotation(Math.PI / 2);                                  // a quarter turn: its local top edge is now the right side
        _s.Selection.Set(r);
        _s.ApplyBevel(new BevelSettings());                                            // world bounds x -50..0, y 0..100: H = 100, d = 2, inset = 1
        var hl = Highlight(r);
        var sh = Shadow(r);
        Assert.True(hl.Geometry.Contains(new Vec2(-2, 50)));                          // along the object's top (now at the right)
        Assert.True(hl.Geometry.Contains(new Vec2(-25, 2)));                          // along the object's left (now at the top)
        Assert.True(sh.Geometry.Contains(new Vec2(-48, 50)));
        Assert.True(sh.Geometry.Contains(new Vec2(-25, 98)));
        Assert.False(hl.Geometry.Contains(new Vec2(-48, 50)));
    }

    // ---- Structure: grouping, stacking, undo ----

    [Fact]
    public void The_crescents_sit_just_above_the_object_grouped_with_it_and_nothing_else_moves()
    {
        var below = AddRect(300, 300, 10, 10);
        var a = AddRect(0, 0, 200, 100);
        var above = AddRect(400, 400, 10, 10);
        _s.Selection.Set(a);
        int steps = UndoSteps;

        _s.ApplyBevel(new BevelSettings());

        Assert.Equal(steps + 1, UndoSteps);                                            // one undo step
        Assert.Equal(5, L.Children.Count);
        Assert.Same(below, L.Children[0]);
        Assert.Same(a, L.Children[1]);
        Assert.Equal(BevelEffect.HighlightName, L.Children[2].Name);                  // owner, highlight, shadow, then what was above
        Assert.Equal(BevelEffect.ShadowName, L.Children[3].Name);
        Assert.Same(above, L.Children[4]);
        var group = Assert.IsType<GroupObject>(a.Parent);                              // R13: grouped with the object, not linked
        Assert.Equal(3, group.Children.Count);
        Assert.Equal(1, Doc.GroupCount);
        Assert.Equal([group], _s.Selection.Items);

        _s.History.Undo();
        Assert.Equal([below, a, above], L.Children);
        Assert.Equal(0, Doc.GroupCount);
        Assert.Same(L, a.Parent);
        _s.History.Redo();
        Assert.Equal(5, L.Children.Count);
        Assert.Equal(1, Doc.GroupCount);
    }

    [Fact]
    public void Every_object_of_a_group_is_beveled_by_itself_with_its_own_height()
    {
        var a = AddRect(0, 0, 200, 100);
        var b = AddRect(300, 0, 200, 50);
        var between = AddRect(600, 600, 10, 10);
        var group = _s.GroupObjects([a, b])!;
        group.Transform = Affine2.Translation(100, 100);
        _s.Selection.Set(group);
        int groups = Doc.GroupCount;

        _s.ApplyBevel(new BevelSettings { Inset = 0 });

        Assert.Equal(groups, Doc.GroupCount);                                          // the crescents joined the existing group
        Assert.Equal(6, group.Children.Count);
        Assert.Equal([a.Name, "Bevel Highlight", "Bevel Shadow", b.Name, "Bevel Highlight", "Bevel Shadow", between.Name],
            L.Children.Select(o => o.Name));
        // a: height 100 -> crescent 2 wide; b: height 50 -> 1 wide (world coordinates; the group is moved by (100, 100))
        var hlA = (PathObject)L.Children[1];
        var hlB = (PathObject)L.Children[4];
        Assert.True(hlA.GetWorldBounds().Left is > 99.9 and < 100.1);
        Assert.True(hlA.Geometry.Contains(new Vec2(1.5, 50)));                          // in the group's own space
        Assert.False(hlA.Geometry.Contains(new Vec2(2.5, 50)));
        Assert.True(hlB.Geometry.Contains(new Vec2(300.5, 25)));
        Assert.False(hlB.Geometry.Contains(new Vec2(301.5, 25)));
        _s.History.Undo();
        Assert.Equal(2, group.Children.Count);
        Assert.Equal([a, b, between], L.Children);
    }

    [Fact]
    public void Nested_groups_are_beveled_member_by_member_each_in_its_own_group()
    {
        var a = AddRect(0, 0, 60, 60);
        var b = AddRect(100, 0, 60, 60);
        var c = AddRect(200, 0, 60, 60);
        var inner = _s.GroupObjects([a, b])!;
        var outer = _s.GroupObjects([inner, c])!;
        _s.Selection.Set(outer);
        int groups = Doc.GroupCount;
        int steps = UndoSteps;

        _s.ApplyBevel(new BevelSettings());

        Assert.Equal(steps + 1, UndoSteps);                                            // one step for all of it
        Assert.Equal(groups, Doc.GroupCount);
        Assert.Equal([a.Name, "Bevel Highlight", "Bevel Shadow", b.Name, "Bevel Highlight", "Bevel Shadow", c.Name, "Bevel Highlight", "Bevel Shadow"],
            L.Children.Select(o => o.Name));                                           // each pair directly above its own member
        Assert.Same(inner, L.Children[1].Parent);                                      // a's and b's crescents are in the inner group
        Assert.Same(inner, L.Children[5].Parent);
        Assert.Same(outer, L.Children[7].Parent);                                      // c's in the outer one
        Assert.Equal(6, inner.Children.Count);
        Assert.Equal(4, outer.Children.Count);                                         // the inner group, c, c's two crescents
        _s.History.Undo();
        Assert.Equal([a, b, c], L.Children);
        Assert.Equal(2, inner.Children.Count);
    }
    [Fact]
    public void Live_text_stays_live_and_gets_crescent_objects_of_its_own()
    {
        var text = Add(new TextObject("Hi", "Arial", 100) { Transform = Affine2.Translation(100, 300), Style = Red });
        _s.Selection.Set(text);

        _s.ApplyBevel(new BevelSettings());

        Assert.Equal("Hi", text.Text);
        Assert.IsType<TextObject>(text);
        var group = Assert.IsType<GroupObject>(text.Parent);
        Assert.Equal(3, group.Children.Count);
        var hl = Highlight(text);
        Assert.IsType<PathObject>(hl);
        Assert.True(text.GetWorldBounds().Contains(new Vec2(hl.GetWorldBounds().Left + 1, hl.GetWorldBounds().Top + 1)));   // it lies on the letters
        text.Text = "Hello";                                                           // no live link: the crescents stay as they were
        Assert.Equal(BevelEffect.HighlightName, hl.Name);
    }

    [Fact]
    public void A_combined_object_is_beveled_around_its_whole_silhouette()
    {
        var a = AddRect(0, 0, 100, 100);
        var b = AddRect(50, 50, 100, 100);
        _s.Selection.Set([a, b]);
        var combined = _s.CombineSelection()!;
        _s.Selection.Set(combined);

        _s.ApplyBevel(new BevelSettings { Inset = 0 });

        var hl = Highlight(combined);
        Assert.True(hl.Geometry.Contains(new Vec2(1, 20)));
        Assert.False(hl.Geometry.Contains(new Vec2(75, 75)));                           // the overlap is not an edge
    }

    [Fact]
    public void Bevelling_again_replaces_the_crescents_instead_of_piling_them_up_and_undo_brings_the_first_ones_back()
    {
        var a = AddRect(0, 0, 200, 100);
        _s.Selection.Set(a);
        _s.ApplyBevel(new BevelSettings());
        var firstHighlight = Highlight(a);
        var firstShadow = Shadow(a);
        int steps = UndoSteps;

        _s.Selection.Set(a.TopLevel);
        _s.ApplyBevel(new BevelSettings { Width = 0.05 });

        Assert.Equal(steps + 1, UndoSteps);
        var group = (GroupObject)a.Parent!;
        Assert.Equal(3, group.Children.Count);                                          // still one pair
        Assert.NotSame(firstHighlight, Highlight(a));
        Assert.True(Highlight(a).Geometry.Contains(new Vec2(5, 50)));

        _s.History.Undo();
        Assert.Same(firstHighlight, Highlight(a));                                      // the very same objects
        Assert.Same(firstShadow, Shadow(a));
        Assert.Equal(3, ((GroupObject)a.Parent!).Children.Count);
        Assert.Equal([a, firstHighlight, firstShadow], L.Children);
    }

    [Fact]
    public void Remove_takes_the_crescents_away_in_one_undo_step()
    {
        var a = AddRect(0, 0, 200, 100);
        _s.Selection.Set(a);
        _s.ApplyBevel(new BevelSettings());
        var hl = Highlight(a);
        int steps = UndoSteps;
        _s.Selection.Set(a.TopLevel);

        Assert.True(_s.HasBevel);
        Assert.True(_s.RemoveBevel());
        Assert.Equal(steps + 1, UndoSteps);
        Assert.Equal([a], L.Children);
        Assert.False(_s.HasBevel);
        Assert.False(_s.RemoveBevel());
        _s.History.Undo();
        Assert.Equal(3, L.Children.Count);
        Assert.Same(hl, L.Children[1]);
    }

    [Fact]
    public void Shadow_and_soft_effect_objects_in_a_group_are_not_beveled()
    {
        var a = AddRect(0, 0, 200, 100);
        _s.Selection.Set(a);
        _s.Shadow(new ShadowSettings { ShiftX = 10, ShiftY = 10 });
        var group = (GroupObject)a.Parent!;
        _s.Selection.Set(group);
        _s.ApplyBevel(new BevelSettings());
        Assert.Equal(4, group.Children.Count);                                          // the object, its shadow, and one pair of crescents (for the object only)
        Assert.Equal(["Shadow", a.Name, "Bevel Highlight", "Bevel Shadow"], L.Children.Select(o => o.Name));
    }

    // ---- When it is available ----

    [Fact]
    public void A_bevel_needs_a_shape_with_an_area_and_both_engines()
    {
        Assert.False(_s.CanBevel);
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        Assert.True(_s.CanBevel);
        var line = Add(new PathObject(new PathBuilder().MoveTo(0, 200).LineTo(50, 250).Build()));
        _s.Selection.Set(line);
        Assert.False(_s.CanBevel);                                                      // a line has no area
        var asset = Doc.Assets.Add([1, 2, 3], "image/png");
        var img = Add(new ImageObject(asset, 50, 50));
        _s.Selection.Set(img);
        Assert.False(_s.CanBevel);
        _s.Selection.Set(a);
        _s.OffsetService = null;
        Assert.False(_s.CanBevel);
        Assert.False(_s.BeginBevel());
    }

    [Fact]
    public void The_presets_set_width_softness_and_strength()
    {
        Assert.Equal(9, BevelPresets.All.Count);
        var heavy = BevelPresets.All.Single(p => p.Name == "Heavy Soft");
        var s = heavy.ApplyTo(new BevelSettings());
        Assert.Equal(0.033, s.Width);
        Assert.Equal(0.06, s.Softness);
        Assert.Equal(0.8, s.Highlight);
        Assert.Same(heavy, BevelPresets.Find(s));
        Assert.Null(BevelPresets.Find(s with { Width = 0.04 }));
        Assert.Equal(0.01, new BevelSettings().Inset);                                  // the defaults
        Assert.Equal(0.02, new BevelSettings().Width);
        Assert.Equal(0.10, new BevelSettings().Softness);
        Assert.Equal(0.8, new BevelSettings().Highlight);
    }

    // ---- The tool ----

    [Fact]
    public async Task The_tool_shows_the_bevel_at_once_and_every_change_is_one_undo_step()
    {
        var a = AddRect(0, 0, 200, 100);
        _s.Selection.Set(a);
        var tool = _s.GetTool<BevelTool>();
        int steps = UndoSteps;

        Assert.True(_s.BeginBevel());
        Assert.Equal("bevel", _s.ActiveTool.Id);
        await tool.Settled;
        Assert.Equal(steps + 1, UndoSteps);
        Assert.Equal(3, L.Children.Count);                                               // the default bevel is there
        var hl = Highlight(a);
        Assert.False(hl.Geometry.Contains(new Vec2(4, 50)));                             // 2 % wide

        tool.Update(s => s with { Width = 0.05 });
        await tool.Settled;
        Assert.Equal(steps + 1, UndoSteps);                                              // the same step, edited
        Assert.True(Highlight(a).Geometry.Contains(new Vec2(4, 50)));
        Assert.Same(hl, Highlight(a));                                                   // the same object, re-shaped

        tool.Update(s => s with { Shadow = 0 });                                         // no shadow crescent
        await tool.Settled;
        Assert.DoesNotContain(((GroupObject)a.Parent!).Children, c => c.Name == BevelEffect.ShadowName);
        tool.Update(s => s with { Shadow = 0.8, Neutral = true });
        await tool.Settled;
        Assert.Equal(DocColor.Black with { A = 204 }, Fill(Shadow(a)).Color);
        Assert.Equal(steps + 1, UndoSteps);

        await tool.Done();
        Assert.Equal("select", _s.ActiveTool.Id);
        Assert.Equal(3, L.Children.Count);
        _s.History.Undo();
        Assert.Equal([a], L.Children);                                                   // all of it, in one step
        Assert.Equal(0, Doc.GroupCount);
    }

    [Fact]
    public async Task Escape_takes_the_bevel_back_and_Remove_deletes_one_that_was_there()
    {
        var a = AddRect(0, 0, 200, 100);
        _s.Selection.Set(a);
        var tool = _s.GetTool<BevelTool>();
        _s.BeginBevel();
        await tool.Settled;
        Assert.Equal(3, L.Children.Count);
        _s.KeyDown(EditorKey.Escape, ModifierKeys.None);
        Assert.Equal("select", _s.ActiveTool.Id);
        Assert.Equal([a], L.Children);                                                   // as if nothing happened
        Assert.Equal(0, Doc.GroupCount);

        _s.Selection.Set(a);
        _s.ApplyBevel(new BevelSettings());
        _s.Selection.Set(a.TopLevel);
        Assert.True(_s.BeginBevel());
        Assert.True(tool.HadBevel);
        await tool.Settled;
        Assert.Equal(3, L.Children.Count);                                               // replaced, not doubled
        tool.Remove();
        Assert.Equal("select", _s.ActiveTool.Id);
        Assert.Equal([a], L.Children);
        _s.History.Undo();
        Assert.Equal(3, L.Children.Count);                                               // Remove is one step, the bevel comes back
    }

    [Fact]
    public async Task The_tool_follows_a_changed_selection_and_leaves_when_nothing_can_be_beveled()
    {
        var a = AddRect(0, 0, 200, 100);
        var b = AddRect(400, 0, 100, 100);
        _s.Selection.Set(a);
        var tool = _s.GetTool<BevelTool>();
        _s.BeginBevel();
        await tool.Settled;
        Assert.Equal(4, L.Children.Count);                                               // only a is beveled: [a, highlight, shadow, b]

        _s.Selection.Clear();
        Assert.Equal("select", _s.ActiveTool.Id);                                        // nothing selected: the tool ends, the bevel stays
        Assert.Equal(4, L.Children.Count);
    }

    // ---- The light direction ----

    [Fact]
    public void The_light_angle_decides_which_edges_get_the_highlight_and_the_default_is_the_upper_left()
    {
        Assert.Equal(135, new BevelSettings().LightAngle);
        var a = AddRect(0, 0, 200, 100);
        _s.Selection.Set(a);
        _s.ApplyBevel(new BevelSettings { LightAngle = 0 });                               // from the right
        Assert.True(Highlight(a).Geometry.Contains(new Vec2(198, 50)));
        Assert.False(Highlight(a).Geometry.Contains(new Vec2(2, 50)));
        Assert.True(Shadow(a).Geometry.Contains(new Vec2(2, 50)));
        Assert.True(Highlight(a).Geometry.Contains(new Vec2(197.1, 50)));                  // 2 wide, as for the upper left
        Assert.False(Highlight(a).Geometry.Contains(new Vec2(196.8, 50)));
        _s.History.Undo();

        _s.Selection.Set(a);
        _s.ApplyBevel(new BevelSettings { LightAngle = 90 });                              // from above
        Assert.True(Highlight(a).Geometry.Contains(new Vec2(100, 2)));
        Assert.False(Highlight(a).Geometry.Contains(new Vec2(2, 50)));
        Assert.True(Shadow(a).Geometry.Contains(new Vec2(100, 98)));
        _s.History.Undo();

        _s.Selection.Set(a);
        _s.ApplyBevel(new BevelSettings { LightAngle = 495 });                             // 495 is 135 again
        Assert.True(Highlight(a).Geometry.Contains(new Vec2(2, 50)));
        Assert.True(Highlight(a).Geometry.Contains(new Vec2(100, 2)));
    }

    [Fact]
    public async Task Dragging_the_light_point_turns_the_light_live_in_one_undo_step_and_the_bar_angle_follows()
    {
        var a = AddRect(0, 0, 200, 100);
        _s.Selection.Set(a);
        var tool = _s.GetTool<BevelTool>();
        int steps = UndoSteps;
        _s.BeginBevel();
        await tool.Settled;
        var start = tool.LightPoint!.Value;
        Assert.True(start.X < 100 && start.Y < 50);                                          // on the dial, toward the upper left of the center (100, 50)
        Assert.Equal(135, tool.Settings.LightAngle);

        _s.PointerDown(At(start.X, start.Y, PointerButton.Left));                            // grab the round point
        _s.PointerMove(At(100, -200));                                                       // straight above the center
        Assert.Equal(90, tool.Settings.LightAngle, 1);
        await tool.Settled;
        Assert.True(Highlight(a).Geometry.Contains(new Vec2(100, 2)));                       // the bevel followed while the pointer was still down
        Assert.False(Highlight(a).Geometry.Contains(new Vec2(2, 50)));
        _s.PointerMove(At(400, 50));                                                         // to the right
        _s.PointerUp(At(400, 50, PointerButton.Left));
        Assert.Equal(0, tool.Settings.LightAngle, 1);
        await tool.Settled;
        Assert.True(Highlight(a).Geometry.Contains(new Vec2(198, 50)));
        Assert.Equal(steps + 1, UndoSteps);                                                  // the whole session is one step

        _s.PointerDown(At(150, 50, PointerButton.Left));                                     // inside the object, away from the dial: nothing happens
        _s.PointerMove(At(100, 0));
        _s.PointerUp(At(100, 0, PointerButton.Left));
        Assert.Equal(0, tool.Settings.LightAngle, 1);

        tool.Update(s => s with { LightAngle = 270 });                                       // the bar's field
        await tool.Settled;
        Assert.True(Highlight(a).Geometry.Contains(new Vec2(100, 98)));                      // from below
        var items = new List<OverlayItem>();
        tool.BuildOverlay(items);
        var dial = Assert.Single(items.OfType<PointHandlesOverlay>());
        Assert.NotNull(dial.Ring);
        Assert.Equal(tool.LightPoint!.Value, dial.Points[0].Position);

        _s.History.Undo();
        Assert.Equal([a], L.Children);
    }

    [Fact]
    public async Task The_light_turns_with_a_turned_object_and_the_point_stays_on_the_side_the_light_comes_from()
    {
        var a = AddRect(0, 0, 200, 100);
        a.Transform = Affine2.Rotation(Math.PI / 2, new Vec2(100, 50));                      // turned a quarter turn (clockwise on the page)
        _s.Selection.Set(a);
        var tool = _s.GetTool<BevelTool>();
        _s.BeginBevel();
        await tool.Settled;
        var center = _s.Selection.GetBounds().Center;
        var p = tool.LightPoint!.Value;
        // for the object the light is at its upper left; a quarter turn clockwise puts that at the upper right of the page
        Assert.True(p.X > center.X && p.Y < center.Y);
        _s.PointerDown(At(p.X, p.Y, PointerButton.Left));
        _s.PointerMove(At(p.X + 1, p.Y - 1));
        _s.PointerUp(At(p.X + 1, p.Y - 1, PointerButton.Left));
        Assert.Equal(135, tool.Settings.LightAngle, 0);                                       // still the object's upper left
    }
}