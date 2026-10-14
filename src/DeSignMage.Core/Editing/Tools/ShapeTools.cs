using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Drag out a box to create a shape; Ctrl = square/circle (help bar). The new object goes to the bottom of the active layer
/// (<see cref="Stacking.NewItemIndex"/>) and becomes the selection, then the editor returns to the Layout (select) tool unless Alt is
/// held or <see cref="EditorSession.ShapeToolsRemainActive"/> is on. New shapes have no color (<see cref="EditorSession.DefaultStyle"/>).
/// </summary>
public abstract class ShapeTool : Tool
{
    private Vec2? _start;
    private Vec2 _current;
    private ModifierKeys _modifiers;

    public override CursorKind Cursor => CursorKind.Cross;

    /// <summary>A box-based shape of this size, at the local origin (the tool places it).</summary>
    protected virtual DocObject CreateObject(double width, double height) => throw new NotSupportedException();

    protected virtual PathGeometry DraftGeometry(Rect2 box) => throw new NotSupportedException();

    /// <summary>Where the press happened (snapped to guides), or the current point when nothing is being dragged.</summary>
    protected Vec2 StartPoint => _start ?? _current;
    protected Vec2 CurrentPoint => _current;
    protected ModifierKeys Modifiers => _modifiers;

    /// <summary>The finished object for the drag, already placed. The default makes <see cref="CreateObject"/> at the box's top-left.</summary>
    protected virtual DocObject CreateShape(Rect2 box)
    {
        var obj = CreateObject(box.Width, box.Height);
        obj.Transform = Affine2.Translation(box.TopLeft);
        return obj;
    }

    /// <summary>The outline shown while dragging, in document coordinates.</summary>
    protected virtual PathGeometry Draft() => DraftGeometry(CurrentBox());

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
        Finish(box, e.Alt);
        _start = null;
        Redraw();
    }

    private void Finish(Rect2 box, bool keepTool)
    {
        if (Math.Max(box.Width, box.Height) * Session.Viewport.Zoom < Session.ClickThresholdPx) return;
        var layer = Session.Document.ActiveLayer;
        if (layer.Locked) return;

        var obj = CreateShape(box);
        obj.Style = Session.DefaultStyle;
        Session.History.Execute(new AddObjectCommand(layer, obj, Stacking.NewItemIndex));   // a new shape goes to the bottom of the layer
        Session.Selection.Set(obj);
        if (!keepTool && !Session.ShapeToolsRemainActive) Session.SetTool("select");
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
        if (_start is not null) items.Add(new OutlineOverlay(Draft()));
    }

    /// <summary>The box being dragged, with modifiers applied. Exposed for tests.</summary>
    internal virtual Rect2 CurrentBox()
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

/// <summary>Layout ▸ Shapes ▸ Rounded Rectangle: like the rectangle, with a corner radius that starts out at a quarter inch.</summary>
public sealed class RoundedRectangleTool : ShapeTool
{
    /// <summary>The corner radius a rounded rectangle starts with, in points.</summary>
    public static readonly double DefaultRadius = Units.FromInches(0.25);

    public override string Id => "roundedRectangle";
    public override string DisplayName => "Rounded Rectangle";
    public override string Hint => "To DRAW a ROUNDED RECTANGLE: Press left button and drag (Ctrl: square   Alt: keep tool)   To ZOOM: Press right button";

    /// <summary>Corner radius for new rounded rectangles, in points (set from the Context Bar).</summary>
    public double CornerRadius { get; set; } = DefaultRadius;

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

/// <summary>Layout ▸ Shapes ▸ Square: press at the center and drag out; the square follows the larger of the two distances.</summary>
public sealed class SquareTool : ShapeTool
{
    public override string Id => "square";
    public override string DisplayName => "Square";
    public override string Hint => "To DRAW a SQUARE: Press left button at the center and drag out (Alt: keep tool)   To ZOOM: Press right button";

    internal override Rect2 CurrentBox()
    {
        var d = CurrentPoint - StartPoint;
        double m = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
        return Rect2.FromCenter(StartPoint, 2 * m, 2 * m);
    }

    protected override DocObject CreateObject(double width, double height) => new RectangleObject(width, height);
    protected override PathGeometry DraftGeometry(Rect2 box) => PathGeometry.Rectangle(box);
}

/// <summary>Layout ▸ Shapes ▸ Circle: press at the center and drag out to the edge.</summary>
public sealed class CircleTool : ShapeTool
{
    public override string Id => "circle";
    public override string DisplayName => "Circle";
    public override string Hint => "To DRAW a CIRCLE: Press left button at the center and drag out to the edge (Alt: keep tool)   To ZOOM: Press right button";

    internal override Rect2 CurrentBox()
    {
        double r = Vec2.Distance(CurrentPoint, StartPoint);
        return Rect2.FromCenter(StartPoint, 2 * r, 2 * r);
    }

