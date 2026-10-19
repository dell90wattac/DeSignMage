using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Geometry;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Live effects: outlines, shadows, soft shadows and bevels follow edits of their source, in the same undo step (user decision 2026-10-18).</summary>
public class LiveEffectTests
{
    private readonly EditorSession _s;
    private Document Doc => _s.Document;
    private Layer L => _s.Document.ActiveLayer;
    private static readonly ObjectStyle Red = ObjectStyle.FillOnly(DocColor.FromHex("#CC0000"));

    public LiveEffectTests()
    {
        _s = new EditorSession(NewDoc())
        {
            BooleanService = GeometryEngines.Booleans,
            OffsetService = GeometryEngines.Offsets,
        };
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);
    }

    private T Add<T>(T o) where T : DocObject
    {
        _s.History.Execute(new AddObjectCommand(L, o));
        return o;
    }

    private RectangleObject AddRect(double x, double y, double w, double h) => Add(Rect(x, y, w, h, Red));

    private int UndoSteps => _s.History.UndoList.Count;

    private static void AssertRect(Rect2 expected, Rect2 actual, double tol = 0.05)
    {
        Assert.InRange(actual.Left, expected.Left - tol, expected.Left + tol);
        Assert.InRange(actual.Top, expected.Top - tol, expected.Top + tol);
        Assert.InRange(actual.Right, expected.Right - tol, expected.Right + tol);
        Assert.InRange(actual.Bottom, expected.Bottom - tol, expected.Bottom + tol);
    }

    private PathObject Outline(DocObject source, double amount, OutlineKind kind = OutlineKind.Outline)
    {
        _s.Selection.Set(source);
        var outcome = _s.RunEffect(OutlineEffect.Plan(_s, kind, new OutlineSettings { First = amount })!);
        Assert.True(outcome.Applied);
        return Assert.IsType<PathObject>(Assert.Single(_s.Selection.Items));
    }

    private PathObject Shadow(DocObject source, double dx = 10, double dy = 10)
    {
        _s.Selection.Set(source.TopLevel);
        Assert.True(_s.Shadow(new ShadowSettings { ShiftX = dx, ShiftY = dy }).Applied);
        return LiveEffects.Find<LiveShadow>(Doc, source)!;
    }

    private void Transform(DocObject o, Affine2 worldDelta, string what = "Size") =>
        _s.History.Execute(TransformObjectsCommand.ApplyWorld(what, [o], worldDelta));

    /// <summary>The effect is what its source and settings make right now.</summary>
    private void AssertInStep(PathObject effect)
    {
        Assert.NotNull(effect.Live);
        var fresh = LiveEffects.Recompute(effect, _s.BooleanService, _s.OffsetService);
        Assert.NotNull(fresh);
        AssertRect(fresh!.Transform(effect.GetWorldTransform()).GetBounds(), effect.GetWorldBounds());
    }

    // ---- Outline / Inline ----

    [Fact]
    public void A_new_outline_is_live_and_follows_a_resize_of_its_source_in_the_same_undo_step()
    {
        var a = AddRect(100, 100, 100, 100);
        var o = Outline(a, 10);
        Assert.Same(a, o.Live!.Source);
        Assert.Null(o.Parent as GroupObject);                                          // standalone, as before
        AssertRect(new Rect2(90, 90, 210, 210), o.GetWorldBounds());
        int steps = UndoSteps;

        Transform(a, Affine2.Scale(2, 1, new Vec2(100, 100)));

        Assert.Equal(steps + 1, UndoSteps);                                            // one step for the resize and the rebuild
        AssertRect(new Rect2(90, 90, 310, 210), o.GetWorldBounds());                   // the outline keeps its 10 pt width
        _s.Undo();
        AssertRect(new Rect2(90, 90, 210, 210), o.GetWorldBounds());
        Assert.Equal(new Rect2(100, 100, 200, 200), a.GetWorldBounds());
        _s.Redo();
        AssertRect(new Rect2(90, 90, 310, 210), o.GetWorldBounds());
        AssertInStep(o);
    }

    [Fact]
    public void Moving_the_source_moves_its_outline_without_recomputing_it()
    {
        var a = AddRect(100, 100, 100, 100);
        var o = Outline(a, 10);
        int rebuilds = _s.LiveUpdater.Rebuilds, shifts = _s.LiveUpdater.Shifts;

        Transform(a, Affine2.Translation(50, 20), "Move");

        Assert.Equal(rebuilds, _s.LiveUpdater.Rebuilds);
        Assert.Equal(shifts + 1, _s.LiveUpdater.Shifts);
        AssertRect(new Rect2(140, 110, 260, 230), o.GetWorldBounds());
        AssertInStep(o);
    }

    [Fact]
    public void A_node_edit_rebuilds_the_inline_and_a_shape_turned_into_a_path_keeps_its_effects()
    {
        var a = AddRect(100, 100, 100, 100);
        var inline = Outline(a, 10, OutlineKind.Inline);
        AssertRect(new Rect2(110, 110, 190, 190), inline.GetWorldBounds());

        // the rectangle becomes a path on its first node edit: the inline follows the new object
        var edited = PathGeometry.Rectangle(new Rect2(0, 0, 200, 100));
        var cmd = new SetVectorsCommand("Move Node", a, -1, edited);
        _s.History.Execute(cmd);

        Assert.NotSame(a, cmd.Result);
        Assert.Same(cmd.Result, inline.Live!.Source);
        AssertRect(new Rect2(110, 110, 290, 190), inline.GetWorldBounds());
        _s.Undo();
        Assert.Same(a, inline.Live!.Source);
        AssertRect(new Rect2(110, 110, 190, 190), inline.GetWorldBounds());
    }

    [Fact]
    public void Reopening_the_outline_tool_on_a_live_outline_edits_its_settings_in_place()
    {
        var a = AddRect(100, 100, 100, 100);
        var o = Outline(a, 10);
        int count = L.Children.Count;
        _s.Selection.Set(o);

        Assert.True(_s.BeginOutline(OutlineKind.Outline));
        var tool = _s.GetTool<OutlineTool>();
        Assert.True(tool.IsEditingLive);
        Assert.Equal(10, tool.Settings.First);
        tool.Update(s => s with { First = 20 });
        Assert.True(_s.RunEffect(OutlineEffect.Plan(_s, tool.Kind, tool.Settings)!).Applied);

        Assert.Equal(count, L.Children.Count);                                         // the same object, no second outline
        AssertRect(new Rect2(80, 80, 220, 220), o.GetWorldBounds());
        Assert.Equal(20, ((LiveOutline)o.Live!.Spec).Settings.First);
        Transform(a, Affine2.Translation(10, 0));
        AssertRect(new Rect2(90, 80, 230, 220), o.GetWorldBounds());
    }

    // ---- Shadows ----

    [Fact]
    public void A_shadow_moves_and_scales_with_its_group_without_being_rebuilt()
    {
        var a = AddRect(100, 100, 100, 100);
        var sh = Shadow(a);
        AssertRect(new Rect2(100, 100, 210, 210), sh.GetWorldBounds());
        int rebuilds = _s.LiveUpdater.Rebuilds;

        Transform(a.TopLevel, Affine2.Scale(2, 2, new Vec2(100, 100)));

        Assert.Equal(rebuilds, _s.LiveUpdater.Rebuilds);
        AssertRect(new Rect2(100, 100, 320, 320), sh.GetWorldBounds());               // scaled with it, shift too
        Assert.NotNull(sh.Live);

        // a later edit of the source comes out the same way (the shadow's frame keeps the group's scale)
        Transform(a, Affine2.Translation(20, 0), "Move");
        AssertRect(new Rect2(120, 100, 340, 320), sh.GetWorldBounds());
    }

    [Fact]
    public void A_shadow_follows_a_resize_of_its_source_alone()
    {
        var a = AddRect(100, 100, 100, 100);
        var sh = Shadow(a);
        int steps = UndoSteps;

        Transform(a, Affine2.Scale(1, 2, new Vec2(100, 100)));

        Assert.Equal(steps + 1, UndoSteps);
        AssertRect(new Rect2(100, 100, 210, 310), sh.GetWorldBounds());
        AssertInStep(sh);
        _s.Undo();
        AssertRect(new Rect2(100, 100, 210, 210), sh.GetWorldBounds());
    }

    [Fact]
    public void Moving_the_shadow_by_itself_keeps_it_there_and_it_stays_live()
    {
        var a = AddRect(100, 100, 100, 100);
        var sh = Shadow(a);
        Transform(sh, Affine2.Translation(30, 0), "Move");
        Assert.NotNull(sh.Live);
        AssertRect(new Rect2(130, 100, 240, 210), sh.GetWorldBounds());

        Transform(a, Affine2.Scale(1, 2, new Vec2(100, 100)));                       // the next rebuild keeps the 30 pt it was moved by

        Assert.NotNull(sh.Live);
        AssertRect(new Rect2(130, 100, 240, 310), sh.GetWorldBounds());
    }

    [Fact]
    public void Editing_the_effect_nodes_by_hand_makes_it_plain_and_undo_brings_the_link_back()
    {
        var a = AddRect(100, 100, 100, 100);
        var sh = Shadow(a);
        _s.History.Execute(new SetVectorsCommand("Move Node", sh, -1, PathGeometry.Rectangle(new Rect2(0, 0, 50, 50))));

        Assert.Null(sh.Live);
        Transform(a, Affine2.Scale(2, 2, new Vec2(100, 100)));
        AssertRect(new Rect2(0, 0, 50, 50), sh.GetWorldBounds());                     // a plain object: it no longer follows
        _s.Undo();
        _s.Undo();
        Assert.NotNull(sh.Live);
        AssertRect(new Rect2(100, 100, 210, 210), sh.GetWorldBounds());
    }

    [Fact]
    public void Recoloring_the_effect_keeps_the_link_and_the_color_survives_a_rebuild()
    {
        var a = AddRect(100, 100, 100, 100);
        var sh = Shadow(a);
        var blue = DocColor.FromHex("#2040C0");
        _s.History.Execute(new SetStyleCommand("Fill", [sh], s => s.WithFill(new SolidPaint(blue))));
        Assert.NotNull(sh.Live);

        Transform(a, Affine2.Scale(1, 2, new Vec2(100, 100)));

        Assert.NotNull(sh.Live);
        Assert.Equal(blue, ((SolidPaint)sh.Style.Fill!).Color);
        AssertRect(new Rect2(100, 100, 210, 310), sh.GetWorldBounds());
    }

    [Fact]
    public void Recoloring_the_source_rebuilds_nothing()
    {
        var a = AddRect(100, 100, 100, 100);
        var sh = Shadow(a);
        int rebuilds = _s.LiveUpdater.Rebuilds;
        _s.History.Execute(new SetStyleCommand("Fill", [a], s => s.WithFill(new SolidPaint(DocColor.White))));
        Assert.Equal(rebuilds, _s.LiveUpdater.Rebuilds);
        Assert.NotNull(sh.Live);
    }

    [Fact]
    public void Deleting_the_source_leaves_the_effect_as_a_plain_object_and_undo_brings_both_back()
    {
        var a = AddRect(100, 100, 100, 100);
        var o = Outline(a, 10);
        _s.Selection.Set(a);
        _s.DeleteSelection();

        Assert.True(Doc.Contains(o));
        Assert.Null(o.Live);
        _s.Undo();
        Assert.True(Doc.Contains(a));
        Assert.Same(a, o.Live!.Source);
        Transform(a, Affine2.Translation(10, 0), "Move");
        AssertRect(new Rect2(100, 90, 220, 210), o.GetWorldBounds());
    }

    [Fact]
    public void Ungrouping_keeps_the_link()
    {
        var a = AddRect(100, 100, 100, 100);
        var sh = Shadow(a);
        _s.Selection.Set(a.TopLevel);
        _s.UngroupSelection();
        Assert.Null(a.Parent as GroupObject);
        Assert.NotNull(sh.Live);

        Transform(a, Affine2.Translation(0, 40), "Move");
        AssertRect(new Rect2(100, 140, 210, 250), sh.GetWorldBounds());
    }

    [Fact]
    public void Break_apart_of_the_group_keeps_the_shadow_following_the_copy_of_its_source()
    {
        var a = AddRect(100, 100, 100, 100);
        Shadow(a);
        _s.Selection.Set(a.TopLevel);
        _s.BreakApartSelection();

        var shadow = Assert.Single(LiveEffects.All(Doc));
        Assert.True(Doc.Contains(shadow.Live!.Source));
        Assert.IsType<RectangleObject>(shadow.Live.Source);
        Transform(shadow.Live.Source, Affine2.Translation(10, 0), "Move");
        AssertRect(new Rect2(110, 100, 220, 210), shadow.GetWorldBounds());
    }

    [Fact]
    public void Copies_keep_their_links_among_themselves_and_an_effect_copied_alone_is_plain()
    {
        var a = AddRect(100, 100, 100, 100);
        var sh = Shadow(a);
        var copies = Stacking.CloneForCopy(Doc, [a.TopLevel]);
        _s.History.Execute(new AddObjectsCommand(L, copies));
        var copyGroup = Assert.IsType<GroupObject>(Assert.Single(copies));
        var copyShadow = copyGroup.Leaves().OfType<PathObject>().Single(p => p.Live is not null);
        var copySource = copyGroup.Leaves().OfType<RectangleObject>().Single();
        Assert.Same(copySource, copyShadow.Live!.Source);

        Transform(copySource, Affine2.Scale(1, 2, new Vec2(100, 100)));
        AssertRect(new Rect2(100, 100, 210, 310), copyShadow.GetWorldBounds());
        AssertRect(new Rect2(100, 100, 210, 210), sh.GetWorldBounds());               // the original is untouched

        var o = Outline(a, 10);
        var alone = Assert.Single(Stacking.CloneForCopy(Doc, [o]));
        Assert.Null(alone.Live);

        var pasted = LiveEffects.CloneSet([a.TopLevel, o]);                            // the paste path: source and outline together
        Assert.Same(((GroupObject)pasted[0]).Leaves().OfType<RectangleObject>().Single(), pasted[1].Live!.Source);
    }

    [Fact]
    public void A_shadow_of_a_live_outline_follows_when_the_letter_changes()
    {
        var a = AddRect(100, 100, 100, 100);
        var o = Outline(a, 10);
        var sh = Shadow(o);
        AssertRect(new Rect2(90, 90, 220, 220), sh.GetWorldBounds());

        Transform(a, Affine2.Scale(2, 1, new Vec2(100, 100)));

        AssertRect(new Rect2(90, 90, 310, 210), o.GetWorldBounds());
        AssertRect(new Rect2(90, 90, 320, 220), sh.GetWorldBounds());
        _s.Undo();
        AssertRect(new Rect2(90, 90, 220, 220), sh.GetWorldBounds());
    }

    [Fact]
    public void Welding_the_source_into_something_else_leaves_its_outline_plain()
    {
        var a = AddRect(100, 100, 100, 100);
        var o = Outline(a, 10);
        var b = AddRect(150, 150, 100, 100);
        _s.Selection.Set([a, b]);
        Assert.True(_s.Weld().Applied);
        Assert.Null(o.Live);
        Assert.True(Doc.Contains(o));
    }

    [Fact]
    public void Make_plain_stops_the_following_and_undo_brings_it_back()
    {
        var a = AddRect(100, 100, 100, 100);
        var sh = Shadow(a);
        _s.Selection.Set(a.TopLevel);
        Assert.True(_s.CanMakePlain);
        Assert.Equal(1, _s.MakePlain());
        Assert.Null(sh.Live);
        Assert.False(_s.CanMakePlain);

        Transform(a, Affine2.Scale(2, 2, new Vec2(100, 100)));
        AssertRect(new Rect2(100, 100, 210, 210), sh.GetWorldBounds());
        _s.Undo();
        _s.Undo();
        Assert.NotNull(sh.Live);
    }

    [Fact]
    public void Undo_leaves_everything_in_step_so_the_next_edit_rebuilds_nothing_else()
    {
        var a = AddRect(100, 100, 100, 100);
        Shadow(a);
        var b = AddRect(400, 100, 100, 100);
        Transform(a, Affine2.Scale(1, 2, new Vec2(100, 100)));
        _s.Undo();
        int rebuilds = _s.LiveUpdater.Rebuilds;
        Transform(b, Affine2.Translation(5, 0), "Move");
        Assert.Equal(rebuilds, _s.LiveUpdater.Rebuilds);
    }

    [Fact]
    public void In_a_document_opened_with_live_effects_the_first_edit_of_a_source_rebuilds_them()
    {
        var a = AddRect(100, 100, 100, 100);
        var o = Outline(a, 10);
        var reopened = new EditorSession(Doc) { BooleanService = GeometryEngines.Booleans, OffsetService = GeometryEngines.Offsets };   // as after opening the file
        o.LiveSync = null;   // nothing seen yet, as for objects just read from a file
        reopened.LiveUpdater.Settled();
        reopened.History.Execute(TransformObjectsCommand.ApplyWorld("Size", [a], Affine2.Scale(2, 1, new Vec2(100, 100))));
        AssertRect(new Rect2(90, 90, 310, 210), o.GetWorldBounds());
    }

    // ---- Text ----

    [Fact]
    public void A_text_shadow_follows_retyping_a_new_size_and_convert_to_curves()
    {
        var t = Add(new TextObject("Hi", "Arial", 100) { Transform = Affine2.Translation(100, 200) });
        var sh = Shadow(t);
        double width = sh.GetWorldBounds().Width;

        _s.History.Execute(new SetTextCommand(t, "Hi there"));
        Assert.True(sh.GetWorldBounds().Width > width * 2);
        AssertInStep(sh);

        _s.History.Execute(new ChangeTextFormatCommand([t], new TextFormatChange(Size: 200)));
        AssertInStep(sh);
        double tall = sh.GetWorldBounds().Height;

        _s.Selection.Set(t.TopLevel);
        _s.ConvertSelectionToCurves();
        Assert.False(Doc.Contains(t));
        Assert.True(Doc.Contains(sh.Live!.Source));                                    // it follows the letters now
        Assert.InRange(sh.GetWorldBounds().Height, tall - 0.5, tall + 0.5);
    }

    // ---- Soft shadow, glow, bevel ----

    [Fact]
    public void A_soft_shadow_follows_a_resize_of_its_source_and_its_blur_scales_with_it()
    {
        var a = AddRect(100, 100, 100, 100);
        _s.Selection.Set(a);
        var soft = Assert.Single(_s.ApplySoftEffect(SoftEffect.Defaults(SoftKind.SoftShadow, 100)));
        Assert.IsType<LiveSoft>(soft.Live!.Spec);
        Assert.Equal(100, soft.Style.Blur!.Reference, 3);

        Transform(a, Affine2.Scale(1, 2, new Vec2(100, 100)));

        Assert.Equal(200, soft.Style.Blur!.Reference, 3);
        AssertRect(new Rect2(110, 110, 210, 310), soft.GetWorldBounds());              // shifted 10 % of the first height, as made
        AssertInStep(soft);
    }

    [Fact]
    public void Bevel_crescents_follow_and_the_tool_reopens_with_the_settings_they_were_made_with()
    {
        var a = AddRect(100, 100, 100, 100);
        _s.Selection.Set(a);
        _s.ApplyBevel(new BevelSettings { Width = 0.05 });
        var crescents = BevelEffect.Existing(a);
        Assert.Equal(2, crescents.Count);
        Assert.All(crescents, c => Assert.IsType<LiveBevel>(c.Live!.Spec));

        Transform(a, Affine2.Scale(2, 1, new Vec2(100, 100)));

        Assert.All(crescents, AssertInStep);
        Assert.True(crescents[0].GetWorldBounds().Width > 150);

        _s.Selection.Set(a.TopLevel);
        Assert.True(_s.BeginBevel());
        Assert.Equal(0.05, _s.GetTool<BevelTool>().Settings.Width, 6);
    }

    [Fact]
    public void Reopening_the_shadow_tool_on_a_shadowed_object_changes_that_shadow()
    {
        var a = AddRect(100, 100, 100, 100);
        var sh = Shadow(a);
        int count = L.Children.Count;
        _s.Selection.Set(a.TopLevel);
        Assert.True(_s.Shadow(new ShadowSettings { ShiftX = 30, ShiftY = 0 }).Applied);
        Assert.Equal(count, L.Children.Count);
        Assert.Same(sh, LiveEffects.Find<LiveShadow>(Doc, a));
        AssertRect(new Rect2(100, 100, 230, 200), sh.GetWorldBounds());
        Assert.Equal(30, ((LiveShadow)sh.Live!.Spec).Settings.ShiftX);
    }
}
