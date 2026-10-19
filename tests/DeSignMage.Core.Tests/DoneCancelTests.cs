using System.Globalization;
using System.Text;
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
/// Done and Cancel for the tools that are modes (<see cref="IModeTool"/>): Done keeps what was done in the mode and goes to the Layout tool with the result
/// selected; Cancel puts the design back exactly as it was when the mode began, leaves no undo or redo step behind, and goes to the Layout tool with the
/// selection the mode began with. One family of tools per test.
/// </summary>
public class DoneCancelTests
{
    private readonly EditorSession _s;
    private Document Doc => _s.Document;
    private Layer L => _s.Document.ActiveLayer;
    private static readonly ObjectStyle Red = ObjectStyle.FillOnly(DocColor.FromHex("#CC0000"));

    public DoneCancelTests()
    {
        _s = new EditorSession(NewDoc())
        {
            BooleanService = GeometryEngines.Booleans,
            OffsetService = GeometryEngines.Offsets,
        };
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);
    }

    // ---- helpers ----

    private RectangleObject AddRect(double x, double y, double w, double h, ObjectStyle? style = null)
    {
        var r = Rect(x, y, w, h, style ?? Red);
        _s.History.Execute(new AddObjectCommand(L, r));
        return r;
    }

    private TextObject AddText(string text = "Hello", double x = 100, double y = 300)
    {
        var t = new TextObject(text, "Arial", 60) { Transform = Affine2.Translation(x, y), Style = ObjectStyle.FillOnly(DocColor.Black) };
        _s.History.Execute(new AddObjectCommand(L, t));
        return t;
    }

    private int UndoSteps => _s.History.UndoList.Count;

    private static PointerInput At(double x, double y, PointerButton b = PointerButton.None, int clicks = 1) =>
        new(new Vec2(x, y), new Vec2(x, y), b, ModifierKeys.None, clicks);

    private void Click(double x, double y, int clicks = 1)
    {
        _s.PointerDown(At(x, y, PointerButton.Left, clicks));
        _s.PointerUp(At(x, y, PointerButton.Left, clicks));
    }

    /// <summary>Everything about the drawing that Cancel must give back, as text: every object with its place, look, effects, live link and text.</summary>
    private string State()
    {
        var sb = new StringBuilder();
        foreach (var o in Doc.AllObjects())
        {
            var b = o.GetWorldBounds();
            sb.Append(CultureInfo.InvariantCulture,
                $"{o.GetType().Name}|{o.Name}|{b.Left:0.###},{b.Top:0.###},{b.Right:0.###},{b.Bottom:0.###}|{o.Style.Fill}|{o.Style.Stroke}|{o.Style.Blur}|{o.Style.Feather}|{o.Live?.Spec}|{o.Parent?.GetType().Name}");
            if (o is RectangleObject r) sb.Append(CultureInfo.InvariantCulture, $"|corner {r.CornerRadius:0.###}");
            if (o is TextObject t) sb.Append(CultureInfo.InvariantCulture, $"|{t.Text}|{t.FontFamily}|{t.FontSize}|{(t.OnPath is null ? "straight" : "on a path")}");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private sealed record Snap(string State, int Steps, bool Modified, IReadOnlyList<DocObject> Selection);

    private Snap Take() => new(State(), UndoSteps, _s.History.IsModified, _s.Selection.Items.ToArray());

    /// <summary>The design is as it was, no undo or redo step is left over, and the Layout tool is active with the first selection.</summary>
    private void AssertBackAsBefore(Snap before)
    {
        Assert.Equal(before.State, State());
        Assert.Equal(before.Steps, UndoSteps);
        Assert.Equal(before.Modified, _s.History.IsModified);
        Assert.Empty(_s.History.RedoList);
        Assert.False(_s.History.CanRedo);
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.False(_s.InMode);
        Assert.Equal(before.Selection, _s.Selection.Items);
    }

    // ---- the history underneath ----

    [Fact]
    public void Rolling_the_history_back_forgets_the_steps_and_does_again_the_steps_the_user_undid()
    {
        AddRect(0, 0, 10, 10);
        AddRect(50, 0, 10, 10);
        var mark = _s.History.MarkNow();
        Assert.True(_s.History.IsAt(mark));

        AddRect(100, 0, 10, 10);
        AddRect(150, 0, 10, 10);
        Assert.False(_s.History.IsAt(mark));
        Assert.True(_s.History.RollBackTo(mark));
        Assert.Equal(2, UndoSteps);
        Assert.Empty(_s.History.RedoList);                                  // nothing to redo: those steps never happened
        Assert.Equal(2, L.Children.Count);
        Assert.True(_s.History.IsAt(mark));

        _s.History.Undo();                                                  // the user undid a step that was done before the mode
        Assert.Equal(1, UndoSteps);
        Assert.True(_s.History.RollBackTo(mark));
        Assert.Equal(2, UndoSteps);                                         // it is done again
        Assert.Equal(2, L.Children.Count);
        Assert.Empty(_s.History.RedoList);
    }

    [Fact]
    public void Rolling_back_with_nothing_done_changes_nothing_and_keeps_the_redo_steps()
    {
        AddRect(0, 0, 10, 10);
        AddRect(50, 0, 10, 10);
        _s.History.Undo();
        var mark = _s.History.MarkNow();
        Assert.True(_s.History.RollBackTo(mark));
        Assert.True(_s.History.CanRedo);                                    // the step undone before the mode can still be redone
        Assert.Equal(1, UndoSteps);
    }

    // ---- which tools are modes ----

    [Fact]
    public void The_tools_that_stay_open_for_adjusting_are_modes_and_the_ones_that_finish_by_themselves_are_not()
    {
        string[] modes = ["bevel", "crop", "feather", "gradient", "loop", "outline", "roundCorners", "shadow", "softeffect", "text", "textpath", "vector", "vectordraw"];
        Assert.Equal(modes, _s.Tools.Where(t => t is IModeTool).Select(t => t.Id).Order().ToArray());
        string[] selfFinishing = ["arrow", "circle", "cookie", "ellipse", "eyedropper", "knife", "place", "polygon", "rectangle", "roundedRectangle", "select", "square"];
        Assert.Equal(selfFinishing, _s.Tools.Where(t => t is not IModeTool).Select(t => t.Id).Order().ToArray());
    }

    [Fact]
    public async Task Outside_a_mode_Done_and_Cancel_do_nothing()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        var before = Take();
        Assert.False(_s.InMode);
        Assert.False(_s.CanFinishMode);
        await _s.DoneMode();
        _s.CancelMode();
        AssertBackAsBefore(before);

        _s.SetTool("knife");                                                // a tool that finishes by itself
        Assert.False(_s.InMode);
        _s.CancelMode();
        Assert.Equal("knife", _s.ActiveTool.Id);
    }

    // ---- Soft Shadow, Glow, Feather ----

    [Fact]
    public async Task Soft_shadow_Done_keeps_the_effect_in_one_step_and_Cancel_takes_it_all_back()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        var before = Take();

        Assert.True(_s.BeginSoftEffect(SoftKind.SoftShadow));
        _s.GetTool<SoftEffectTool>().Update(s => s with { Opacity = 0.9 });
        Assert.True(_s.InMode && _s.CanFinishMode);
        await _s.DoneMode();
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.Equal(before.Steps + 1, UndoSteps);
        Assert.IsType<PathObject>(SoftEffect.Find(a, SoftKind.SoftShadow));
        Assert.Equal([a.TopLevel], _s.Selection.Items);                    // the result (the source with its shadow) is selected
        Assert.False(_s.InMode);

        _s.Undo();
        _s.Selection.Set(a);
        Assert.True(_s.BeginSoftEffect(SoftKind.SoftShadow));
        _s.GetTool<SoftEffectTool>().Update(s => s with { Opacity = 0.9, ShiftX = 30 });
        Assert.True(_s.ModeHasChanges);
        _s.CancelMode();
        AssertBackAsBefore(before);
        Assert.Null(SoftEffect.Find(a, SoftKind.SoftShadow));
    }

    [Fact]
    public void Cancel_brings_back_an_existing_live_soft_shadow_as_it_was_and_it_still_follows_its_source()
    {
        var a = AddRect(100, 100, 100, 100);
        _s.Selection.Set(a);
        _s.ApplySoftEffect(SoftEffect.Defaults(SoftKind.SoftShadow, 100));
        var shadow = LiveEffects.Find<LiveSoft>(Doc, a)!;
        _s.Selection.Set(a.TopLevel);
        var before = Take();
        var shadowBounds = shadow.GetWorldBounds();
        var link = shadow.Live;

        Assert.True(_s.BeginSoftEffect(SoftKind.SoftShadow));                // reopened on the effect that is there
        _s.GetTool<SoftEffectTool>().Update(s => s with { ShiftX = 60, ShiftY = 60, Softness = 1 });
        Assert.NotEqual(before.State, State());
        _s.CancelMode();

        AssertBackAsBefore(before);
        Assert.Equal(shadowBounds, LiveEffects.Find<LiveSoft>(Doc, a)!.GetWorldBounds());
        Assert.Same(shadow, LiveEffects.Find<LiveSoft>(Doc, a));
        Assert.Same(link, shadow.Live);                                     // the link is the one it had, not a copy made by the rollback

        // the live state is restored too: moving the source still moves the shadow with it
        _s.History.Execute(TransformObjectsCommand.ApplyWorld("Move", [a], Affine2.Translation(40, 0)));
        Assert.Equal(shadowBounds.Left + 40, shadow.GetWorldBounds().Left, 3);
    }

    [Fact]
    public void Cancel_of_a_node_edit_also_takes_back_what_the_live_effect_did_in_the_same_steps()
    {
        var a = AddRect(100, 100, 100, 100);
        _s.Selection.Set(a);
        _s.ApplySoftEffect(SoftEffect.Defaults(SoftKind.SoftShadow, 100));
        _s.UngroupSelection();                                              // the source alone, with its live shadow beside it
        var shadow = LiveEffects.Find<LiveSoft>(Doc, a)!;
        _s.Selection.Set(a);
        var shadowBefore = shadow.GetWorldBounds();
        var link = shadow.Live;

        _s.SetTool("vector");
        var tool = _s.GetTool<VectorEditTool>();
        var before = Take();
        Click(100, 100);
        tool.SetAnchorPosition(new Vec2(40, 40));                           // pulls a corner out: the live shadow is rebuilt in the same step
        Assert.NotEqual(shadowBefore, shadow.GetWorldBounds());
        Assert.Equal(before.Steps + 1, UndoSteps);
        _s.CancelMode();

        AssertBackAsBefore(before);
        Assert.Equal(shadowBefore, shadow.GetWorldBounds());
        Assert.Same(link, shadow.Live);
        _s.History.Execute(TransformObjectsCommand.ApplyWorld("Move", [a], Affine2.Translation(30, 0)));   // and it follows again
        Assert.Equal(shadowBefore.Left + 30, shadow.GetWorldBounds().Left, 3);
    }

    [Fact]
    public async Task Glow_and_Feather_Done_keep_and_Cancel_take_back()
    {
        var a = AddRect(0, 0, 100, 100);
        _s.Selection.Set(a);
        var before = Take();

        Assert.True(_s.BeginSoftEffect(SoftKind.Glow));
        _s.CancelMode();
        AssertBackAsBefore(before);

        Assert.True(_s.BeginFeather());
        _s.GetTool<FeatherTool>().Update(f => f with { Amount = 0.1 });
        Assert.NotNull(a.Style.Feather);
        _s.CancelMode();
        AssertBackAsBefore(before);
        Assert.Null(a.Style.Feather);

        Assert.True(_s.BeginFeather());
        _s.GetTool<FeatherTool>().Update(f => f with { Amount = 0.1 });
        await _s.DoneMode();
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.NotNull(a.Style.Feather);
        Assert.Equal(before.Steps + 1, UndoSteps);
        Assert.Equal([a], _s.Selection.Items);
    }

    // ---- Easy Bevel ----

    [Fact]
    public async Task Bevel_Done_keeps_the_crescents_and_Cancel_takes_them_back()
    {
        var a = AddRect(0, 0, 200, 100);
        _s.Selection.Set(a);
        var before = Take();
        var tool = _s.GetTool<BevelTool>();

        Assert.True(_s.BeginBevel());
        await tool.Settled;
        tool.Update(s => s with { Width = 0.05 });
        await tool.Settled;
        Assert.Equal(3, L.Children.Count);
        _s.CancelMode();
        AssertBackAsBefore(before);
        Assert.Equal([a], L.Children);

        Assert.True(_s.BeginBevel());
        await tool.Settled;
        await _s.DoneMode();
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.Equal(3, L.Children.Count);
        Assert.Equal(before.Steps + 1, UndoSteps);
        Assert.NotEmpty(_s.Selection.Items);
    }

    // ---- Gradient Blend ----

    [Fact]
    public async Task Gradient_Done_keeps_every_change_and_Cancel_undoes_them_all()
    {
        var a = AddRect(100, 100, 200, 100);
        _s.Selection.Set(a);
        var before = Take();
        var fill = a.Style.Fill;
        var tool = _s.GetTool<GradientTool>();

        Assert.True(_s.BeginGradient());
        tool.SetKind(GradientKind.Linear);
        tool.SetReflect(true);
        Assert.True(UndoSteps > before.Steps);
        Assert.IsType<GradientPaint>(a.Style.Fill);
        _s.CancelMode();
        AssertBackAsBefore(before);
        Assert.Equal(fill, a.Style.Fill);                                   // the plain color it had

        Assert.True(_s.BeginGradient());
        tool.SetKind(GradientKind.Circle);
        int steps = UndoSteps;
        await _s.DoneMode();
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.IsType<GradientPaint>(a.Style.Fill);
        Assert.Equal(steps, UndoSteps);
        Assert.Equal([a], _s.Selection.Items);
    }

    // ---- Outline / Inline and Shadow ----

    [Fact]
    public async Task Outline_Done_makes_the_object_and_Cancel_leaves_everything_alone()
    {
        var a = AddRect(100, 100, 100, 100);
        _s.Selection.Set(a);
        var before = Take();
        var tool = _s.GetTool<OutlineTool>();

        Assert.True(_s.BeginOutline(OutlineKind.Outline));
        Assert.True(_s.InMode);
        Assert.False(_s.CanFinishMode);                                     // no distance yet: nothing to make, so no Done
        tool.Update(o => o with { First = 10 });
        Assert.True(_s.CanFinishMode);
        _s.CancelMode();
        AssertBackAsBefore(before);

        Assert.True(_s.BeginOutline(OutlineKind.Outline));
        tool.Update(o => o with { First = 10 });
        await _s.DoneMode();
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.Equal(before.Steps + 1, UndoSteps);
        Assert.Equal(2, L.Children.Count);
        Assert.Equal("Outline", Assert.Single(_s.Selection.Items).Name);   // the new object is selected
    }

    [Fact]
    public async Task Shadow_Done_makes_the_shadow_and_Cancel_leaves_everything_alone()
    {
        var a = AddRect(100, 100, 100, 100);
        _s.Selection.Set(a);
        var before = Take();

        Assert.True(_s.BeginShadow(ShadowForm.Flat));
        Assert.True(_s.CanFinishMode);                                      // the default shift is 3 % of the height
        _s.GetTool<ShadowTool>().Update(s => s with { ShiftX = 20, ShiftY = 20 });
        _s.CancelMode();
        AssertBackAsBefore(before);

        Assert.True(_s.BeginShadow(ShadowForm.Cast));
        await _s.DoneMode();
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.Equal(before.Steps + 1, UndoSteps);
        Assert.NotEmpty(_s.Selection.Items);
        Assert.Equal(1, Doc.GroupCount);                                    // grouped with its source
    }

    // ---- Text on Path ----

    [Fact]
    public async Task Text_on_path_Done_keeps_the_path_and_Cancel_gives_back_the_straight_text()
    {
        var t = AddText("Sign");
        _s.Selection.Set(t);
        var before = Take();
        var tool = _s.GetTool<TextPathTool>();

        Assert.True(_s.BeginTextOnPath());                                  // the first "Text on Path" step is done before the tool opens
        Assert.True(t.IsOnPath);
        tool.Update(s => s with { Raise = 8 }, "Text on Path Raise");
        Assert.Equal(before.Steps + 2, UndoSteps);
        _s.CancelMode();
        AssertBackAsBefore(before);
        Assert.False(t.IsOnPath);                                           // that first step is taken back too

        Assert.True(_s.BeginTextOnPath());
        tool.Update(s => s with { Raise = 8 }, "Text on Path Raise");
        await _s.DoneMode();
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.True(t.IsOnPath);
        Assert.Equal(8, t.OnPath!.Raise);
        Assert.Equal(before.Steps + 2, UndoSteps);
        Assert.Equal([t], _s.Selection.Items);
    }

    // ---- Vector Edit and Loop Edit ----

    private (RectangleObject Rect, VectorEditTool Tool) OpenVectorEdit()
    {
        var a = AddRect(100, 100, 100, 50);
        _s.Selection.Set(a);
        _s.SetTool("vector");
        return (a, _s.GetTool<VectorEditTool>());
    }

    [Fact]
    public async Task Vector_edit_Done_keeps_the_node_edits_and_Cancel_takes_them_all_back()
    {
        var (a, tool) = OpenVectorEdit();
        var before = Take();
        Assert.True(_s.InMode);                                             // an object is open

        Click(100, 100);                                                    // the top left node
        tool.SetAnchorPosition(new Vec2(80, 90));
        Click(200, 100);
        tool.SetAnchorPosition(new Vec2(230, 90));
        Assert.Equal(before.Steps + 2, UndoSteps);
        Assert.True(_s.ModeHasChanges);
        Assert.IsType<PathObject>(Assert.Single(L.Children));               // the rectangle became a plain path on the first edit
        _s.CancelMode();
        AssertBackAsBefore(before);
        Assert.Same(a, Assert.Single(L.Children));                          // the very rectangle, not a copy
        Assert.IsType<RectangleObject>(a);

        _s.Selection.Set(a);
        _s.SetTool("vector");
        Click(100, 100);
        tool.SetAnchorPosition(new Vec2(80, 90));
        var edited = Assert.Single(L.Children);
        await _s.DoneMode();
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.Same(edited, Assert.Single(L.Children));
        Assert.Equal(before.Steps + 1, UndoSteps);
        Assert.Equal([edited], _s.Selection.Items);                         // the edited object is selected
    }

    [Fact]
    public void Vector_edit_buttons_need_an_open_object()
    {
        var a = AddRect(100, 100, 100, 50);
        _s.SetTool("vector");
        Assert.False(_s.InMode);                                            // nothing open: nothing to finish or cancel
        Click(150, 100);                                                    // click an object: it opens
        Assert.Same(a, _s.GetTool<VectorEditTool>().Target);
        Assert.True(_s.InMode);
    }

    [Fact]
    public void Text_turned_into_curves_by_vector_editing_is_turned_back_by_Cancel()
    {
        var t = AddText("Hi");
        _s.Selection.Set(t);
        var before = Take();
        _s.EditVectors(t);                                                  // the text becomes curves the moment its vectors open
        Assert.IsType<VectorEditTool>(_s.ActiveTool);
        Assert.False(Doc.Contains(t));
        _s.CancelMode();
        AssertBackAsBefore(before);
        Assert.True(Doc.Contains(t));
        Assert.IsType<TextObject>(Assert.Single(L.Children));
    }

    [Fact]
    public async Task Loop_edit_Done_keeps_and_Cancel_takes_back_a_loop_change()
    {
        var a = AddRect(100, 100, 100, 50);
        _s.Selection.Set(a);
        _s.SetTool("loop");
        var tool = _s.GetTool<LoopEditTool>();
        var before = Take();

        Click(150, 100);                                                    // the top edge selects the loop
        tool.ReverseDirection();
        Assert.Equal(before.Steps + 1, UndoSteps);
        Assert.NotEqual(before.State, State());
        _s.CancelMode();
        AssertBackAsBefore(before);

        _s.Selection.Set(a);
        _s.SetTool("loop");
        Click(150, 100);
        tool.ReverseDirection();
        await _s.DoneMode();
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.Equal(before.Steps + 1, UndoSteps);
        Assert.NotEmpty(_s.Selection.Items);
    }

    // ---- Vector Draw ----

    [Fact]
    public async Task Vector_draw_Done_finishes_the_open_path_and_Cancel_throws_the_unfinished_path_away()
    {
        _s.SetTool("vectordraw");
        Assert.False(_s.InMode);                                            // nothing drawn yet
        var before = Take();

        Click(100, 100); Click(200, 100); Click(200, 200);
        Assert.True(_s.InMode && _s.CanFinishMode);
        _s.CancelMode();
        AssertBackAsBefore(before);
        Assert.Empty(L.Children);

        _s.SetTool("vectordraw");
        Click(100, 100); Click(200, 100); Click(200, 200);
        await _s.DoneMode();
        Assert.IsType<SelectTool>(_s.ActiveTool);
        var path = Assert.IsType<PathObject>(Assert.Single(L.Children));
        Assert.False(PathEditing.ToLoops(path.Geometry)[0].Closed);         // finished where it is, left open
        Assert.Equal(before.Steps + 1, UndoSteps);
        Assert.Equal([path], _s.Selection.Items);
    }

    [Fact]
    public void Vector_draw_Cancel_throws_away_only_the_path_in_progress_not_the_ones_finished_before()
    {
        _s.SetTool("vectordraw");
        Click(100, 100); Click(200, 100);
        _s.KeyDown(EditorKey.Enter, ModifierKeys.None);                     // the first path is finished, the tool stays on
        var first = Assert.IsType<PathObject>(Assert.Single(L.Children));
        var before = Take();

        Click(300, 100); Click(400, 100); Click(400, 200);
        _s.CancelMode();
        Assert.Same(first, Assert.Single(L.Children));
        Assert.Equal(before.Steps, UndoSteps);
        Assert.Equal(before.State, State());
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.Equal(before.Selection, _s.Selection.Items);                 // the selection the second path began with
    }

    // ---- Text typing ----

    [Fact]
    public async Task New_text_Done_finishes_it_as_clicking_away_does_and_Cancel_discards_it()
    {
        _s.SetTool("text");
        Assert.False(_s.InMode);                                            // not typing yet
        var before = Take();
        var tool = _s.GetTool<TextTool>();

        tool.BeginNew(new Vec2(100, 300));
        _s.TextInput("Hi");
        Assert.True(_s.InMode && _s.CanFinishMode);
        _s.CancelMode();
        AssertBackAsBefore(before);
        Assert.Empty(L.Children);
        Assert.False(_s.IsTextEditing);

        _s.SetTool("text");
        tool.BeginNew(new Vec2(100, 300));
        _s.TextInput("Hi");
        await _s.DoneMode();
        Assert.IsType<SelectTool>(_s.ActiveTool);
        var text = Assert.IsType<TextObject>(Assert.Single(L.Children));
        Assert.Equal("Hi", text.Text);
        Assert.Equal(before.Steps + 1, UndoSteps);
        Assert.Equal("Add Text", _s.History.UndoDescription);
        Assert.Equal([text], _s.Selection.Items);
    }

    [Fact]
    public async Task Editing_existing_text_Cancel_puts_back_the_old_text_and_look_and_Done_keeps_the_edits()
    {
        var t = AddText("Hello");
        _s.Selection.Set(t);
        var before = Take();

        _s.EditText(t);
        _s.TextInput("XYZ");
        _s.ApplyTextFormat(new TextFormatChange(Size: 33));                // a change of look made while editing is an undo step of its own
        Assert.Equal("HelloXYZ", t.Text);
        Assert.Equal(33, t.FontSize);
        _s.CancelMode();
        AssertBackAsBefore(before);
        Assert.Equal("Hello", t.Text);
        Assert.Equal(60, t.FontSize);

        _s.EditText(t);
        _s.TextInput("!");
        await _s.DoneMode();
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.Equal("Hello!", t.Text);
        Assert.Equal(before.Steps + 1, UndoSteps);
        Assert.Equal([t], _s.Selection.Items);
    }

    [Fact]
    public void Text_Cancel_after_Enter_discards_only_the_line_being_typed()
    {
        _s.SetTool("text");
        var tool = _s.GetTool<TextTool>();
        tool.BeginNew(new Vec2(100, 300));
        _s.TextInput("First");
        _s.KeyDown(EditorKey.Enter, ModifierKeys.None);                     // the first line is a text of its own now; the next one starts
        var first = L.Children.OfType<TextObject>().Single(t => t.Text == "First");
        int steps = UndoSteps;
        _s.TextInput("Second");
        _s.CancelMode();
        Assert.Same(first, Assert.Single(L.Children));
        Assert.Equal("First", first.Text);
        Assert.Equal(steps, UndoSteps);
        Assert.IsType<SelectTool>(_s.ActiveTool);
    }

    // ---- Convert to Rounded Rectangle and Crop ----

    [Fact]
    public async Task Convert_to_rounded_rectangle_Done_applies_and_Cancel_leaves_the_rectangle_alone()
    {
        var a = AddRect(100, 100, 100, 50);
        _s.Selection.Set(a);
        var before = Take();
        var tool = _s.GetTool<RoundCornersTool>();

        _s.BeginConvertToRoundedRectangle();
        tool.Radius = 12;
        Assert.True(_s.InMode && _s.CanFinishMode);
        _s.CancelMode();
        AssertBackAsBefore(before);

        _s.BeginConvertToRoundedRectangle();
        tool.Radius = 12;
        await _s.DoneMode();
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.Equal(before.Steps + 1, UndoSteps);
        Assert.NotEqual(before.State, State());
        Assert.NotEmpty(_s.Selection.Items);
    }

    [Fact]
    public void Convert_to_rounded_rectangle_with_nothing_to_round_offers_Cancel_but_no_Done()
    {
        _s.SetTool("roundCorners");
        Assert.True(_s.InMode);
        Assert.False(_s.CanFinishMode);
        _s.CancelMode();
        Assert.IsType<SelectTool>(_s.ActiveTool);
    }

    [Fact]
    public void Crop_Cancel_leaves_the_picture_alone_and_Done_needs_a_box()
    {
        var img = new ImageObject("asset", 200, 100, 400, 200) { Transform = Affine2.Translation(100, 100) };
        _s.History.Execute(new AddObjectCommand(L, img));
        _s.Selection.Set(img);
        var before = Take();

        _s.SetTool("crop");
        _s.GetTool<CropTool>().SetTarget(img);
        Assert.True(_s.InMode);
        Assert.False(_s.CanFinishMode);                                     // no box dragged yet
        _s.PointerDown(At(120, 120, PointerButton.Left));
        _s.PointerMove(At(200, 160));
        _s.PointerUp(At(200, 160, PointerButton.Left));
        Assert.True(_s.CanFinishMode);
        _s.CancelMode();
        AssertBackAsBefore(before);
    }
}