    protected override DocObject CreateObject(double width, double height) => new EllipseObject(width, height);
    protected override PathGeometry DraftGeometry(Rect2 box) => PathGeometry.Ellipse(box);
}

/// <summary>
/// Layout ▸ Shapes ▸ Polygon: press at the center and drag out; the first corner follows the pointer (Ctrl snaps the angle to 15°).
/// The Context Bar sets the number of sides and, for a star, the inner radius; the same settings reshape a selected polygon.
/// </summary>
public sealed class PolygonTool : ShapeTool
{
    public const double DefaultInnerRatio = 0.5;

    public override string Id => "polygon";
    public override string DisplayName => "Polygon";
    public override string Hint => "To DRAW a POLYGON: Press left button at the center and drag out (Ctrl: snap the angle   Alt: keep tool)   To ZOOM: Press right button";

    /// <summary>Number of sides of new polygons.</summary>
    public int Sides { get; set; } = 5;

    /// <summary>True: new polygons are stars with <see cref="InnerRatio"/>.</summary>
    public bool Star { get; set; }

    /// <summary>Inner radius of a star as a fraction of the outer radius (0.05 to 0.95).</summary>
    public double InnerRatio { get; set; } = DefaultInnerRatio;

    internal override Rect2 CurrentBox()
    {
        double r = Vec2.Distance(CurrentPoint, StartPoint);
        return Rect2.FromCenter(StartPoint, 2 * r, 2 * r);
    }

    /// <summary>Direction of the first corner for the drag so far.</summary>
    private double Angle()
    {
        var d = CurrentPoint - StartPoint;
        if (d.Length < 1e-9) return -Math.PI / 2;
        double a = Math.Atan2(d.Y, d.X);
        if ((Modifiers & ModifierKeys.Control) != 0) a = Math.Round(a / (Math.PI / 12)) * (Math.PI / 12);
        return a;
    }

    private double Ratio => Star ? Math.Clamp(InnerRatio, 0.05, PolygonObject.MaxInnerRatio) : 0;

    protected override DocObject CreateShape(Rect2 box) =>
        new PolygonObject(Sides, Vec2.Distance(CurrentPoint, StartPoint), Ratio, Angle()) { Transform = Affine2.Translation(StartPoint) };

    protected override PathGeometry Draft() =>
        PolygonObject.Build(PolygonObject.ClampSides(Sides), Vec2.Distance(CurrentPoint, StartPoint), Ratio, Angle()).Transform(Affine2.Translation(StartPoint));
}

/// <summary>
/// Layout ▸ Shapes ▸ Arrow: drag from the tail to the tip (Ctrl snaps the direction to 45° steps). The Context Bar sets the head and
/// shaft proportions, which also reshape a selected arrow.
/// </summary>
public sealed class ArrowTool : ShapeTool
{
    public const double DefaultHeadFraction = 0.4, DefaultShaftFraction = 0.4, DefaultWidthRatio = 0.3;

    public override string Id => "arrow";
    public override string DisplayName => "Arrow";
    public override string Hint => "To DRAW an ARROW: Press left button at the tail and drag to the tip (Ctrl: snap the direction   Alt: keep tool)   To ZOOM: Press right button";

    /// <summary>Head length as a fraction of the arrow's length.</summary>
    public double HeadFraction { get; set; } = DefaultHeadFraction;

    /// <summary>Shaft width as a fraction of the head's width.</summary>
    public double ShaftFraction { get; set; } = DefaultShaftFraction;

    /// <summary>Head width as a fraction of the arrow's length (for new arrows).</summary>
    public double WidthRatio { get; set; } = DefaultWidthRatio;

    /// <summary>The tip for the drag so far (the direction snaps to 45° steps with Ctrl).</summary>
    private Vec2 Tip()
    {
        var d = CurrentPoint - StartPoint;
        if ((Modifiers & ModifierKeys.Control) == 0 || d.Length < 1e-9) return CurrentPoint;
        double a = Math.Round(Math.Atan2(d.Y, d.X) / (Math.PI / 4)) * (Math.PI / 4);
        return StartPoint + new Vec2(Math.Cos(a), Math.Sin(a)) * d.Length;
    }

    internal override Rect2 CurrentBox() => Rect2.FromPoints(StartPoint, Tip());

    private (double Length, Affine2 Place) Frame()
    {
        var d = Tip() - StartPoint;
        return (d.Length, Affine2.Rotation(Math.Atan2(d.Y, d.X)) * Affine2.Translation(StartPoint));
    }

    protected override DocObject CreateShape(Rect2 box)
    {
        var (length, place) = Frame();
        return new ArrowObject(length, length * WidthRatio, HeadFraction, ShaftFraction) { Transform = place };
    }

    protected override PathGeometry Draft()
    {
        var (length, place) = Frame();
        return ArrowObject.Build(length, length * WidthRatio, ArrowObject.ClampFraction(HeadFraction), ArrowObject.ClampFraction(ShaftFraction)).Transform(place);
    }
}
