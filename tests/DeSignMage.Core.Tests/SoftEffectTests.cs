using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Geometry;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Effects ▸ Soft Shadow, Glow and Feather (effects step 6, the soft-paint renderer's side that lives in the model; R7, R8, R13, R22).</summary>
public class SoftEffectTests
{
    private readonly EditorSession _s;
    private Document Doc => _s.Document;
    private Layer L => _s.Document.ActiveLayer;
    private static readonly ObjectStyle Red = ObjectStyle.FillOnly(DocColor.FromHex("#CC0000"));

    public SoftEffectTests()
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

    private int UndoSteps => _s.History.UndoList.Count;

    private static PointerInput At(double x, double y) => new(new Vec2(x, y), new Vec2(x, y), PointerButton.Left, ModifierKeys.None);

    private SoftEffectTool Tool => _s.GetTool<SoftEffectTool>();
    private FeatherTool Feather => _s.GetTool<FeatherTool>();

    private static PathObject EffectOf(DocObject source, SoftKind kind) =>
        Assert.IsType<PathObject>(SoftEffect.Find((source as GroupObject) ?? (GroupObject)source.Parent!, kind));

    // ---- Soft Shadow ----

    [Fact]
    public void Starting_the_Soft_Shadow_tool_makes_the_shadow_at_once_as_one_grouped_object_below_the_source()
    {
        var below = AddRect(300, 300, 10, 10);
        var a = AddRect(0, 0, 100, 100);
        var above = AddRect(400, 400, 10, 10);
        _s.Selection.Set(a);
        int steps = UndoSteps;

        Assert.True(_s.BeginSoftEffect(SoftKind.SoftShadow));

        Assert.Equal(steps + 1, UndoSteps);
        Assert.Equal(4, L.Children.Count);
        var shadow = Assert.IsType<PathObject>(L.Children[1]);                       // [below, shadow, a, above]
        Assert.Same(below, L.Children[0]);
        Assert.Same(a, L.Children[2]);
        Assert.Same(above, L.Children[3]);
        Assert.Equal("Soft Shadow", shadow.Name);
        var group = Assert.IsType<GroupObject>(a.Parent);
        Assert.Same(group, shadow.Parent);
        Assert.Equal([group], _s.Selection.Items);

        var blur = Assert.IsType<BlurStyle>(shadow.Style.Blur);
        Assert.Equal(SoftKind.SoftShadow, blur.Kind);
        Assert.Equal(0.5, blur.Opacity);
        Assert.Equal(0.5, blur.Softness);
        Assert.Equal(100, blur.Reference, 6);                                         // the blur is a fraction of the source's height
        Assert.Equal(10, blur.Hs, 6);                                                 // 10 % of the height, to the right
        Assert.Equal(-10, blur.Vs, 6);                                                // and down (Vs counts up)
        Assert.Equal(DocColor.Black, ((SolidPaint)shadow.Style.Fill!).Color);
        Assert.Null(shadow.Style.Stroke);
        Assert.Equal(new Rect2(10, 10, 110, 110), shadow.GetWorldBounds());           // the outline is vector and sits where it was moved to
        Assert.Equal(0.068 * 0.5 * 100, blur.Sigma, 6);
    }

    [Fact]
    public void Changing_fields_edits_the_same_undo_step_and_Done_keeps_it()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        int steps = UndoSteps;
        _s.BeginSoftEffect(SoftKind.SoftShadow);

        Tool.Update(s => s with { Opacity = 0.8 });
        Tool.Update(s => s with { Softness = 1.2 });
        Tool.Update(s => s with { ShiftX = 20, ShiftY = 30, Color = DocColor.FromHex("#202020") });

        Assert.Equal(steps + 1, UndoSteps);                                           // still one step
        var shadow = EffectOf(a, SoftKind.SoftShadow);
        var blur = shadow.Style.Blur!;
        Assert.Equal(0.8, blur.Opacity);
        Assert.Equal(1.2, blur.Softness);
        Assert.Equal(20, blur.Hs);
        Assert.Equal(-30, blur.Vs);
        Assert.Equal(new Rect2(20, 30, 120, 130), shadow.GetWorldBounds());
        Assert.Equal(DocColor.FromHex("#202020"), ((SolidPaint)shadow.Style.Fill!).Color);

        Tool.Done();
        Assert.Equal("select", _s.ActiveTool.Id);
        Assert.Equal(steps + 1, UndoSteps);
        Assert.Equal(1, Doc.GroupCount);

        _s.History.Undo();
        Assert.Equal(0, Doc.GroupCount);
        Assert.Equal([a], L.Children);
        _s.History.Redo();
        var back = EffectOf(a, SoftKind.SoftShadow);
        Assert.Equal(0.8, back.Style.Blur!.Opacity);                                  // redo brings back the last state, not the first
        Assert.Equal(new Rect2(20, 30, 120, 130), back.GetWorldBounds());
    }

    [Fact]
    public void Escape_takes_back_everything_the_tool_did()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        int steps = UndoSteps;
        _s.BeginSoftEffect(SoftKind.SoftShadow);
        Tool.Update(s => s with { Opacity = 0.9 });

        Assert.True(Tool.KeyDown(EditorKey.Escape, ModifierKeys.None));

        Assert.Equal("select", _s.ActiveTool.Id);
        Assert.Equal(steps, UndoSteps);
        Assert.Equal(0, Doc.GroupCount);
        Assert.Equal([a], L.Children);
    }

    [Fact]
    public void Dragging_on_the_canvas_moves_the_shadow()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        _s.BeginSoftEffect(SoftKind.SoftShadow);
        Tool.Update(s => s with { ShiftX = 0, ShiftY = 0 });
        int steps = UndoSteps;

        Tool.PointerDown(At(50, 50));
        Tool.PointerMove(At(60, 70));
        Tool.PointerUp(At(65, 80));

        var blur = EffectOf(a, SoftKind.SoftShadow).Style.Blur!;
        Assert.Equal(15, blur.Hs, 6);
        Assert.Equal(-30, blur.Vs, 6);
        Assert.Equal(steps, UndoSteps);                                               // the drag is part of the same step
    }

    [Fact]
    public void Reopening_the_tool_on_a_shadowed_object_edits_that_shadow_instead_of_making_another()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        _s.BeginSoftEffect(SoftKind.SoftShadow);
        Tool.Update(s => s with { Opacity = 0.7, Softness = 0.9, ShiftX = 14, ShiftY = 6 });
        Tool.Done();
        int steps = UndoSteps;
        var group = (GroupObject)a.Parent!;
        Assert.True(_s.HasSoftEffect(SoftKind.SoftShadow));

        _s.Selection.Set(group);
        _s.BeginSoftEffect(SoftKind.SoftShadow);

        Assert.True(Tool.HadEffect);
        Assert.Equal(0.7, Tool.Settings.Opacity);
        Assert.Equal(0.9, Tool.Settings.Softness);
        Assert.Equal(14, Tool.Settings.ShiftX);
        Assert.Equal(2, group.Children.Count);                                        // still the source and one shadow
        Assert.Equal(steps, UndoSteps);                                               // nothing changed by only opening it

        Tool.Update(s => s with { Opacity = 0.4 });
        Assert.Equal(steps + 1, UndoSteps);
        Assert.Equal(2, group.Children.Count);
        Assert.Equal(0.4, EffectOf(group, SoftKind.SoftShadow).Style.Blur!.Opacity);
        Tool.Done();
        _s.History.Undo();
        Assert.Equal(0.7, EffectOf(group, SoftKind.SoftShadow).Style.Blur!.Opacity);
    }

    [Fact]
    public void Remove_deletes_the_effect_and_undo_brings_it_back()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        _s.BeginSoftEffect(SoftKind.SoftShadow);
        Tool.Done();
        var group = (GroupObject)a.Parent!;
        var shadow = EffectOf(group, SoftKind.SoftShadow);
        int steps = UndoSteps;

        _s.Selection.Set(group);
        _s.BeginSoftEffect(SoftKind.SoftShadow);
        Tool.Remove();

        Assert.Equal(steps + 1, UndoSteps);
        Assert.False(_s.HasSoftEffect(SoftKind.SoftShadow));
        Assert.Equal([a], L.Children.OfType<RectangleObject>());
        Assert.DoesNotContain(shadow, L.Children);

        _s.History.Undo();
        Assert.Contains(shadow, L.Children);
        Assert.Same(group, shadow.Parent);
    }

    [Fact]
    public void Remove_on_a_new_effect_just_takes_it_back()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        int steps = UndoSteps;
        _s.BeginSoftEffect(SoftKind.SoftShadow);
        Tool.Remove();
        Assert.Equal(steps, UndoSteps);
        Assert.Equal([a], L.Children);
    }

    [Fact]
    public void The_menu_commands_need_a_shape_with_an_area()
    {
        Assert.False(_s.CanSoftEffect);
        var line = new PathObject(new PathBuilder().MoveTo(0, 0).LineTo(100, 0).Build()) { Style = new ObjectStyle(null, StrokeStyle.Solid(DocColor.Black, 2)) };
        _s.History.Execute(new AddObjectCommand(L, line));
        _s.Selection.Set(line);
        Assert.False(_s.CanSoftEffect);
        Assert.False(_s.BeginSoftEffect(SoftKind.Glow));
        var a = AddRect(0, 0, 10, 10);
        _s.Selection.Set(a);
        Assert.True(_s.CanSoftEffect);
    }

    [Fact]
    public void A_group_gets_one_shadow_from_the_union_of_its_members_that_joins_the_group()
    {
        var a = AddRect(0, 0, 50, 50);
        var b = AddRect(30, 0, 50, 50);   // overlaps a: one shape, one shadow, no double dark where they overlap
        var group = _s.GroupObjects([a, b])!;
        _s.Selection.Set(group);

        _s.BeginSoftEffect(SoftKind.SoftShadow);

        Assert.Equal(3, group.Children.Count);
        var shadow = EffectOf(group, SoftKind.SoftShadow);
        Assert.Equal(new Rect2(5, 5, 85, 55), shadow.GetWorldBounds());               // the union (0..80 x 0..50), moved by 10 % of its height (5)
        Assert.Equal(50, shadow.Style.Blur!.Reference, 6);
        Assert.Equal(1, Doc.GroupCount);
        // the shadow is below everything in the group
        Assert.Same(shadow, L.Children[0]);
    }

    [Fact]
    public void The_blur_scales_with_the_group_it_is_in()
    {
        var a = AddRect(0, 0, 100, 100);
        var b = AddRect(200, 0, 100, 100);
        var group = _s.GroupObjects([a, b])!;
        _s.History.Execute(new TransformObjectsCommand("Scale", [(group, Affine2.Scale(2, 2))]));
        _s.Selection.Set(group);

        _s.BeginSoftEffect(SoftKind.SoftShadow);

        var shadow = EffectOf(group, SoftKind.SoftShadow);
        var blur = shadow.Style.Blur!;
        Assert.Equal(200, shadow.GetWorldBounds().Height - 0, 0);                    // the shadow of two 200-point boxes: height 200
        Assert.Equal(100, blur.Reference, 6);                                         // in the group's own units (the group is scaled by 2)
        var world = shadow.GetWorldTransform();
        Assert.Equal(0.068 * 0.5 * 200, blur.Sigma * Math.Sqrt(Math.Abs(world.Determinant)), 6);   // 6.8 points on the page
    }

    [Fact]
    public void Inset_pulls_the_shadows_outline_in_and_a_negative_inset_grows_it()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        var shadow = _s.ApplySoftEffect(new SoftSettings { Inset = 0.5 }).Single();   // 0.04 * 0.5 * 100 = 2 points per side
        var b = shadow.GetWorldBounds();
        Assert.Equal(2, b.Left, 1);
        Assert.Equal(98, b.Right, 1);

        _s.History.Undo();
        _s.Selection.Set(a);
        var grown = _s.ApplySoftEffect(new SoftSettings { Inset = -0.5 }).Single();
        Assert.Equal(-2, grown.GetWorldBounds().Left, 1);
    }

    [Fact]
    public void A_cast_shadow_is_squashed_about_the_bottom_and_slanted()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        var shadow = _s.ApplySoftEffect(new SoftSettings { HeightScale = 0.5, Slant = 45 }).Single();
        var bounds = shadow.GetWorldBounds();
        Assert.Equal(50, bounds.Top, 3);                                              // the bottom stays, the top is half way down
        Assert.Equal(100, bounds.Bottom, 3);
        Assert.Equal(0, bounds.Left, 3);                                              // the bottom edge is the pivot
        Assert.Equal(200, bounds.Right, 3);                                           // the top edge is pushed sideways by tan 45° × the full height (the slant is applied before the squash)
    }

    [Fact]
    public void Recoloring_a_group_does_not_recolor_its_shadow()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        _s.BeginSoftEffect(SoftKind.SoftShadow);
        Tool.Update(s => s with { Color = DocColor.FromHex("#303030") });
        Tool.Done();
        var group = (GroupObject)a.Parent!;
        _s.Selection.Set(group);

        _s.ApplyFill(new SolidPaint(DocColor.FromHex("#00AA00")));

        Assert.Equal(DocColor.FromHex("#00AA00"), ((SolidPaint)a.Style.Fill!).Color);
        Assert.Equal(DocColor.FromHex("#303030"), ((SolidPaint)EffectOf(group, SoftKind.SoftShadow).Style.Fill!).Color);
    }

    [Fact]
    public void A_shadow_follows_its_group_and_can_be_selected_alone_after_ungrouping()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        _s.BeginSoftEffect(SoftKind.SoftShadow);
        Tool.Done();
        var group = (GroupObject)a.Parent!;
        var shadow = EffectOf(group, SoftKind.SoftShadow);
        var before = shadow.GetWorldBounds();

        _s.History.Execute(TransformObjectsCommand.ApplyWorld("Move", [group], Affine2.Translation(50, 0)));
        Assert.Equal(before.Left + 50, shadow.GetWorldBounds().Left, 6);              // the shadow moved with the source: it is a member

        _s.Selection.Set(group);
        _s.History.Execute(new UngroupCommand(group));
        Assert.IsNotType<GroupObject>(shadow.Parent);                                  // R7: no link; the shadow is an ordinary object of the layer
        Assert.Contains(shadow, L.Children);
    }

    // ---- Glow ----

    [Fact]
    public void A_glow_is_a_vector_halo_that_grows_the_outline_and_has_no_shift()
    {
        var a = AddRect(0, 0, 100, 100, ObjectStyle.FillOnly(DocColor.FromHex("#101010")));
        _s.Selection.Set(a);
        int steps = UndoSteps;

        Assert.True(_s.BeginSoftEffect(SoftKind.Glow));

        Assert.Equal(steps + 1, UndoSteps);
        var glow = EffectOf(a, SoftKind.Glow);
        Assert.Equal("Glow", glow.Name);
        Assert.Equal(DocColor.White, ((SolidPaint)glow.Style.Fill!).Color);           // white on a dark source
        var blur = glow.Style.Blur!;
        Assert.Equal(0, blur.Hs);
        Assert.Equal(0, blur.Vs);
        Assert.Equal(-0.5, blur.Inset, 6);                                            // thickness 0.5 is stored as a negative inset
        Assert.Equal(-2, glow.GetWorldBounds().Left, 1);                              // grown by 0.04 × 0.5 × 100 = 2 points
        Assert.Equal(102, glow.GetWorldBounds().Right, 1);

        Tool.Update(s => s with { Thickness = 3 });
        Assert.Equal(-12, glow.GetWorldBounds().Left, 1);
        Assert.Equal(steps + 1, UndoSteps);
    }

    [Fact]
    public void Dragging_out_from_the_shape_sets_the_glow_thickness_as_twelve_times_distance_over_height()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        _s.BeginSoftEffect(SoftKind.Glow);

        Tool.PointerDown(At(100, 50));
        Tool.PointerMove(At(110, 50));        // 10 points outside of the right edge

        Assert.Equal(12 * 10 / 100.0, Tool.Settings.Thickness, 6);
        Tool.PointerMove(At(50, 50));         // inside: ignored
        Assert.Equal(12 * 10 / 100.0, Tool.Settings.Thickness, 6);
        Tool.PointerUp(At(110, 50));
    }

    [Fact]
    public void A_shadow_and_a_glow_can_live_in_the_same_group_the_newest_at_the_bottom()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        _s.BeginSoftEffect(SoftKind.SoftShadow);
        Tool.Done();
        var group = (GroupObject)a.Parent!;
        _s.Selection.Set(group);
        _s.BeginSoftEffect(SoftKind.Glow);
        Tool.Done();

        Assert.Equal(3, group.Children.Count);
        var shadow = EffectOf(group, SoftKind.SoftShadow);
        var glow = EffectOf(group, SoftKind.Glow);
        Assert.Equal([glow, shadow, a], L.Children);
        Assert.True(_s.HasSoftEffect(SoftKind.Glow));
        Assert.Equal(0, shadow.Style.Blur!.Vs + 10, 6);                               // the shadow is untouched
    }

    // ---- Feather ----

    [Fact]
    public void Starting_Feather_feathers_the_selection_at_once_with_the_legacy_defaults()
    {
        var a = AddRect(0, 0, 100, 40);
        _s.Selection.Set(a);
        int steps = UndoSteps;

        Assert.True(_s.BeginFeather());

        Assert.Equal(steps + 1, UndoSteps);
        Assert.Equal(new FeatherStyle(0.04, 0.38), a.Style.Feather);
        Assert.Equal(Red.Fill, a.Style.Fill);                                          // the paint is untouched
        Assert.Single(L.Children);                                                    // no new object: the soft paint belongs to the object

        Feather.Update(f => f with { Amount = 0.1 });
        Feather.Update(f => f with { Inset = 0.5 });
        Assert.Equal(steps + 1, UndoSteps);
        Assert.Equal(new FeatherStyle(0.1, 0.5), a.Style.Feather);

        Feather.Remove();
        Assert.Null(a.Style.Feather);                                                 // new in this session: taken back
        Assert.Equal(steps, UndoSteps);
    }

    [Fact]
    public void Removing_an_existing_feather_is_its_own_undo_step()
    {
        var a = AddRect(0, 0, 100, 40);
        _s.Selection.Set(a);
        Assert.True(_s.SetFeather(0.08, 0.2));
        int steps = UndoSteps;

        _s.BeginFeather();
        Assert.True(Feather.HadEffect);
        Assert.Equal(new FeatherStyle(0.08, 0.2), Feather.Settings);
        Assert.Equal(steps, UndoSteps);
        Feather.Remove();

        Assert.Null(a.Style.Feather);
        Assert.Equal(steps + 1, UndoSteps);
        Assert.Equal("Remove Feather", _s.History.UndoList[^1].Description);
        _s.History.Undo();
        Assert.Equal(new FeatherStyle(0.08, 0.2), a.Style.Feather);
    }

    [Fact]
    public void Dragging_sets_the_feather_from_the_distance_over_the_height_and_a_group_feathers_its_members()
    {
        var a = AddRect(0, 0, 100, 100);
        var b = AddRect(200, 0, 100, 100);
        var group = _s.GroupObjects([a, b])!;
        _s.Selection.Set(group);
        _s.BeginFeather();

        Feather.PointerDown(At(10, 10));
        Feather.PointerMove(At(10, 20));      // 10 points of a 100-point object

        Assert.Equal(0.1, Feather.Settings.Amount, 6);
        Assert.Equal(0.1, a.Style.Feather!.Amount, 6);
        Assert.Equal(0.1, b.Style.Feather!.Amount, 6);                                // each member
        Assert.Null(group.Style.Feather);                                              // a group itself is not feathered
        Feather.Cancel();
        Assert.Equal(0.04, a.Style.Feather!.Amount, 6);                               // Esc during a drag puts it back
    }

    [Fact]
    public void Feather_stays_off_the_shadow_objects_of_a_group()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        _s.BeginSoftEffect(SoftKind.SoftShadow);
        Tool.Done();
        var group = (GroupObject)a.Parent!;
        _s.Selection.Set(group);
        _s.SetFeather(0.05, 0.3);
        Assert.NotNull(a.Style.Feather);
        Assert.Null(EffectOf(group, SoftKind.SoftShadow).Style.Feather);
    }

    [Fact]
    public void Feather_amount_zero_means_no_feather()
    {
        Assert.Null(new ObjectStyle(null, null).WithFeather(new FeatherStyle(0, 0.4)).Feather);
        Assert.Equal(new FeatherStyle(0.04, 0.38), new ObjectStyle(null, null).WithFeather(new FeatherStyle(0.04, 0.38)).Feather);
    }
}
