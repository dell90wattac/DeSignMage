using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Imaging;
using DeSignMage.Rendering;

namespace DeSignMage.App.Shell;

/// <summary>The zoom and position the two panes of the Picture Editor share, in pixels of the canvas (so both always show the same part of the picture).</summary>
internal sealed class PictureView
{
    public double Zoom { get; private set; } = 1;     // screen DIPs per canvas pixel
    public double CenterX { get; private set; }       // the canvas point in the middle of each pane
    public double CenterY { get; private set; }

    public event Action? Changed;

    public const double MinZoom = 0.02, MaxZoom = 64;

    public void Set(double zoom, double cx, double cy)
    {
        Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        CenterX = cx; CenterY = cy;
        Changed?.Invoke();
    }

    /// <summary>Fits <paramref name="area"/> (canvas pixels) into a pane of the given size with a small margin.</summary>
    public void Fit(RectI area, double paneWidth, double paneHeight)
    {
        if (paneWidth < 8 || paneHeight < 8 || area.IsEmpty) return;
        double zoom = Math.Min((paneWidth - 16) / area.Width, (paneHeight - 16) / area.Height);
        Set(zoom, area.X + area.Width / 2.0, area.Y + area.Height / 2.0);
    }
}

/// <summary>
/// One of the Picture Editor's panes: a picture on the transparency checkerboard (R27), drawn at the zoom and position of the shared <see cref="PictureView"/>.
/// The mouse wheel zooms about the pointer, the middle or right button drags the picture; the left button is the owner's (the crop frame, the straighten line).
/// A pane can also draw the crop frame with its eight handles over the picture, and the line being drawn to straighten it.
/// </summary>
internal sealed class PicturePane : Control
{
    private WriteableBitmap? _bitmap;
    private int _offsetX, _offsetY;          // where the picture's top-left lies on the canvas
    private double _toCanvas = 1;            // canvas pixels per bitmap pixel (the preview is a reduced copy)
    private bool _panning;
    private Point _panFrom;

    public PictureView View { get; set; } = new();

    /// <summary>The canvas (the straightened original): its size in pixels. Only this area is checkerboard.</summary>
    public int CanvasWidth { get; set; } = 1;
    public int CanvasHeight { get; set; } = 1;

    /// <summary>The crop frame to draw with handles (null = none) and whether the outside of it is dimmed.</summary>
    public RectI? Frame { get; set; }
    public bool DimOutsideFrame { get; set; }

    /// <summary>The straighten line being drawn, in canvas pixels.</summary>
    public (Vec2 From, Vec2 To)? Line { get; set; }

    /// <summary>The part of the canvas that is checkerboard (null = all of it): the result pane only shows the kept part.</summary>
    public RectI? CheckerArea { get; set; }

    public string Caption { get; set; } = "";

    /// <summary>The pointer events of the left button (and moves), in canvas pixels, for the owner.</summary>
    public event Action<Vec2, PointerEventArgs>? CanvasPointerPressed, CanvasPointerMoved, CanvasPointerReleased;

    public PicturePane()
    {
        ClipToBounds = true;
        Focusable = true;
        View.Changed += OnViewChanged;
        AppTheme.Changed += InvalidateVisual;
    }

    /// <summary>Gives the pane a view (shared with the other pane).</summary>
    public void Attach(PictureView view)
    {
        View.Changed -= OnViewChanged;
        View = view;
        View.Changed += OnViewChanged;
        InvalidateVisual();
    }

    private void OnViewChanged()
    {
        RenderOptions.SetBitmapInterpolationMode(this, View.Zoom >= 2.5 ? BitmapInterpolationMode.None : View.Zoom >= 1 ? BitmapInterpolationMode.MediumQuality : BitmapInterpolationMode.HighQuality);
        InvalidateVisual();
    }

