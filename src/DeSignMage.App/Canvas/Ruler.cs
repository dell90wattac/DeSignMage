using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DeSignMage.App.Shell;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Canvas;

/// <summary>
/// Inch ruler along the top or left of the canvas. The x values count from the page's left edge and
/// y counts UP from the page's bottom edge.
/// </summary>
public sealed class Ruler : Control
{
    /// <summary>How thick the rulers are (the top one's height, the left one's width).</summary>
    public const double Breadth = 16;

    private static readonly double[] Steps =[1 / 16.0, 1 / 8.0, 1 / 4.0, 1 / 2.0, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000];
    // The rulers are part of the chrome: their colors follow Dark Menus (the themed brushes change in place, and the ruler redraws).
    private static readonly IBrush Background = AppTheme.Brush("Ui.RulerBack");
    private static readonly IBrush Ink = AppTheme.Brush("Ui.RulerTick");
    private static readonly IPen TickPen = new Pen(Ink, 1);
    private static readonly IPen CursorPen = new Pen(Brushes.Red, 1);
    private static readonly Typeface Face = new("Segoe UI");

    private static readonly IPen MarkerPen = new Pen(AppTheme.Brush("Ui.RulerMarker"), 1);
    private static readonly IPen EdgePen = new Pen(AppTheme.Brush("Ui.RulerEdge"), 1);

    private EditorSession? _session;
    private double? _cursor;
    private bool _dragging;

    public bool Vertical { get; init; }

    /// <summary>The drawing canvas: pointer positions during a guide drag are reported in its coordinates.</summary>
    public Control? CanvasRef { get; set; }

    public EditorSession? Session
    {
        get => _session;
        set
        {
            // Redraws also cover guide edits (selection, moves, undo), not just pan and zoom.
            if (_session is not null) _session.RedrawRequested -= OnViewChanged;
            _session = value;
            if (_session is not null) _session.RedrawRequested += OnViewChanged;
            InvalidateVisual();
        }
    }

    // ---- Dragging a guide out of the ruler (requirement R4): top ruler → horizontal, left ruler → vertical ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_session is null || CanvasRef is null || _dragging) return;
        // Left button only: the right button belongs to zoom and must never touch guides.
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        _session.BeginGuideFromRuler(Vertical ? GuideOrientation.Vertical : GuideOrientation.Horizontal, OnCanvas(e));
        e.Handled = true;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        // The arrow shows the direction the new guide will be pulled in.
        Cursor ??= new Cursor(Vertical ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging && _session is not null) _session.MoveGuideDrag(OnCanvas(e));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging || e.InitialPressMouseButton != MouseButton.Left) return;
        _dragging = false;
        e.Pointer.Capture(null);
        _session?.EndGuideDrag(OnCanvas(e));
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_dragging) return;
        _dragging = false;
        _session?.CancelGuideDrag();
    }

    private Vec2 OnCanvas(PointerEventArgs e)
    {
        var p = e.GetPosition(CanvasRef);
        return new Vec2(p.X, p.Y);
    }

    /// <summary>Pointer position in document coordinates, marked on the ruler.</summary>
    public void SetCursor(Vec2? doc)
    {
        _cursor = doc is null ? null : Vertical ? doc.Value.Y : doc.Value.X;
        InvalidateVisual();
    }

    private void OnViewChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void OnThemeChanged() => InvalidateVisual();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AppTheme.Changed += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        AppTheme.Changed -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.FillRectangle(Background, new Rect(size));
        if (_session is null) return;

        var view = _session.Viewport;
        var page = _session.Document.Page;
        double length = Vertical ? size.Height : size.Width;
        double thickness = Vertical ? size.Width : size.Height;
        double pxPerInch = view.Zoom * Units.PointsPerInch;
        double major = Steps.FirstOrDefault(s => s * pxPerInch >= 60, Steps[^1]);
        int subdivisions = major >= 1 && major * pxPerInch >= 160 ? 8 : 4;
        double minor = major / subdivisions;

        // Screen coordinate (along the ruler) of a user-space inch value, and back.
        double ToScreen(double inches) => Vertical
            ? view.ToScreen(new Vec2(0, page.Y + page.Height - Units.FromInches(inches))).Y
            : view.ToScreen(new Vec2(page.X + Units.FromInches(inches), 0)).X;
        double FromScreen(double s) => Vertical
            ? Units.ToInches(page.Y + page.Height - view.ToDoc(new Vec2(0, s)).Y)
            : Units.ToInches(view.ToDoc(new Vec2(s, 0)).X - page.X);

        double a = FromScreen(0), b = FromScreen(length);
        double lo = Math.Min(a, b), hi = Math.Max(a, b);
        long first = (long)Math.Floor(lo / minor), last = (long)Math.Ceiling(hi / minor);
        if (last - first > 4000) return;

        for (long i = first; i <= last; i++)
        {
            double v = i * minor;
            double s = Math.Floor(ToScreen(v)) + 0.5;
            bool isMajor = i % subdivisions == 0;
            double tick = isMajor ? thickness : (i % (subdivisions / 2) == 0 ? thickness * 0.4 : thickness * 0.22);
            if (Vertical) ctx.DrawLine(TickPen, new Point(thickness - tick, s), new Point(thickness, s));
            else ctx.DrawLine(TickPen, new Point(s, thickness - tick), new Point(s, thickness));

            if (isMajor)
            {
                var label = new FormattedText(Format(v), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 8.5, Ink);
                if (Vertical)
                {
                    using (ctx.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(0, s - 2)))
                        ctx.DrawText(label, new Point(0, 0));
                }
                else ctx.DrawText(label, new Point(s + 2, -1));
            }
        }

        if (_cursor is { } c)
        {
            double s = Math.Floor(Vertical ? view.ToScreen(new Vec2(0, c)).Y : view.ToScreen(new Vec2(c, 0)).X) + 0.5;
            if (Vertical) ctx.DrawLine(CursorPen, new Point(0, s), new Point(thickness, s));
            else ctx.DrawLine(CursorPen, new Point(s, 0), new Point(s, thickness));
        }

        // Marker for the selected (or dragged) guide: its x on the top ruler, its y on the left ruler.
        if (_session.ActiveGuideMarker is { } marker && marker.Orientation != (Vertical ? GuideOrientation.Vertical : GuideOrientation.Horizontal))
        {
            var p = view.ToScreen(marker.Origin);
            double m = Math.Floor(Vertical ? p.Y : p.X);
            if (Vertical)
            {
                ctx.DrawLine(MarkerPen, new Point(0, m + 0.5), new Point(thickness, m + 0.5));
                ctx.DrawLine(MarkerPen, new Point(0, m + 3.5), new Point(thickness, m + 3.5));
            }
            else
            {
                ctx.DrawLine(MarkerPen, new Point(m + 0.5, 0), new Point(m + 0.5, thickness));
                ctx.DrawLine(MarkerPen, new Point(m + 3.5, 0), new Point(m + 3.5, thickness));
            }
        }

        if (Vertical) ctx.DrawLine(EdgePen, new Point(thickness - 0.5, 0), new Point(thickness - 0.5, size.Height));
        else ctx.DrawLine(EdgePen, new Point(0, thickness - 0.5), new Point(size.Width, thickness - 0.5));
    }

    private static string Format(double v) =>
        Math.Abs(v - Math.Round(v)) < 1e-9 ? ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture) : v.ToString("0.###", CultureInfo.InvariantCulture);
}
