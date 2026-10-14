using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>
/// R22 / R32: the user's test of the reference program: "When I copy and then paste something, or drag an item or draw a new square, it always
/// ends up on the bottom most layer, under everything that came before it." New items (drawn shapes, new text, pasted, dropped or imported
/// items) go to the very bottom of the layer's drawing order; moving an existing object never changes its stacking.
/// </summary>
public class NewItemsAtBottomTests
{
    private readonly EditorSession _s;
    private Layer L => _s.Document.ActiveLayer;
    private readonly RectangleObject _a, _b, _c;

    public NewItemsAtBottomTests()
    {
        _s = new EditorSession(NewDoc());
        _s.TextDefaults.Size = 40;
        _s.TextDefaults.Family = "Arial";
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);
        _a = Add(0, 0); _b = Add(100, 0); _c = Add(200, 0);          // [a, b, c]
    }

    private RectangleObject Add(double x, double y)
    {
        var r = Rect(x, y, 50, 50);
        _s.History.Execute(new AddObjectCommand(L, r));
        return r;
    }

    private PointerInput At(double x, double y, PointerButton b = PointerButton.None, ModifierKeys m = ModifierKeys.None, int clicks = 1) =>
        new(new Vec2(x, y), new Vec2(x, y), b, m, clicks);

    private void Drag(double x0, double y0, double x1, double y1, ModifierKeys m = ModifierKeys.None)
    {
        _s.PointerDown(At(x0, y0, PointerButton.Left, m));
        _s.PointerMove(At((x0 + x1) / 2, (y0 + y1) / 2, PointerButton.None, m));
        _s.PointerMove(At(x1, y1, PointerButton.None, m));
        _s.PointerUp(At(x1, y1, PointerButton.Left, m));
    }

    private void Click(double x, double y)
    {
        _s.PointerDown(At(x, y, PointerButton.Left));
        _s.PointerUp(At(x, y, PointerButton.Left));
    }

    private static void AssertOnTheBottom(Layer layer, DocObject created, params DocObject[] before)
    {
        Assert.Same(created, layer.Children[0]);                              // the very bottom
        Assert.Equal(before, layer.Children.Skip(1));                         // everything that was there before keeps its order, above it
    }

    // ---- Drawn shapes ----

    [Fact]
    public void A_new_rectangle_goes_to_the_bottom()
    {
        _s.SetTool("rectangle");
        Drag(400, 400, 480, 470);
        AssertOnTheBottom(L, Assert.IsType<RectangleObject>(Assert.Single(_s.Selection.Items)), _a, _b, _c);
        _s.Undo();
        Assert.Equal([_a, _b, _c], L.Children);
        _s.Redo();
        Assert.Equal(4, L.Children.Count);
        Assert.IsType<RectangleObject>(L.Children[0]);
    }

    [Fact]
    public void A_new_ellipse_goes_to_the_bottom()
    {
        _s.SetTool("ellipse");
        Drag(400, 400, 480, 470);
        AssertOnTheBottom(L, Assert.IsType<EllipseObject>(Assert.Single(_s.Selection.Items)), _a, _b, _c);
    }

    [Fact]
    public void A_new_Vector_Draw_shape_goes_to_the_bottom()
    {
        _s.SetTool("vectordraw");
        foreach (var (x, y) in new[] { (400.0, 400.0), (480.0, 400.0), (480.0, 480.0) })
        {
            _s.PointerDown(At(x, y, PointerButton.Left));
            _s.PointerUp(At(x, y, PointerButton.Left));
        }
        _s.KeyDown(EditorKey.Enter, ModifierKeys.None);
        AssertOnTheBottom(L, Assert.IsType<PathObject>(Assert.Single(_s.Selection.Items)), _a, _b, _c);
    }

    // ---- Text ----

    [Fact]
    public void New_text_and_the_new_line_Enter_starts_go_to_the_bottom()
    {
        _s.SetTool("text");
        Click(400, 500);
        Assert.True(_s.TextInput("One"));
        _s.KeyDown(EditorKey.Enter, ModifierKeys.None);           // finishes "One" and starts the next line as a new text
        Assert.True(_s.TextInput("Two"));
        _s.KeyDown(EditorKey.Escape, ModifierKeys.None);

        var texts = L.Children.OfType<TextObject>().ToList();
        Assert.Equal(["Two", "One"], texts.Select(t => t.Text));  // each new one under the one before
        Assert.Equal([texts[0], texts[1], _a, _b, _c], L.Children);
    }

    // ---- Pasted, dropped and imported items ----

    [Fact]
    public void A_paste_goes_to_the_bottom_when_it_is_put_down()
    {
        _s.Selection.Set(_c);
        _s.Copy();
        _s.Paste();
        Click(600, 300);                                          // puts it down
        var pasted = Assert.Single(_s.Selection.Items);
        AssertOnTheBottom(L, pasted, _a, _b, _c);
        Assert.NotSame(_c, pasted);
        _s.Undo();
        Assert.Equal([_a, _b, _c], L.Children);
    }

    [Fact]
    public void A_pasted_group_goes_to_the_bottom_with_its_members_in_their_own_order()
    {
        _s.Selection.Set([_a, _c]);
        var group = _s.GroupSelection()!;                         // a and c grouped, b between them
        _s.Selection.Set(group);
        _s.Copy();
        _s.Paste();
        Click(600, 300);
        var copy = Assert.IsType<GroupObject>(Assert.Single(_s.Selection.Items));
        Assert.Equal(copy.Children, L.Children.Take(2));          // the copy's two members are the two lowest objects
        Assert.Equal([_a, _b, _c], L.Children.Skip(2));
    }

    [Fact]
    public void Dropped_or_imported_pictures_and_import_dialog_results_go_to_the_bottom()
    {
        var picture = new ImageObject("none", 40, 40) { Transform = Affine2.Translation(300, 300) };
        _s.PlaceImages([picture]);                                // a dropped / imported picture
        AssertOnTheBottom(L, picture, _a, _b, _c);

        var imported = Rect(500, 300, 40, 40);                    // another import (File > Import, hanging on the cursor until clicked)
        _s.BeginPlacing([imported], "Import test");
        Click(600, 400);
        Assert.Same(imported, L.Children[0]);
        Assert.Equal([imported, picture, _a, _b, _c], L.Children);
    }

    [Fact]
    public void Several_pictures_placed_together_keep_their_order_at_the_bottom()
    {
        var p1 = new ImageObject("none", 40, 40);
        var p2 = new ImageObject("none", 40, 40);
        _s.PlaceImages([p1, p2]);
        Assert.Equal([p1, p2, _a, _b, _c], L.Children);
    }

    // ---- Not new: moving, copy-dragging, effects ----

    [Fact]
    public void Moving_an_object_with_the_mouse_does_not_change_its_stacking()
    {
        Drag(225, 25, 625, 325);                                  // drag c (the topmost) away
        Assert.Equal([_a, _b, _c], L.Children);
        Drag(125, 25, 125, 225);                                  // and b
        Assert.Equal([_a, _b, _c], L.Children);
        Assert.Equal(200, _b.GetWorldBounds().Top, 9);
        _s.Selection.Set(_a);
        _s.NudgeSelection(new Vec2(3, 3));                        // keyboard nudge too
        Assert.Equal([_a, _b, _c], L.Children);
    }

    [Fact]
    public void A_copy_made_by_Ctrl_drag_is_not_a_paste_and_stays_where_it_always_went_on_top()
    {
        _s.Selection.Set(_b);
        Drag(125, 25, 125, 325, ModifierKeys.Control);
        var copy = Assert.Single(_s.Selection.Items);
        Assert.NotSame(_b, copy);
        Assert.Same(copy, L.Children[^1]);
        Assert.Equal([_a, _b, _c, copy], L.Children);
    }
}
