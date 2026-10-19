using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Geometry;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>
/// Live effects (user, 2026-10-18: "vector based objects stay, effects don't"): Outline, Inline and the Shadows are plain objects that never change after
/// they are made; Soft Shadow, Glow and the Easy Bevel crescents follow edits of their source, in the same undo step.
/// </summary>
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

    /// <summary>A hard shadow of <paramref name="source"/>: the plain path just below it in its group.</summary>
    private PathObject Shadow(DocObject source, ShadowForm form = ShadowForm.Flat, double dx = 10, double dy = 10)
    {
        _s.Selection.Set(source.TopLevel);
        Assert.True(_s.Shadow(new ShadowSettings { Form = form, ShiftX = dx, ShiftY = dy, Outline = form is ShadowForm.Extruded or ShadowForm.Perspective ? 1 : 0 }).Applied);
        var layer = source.Layer!;
        return Assert.IsType<PathObject>(layer.Children[layer.IndexOf(source) - 1]);
    }

    /// <summary>A live soft shadow of <paramref name="source"/>: shifted 10 % of its height down and right, 50 % blur.</summary>
    private PathObject Soft(DocObject source)
    {
        _s.Selection.Set(source);
        _s.ApplySoftEffect(SoftEffect.Defaults(SoftKind.SoftShadow, source.GetWorldBounds().Height));
        return LiveEffects.Find<LiveSoft>(Doc, source)!;
    }

    private void Transform(DocObject o, Affine2 worldDelta, string what = "Size") =>
        _s.History.Execute(TransformObjectsCommand.ApplyWorld(what, [o], worldDelta));

    private void Reshape(DocObject o, double w, double h) =>
        _s.History.Execute(new SetVectorsCommand("Move Node", o, -1, PathGeometry.Rectangle(new Rect2(0, 0, w, h))));

    /// <summary>The effect is what its source and settings make right now.</summary>
    private void AssertInStep(PathObject effect)
    {
        Assert.NotNull(effect.Live);
        var fresh = LiveEffects.Recompute(effect, _s.BooleanService, _s.OffsetService);
        Assert.NotNull(fresh);
        AssertRect(fresh!.Transform(effect.GetWorldTransform()).GetBounds(), effect.GetWorldBounds());
    }

    // ---- Vector effects stay: Outline, Inline, Shadow ▸ ----

    [Fact]
    public void Outlines_inlines_and_every_kind_of_shadow_are_plain_and_never_change_when_their_source_does()
    {
        var a = Add(new PathObject(PathGeometry.Rectangle(new Rect2(0, 0, 100, 100))) { Transform = Affine2.Translation(100, 100), Style = Red });
        var outline = Outline(a, 10);
        var inline = Outline(a, 10, OutlineKind.Inline);
        var shadows = new List<PathObject>();
        foreach (var form in new[] { ShadowForm.Flat, ShadowForm.Extruded, ShadowForm.Perspective, ShadowForm.Cast })
        {
            var b = AddRect(300 + 200 * (int)form, 100, 100, 100);
            shadows.Add(Shadow(b, form));
        }
        var effects = shadows.Prepend(inline).Prepend(outline).ToList();
        Assert.All(effects, e => Assert.Null(e.Live));
        Assert.Empty(LiveEffects.All(Doc));
        var before = effects.Select(e => (e.Geometry, e.GetWorldBounds())).ToList();

        Reshape(a, 200, 100);                                                          // a node edit of the source
        Transform(a, Affine2.Scale(1, 2, new Vec2(100, 100)));                       // a resize of the source alone
        foreach (var sh in shadows)
        {
            var source = sh.Layer!.Children[sh.Layer.IndexOf(sh) + 1];
            Transform(source, Affine2.Scale(2, 2, source.GetWorldBounds().Center));
            Transform(source, Affine2.Translation(15, 0), "Move");
        }

        for (int i = 0; i < effects.Count; i++)
        {
            Assert.Same(before[i].Geometry, effects[i].Geometry);
            Assert.Equal(before[i].Item2, effects[i].GetWorldBounds());
        }
    }

    [Fact]
    public void A_hard_shadow_is_grouped_with_its_source_and_moves_with_the_group()
    {
        var a = AddRect(100, 100, 100, 100);
        var sh = Shadow(a);
        var group = Assert.IsType<GroupObject>(a.Parent);
        Assert.Same(group, sh.Parent);
        AssertRect(new Rect2(100, 100, 210, 210), sh.GetWorldBounds());

        Transform(group, Affine2.Translation(50, 0), "Move");
        AssertRect(new Rect2(150, 100, 260, 210), sh.GetWorldBounds());
    }

    [Fact]
    public void Selecting_and_dragging_the_text_leaves_its_outline_alone()
    {
        var t = Add(new TextObject("AB", "Arial", 100) { Transform = Affine2.Translation(100, 200), Style = Red });
        var o = Outline(t, 6);
        Assert.Null(o.Parent as GroupObject);
        var before = o.GetWorldBounds();

        _s.Selection.Clear();
        _s.Viewport.Set(4, Vec2.Zero);                                                  // zoomed in: the click tolerance is 2 pt, the outline is 6 pt away
        var onText = t.GetLocalGeometry()!.Transform(t.GetWorldTransform()).Points[0];
        Assert.Same(t, _s.HitTestAt(onText));
        _s.Selection.Set(t);
        Assert.Equal([t], _s.Selection.Items);
        Assert.DoesNotContain(o, LiveEffects.WithFollowers(Doc, [t]));                // the drag preview does not take it along

        _s.TransformSelection("Move", Affine2.Translation(300, 0));
        _s.History.Execute(new SetTextCommand(t, "ABAB"));
        AssertRect(before, o.GetWorldBounds());
    }

    [Fact]
    public void Running_Outline_or_Shadow_again_makes_another_one_as_before()
    {
        var a = AddRect(100, 100, 100, 100);
        var o = Outline(a, 10);
        int count = L.Children.Count;
        var second = Outline(o, 5);                                                    // an outline of the outline
        Assert.Equal(count + 1, L.Children.Count);
        AssertRect(new Rect2(85, 85, 215, 215), second.GetWorldBounds());

        var b = AddRect(400, 100, 100, 100);
        Shadow(b);
        count = L.Children.Count;
        _s.Selection.Set(b.TopLevel);
        Assert.True(_s.Shadow(new ShadowSettings { ShiftX = 30, ShiftY = 0 }).Applied);
        Assert.Equal(count + 1, L.Children.Count);                                     // a second shadow
    }

    // ---- Soft effects follow: Soft Shadow, Glow, Easy Bevel ----

    [Fact]
    public void A_soft_shadow_is_live_and_follows_a_resize_of_its_source_in_the_same_undo_step()
    {
        var a = AddRect(100, 100, 100, 100);
        var soft = Soft(a);
        Assert.Same(a, soft.Live!.Source);
        Assert.Equal(100, soft.Style.Blur!.Reference, 3);
        AssertRect(new Rect2(110, 110, 210, 210), soft.GetWorldBounds());
        int steps = UndoSteps;

        Transform(a, Affine2.Scale(1, 2, new Vec2(100, 100)));

        Assert.Equal(steps + 1, UndoSteps);                                            // one step for the resize and the rebuild
        AssertRect(new Rect2(110, 110, 210, 310), soft.GetWorldBounds());              // shifted 10 % of the first height, as made
        Assert.Equal(200, soft.Style.Blur!.Reference, 3);                              // the blur follows the new height
        _s.Undo();
        AssertRect(new Rect2(110, 110, 210, 210), soft.GetWorldBounds());
        _s.Redo();
        AssertRect(new Rect2(110, 110, 210, 310), soft.GetWorldBounds());
        AssertInStep(soft);
    }

    [Fact]
    public void Moving_the_source_alone_shifts_its_soft_shadow_without_recomputing()
    {
        var a = AddRect(100, 100, 100, 100);
        var soft = Soft(a);
        Assert.Contains(soft, LiveEffects.WithFollowers(Doc, [a]));                   // the drag preview takes it along
        int rebuilds = _s.LiveUpdater.Rebuilds, shifts = _s.LiveUpdater.Shifts;

        Transform(a, Affine2.Translation(50, 20), "Move");

        Assert.Equal(rebuilds, _s.LiveUpdater.Rebuilds);
        Assert.Equal(shifts + 1, _s.LiveUpdater.Shifts);
        AssertRect(new Rect2(160, 130, 260, 230), soft.GetWorldBounds());
        AssertInStep(soft);
    }

    [Fact]
    public void A_soft_shadow_moves_and_scales_with_its_group_without_being_rebuilt()
    {
        var a = AddRect(100, 100, 100, 100);
        var soft = Soft(a);
        int rebuilds = _s.LiveUpdater.Rebuilds;

        Transform(a.TopLevel, Affine2.Scale(2, 2, new Vec2(100, 100)));

        Assert.Equal(rebuilds, _s.LiveUpdater.Rebuilds);
        AssertRect(new Rect2(120, 120, 320, 320), soft.GetWorldBounds());             // scaled with it, shift too
        Transform(a, Affine2.Translation(20, 0), "Move");                              // a later edit comes out the same way
        AssertRect(new Rect2(140, 120, 340, 320), soft.GetWorldBounds());
    }

    [Fact]
    public void Moving_the_soft_shadow_by_itself_keeps_it_there_and_it_stays_live()
    {
        var a = AddRect(100, 100, 100, 100);
        var soft = Soft(a);
        Transform(soft, Affine2.Translation(30, 0), "Move");
        Assert.NotNull(soft.Live);
        AssertRect(new Rect2(140, 110, 240, 210), soft.GetWorldBounds());

        Transform(a, Affine2.Scale(1, 2, new Vec2(100, 100)));                       // the next rebuild keeps the 30 pt it was moved by

        AssertRect(new Rect2(140, 110, 240, 310), soft.GetWorldBounds());
    }

    [Fact]
    public void Editing_the_effect_nodes_by_hand_makes_it_plain_and_undo_brings_the_link_back()
    {
        var a = AddRect(100, 100, 100, 100);
        var soft = Soft(a);
        Reshape(soft, 50, 50);

        Assert.Null(soft.Live);
        Transform(a, Affine2.Scale(2, 2, new Vec2(100, 100)));
        AssertRect(new Rect2(0, 0, 50, 50), soft.GetWorldBounds());                   // a plain object: it no longer follows
        _s.Undo();
        _s.Undo();
        Assert.NotNull(soft.Live);
        AssertRect(new Rect2(110, 110, 210, 210), soft.GetWorldBounds());
    }

    [Fact]
    public void Recoloring_the_effect_keeps_the_link_and_recoloring_the_source_rebuilds_nothing()
    {
        var a = AddRect(100, 100, 100, 100);
        var soft = Soft(a);
        var blue = DocColor.FromHex("#2040C0");
        _s.History.Execute(new SetStyleCommand("Fill", [soft], s => s.WithFill(new SolidPaint(blue))));
        Assert.NotNull(soft.Live);
        int rebuilds = _s.LiveUpdater.Rebuilds;
        _s.History.Execute(new SetStyleCommand("Fill", [a], s => s.WithFill(new SolidPaint(DocColor.White))));
        Assert.Equal(rebuilds, _s.LiveUpdater.Rebuilds);

        Transform(a, Affine2.Scale(1, 2, new Vec2(100, 100)));

        Assert.Equal(blue, ((SolidPaint)soft.Style.Fill!).Color);
        AssertRect(new Rect2(110, 110, 210, 310), soft.GetWorldBounds());
    }

    [Fact]
    public void Deleting_the_source_leaves_the_effect_as_a_plain_object_and_undo_brings_both_back()
    {
        var a = AddRect(100, 100, 100, 100);
        var soft = Soft(a);
        _s.Selection.Set(a);
        _s.DeleteSelection();

        Assert.True(Doc.Contains(soft));
        Assert.Null(soft.Live);
        _s.Undo();
        Assert.Same(a, soft.Live!.Source);
        Transform(a, Affine2.Translation(10, 0), "Move");
        AssertRect(new Rect2(120, 110, 220, 210), soft.GetWorldBounds());
    }

    [Fact]
    public void Ungroup_and_break_apart_keep_the_link()
    {
        var a = AddRect(100, 100, 100, 100);
        var soft = Soft(a);
        _s.Selection.Set(a.TopLevel);
        _s.UngroupSelection();
        Assert.NotNull(soft.Live);
        Transform(a, Affine2.Translation(0, 40), "Move");
        AssertRect(new Rect2(110, 150, 210, 250), soft.GetWorldBounds());

        var b = AddRect(400, 100, 100, 100);
        Soft(b);
        _s.Selection.Set(b.TopLevel);
        _s.BreakApartSelection();                                                      // the pieces are copies: the effect follows the copy of its source
        var copy = LiveEffects.All(Doc).Single(e => e.GetWorldBounds().Left > 400);
        Assert.True(Doc.Contains(copy.Live!.Source));
        Transform(copy.Live.Source, Affine2.Translation(10, 0), "Move");
        AssertRect(new Rect2(420, 110, 520, 210), copy.GetWorldBounds());
    }

    [Fact]
    public void Copies_keep_their_links_among_themselves_and_an_effect_copied_alone_is_plain()
    {
        var a = AddRect(100, 100, 100, 100);
        var soft = Soft(a);
        var copies = Stacking.CloneForCopy(Doc, [a.TopLevel]);
        _s.History.Execute(new AddObjectsCommand(L, copies));
        var copyGroup = Assert.IsType<GroupObject>(Assert.Single(copies));
        var copySoft = copyGroup.Leaves().OfType<PathObject>().Single(p => p.Live is not null);
        var copySource = copyGroup.Leaves().OfType<RectangleObject>().Single();
        Assert.Same(copySource, copySoft.Live!.Source);

        Transform(copySource, Affine2.Scale(1, 2, new Vec2(100, 100)));
        AssertRect(new Rect2(110, 110, 210, 310), copySoft.GetWorldBounds());
        AssertRect(new Rect2(110, 110, 210, 210), soft.GetWorldBounds());             // the original is untouched

        Assert.Null(Assert.Single(Stacking.CloneForCopy(Doc, [soft])).Live);
        var pasted = LiveEffects.CloneSet([a, soft]);                                  // the paste path: source and effect together
        Assert.Same(pasted[0], pasted[1].Live!.Source);
    }

    [Fact]
    public void A_soft_shadow_of_a_plain_outline_follows_that_outline()
    {
        var a = AddRect(100, 100, 100, 100);
        var o = Outline(a, 10);
        Assert.Null(o.Live);
        var soft = Soft(o);
        AssertRect(new Rect2(102, 102, 222, 222), soft.GetWorldBounds());              // 10 % of the outline's 120 pt

        Reshape(o, 200, 100);                                                          // the outline's nodes are edited (its path is in page coordinates)

        Assert.Null(o.Live);
        var followed = Assert.Single(LiveEffects.All(Doc));
        Assert.Same(followed, soft);
        AssertRect(new Rect2(12, 12, 212, 112), soft.GetWorldBounds());
    }

    [Fact]
    public void Make_plain_stops_the_following_and_undo_brings_it_back()
    {
        var a = AddRect(100, 100, 100, 100);
        var soft = Soft(a);
        _s.Selection.Set(a.TopLevel);
        Assert.True(_s.CanMakePlain);
        Assert.Equal(1, _s.MakePlain());
        Assert.Null(soft.Live);
        Assert.False(_s.CanMakePlain);

        Transform(a, Affine2.Scale(2, 2, new Vec2(100, 100)));
        AssertRect(new Rect2(110, 110, 210, 210), soft.GetWorldBounds());
        _s.Undo();
        _s.Undo();
        Assert.NotNull(soft.Live);
    }

    [Fact]
    public void Undo_leaves_everything_in_step_so_the_next_edit_rebuilds_nothing_else()
    {
        var a = AddRect(100, 100, 100, 100);
        Soft(a);
        var b = AddRect(400, 100, 100, 100);
        Transform(a, Affine2.Scale(1, 2, new Vec2(100, 100)));
        _s.Undo();
        int rebuilds = _s.LiveUpdater.Rebuilds;
        Transform(b, Affine2.Translation(5, 0), "Move");
        Assert.Equal(rebuilds, _s.LiveUpdater.Rebuilds);
    }

    [Fact]
    public void A_shape_turned_into_a_path_by_a_node_edit_keeps_its_soft_shadow()
    {
        var a = AddRect(100, 100, 100, 100);
        var soft = Soft(a);
        var cmd = new SetVectorsCommand("Move Node", a, -1, PathGeometry.Rectangle(new Rect2(0, 0, 200, 100)));
        _s.History.Execute(cmd);
        Assert.NotSame(a, cmd.Result);
        Assert.Same(cmd.Result, soft.Live!.Source);
        AssertRect(new Rect2(110, 110, 310, 210), soft.GetWorldBounds());
        _s.Undo();
        Assert.Same(a, soft.Live!.Source);
    }

    [Fact]
    public void In_a_document_opened_with_live_effects_the_first_edit_of_a_source_rebuilds_them()
    {
        var a = AddRect(100, 100, 100, 100);
        var soft = Soft(a);
        var reopened = new EditorSession(Doc) { BooleanService = GeometryEngines.Booleans, OffsetService = GeometryEngines.Offsets };   // as after opening the file
        soft.LiveSync = null;   // nothing seen yet, as for objects just read from a file
        reopened.LiveUpdater.Settled();
        reopened.History.Execute(TransformObjectsCommand.ApplyWorld("Size", [a], Affine2.Scale(1, 2, new Vec2(100, 100))));
        AssertRect(new Rect2(110, 110, 210, 310), soft.GetWorldBounds());
    }

    [Fact]
    public void A_text_soft_shadow_and_glow_follow_retyping_a_new_size_and_convert_to_curves()
    {
        var t = Add(new TextObject("Hi", "Arial", 100) { Transform = Affine2.Translation(100, 200) });
        var soft = Soft(t);
        _s.Selection.Set(t);
        _s.ApplySoftEffect(SoftEffect.Defaults(SoftKind.Glow, 100));
        var glow = LiveEffects.Find<LiveSoft>(Doc, t, k => k.Kind == SoftKind.Glow)!;
        double width = soft.GetWorldBounds().Width;

        _s.History.Execute(new SetTextCommand(t, "Hi there"));
        Assert.True(soft.GetWorldBounds().Width > width * 2);
        AssertInStep(soft);
        AssertInStep(glow);

        _s.History.Execute(new ChangeTextFormatCommand([t], new TextFormatChange(Size: 200)));
        AssertInStep(soft);
        double tall = soft.GetWorldBounds().Height;

        _s.Selection.Set(t.TopLevel);
        _s.ConvertSelectionToCurves();
        Assert.False(Doc.Contains(t));
        Assert.True(Doc.Contains(soft.Live!.Source));                                  // it follows the letters now
        Assert.InRange(soft.GetWorldBounds().Height, tall - 0.5, tall + 0.5);
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
}
