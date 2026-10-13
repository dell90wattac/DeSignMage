using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>Colors, fills and strokes (task C.3): conversions, identity, apply commands with undo, gestures, eyedropper.</summary>
public class ColorTests
{
    private readonly EditorSession _s;
    private Layer L => _s.Document.ActiveLayer;

    private static readonly DocColor Gold = new(242, 183, 5, 255, "Golden Yellow", "Oracal 651");

    public ColorTests()
    {
        _s = new EditorSession(NewDoc());
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);
    }

    private RectangleObject Add(double x, double y, ObjectStyle? style = null)
    {
        var r = Rect(x, y, 20, 20, style ?? new ObjectStyle(new SolidPaint(DocColor.Black), StrokeStyle.Solid(DocColor.White, 2)));
        _s.History.Execute(new AddObjectCommand(L, r));
        return r;
    }

    private static DocColor? FillOf(DocObject o) => (o.Style.Fill as SolidPaint)?.Color;
    private static DocColor? StrokeOf(DocObject o) => (o.Style.Stroke?.Paint as SolidPaint)?.Color;

    // ---- Conversions ----

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(255, 0, 0, 0, 100, 100)]
    [InlineData(0, 255, 0, 120, 100, 100)]
    [InlineData(0, 0, 255, 240, 100, 100)]
    [InlineData(255, 255, 255, 0, 0, 100)]
    [InlineData(128, 128, 128, 0, 0, 50.196)]
    [InlineData(255, 255, 0, 60, 100, 100)]
    public void Rgb_to_hsv_and_back(byte r, byte g, byte b, double h, double s, double v)
    {
        var (hh, ss, vv) = ColorMath.RgbToHsv(r, g, b);
        Assert.Equal(h, hh, 3);
        Assert.Equal(s, ss, 3);
        Assert.Equal(v, vv, 2);
        Assert.Equal((r, g, b), ColorMath.HsvToRgb(hh, ss, vv));
    }

    [Fact]
    public void Hsv_round_trips_every_8_bit_gray_and_a_color_sweep()
    {
        for (int i = 0; i < 256; i += 5)
            Assert.Equal(((byte)i, (byte)i, (byte)i), ColorMath.HsvToRgb(0, 0, ColorMath.RgbToHsv((byte)i, (byte)i, (byte)i).V));
        for (int r = 0; r < 256; r += 51)
        for (int g = 0; g < 256; g += 51)
        for (int b = 0; b < 256; b += 51)
        {
            var (h, s, v) = ColorMath.RgbToHsv((byte)r, (byte)g, (byte)b);
            Assert.Equal(((byte)r, (byte)g, (byte)b), ColorMath.HsvToRgb(h, s, v));
        }
    }

    [Fact]
    public void Cmyk_to_rgb_matches_the_reference_parser_formula()
    {
        // round(255 * (1 - v) * (1 - k)) with v and k as fractions; e.g. C=0 M=50 Y=100 K=20.
        Assert.Equal(((byte)204, (byte)102, (byte)0), ColorMath.CmykToRgb(0, 50, 100, 20));
        Assert.Equal(((byte)255, (byte)255, (byte)255), ColorMath.CmykToRgb(0, 0, 0, 0));
        Assert.Equal(((byte)0, (byte)0, (byte)0), ColorMath.CmykToRgb(0, 0, 0, 100));
        Assert.Equal(((byte)255, (byte)0, (byte)255), ColorMath.CmykToRgb(0, 100, 0, 0));
    }

    [Fact]
    public void Rgb_to_cmyk_and_back_is_close()
    {
        foreach (var (r, g, b) in new (byte, byte, byte)[] { (242, 183, 5), (10, 200, 90), (0, 0, 0), (255, 255, 255), (30, 30, 200) })
        {
            var c = ColorMath.RgbToCmyk(r, g, b);
            var back = ColorMath.CmykToRgb(c.C, c.M, c.Y, c.K);
            Assert.InRange(back.R - r, -1, 1);
            Assert.InRange(back.G - g, -1, 1);
            Assert.InRange(back.B - b, -1, 1);
        }
    }

    // ---- Identity (D7) ----

    [Fact]
    public void A_color_keeps_name_vendor_and_model_and_they_make_it_different()
    {
        var plain = new DocColor(242, 183, 5);
        Assert.NotEqual(plain, Gold);
        Assert.NotEqual(Gold, Gold with { Vendor = "3M" });
        Assert.Equal("Oracal 651 Golden Yellow", Gold.DisplayName);
        Assert.Null(plain.DisplayName);

        var cmyk = DocColor.FromCmyk(0, 50, 100, 20, "RVW-PR48K", "Roland");
        Assert.Equal(ColorModel.Cmyk, cmyk.Model);
        Assert.Equal(new Cmyk(0, 50, 100, 20), cmyk.ToCmyk());
        Assert.Equal((204, 102, 0), (cmyk.R, cmyk.G, cmyk.B));
        Assert.NotEqual(cmyk, new DocColor(cmyk.R, cmyk.G, cmyk.B, 255, "RVW-PR48K", "Roland"));   // same RGB, other model

        var edited = Gold.Anonymous();
        Assert.Null(edited.Name);
        Assert.Null(edited.Vendor);
        Assert.Equal((Gold.R, Gold.G, Gold.B), (edited.R, edited.G, edited.B));
    }

    // ---- Apply fill / stroke ----

    [Fact]
    public void Apply_fill_sets_the_selection_in_one_undo_step_and_keeps_the_color_identity()
    {
        var a = Add(0, 0); var b = Add(40, 0);
        _s.Selection.Set([a, b]);
        int before = _s.History.UndoList.Count;

        _s.ApplyFill(new SolidPaint(Gold));
        Assert.Equal(Gold, FillOf(a));
        Assert.Equal(Gold, FillOf(b));
        Assert.Equal(before + 1, _s.History.UndoList.Count);
        Assert.Equal("Fill Color", _s.History.UndoDescription);

        _s.Undo();
        Assert.Equal(DocColor.Black, FillOf(a));
        Assert.Equal(DocColor.Black, FillOf(b));
        _s.Redo();
        Assert.Equal(Gold, FillOf(a));
    }

    [Fact]
    public void Apply_fill_none_and_stroke_none_remove_them_and_undo_brings_them_back()
    {
        var a = Add(0, 0);
        _s.Selection.Set(a);
        _s.ApplyFill(null);
        Assert.Null(a.Style.Fill);
        _s.ApplyStrokePaint(null);
        Assert.Null(a.Style.Stroke);
        _s.Undo();
        Assert.Equal(DocColor.White, StrokeOf(a));
        _s.Undo();
        Assert.Equal(DocColor.Black, FillOf(a));
    }

    [Fact]
    public void Apply_stroke_paint_keeps_width_join_and_cap()
    {
        var a = Add(0, 0, new ObjectStyle(null, new StrokeStyle(new SolidPaint(DocColor.White), 5, LineJoin.Round, LineCap.Square)));
        _s.Selection.Set(a);
        _s.ApplyStrokePaint(new SolidPaint(Gold));
        Assert.Equal(new StrokeStyle(new SolidPaint(Gold), 5, LineJoin.Round, LineCap.Square), a.Style.Stroke);
    }

    [Fact]
    public void Stroke_width_join_and_cap_apply_and_a_new_stroke_remembers_them()
    {
        var a = Add(0, 0, ObjectStyle.FillOnly(DocColor.Black));   // no stroke
        _s.Selection.Set(a);
        _s.ApplyStrokeJoin(LineJoin.Round);           // nothing to change on an object without a stroke
        Assert.Null(a.Style.Stroke);
        _s.ApplyStrokeCap(LineCap.Round);
        _s.ApplyStrokeWidth(4);                        // creates a stroke from what was chosen
        Assert.Equal(new StrokeStyle(new SolidPaint(DocColor.Black), 4, LineJoin.Round, LineCap.Round), a.Style.Stroke);

        _s.ApplyStrokeJoin(LineJoin.Bevel);
        Assert.Equal(LineJoin.Bevel, a.Style.Stroke!.Join);
        _s.ApplyStrokeWidth(0);
        Assert.Null(a.Style.Stroke);
        _s.Undo();
        Assert.Equal(4, a.Style.Stroke!.Width);
    }

    [Fact]
    public void Nothing_selected_edits_the_default_style_for_new_objects_without_undo()
    {
        int steps = _s.History.UndoList.Count;
        int changes = 0;
        _s.DefaultStyleChanged += (_, _) => changes++;
        _s.ApplyFill(new SolidPaint(Gold));
        _s.ApplyStrokePaint(new SolidPaint(DocColor.White));
        _s.ApplyStrokeWidth(3);
        Assert.Equal(3, changes);
        Assert.Equal(steps, _s.History.UndoList.Count);

        _s.SetTool("rectangle");
        _s.PointerDown(Pt(10, 10, PointerButton.Left));
        _s.PointerMove(Pt(60, 60));
        _s.PointerUp(Pt(60, 60, PointerButton.Left));
        var r = Assert.IsType<RectangleObject>(L.Children[^1]);
        Assert.Equal(Gold, FillOf(r));
        Assert.Equal(3, r.Style.Stroke!.Width);
    }

    private static PointerInput Pt(double x, double y, PointerButton b = PointerButton.None) => new(new Vec2(x, y), new Vec2(x, y), b, ModifierKeys.None);

    // ---- Groups, combined objects, live text ----

    [Fact]
    public void A_group_passes_fill_and_stroke_to_every_member_at_any_depth()
    {
        var a = Add(0, 0); var b = Add(30, 0); var c = Add(60, 0);
        var inner = _s.GroupObjects([a, b])!;
        var outer = _s.GroupObjects([inner, c])!;
        _s.Selection.Set(outer);

        _s.ApplyFill(new SolidPaint(Gold));
        _s.ApplyStrokePaint(new SolidPaint(Gold));
        foreach (var o in new[] { a, b, c })
        {
            Assert.Equal(Gold, FillOf(o));
            Assert.Equal(Gold, StrokeOf(o));
        }
        Assert.Equal(Gold, (_s.CurrentStyle.Fill as SolidPaint)!.Color);   // the panel shows the group's members' color

        _s.Undo(); _s.Undo();
        foreach (var o in new[] { a, b, c }) Assert.Equal(DocColor.Black, FillOf(o));
    }

    [Fact]
    public void A_combined_object_recolors_every_part()
    {
        var a = Add(0, 0); var b = Add(10, 0, ObjectStyle.FillOnly(DocColor.White));
        _s.Selection.Set([a, b]);
        var combined = _s.CombineSelection()!;
        _s.Selection.Set(combined);

        _s.ApplyFill(new SolidPaint(Gold));
        Assert.All(combined.Parts, p => Assert.Equal(Gold, FillOf(p)));
        Assert.Equal(Gold, FillOf(combined));
        _s.Undo();
        Assert.Equal(DocColor.Black, FillOf(combined.Parts[0]));
        Assert.Equal(DocColor.White, FillOf(combined.Parts[1]));
    }

    [Fact]
    public void Live_text_takes_fill_and_stroke_and_keeps_its_text()
    {
        var t = new TextObject("Sign", "Arial", 24);
        _s.History.Execute(new AddObjectCommand(L, t));
        _s.Selection.Set(t);
        _s.ApplyFill(new SolidPaint(Gold));
        _s.ApplyStrokePaint(new SolidPaint(DocColor.Black));
        Assert.Equal(Gold, FillOf(t));
        Assert.Equal(DocColor.Black, StrokeOf(t));
        Assert.Equal("Sign", t.Text);
    }

    // ---- Gestures: one undo step per drag ----

    [Fact]
    public void A_gesture_updates_live_and_is_one_undo_step()
    {
        var a = Add(0, 0); var b = Add(40, 0);
        _s.Selection.Set([a, b]);
        int steps = _s.History.UndoList.Count;
        int versionSeen = 0;
        _s.Document.Changed += (_, _) => versionSeen++;

        _s.BeginStyleGesture();
        for (int v = 10; v <= 250; v += 20)
        {
            _s.ApplyFill(new SolidPaint(new DocColor((byte)v, 0, 0)));
            Assert.Equal(new DocColor((byte)v, 0, 0), FillOf(a));   // live on every tick
        }
        _s.EndStyleGesture();

        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.True(versionSeen >= 13);   // the document announced every tick
        Assert.Equal(new DocColor(250, 0, 0), FillOf(b));

        _s.Undo();
        Assert.Equal(DocColor.Black, FillOf(a));
        Assert.Equal(DocColor.Black, FillOf(b));
        Assert.Equal(steps, _s.History.UndoList.Count);
        _s.Redo();
        Assert.Equal(new DocColor(250, 0, 0), FillOf(a));
    }

    [Fact]
    public void After_a_gesture_ends_the_next_change_is_a_new_step()
    {
        var a = Add(0, 0);
        _s.Selection.Set(a);
        _s.BeginStyleGesture();
        _s.ApplyFill(new SolidPaint(new DocColor(1, 0, 0)));
        _s.ApplyFill(new SolidPaint(new DocColor(2, 0, 0)));
        _s.EndStyleGesture();
        int steps = _s.History.UndoList.Count;
        _s.ApplyFill(new SolidPaint(new DocColor(3, 0, 0)));
        Assert.Equal(steps + 1, _s.History.UndoList.Count);
    }

    [Fact]
    public void A_gesture_that_changes_nothing_adds_no_undo_step()
    {
        var a = Add(0, 0);
        _s.Selection.Set(a);
        int steps = _s.History.UndoList.Count;
        _s.BeginStyleGesture();
        _s.ApplyFill(new SolidPaint(DocColor.Black));   // already black
        _s.EndStyleGesture();
        Assert.Equal(steps, _s.History.UndoList.Count);
    }

    [Fact]
    public void A_gesture_does_not_swallow_a_command_made_in_between()
    {
        var a = Add(0, 0);
        _s.Selection.Set(a);
        _s.BeginStyleGesture();
        _s.ApplyFill(new SolidPaint(new DocColor(1, 0, 0)));
        _s.NudgeSelection(new Vec2(5, 0));              // something else lands on top of the history
        _s.ApplyFill(new SolidPaint(new DocColor(2, 0, 0)));
        _s.EndStyleGesture();
        Assert.Equal(new DocColor(2, 0, 0), FillOf(a));
        var moved = a.Transform;
        _s.Undo();                                      // the second color goes first
        Assert.Equal(new DocColor(1, 0, 0), FillOf(a));
        Assert.Equal(moved, a.Transform);
        _s.Undo();                                      // then the nudge
        Assert.NotEqual(moved, a.Transform);
        Assert.Equal(new DocColor(1, 0, 0), FillOf(a));
        _s.Undo();                                      // then the first color
        Assert.Equal(DocColor.Black, FillOf(a));
    }

    // ---- Eyedropper ----

    [Fact]
    public void Eyedropper_picks_the_leaf_under_the_cursor_and_returns_to_the_previous_tool()
    {
        var a = Add(0, 0, ObjectStyle.FillOnly(Gold));
        var b = Add(40, 0, ObjectStyle.FillOnly(new DocColor(1, 2, 3)));
        var group = _s.GroupObjects([a, b])!;
        _s.Selection.Clear();
        ColorPickedEventArgs? got = null;
        _s.ColorPicked += (_, e) => got = e;

        _s.BeginEyedropper();
        Assert.IsType<EyedropperTool>(_s.ActiveTool);
        _s.PointerDown(Pt(10, 10, PointerButton.Left));
        _s.PointerUp(Pt(10, 10, PointerButton.Left));

        Assert.NotNull(got);
        Assert.Same(a, got!.Source);                        // the member, not the group
        Assert.Equal(Gold, FillOf(got.Source));
        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.Equal(0, _s.History.UndoList.Count(c => c.Description.Contains("Fill")));   // picking never edits
        Assert.True(_s.Document.Contains(group));   // the group is still there, and so is its place in the drawing order
    }

    [Fact]
    public void Eyedropper_on_empty_space_does_nothing_and_escape_cancels()
    {
        Add(0, 0);
        bool picked = false;
        _s.ColorPicked += (_, _) => picked = true;
        _s.SetTool("vector");
        _s.BeginEyedropper();
        _s.PointerDown(Pt(500, 500, PointerButton.Left));
        Assert.False(picked);
        Assert.IsType<EyedropperTool>(_s.ActiveTool);
        _s.KeyDown(EditorKey.Escape, ModifierKeys.None);
        Assert.Equal("vector", _s.ActiveTool.Id);           // back where it was
    }
}
