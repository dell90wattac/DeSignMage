using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Geometry;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Effects ▸ Shadow, Extruded Shadow, Perspective Shadow and Cast Shadow (effects step 3; R7, R13, R22).</summary>
public class ShadowTests
{
    private readonly EditorSession _s;
    private Document Doc => _s.Document;
    private Layer L => _s.Document.ActiveLayer;
    private static readonly ObjectStyle Red = ObjectStyle.FillOnly(DocColor.FromHex("#CC0000"));

    public ShadowTests()
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

    private static PointerInput At(double x, double y, PointerButton b = PointerButton.None, ModifierKeys m = ModifierKeys.None) =>
        new(new Vec2(x, y), new Vec2(x, y), b, m);

    private int UndoSteps => _s.History.UndoList.Count;

    private static ShadowSettings Drop(double dx, double dy, double outline = 0) => new() { ShiftX = dx, ShiftY = dy, Outline = outline };

    private static void AssertRect(Rect2 expected, Rect2 actual, double tol = 0.05)
    {
        Assert.InRange(actual.Left, expected.Left - tol, expected.Left + tol);
        Assert.InRange(actual.Top, expected.Top - tol, expected.Top + tol);
        Assert.InRange(actual.Right, expected.Right - tol, expected.Right + tol);
        Assert.InRange(actual.Bottom, expected.Bottom - tol, expected.Bottom + tol);
    }

    /// <summary>The shadow object a command made for <paramref name="source"/> (the one named like a shadow in the same group).</summary>
    private static PathObject ShadowOf(DocObject source)
    {
        var group = source.Parent as GroupObject ?? (GroupObject)source;
        return group.Children.OfType<PathObject>().Single(p => p.Name?.Contains("Shadow") == true);
    }

    // ---- Drop and relief, flat ----

    [Fact]
    public void A_drop_shadow_is_a_new_object_grouped_with_its_source_and_just_below_it()
    {
        var below = AddRect(300, 300, 10, 10);
        var a = AddRect(0, 0, 100, 100);
        var above = AddRect(400, 400, 10, 10);
        _s.Selection.Set(a);
        int steps = UndoSteps;

        var outcome = _s.Shadow(Drop(10, 10));

        Assert.True(outcome.Applied);
        Assert.Equal(steps + 1, UndoSteps);                                          // one undo step
        Assert.Equal(4, L.Children.Count);
        var shadow = Assert.IsType<PathObject>(L.Children[1]);                       // [below, shadow, a, above]
        Assert.Same(below, L.Children[0]);
        Assert.Same(a, L.Children[2]);
        Assert.Same(above, L.Children[3]);                                           // grouping and the shadow moved nothing else
        Assert.Equal("Shadow", shadow.Name);
        var group = Assert.IsType<GroupObject>(a.Parent);                            // R13: grouped with its source...
        Assert.Same(group, shadow.Parent);
        Assert.Equal(2, group.Children.Count);
        Assert.Equal([group], _s.Selection.Items);
        Assert.Equal(1, Doc.GroupCount);
        Assert.Equal(new Rect2(0, 0, 110, 110), shadow.GetWorldBounds());           // the object united with its copy moved by (10, 10)
        Assert.True(shadow.Geometry.Contains(new Vec2(105, 105)));
        Assert.True(shadow.Geometry.Contains(new Vec2(50, 50)));                     // a drop shadow contains the original
        Assert.False(shadow.Geometry.Contains(new Vec2(105, 5)));
        Assert.False(shadow.Geometry.Contains(new Vec2(5, 105)));
        Assert.Equal(new Rect2(0, 0, 100, 100), a.GetWorldBounds());                 // ...and the source is untouched
        Assert.Equal(Red, shadow.Style);                                              // copy of the source's color by default

        _s.History.Undo();
        Assert.Equal([below, a, above], L.Children);
        Assert.Equal(0, Doc.GroupCount);
        Assert.Same(L, a.Parent);
        _s.History.Redo();
        Assert.Equal(4, L.Children.Count);
        Assert.Equal(1, Doc.GroupCount);
    }

