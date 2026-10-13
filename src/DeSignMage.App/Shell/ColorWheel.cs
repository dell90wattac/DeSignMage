using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Shell;

/// <summary>
/// HSV picker: a hue ring around a saturation/value square. Dragging the ring sets the hue (0 at the top, clockwise);
/// dragging in the square sets saturation (left to right) and value (bottom to top). A drag raises
/// <see cref="GestureStarted"/>, any number of <see cref="Changed"/>, then <see cref="GestureEnded"/>, so the shell can make
/// the whole drag one undo step. Original drawing code.
/// </summary>
public sealed class ColorWheel : Control
{
    private double RingWidth => 13 * Math.Max(Bounds.Width, 1) / 118;   // drawn relative to the wheel size (it grows with the icon scale)
    private enum Zone { None, Ring, Square }
    private Zone _drag;

    public double Hue { get; private set; }
    public double Saturation { get; private set; }
    public double Value { get; private set; } = 100;

    /// <summary>Raised for user input only, never when the shell sets the values with <see cref="Set"/>.</summary>
    public event EventHandler? Changed, GestureStarted, GestureEnded;

    public ColorWheel()
    {
        this.ScaledWidth(118, icon: true).ScaledHeight(118, icon: true);
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    /// <summary>Sets the shown color (does not raise <see cref="Changed"/>).</summary>
    public void Set(double hue, double saturation, double value)
    {
        Hue = hue; Saturation = saturation; Value = value;
        InvalidateVisual();
    }

    // ---- Geometry ----

    private Point Center => new(Bounds.Width / 2, Bounds.Height / 2);
    private double OuterRadius => Math.Min(Bounds.Width, Bounds.Height) / 2 - 1;
    private double InnerRadius => OuterRadius - RingWidth;

    /// <summary>The square inside the ring (its corners touch just inside the ring).</summary>
    internal Rect SquareRect
    {
        get
        {
            double half = (InnerRadius - 3) / Math.Sqrt(2);
            return new Rect(Center.X - half, Center.Y - half, half * 2, half * 2);
        }
    }

    private Point HueMarker
    {
        get
        {
            double a = (Hue - 90) * Math.PI / 180, r = OuterRadius - RingWidth / 2;
            return new Point(Center.X + r * Math.Cos(a), Center.Y + r * Math.Sin(a));
        }
    }

    /// <summary>Where the ring's hue marker is, in this control's coordinates (for tests).</summary>
    internal Point HueMarkerPoint => HueMarker;

    /// <summary>Where the square's marker is, in this control's coordinates (for tests).</summary>
    internal Point SquareMarkerPoint => SquarePointFor(Saturation, Value);

    internal Point SquarePointFor(double saturation, double value)
    {
        var sq = SquareRect;
        return new Point(sq.X + sq.Width * saturation / 100, sq.Y + sq.Height * (1 - value / 100));
    }

    // ---- Drawing ----

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));   // makes the whole box hit-testable

        // Hue ring: a conic gradient around a stroked circle.
        var stops = new GradientStops();
        for (int i = 0; i <= 6; i++)
        {
            var (r, g, b) = ColorMath.HsvToRgb(i * 60 % 360, 100, 100);
            stops.Add(new GradientStop(Color.FromRgb(r, g, b), i / 6.0));
        }
        var conic = new ConicGradientBrush { Angle = 0, GradientStops = stops };
        double mid = OuterRadius - RingWidth / 2;
        context.DrawGeometry(null, new Pen(conic, RingWidth), new EllipseGeometry(new Rect(Center.X - mid, Center.Y - mid, mid * 2, mid * 2)));

        // Saturation/value square for the current hue.
        var sq = SquareRect;
        var (hr, hg, hb) = ColorMath.HsvToRgb(Hue, 100, 100);
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(hr, hg, hb)), null, sq);
        context.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = [new GradientStop(Colors.White, 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)],
        }, null, sq);
        context.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = [new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Colors.Black, 1)],
        }, null, sq);
        context.DrawRectangle(null, new Pen(Brushes.Gray, 1), sq);

        DrawMarker(context, HueMarker);
        DrawMarker(context, SquarePointFor(Saturation, Value));
    }

    private static void DrawMarker(DrawingContext context, Point p)
    {
        context.DrawEllipse(null, new Pen(Brushes.Black, 3), p, 5, 5);
        context.DrawEllipse(null, new Pen(Brushes.White, 1.5), p, 5, 5);
    }

    // ---- Input ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        double d = Math.Sqrt(Math.Pow(p.X - Center.X, 2) + Math.Pow(p.Y - Center.Y, 2));
        _drag = d >= InnerRadius - 1 && d <= OuterRadius + 2 ? Zone.Ring
              : SquareRect.Inflate(2).Contains(p) ? Zone.Square
              : Zone.None;
        if (_drag == Zone.None) return;
        e.Pointer.Capture(this);
        e.Handled = true;
        GestureStarted?.Invoke(this, EventArgs.Empty);
        Update(p);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag != Zone.None) Update(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (_drag == Zone.None) return;
        _drag = Zone.None;
        GestureEnded?.Invoke(this, EventArgs.Empty);
    }

    private void Update(Point p)
    {
        if (_drag == Zone.Ring)
        {
            double deg = Math.Atan2(p.Y - Center.Y, p.X - Center.X) * 180 / Math.PI + 90;
            Hue = ((deg % 360) + 360) % 360;
        }
        else
        {
            var sq = SquareRect;
            Saturation = Math.Clamp((p.X - sq.X) / sq.Width, 0, 1) * 100;
            Value = (1 - Math.Clamp((p.Y - sq.Y) / sq.Height, 0, 1)) * 100;
        }
        InvalidateVisual();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
