using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Drag out a box to create a shape; Ctrl = square/circle (help bar). The new object goes on the active
/// layer and becomes the selection, then the editor returns to the Layout (select) tool unless Alt is held or
/// <see cref="EditorSession.ShapeToolsRemainActive"/> is on.
/// </summary>
public abstract class ShapeTool : Tool
{
    private Vec2? _start;
    private Vec2 _current;
    private ModifierKeys _modifiers;

    public override CursorKind Cursor => CursorKind.Cross;

    protected abstract DocObject CreateObject(double width, double height);
    protected abstract PathGeometry DraftGeometry(Rect2 box);

    public override void PointerDown(PointerInput e)
    {
        _start = _current = Session.SnapPoint(e.Doc);   // corners snap to ruler guides
        _modifiers = e.Modifiers;
    }

    public override void PointerMove(PointerInput e)
    {
        if (_start is null) return;
        _current = Session.SnapPoint(e.Doc);
        _modifiers = e.Modifiers;
        Redraw();
    }

    public override void PointerUp(PointerInput e)
    {
        if (_start is null) return;
        _current = Session.SnapPoint(e.Doc);
        _modifiers = e.Modifiers;
        var box = CurrentBox();
        _start = null;
        Redraw();

        if (Math.Max(box.Width, box.Height) * Session.Viewport.Zoom < Session.ClickThresholdPx) return;
        var layer = Session.Document.ActiveLayer;
        if (layer.Locked) return;

        var obj = CreateObject(box.Width, box.Height);
        obj.Transform = Affine2.Translation(box.TopLeft);
        obj.Style = Session.DefaultStyle;
        Session.History.Execute(new AddObjectCommand(layer, obj));
        Session.Selection.Set(obj);
        if (!e.Alt && !Session.ShapeToolsRemainActive) Session.SetTool("select");
    }

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        if (key == EditorKey.Escape && _start is not null)
        {
            Cancel();
            return true;
        }
        return false;
    }

    public override void Cancel()
    {
        _start = null;
        Redraw();
    }

    public override void BuildOverlay(List<OverlayItem> items)
    {
        base.BuildOverlay(items);
        if (_start is not null) items.Add(new OutlineOverlay(DraftGeometry(CurrentBox())));
    }

    /// <summary>The box being dragged, with modifiers applied. Exposed for tests.</summary>
    internal Rect2 CurrentBox()
    {
        var start = _start ?? _current;
        var d = _current - start;
        if ((_modifiers & ModifierKeys.Control) != 0)
        {
            double m = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
            d = new Vec2(d.X < 0 ? -m : m, d.Y < 0 ? -m : m);
        }
        return Rect2.FromPoints(start, start + d);
    }
}

public sealed class RectangleTool : ShapeTool
{
    public override string Id => "rectangle";
    public override string DisplayName => "Rectangle";
    public override string Hint => "To DRAW a RECTANGLE: Press left button and drag (Ctrl: square   Alt: keep tool)   To ZOOM: Press right button";

    /// <summary>Corner radius for new rectangles, in points (set from the Context Bar).</summary>
    public double CornerRadius { get; set; }

    protected override DocObject CreateObject(double width, double height) => new RectangleObject(width, height, CornerRadius);
    protected override PathGeometry DraftGeometry(Rect2 box) => PathGeometry.Rectangle(box, CornerRadius);
}

public sealed class EllipseTool : ShapeTool
{
    public override string Id => "ellipse";
    public override string DisplayName => "Ellipse";
    public override string Hint => "To DRAW an ELLIPSE: Press left button and drag (Ctrl: circle   Alt: keep tool)   To ZOOM: Press right button";

    protected override DocObject CreateObject(double width, double height) => new EllipseObject(width, height);
    protected override PathGeometry DraftGeometry(Rect2 box) => PathGeometry.Ellipse(box);
}