    /// <summary>
    /// Shows <paramref name="picture"/> (null = nothing). <paramref name="toCanvas"/> is how many canvas pixels one of its pixels covers (1 / the preview's
    /// reduction); <paramref name="offsetX"/>, <paramref name="offsetY"/> is its top-left on the canvas.
    /// </summary>
    public void SetPicture(PixelBuffer? picture, double toCanvas, int offsetX, int offsetY)
    {
        _bitmap?.Dispose();
        _bitmap = null;
        if (picture is not null)
        {
            var wb = new WriteableBitmap(new PixelSize(picture.Width, picture.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Premul);
            using (var fb = wb.Lock())
            {
                var src = picture.Rgba;
                var row = new byte[picture.Width * 4];
                for (int y = 0; y < picture.Height; y++)
                {
                    int s = y * row.Length;
                    for (int x = 0; x < row.Length; x += 4)
                    {
                        int a = src[s + x + 3];
                        row[x] = (byte)((src[s + x] * a + 127) / 255);
                        row[x + 1] = (byte)((src[s + x + 1] * a + 127) / 255);
                        row[x + 2] = (byte)((src[s + x + 2] * a + 127) / 255);
                        row[x + 3] = (byte)a;
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
                }
            }
            _bitmap = wb;
        }
        _toCanvas = toCanvas;
        _offsetX = offsetX; _offsetY = offsetY;
        InvalidateVisual();
    }

    // ---- coordinates ----

    public Vec2 ToCanvas(Point screen) =>
        new((screen.X - Bounds.Width / 2) / View.Zoom + View.CenterX, (screen.Y - Bounds.Height / 2) / View.Zoom + View.CenterY);

    public Point ToScreen(Vec2 canvas) =>
        new((canvas.X - View.CenterX) * View.Zoom + Bounds.Width / 2, (canvas.Y - View.CenterY) * View.Zoom + Bounds.Height / 2);

    private Rect ToScreen(double x, double y, double w, double h)
    {
        var p = ToScreen(new Vec2(x, y));
        return new Rect(p.X, p.Y, w * View.Zoom, h * View.Zoom);
    }

    // ---- drawing ----

    private static IBrush CheckerBrush()
    {
        var theme = AppTheme.DarkMenus ? CanvasTheme.Dark : CanvasTheme.Light;
        var a = Color.FromRgb(theme.CheckerA.Red, theme.CheckerA.Green, theme.CheckerA.Blue);
        var b = Color.FromRgb(theme.CheckerB.Red, theme.CheckerB.Green, theme.CheckerB.Blue);
        var tile = new DrawingGroup();
        tile.Children.Add(new GeometryDrawing { Brush = new SolidColorBrush(a), Geometry = new RectangleGeometry(new Rect(0, 0, 16, 16)) });
        tile.Children.Add(new GeometryDrawing { Brush = new SolidColorBrush(b), Geometry = new RectangleGeometry(new Rect(0, 0, 8, 8)) });
        tile.Children.Add(new GeometryDrawing { Brush = new SolidColorBrush(b), Geometry = new RectangleGeometry(new Rect(8, 8, 8, 8)) });
        return new DrawingBrush { Drawing = tile, TileMode = TileMode.Tile, DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute), Stretch = Stretch.None };
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var all = new Rect(Bounds.Size);
        context.DrawRectangle(AppTheme.Brush("Ui.Panel"), null, all);

        var canvasRect = ToScreen(0, 0, CanvasWidth, CanvasHeight);
        using (context.PushClip(all))
        {
            var area = CheckerArea is { } ca ? ToScreen(ca.X, ca.Y, ca.Width, ca.Height) : canvasRect;
            context.DrawRectangle(CheckerBrush(), null, area.Intersect(all));
            if (_bitmap is not null)
            {
                var dest = ToScreen(_offsetX, _offsetY, _bitmap.PixelSize.Width * _toCanvas, _bitmap.PixelSize.Height * _toCanvas);
                context.DrawImage(_bitmap, new Rect(_bitmap.Size), dest);
            }
            if (Frame is { } f) DrawFrame(context, f, canvasRect);
            if (Line is { } line)
            {
                var a = ToScreen(line.From); var b = ToScreen(line.To);
                context.DrawLine(new Pen(Brushes.Black, 3), a, b);
                context.DrawLine(new Pen(Brushes.Yellow, 1.5), a, b);
            }
        }
        context.DrawRectangle(null, new Pen(AppTheme.Brush("Ui.ChromeBorder"), 1), all.Deflate(0.5));
        if (Caption.Length > 0)
        {
            var text = new FormattedText(Caption, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White);
            var box = new Rect(6, 6, text.Width + 12, text.Height + 4);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, box, 3, 3);
            context.DrawText(text, new Point(12, 8));
        }
    }

