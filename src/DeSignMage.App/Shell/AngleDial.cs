using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;

namespace DeSignMage.App.Shell;

/// <summary>
/// A small dial for an angle of -180..180 degrees: 0 points up, positive turns clockwise (the way the picture is turned). Drag the pointer around it;
/// it snaps to whole degrees, and to 0, 90 and 180 when it comes within 2 degrees of them. The exact value is typed in the box next to it.
/// </summary>
internal sealed class AngleDial : Control
{
    private bool _dragging;
    private double _angle;

    public AngleDial()
    {
        Width = UiScale.Sp(60);
        Height = UiScale.Sp(60);
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = false;
        ToolTip.SetTip(this, "Drag to turn the picture. Type an exact angle in the box.");
    }

    /// <summary>The angle shown, -180..180 degrees clockwise from straight up.</summary>
    public double Angle
    {
        get => _angle;
        set
        {
            double v = PictureGeometry.WrapAngle(value);
            if (Math.Abs(v - _angle) < 1e-9) return;
            _angle = v;
            InvalidateVisual();
        }
    }

    /// <summary>Raised on the UI thread while the dial is dragged, with the new angle.</summary>
    public event Action<double>? AngleChanged;

    /// <summary>The angle for a point of the dial (<paramref name="x"/>, <paramref name="y"/> from its top-left corner); null in the middle where there is no direction.</summary>
    internal static double? AngleAt(double x, double y, double width, double height)
    {
        double dx = x - width / 2, dy = y - height / 2;
        if (dx * dx + dy * dy < 16) return null;
        double a = Math.Atan2(dx, -dy) * 180 / Math.PI;   // 0 = up, clockwise positive
        a = Math.Round(a);
        foreach (double snap in new[] { 0.0, 90, -90, 180, -180 })
            if (Math.Abs(a - snap) <= 2) return snap == -180 ? 180 : snap;
        return a;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        Follow(e);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) Follow(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void Follow(PointerEventArgs e) => DragTo(e.GetPosition(this));

    /// <summary>The pointer is at <paramref name="p"/> (from the dial's top-left corner) while dragging: the pointer follows it (also used by the tests).</summary>
    internal void DragTo(Point p)
    {
        if (AngleAt(p.X, p.Y, Bounds.Width, Bounds.Height) is not { } a) return;
        if (Math.Abs(a - _angle) < 1e-9) return;
        Angle = a;
        AngleChanged?.Invoke(a);
    }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        var center = new Point(w / 2, h / 2);
        double radius = Math.Min(w, h) / 2 - 2;
        var outline = new Pen(AppTheme.Brush("Ui.ChromeBorder"), 1.5);
        context.DrawEllipse(AppTheme.Brush("Ui.Panel"), outline, center, radius, radius);
        var tick = new Pen(AppTheme.Brush("Ui.Muted"), 1);
        for (int deg = 0; deg < 360; deg += 45)
        {
            double t = deg * Math.PI / 180, sin = Math.Sin(t), cos = -Math.Cos(t);
            double inner = radius - (deg % 90 == 0 ? 7 : 4);
            context.DrawLine(tick, new Point(center.X + sin * inner, center.Y + cos * inner), new Point(center.X + sin * (radius - 1), center.Y + cos * (radius - 1)));
        }
        double a = _angle * Math.PI / 180;
        var tip = new Point(center.X + Math.Sin(a) * (radius - 5), center.Y - Math.Cos(a) * (radius - 5));
        context.DrawLine(new Pen(AppTheme.Brush("Ui.IconAccent"), 2.5, lineCap: PenLineCap.Round), center, tip);
        context.DrawEllipse(AppTheme.Brush("Ui.IconAccent"), null, tip, 3.5, 3.5);
        context.DrawEllipse(AppTheme.Brush("Ui.Muted"), null, center, 2, 2);
    }
}