    [Fact]
    public void A_relief_shadow_has_the_original_cut_out_of_it()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        _s.Shadow(Drop(10, 10) with { Type = ShadowType.Relief });
        var shadow = ShadowOf(a);
        Assert.True(shadow.Geometry.Contains(new Vec2(105, 105)));
        Assert.True(shadow.Geometry.Contains(new Vec2(105, 50)));                    // the strip the shadow shows beyond the original
        Assert.False(shadow.Geometry.Contains(new Vec2(50, 50)));                    // the original's own area is cut out
        AssertRect(new Rect2(10, 10, 110, 110), shadow.GetWorldBounds());
    }

    [Fact]
    public void The_outline_grows_or_shrinks_the_shadow_body()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        _s.Shadow(Drop(10, 10, outline: 5));
        AssertRect(new Rect2(0, 0, 115, 115), ShadowOf(a).GetWorldBounds());        // body 5..115 united with the object
        _s.History.Undo();
        _s.Selection.Set(a);
        _s.Shadow(Drop(10, 10, outline: -5));                                        // the vinyl-shrink trap: the body is smaller
        AssertRect(new Rect2(0, 0, 105, 105), ShadowOf(a).GetWorldBounds());
    }

    [Fact]
    public void A_shape_with_a_hole_keeps_its_hole_in_the_shadow()
    {
        var b = new PathBuilder();
        b.MoveTo(0, 0).LineTo(100, 0).LineTo(100, 100).LineTo(0, 100).Close();
        b.MoveTo(30, 30).LineTo(30, 70).LineTo(70, 70).LineTo(70, 30).Close();
        var ring = Add(new PathObject(b.Build()) { Style = Red });
        _s.Selection.Set(ring);
        _s.Shadow(Drop(100, 0));
        var shadow = ShadowOf(ring);
        Assert.True(shadow.Geometry.Contains(new Vec2(110, 10)));
        Assert.False(shadow.Geometry.Contains(new Vec2(150, 50)));                   // the shifted hole stays a hole
        Assert.False(shadow.Geometry.Contains(new Vec2(50, 50)));                    // so does the original's own
    }

    // ---- Cast ----

    [Fact]
    public void A_cast_shadow_slants_the_top_edge_and_keeps_the_bottom_edge()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        _s.Shadow(new ShadowSettings { Form = ShadowForm.Cast, ShiftX = 20, ShiftY = 0 });   // Hs = 20, Vs = 0: same height, slanted
        var shadow = ShadowOf(a);
        AssertRect(new Rect2(0, 0, 120, 100), shadow.GetWorldBounds());
        Assert.True(shadow.Geometry.Contains(new Vec2(115, 5)));                     // the top edge moved 20 to the right
        Assert.False(shadow.Geometry.Contains(new Vec2(115, 95)));                   // at the bottom it has not
        Assert.True(shadow.Geometry.Contains(new Vec2(5, 95)));
    }

    [Fact]
    public void A_cast_shadow_with_a_negative_height_lies_under_the_baseline_like_a_floor_shadow()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        // Vs = -200 makes the height 100 + (-200) = -100: the shadow is flipped under the object's bottom edge, slanted by Hs = 30 at its far edge
        _s.Shadow(new ShadowSettings { Form = ShadowForm.Cast, ShiftX = 30, ShiftY = 200 });
        var shadow = ShadowOf(a);
        AssertRect(new Rect2(0, 0, 130, 200), shadow.GetWorldBounds());
        Assert.True(shadow.Geometry.Contains(new Vec2(125, 195)));                   // the far edge, pushed 30 sideways
        Assert.False(shadow.Geometry.Contains(new Vec2(125, 105)));                  // the edge on the baseline stays put
        Assert.True(shadow.Geometry.Contains(new Vec2(50, 150)));
    }

    [Fact]
    public void A_cast_shadow_whose_height_is_zero_is_not_made()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        int steps = UndoSteps;
        var outcome = _s.Shadow(new ShadowSettings { Form = ShadowForm.Cast, ShiftX = 10, ShiftY = 100 });   // Vs = -100 = -height
        Assert.False(outcome.Applied);
        Assert.Equal(steps, UndoSteps);
        Assert.Equal([a], L.Children);
    }

    // ---- Extruded and perspective ----

    [Fact]
    public void An_extruded_shadow_is_the_solid_sweep_from_the_object_to_its_moved_copy()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        _s.Shadow(new ShadowSettings { Form = ShadowForm.Extruded, ShiftX = 30, ShiftY = 30 });
        var shadow = ShadowOf(a);
        Assert.Equal("Extruded Shadow", shadow.Name);
        AssertRect(new Rect2(0, 0, 130, 130), shadow.GetWorldBounds());
        Assert.Equal(1, EffectGeometry.CountLoops(shadow.Geometry).Closed);          // one welded outline, no loose planes
        Assert.True(shadow.Geometry.Contains(new Vec2(125, 35)));                    // the far copy
        Assert.True(shadow.Geometry.Contains(new Vec2(35, 125)));                    // the plane under the bottom edge
        Assert.False(shadow.Geometry.Contains(new Vec2(5, 125)));                    // outside the sweep
        Assert.False(shadow.Geometry.Contains(new Vec2(125, 5)));
        Assert.Same(a.Parent, shadow.Parent);
    }

    [Fact]
    public void A_perspective_shadow_narrows_to_the_depth_percentage()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        _s.Shadow(new ShadowSettings { Form = ShadowForm.Perspective, ShiftX = 100, ShiftY = 0, DepthPercent = 50 });
        var shadow = ShadowOf(a);
        Assert.Equal("Perspective Shadow", shadow.Name);
        // the far copy is half size about the center (50, 50), then moved 100: x 125..175, y 25..75
        AssertRect(new Rect2(0, 0, 175, 100), shadow.GetWorldBounds());
        Assert.True(shadow.Geometry.Contains(new Vec2(150, 50)));
        Assert.True(shadow.Geometry.Contains(new Vec2(150, 30)));                    // inside the converging planes
        Assert.False(shadow.Geometry.Contains(new Vec2(150, 10)));                   // above the top plane, which runs from (100,0) to (125,25)
        Assert.False(shadow.Geometry.Contains(new Vec2(170, 90)));
        Assert.Equal(1, EffectGeometry.CountLoops(shadow.Geometry).Closed);
    }

    [Fact]
    public void An_extruded_relief_shadow_cuts_the_object_out_of_the_sweep()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        _s.Shadow(new ShadowSettings { Form = ShadowForm.Extruded, Type = ShadowType.Relief, ShiftX = 30, ShiftY = 0 });
        var shadow = ShadowOf(a);
        Assert.True(shadow.Geometry.Contains(new Vec2(115, 50)));
        Assert.False(shadow.Geometry.Contains(new Vec2(50, 50)));
        AssertRect(new Rect2(100, 0, 130, 100), shadow.GetWorldBounds());
    }

    // ---- Sources: combined objects, groups, text, several objects ----

    [Fact]
    public void A_combined_source_shadows_its_whole_silhouette_not_its_knocked_out_overlap()
    {
        var a = AddRect(0, 0, 100, 100);
        var b = AddRect(50, 50, 100, 100);
        _s.Selection.Set([a, b]);
        var combined = _s.CombineSelection()!;
        _s.Selection.Set(combined);

        _s.Shadow(Drop(10, 10));

        var shadow = ShadowOf(combined);
        Assert.True(shadow.Geometry.Contains(new Vec2(75, 75)));                    // the overlap is part of the silhouette
        AssertRect(new Rect2(0, 0, 160, 160), shadow.GetWorldBounds());
        Assert.Same(combined, combined.Parent is GroupObject g ? g.Children.Single(c => c == combined) : null);
    }

    [Fact]
    public void A_group_source_gets_the_shadow_as_one_more_member_below_all_of_its_members()
    {
        var a = AddRect(0, 0, 50, 50);
        var between = AddRect(500, 500, 10, 10);
        var b = AddRect(60, 0, 50, 50);
        var group = _s.GroupObjects([a, b])!;
        group.Transform = Affine2.Translation(200, 100);                            // a moved group: the shadow must land in the right place
        _s.Selection.Set(group);
        int groups = Doc.GroupCount;

        _s.Shadow(Drop(10, 10));

        Assert.Equal(groups, Doc.GroupCount);                                        // no new group: the shadow joined this one
        Assert.Equal(3, group.Children.Count);
        var shadow = ShadowOf(group);
        Assert.Same(group, shadow.Parent);
        Assert.Equal([shadow, a, between, b], L.Children);                          // below everything of the group; the object between stays between
        AssertRect(new Rect2(200, 100, 320, 160), shadow.GetWorldBounds());        // both squares united with their copy moved by (10, 10)
        _s.History.Undo();
        Assert.Equal([a, between, b], L.Children);
        Assert.Equal(2, group.Children.Count);
    }

    [Fact]
    public async Task A_text_source_stays_live_text_and_gets_a_separate_shadow_object()
    {
        var text = Add(new TextObject("Hi", "Arial", 100) { Transform = Affine2.Translation(100, 300) });
        _s.Selection.Set(text);
        _s.Shadow(Drop(6, 6));

        Assert.Equal("Hi", text.Text);
        Assert.IsType<TextObject>(text);
        var group = Assert.IsType<GroupObject>(text.Parent);
        var shadow = ShadowOf(text);
        Assert.IsType<PathObject>(shadow);
        Assert.Same(shadow, L.Children[0]);
        Assert.True(shadow.GetWorldBounds().Width > text.GetWorldBounds().Width + 5);
        Assert.Equal(text.Style, shadow.Style);

        text.Text = "Hello";                                                            // no live link: the old shadow stays as it was
        Assert.Equal("Shadow", shadow.Name);
        await Task.CompletedTask;
    }

    [Fact]
    public void Several_selected_objects_each_get_their_own_shadow_in_one_undo_step()
    {
        var a = AddRect(0, 0, 50, 50);
        var b = AddRect(200, 0, 50, 50);
        _s.Selection.Set([a, b]);
        int steps = UndoSteps;
        _s.Shadow(Drop(10, 10));
        Assert.Equal(steps + 1, UndoSteps);
        Assert.Equal(4, L.Children.Count);
        Assert.Equal(2, Doc.GroupCount);
        Assert.NotSame(a.Parent, b.Parent);
        Assert.Equal(2, _s.Selection.Count);
        _s.History.Undo();
        Assert.Equal([a, b], L.Children);
        Assert.Equal(0, Doc.GroupCount);
    }

    [Fact]
    public void The_shadow_color_can_be_chosen_or_copied_from_the_source()
    {
        var stroked = new ObjectStyle(new SolidPaint(DocColor.FromHex("#CC0000")), StrokeStyle.Solid(DocColor.Black, 2));
        var a = AddRect(0, 0, 100, 100, stroked);
        _s.Selection.Set(a);
        _s.Shadow(Drop(10, 10));
        Assert.Equal(stroked, ShadowOf(a).Style);                                     // default: a copy, stroke and all
        _s.History.Undo();
        _s.Selection.Set(a);
        _s.Shadow(Drop(10, 10) with { Color = DocColor.FromHex("#404040") });
        Assert.Equal(ObjectStyle.FillOnly(DocColor.FromHex("#404040")), ShadowOf(a).Style);
    }

    // ---- When it is available ----

    [Fact]
    public void A_shadow_needs_a_shape_with_an_area_a_shift_and_no_picture()
    {
        Assert.False(_s.CanShadow);                                                   // nothing selected
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        Assert.True(_s.CanShadow);
        Assert.False(_s.Shadow(new ShadowSettings()).Applied);                         // a shift of 0 makes no shadow
        var line = Add(new PathObject(new PathBuilder().MoveTo(0, 200).LineTo(50, 250).Build()));
        _s.Selection.Set(line);
        Assert.False(_s.CanShadow);                                                   // a line has no area
        var asset = Doc.Assets.Add([1, 2, 3], "image/png");
        var img = Add(new ImageObject(asset, 50, 50));
        _s.Selection.Set([a, img]);
        Assert.False(_s.CanShadow);
        _s.Selection.Set(a);
        _s.OffsetService = null;
        Assert.False(_s.CanShadow);
        Assert.False(_s.BeginShadow(ShadowForm.Flat));
    }

    // ---- The tool ----

    [Fact]
    public void The_tool_starts_with_a_three_percent_shift_and_an_extruded_form_with_a_one_percent_outline()
    {
        var a = AddRect(0, 0, 100, 200);
        _s.Selection.Set(a);
        var tool = _s.GetTool<ShadowTool>();
        tool.PreviewDelay = TimeSpan.Zero;
        Assert.True(_s.BeginShadow(ShadowForm.Flat));
        Assert.Equal("shadow", _s.ActiveTool.Id);
        Assert.Equal(6, tool.Settings.ShiftX, 6);                                      // 3 % of the 200 pt height, to the lower right
        Assert.Equal(6, tool.Settings.ShiftY, 6);
        Assert.Equal(0, tool.Settings.Outline);
        Assert.True(tool.CanApply);

        _s.BeginShadow(ShadowForm.Extruded);                                           // another form from the menu: same tool
        Assert.Equal(ShadowForm.Extruded, tool.Settings.Form);
        Assert.Equal(2, tool.Settings.Outline, 6);                                     // 1 %
        tool.SetForm(ShadowForm.Perspective);
        Assert.Equal(ShadowForm.Perspective, tool.Settings.Form);
        Assert.Equal(75, tool.Settings.DepthPercent);
    }

    [Fact]
    public async Task Dragging_moves_the_shadow_and_shift_dragging_from_an_edge_sets_the_outline_then_apply_makes_it()
    {
        var a = AddRect(200, 200, 100, 100);
        _s.Selection.Set(a);
        var tool = _s.GetTool<ShadowTool>();
        tool.PreviewDelay = TimeSpan.Zero;
        _s.BeginShadow(ShadowForm.Flat);
        double h0 = tool.Settings.ShiftX;

        _s.PointerDown(At(250, 250, PointerButton.Left));
        _s.PointerMove(At(270, 240));
        _s.PointerUp(At(270, 240, PointerButton.Left));
        Assert.Equal(h0 + 20, tool.Settings.ShiftX, 6);                                // moved by the drag, added to the shift it started with
        Assert.Equal(h0 - 10, tool.Settings.ShiftY, 6);

        _s.PointerDown(At(306, 250, PointerButton.Left, ModifierKeys.Shift));          // 6 outside the right edge, within the pick distance
        _s.PointerUp(At(306, 250, PointerButton.Left, ModifierKeys.Shift));
        Assert.Equal(6, tool.Settings.Outline, 6);
        _s.PointerDown(At(295, 250, PointerButton.Left, ModifierKeys.Shift));          // inside: negative, clamped to 0
        _s.PointerUp(At(295, 250, PointerButton.Left, ModifierKeys.Shift));
        Assert.Equal(0, tool.Settings.Outline);
        _s.PointerDown(At(250, 250, PointerButton.Left, ModifierKeys.Shift));          // not on an edge: Shift+drag does nothing
        _s.PointerMove(At(255, 255, PointerButton.None, ModifierKeys.Shift));
        _s.PointerUp(At(255, 255, PointerButton.Left, ModifierKeys.Shift));
        Assert.Equal(0, tool.Settings.Outline);

        await tool.PreviewSettled;
        Assert.Single(tool.PreviewPaths);
        Assert.Single(L.Children);                                                     // nothing is made until Apply

        Assert.True(await tool.Apply());
        Assert.Equal("select", _s.ActiveTool.Id);
        Assert.Equal(2, L.Children.Count);
        Assert.Equal("Shadow", L.Children[0].Name);
    }

    [Fact]
    public async Task Apply_is_refused_with_no_shift_and_escape_during_a_drag_puts_the_shadow_back()
    {
        var a = AddRect(200, 200, 100, 100);
        _s.Selection.Set(a);
        var tool = _s.GetTool<ShadowTool>();
        tool.PreviewDelay = TimeSpan.Zero;
        _s.BeginShadow(ShadowForm.Flat);
        tool.Update(s => s with { ShiftX = 0, ShiftY = 0 });
        Assert.False(tool.CanApply);
        Assert.False(await tool.Apply());
        Assert.Single(L.Children);

        tool.Update(s => s with { ShiftX = 8, ShiftY = 8 });
        _s.PointerDown(At(250, 250, PointerButton.Left));
        _s.PointerMove(At(300, 300));
        Assert.Equal(58, tool.Settings.ShiftX, 6);
        _s.KeyDown(EditorKey.Escape, ModifierKeys.None);
        Assert.Equal(8, tool.Settings.ShiftX, 6);
    }

    [Fact]
    public async Task The_tool_follows_the_object_when_it_is_moved_while_open()
    {
        var a = AddRect(200, 200, 100, 100);
        _s.Selection.Set(a);
        var tool = _s.GetTool<ShadowTool>();
        tool.PreviewDelay = TimeSpan.Zero;
        _s.BeginShadow(ShadowForm.Flat);
        tool.Update(s => s with { ShiftX = 10, ShiftY = 10 });
        _s.NudgeSelection(new Vec2(50, 0));
        Assert.True(await tool.Apply());
        var shadow = ShadowOf(a);
        AssertRect(new Rect2(250, 200, 360, 310), shadow.GetWorldBounds());
    }
}