    private void DrawFrame(DrawingContext context, RectI f, Rect canvasRect)
    {
        var r = ToScreen(f.X, f.Y, f.Width, f.Height);
        if (DimOutsideFrame)
        {
            var dim = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));
            var c = canvasRect;
            context.DrawRectangle(dim, null, new Rect(c.X, c.Y, c.Width, Math.Max(0, r.Y - c.Y)));
            context.DrawRectangle(dim, null, new Rect(c.X, r.Bottom, c.Width, Math.Max(0, c.Bottom - r.Bottom)));
            context.DrawRectangle(dim, null, new Rect(c.X, r.Y, Math.Max(0, r.X - c.X), r.Height));
            context.DrawRectangle(dim, null, new Rect(r.Right, r.Y, Math.Max(0, c.Right - r.Right), r.Height));
        }
        context.DrawRectangle(null, new Pen(Brushes.Black, 3), r);
        context.DrawRectangle(null, new Pen(Brushes.White, 1.5), r);
        var inner = new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1);
        for (int i = 1; i <= 2; i++)   // the thirds
        {
            context.DrawLine(inner, new Point(r.X + r.Width * i / 3, r.Y), new Point(r.X + r.Width * i / 3, r.Bottom));
            context.DrawLine(inner, new Point(r.X, r.Y + r.Height * i / 3), new Point(r.Right, r.Y + r.Height * i / 3));
        }
        var bounds = new Rect2(r.X, r.Y, r.Right, r.Bottom);
        foreach (var h in SelectionHandles.All)
        {
            var p = SelectionHandles.Position(bounds, h);
            var box = new Rect(Math.Floor(p.X) - 4, Math.Floor(p.Y) - 4, 8, 8);
            context.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1), box);
        }
    }

    // ---- mouse ----

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var at = ToCanvas(e.GetPosition(this));
        double zoom = Math.Clamp(View.Zoom * Math.Pow(1.2, e.Delta.Y), PictureView.MinZoom, PictureView.MaxZoom);
        // keep the canvas point under the pointer where it is
        var p = e.GetPosition(this);
        View.Set(zoom, at.X - (p.X - Bounds.Width / 2) / zoom, at.Y - (p.Y - Bounds.Height / 2) / zoom);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsMiddleButtonPressed || props.IsRightButtonPressed)
        {
            _panning = true;
            _panFrom = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (props.IsLeftButtonPressed)
        {
            e.Pointer.Capture(this);
            CanvasPointerPressed?.Invoke(ToCanvas(e.GetPosition(this)), e);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_panning)
        {
            View.Set(View.Zoom, View.CenterX - (p.X - _panFrom.X) / View.Zoom, View.CenterY - (p.Y - _panFrom.Y) / View.Zoom);
            _panFrom = p;
            return;
        }
        CanvasPointerMoved?.Invoke(ToCanvas(p), e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_panning)
        {
            _panning = false;
            e.Pointer.Capture(null);
            return;
        }
        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            CanvasPointerReleased?.Invoke(ToCanvas(e.GetPosition(this)), e);
            e.Pointer.Capture(null);
        }
    }
}
