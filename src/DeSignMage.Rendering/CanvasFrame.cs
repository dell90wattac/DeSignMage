using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using SkiaSharp;

namespace DeSignMage.Rendering;

/// <summary>
/// Everything needed to draw one canvas frame, captured on the UI thread. Immutable apart from the picture
/// references it owns, so it can be drawn on the render thread. <see cref="Dispose"/> releases the pictures.
/// </summary>
public sealed class CanvasFrame : IDisposable
{
    public required float Width { get; init; }
    public required float Height { get; init; }
    public required SKMatrix View { get; init; }
    public required SKRect PageScreenRect { get; init; }
    public required SharedPicture Document { get; init; }
    public SharedPicture? Preview { get; init; }
    public required SharedPicture Overlay { get; init; }
    /// <summary>The colors of what the program itself draws (R19). Only the on-screen canvas passes <see cref="CanvasTheme.Dark"/>.</summary>
    public CanvasTheme Theme { get; init; } = CanvasTheme.Light;

    /// <summary>Show Transparency (R27): the page is drawn as a checkerboard instead of its plain color, so whatever is not covered shows it through. Never part of an export.</summary>
    public bool Checker { get; init; }

    /// <summary>Side of one checkerboard square on screen, in the canvas' units: the same at any zoom.</summary>
    public const float CheckerCell = 8;

    /// <summary>
    /// The view has not moved for a moment after a pan. A bitmap made by moving the old pixels can differ from drawing the picture in a few antialiased edge
    /// pixels (a shape that crosses the edge of the window is antialiased a little differently clipped than whole), so the settled frame draws it again.
    /// </summary>
    public bool Settled { get; init; }

    /// <summary>The view is different from the one of the previous frame and was panned or zoomed in (zooming out is drawn from the picture).</summary>
    public bool ViewMoved { get; init; }

    /// <summary>The view moved, and the one before it moved a moment ago too: a wheel zoom or a pan in progress, where a stretched copy of the last sharp picture will do until it settles.</summary>
    public bool InGesture { get; init; }

    /// <summary>Where the still part of the picture is kept as pixels while something is dragged over it (see <see cref="StaticLayerCache"/>). Null: always draw it from the picture.</summary>
    public StaticLayerCache? StaticLayer { get; init; }

    public void Draw(SKCanvas canvas)
    {
        canvas.Save();
        canvas.ClipRect(SKRect.Create(Width, Height));

        // While objects are dragged, a rubber band is drawn, or the pointer just moves over the drawing, everything under the overlay stays the same from
        // frame to frame: draw it from kept pixels when it is a lot to draw.
        if (StaticLayer is null || !StaticLayer.TryDraw(canvas, this))
            DrawBase(canvas);

        if (Preview is not null)
        {
            canvas.Save();
            canvas.Concat(View);
            canvas.DrawPicture(Preview.Picture);
            canvas.Restore();
        }

        // Page boundary drawn as a dashed line over the artwork.
        using (var pagePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = Theme.PageEdge,
            StrokeWidth = 1,
            PathEffect = SKPathEffect.CreateDash([3, 3], 0),
        })
            canvas.DrawRect(PageScreenRect, pagePaint);

        canvas.DrawPicture(Overlay.Picture);
        canvas.Restore();
    }

    /// <summary>The pasteboard, the page and the document picture: what lies under a drag preview.</summary>
    internal void DrawBase(SKCanvas canvas)
    {
        using (var bg = new SKPaint { Color = Theme.Pasteboard })
            canvas.DrawRect(SKRect.Create(Width, Height), bg);
        if (Checker) DrawChecker(canvas);
        else if (Theme.Page is { } pageColor)
        {
            using var page = new SKPaint { Color = pageColor };
            canvas.DrawRect(PageScreenRect, page);
        }

        canvas.Save();
        canvas.Concat(View);
        canvas.DrawPicture(Document.Picture);
        canvas.Restore();
    }

    /// <summary>
    /// The checkerboard on the page only (not on the pasteboard): squares of <see cref="CheckerCell"/> on screen, starting from the page's
    /// top-left corner, so the pattern moves with the page when it is panned and keeps its size when it is zoomed.
    /// </summary>
    private void DrawChecker(SKCanvas canvas)
    {
        float scale = Math.Max(0.1f, canvas.TotalMatrix.ScaleX);
        int px = Math.Max(1, (int)Math.Round(CheckerCell * scale));    // one square in device pixels: whole pixels, so the edges stay crisp
        using var tile = new SKBitmap(2 * px, 2 * px);
        using (var c = new SKCanvas(tile))
        {
            c.Clear(Theme.CheckerA);
            using var b = new SKPaint { Color = Theme.CheckerB };
            c.DrawRect(SKRect.Create(px, 0, px, px), b);
            c.DrawRect(SKRect.Create(0, px, px, px), b);
        }
        using var image = SKImage.FromBitmap(tile);
        float k = CheckerCell / px;     // back to the canvas' units
        var local = SKMatrix.CreateScaleTranslation(k, k, MathF.Round(PageScreenRect.Left), MathF.Round(PageScreenRect.Top));
        using var shader = image.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), local);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(PageScreenRect, paint);
    }

    public void Dispose()
    {
        Document.Release();
        Preview?.Release();
        Overlay.Release();
    }
}

/// <summary>
/// The still part of the canvas (pasteboard, page, document) kept as one bitmap while something is drawn over it: a drag changes only the
/// small preview on top, but replaying the whole document picture for every frame cost 5 to 20 ms for a layout of a thousand shapes
/// (more on a slower PC or without a graphics card), which is what made grabbing and moving things in big files feel heavy.
/// The bitmap is made on the first frame of a drag that needs it and reused until the picture, the view, the size or the colors
/// change; it is drawn exactly where the picture would have been (device pixels, any scale, any fractional offset), so the page does
/// not shift when a drag starts. Used by the render thread only; <see cref="Dispose"/> from any thread.
/// </summary>
public sealed class StaticLayerCache : IDisposable
{
    /// <summary>Pictures with fewer drawing operations than this are drawn directly: a bitmap would not pay for itself.</summary>
    public const int MinOperations = 150;

    private readonly object _gate = new();
    private SKImage? _image;
    private Key _key, _lastKey;
    private int _left, _top;
    private bool _exact = true;    // false after the bitmap was made by moving the old pixels

    private readonly record struct Key(uint Picture, SKMatrix Device, SKMatrix View, float Width, float Height, SKColor Pasteboard, SKColor? Page, SKRect PageRect, bool Checker);

    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    /// <summary>
    /// True when <paramref name="b"/> is <paramref name="a"/> panned by a whole number of device pixels (same picture, scale, size and colors):
    /// the picture is then the same pixels moved, apart from what scrolled into view.
    /// </summary>
    private static bool PannedOnly(in Key a, in Key b, out int dx, out int dy)
    {
        dx = dy = 0;
        if (a.Picture != b.Picture || !a.Device.Equals(b.Device) || a.Width != b.Width || a.Height != b.Height || a.Pasteboard != b.Pasteboard || a.Page != b.Page || a.Checker != b.Checker) return false;
        var va = a.View; var vb = b.View;
        if (va.ScaleX != vb.ScaleX || va.ScaleY != vb.ScaleY || va.SkewX != vb.SkewX || va.SkewY != vb.SkewY || va.Persp0 != vb.Persp0 || va.Persp1 != vb.Persp1) return false;
        double vx = vb.TransX - va.TransX, vy = vb.TransY - va.TransY;
        double px = vx * b.Device.ScaleX, py = vy * b.Device.ScaleY;
        if (Math.Abs(px - Math.Round(px)) > 0.01 || Math.Abs(py - Math.Round(py)) > 0.01) return false;
        // The page outline moves with the view; if it moved differently (the page is being resized) the bitmap is not just shifted.
        if (Math.Abs(b.PageRect.Left - a.PageRect.Left - vx) > 1e-3 || Math.Abs(b.PageRect.Top - a.PageRect.Top - vy) > 1e-3 ||
            Math.Abs(b.PageRect.Width - a.PageRect.Width) > 1e-3 || Math.Abs(b.PageRect.Height - a.PageRect.Height) > 1e-3) return false;
        dx = (int)Math.Round(px);
        dy = (int)Math.Round(py);
        return true;
    }

    /// <summary>Draws the base of <paramref name="frame"/> from the kept bitmap (making it if needed). False when the frame should be drawn the usual way.</summary>
    public bool TryDraw(SKCanvas canvas, CanvasFrame frame)
    {
        var picture = frame.Document.Picture;
        if (picture.ApproximateOperationCount < MinOperations) return false;
        var m = canvas.TotalMatrix;
        // Only a plain scale and shift (what a control on a window has) can be kept pixel for pixel.
        if (m.SkewX != 0 || m.SkewY != 0 || m.Persp0 != 0 || m.Persp1 != 0 || m.Persp2 != 1 || m.ScaleX <= 0 || m.ScaleY <= 0) return false;

        int left = (int)Math.Floor(m.TransX), top = (int)Math.Floor(m.TransY);
        int right = (int)Math.Ceiling(m.TransX + frame.Width * m.ScaleX), bottom = (int)Math.Ceiling(m.TransY + frame.Height * m.ScaleY);
        int w = right - left, h = bottom - top;
        if (w <= 0 || h <= 0 || (long)w * h > 64_000_000) return false;

        var key = new Key(picture.UniqueId, m, frame.View, frame.Width, frame.Height, frame.Theme.Pasteboard, frame.Theme.Page, frame.PageScreenRect, frame.Checker);
        lock (_gate)
        {
            bool sameSize = _image is not null && _image.Width == w && _image.Height == h && _left == left && _top == top;
            if (sameSize && _key.Equals(key) && !_exact && frame.Settled)
            {
                // The pan is over: make the bitmap the way drawing the picture would.
                if (!Build(frame, m, left, top, w, h)) return false;
                Rebuilds++;
                Settles++;
            }
            else if (!(sameSize && _key.Equals(key)))
            {
                if (sameSize && frame.InGesture && !frame.Settled && Magnification(_key, key, left, top, m, frame, out var magnify))
                {
                    // Zooming in during a wheel zoom: the last sharp picture, stretched. The sharp one comes when the view has stood still for a moment.
                    canvas.Save();
                    canvas.SetMatrix(magnify);
                    canvas.DrawImage(_image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                    canvas.Restore();
                    Magnified++;
                    return true;
                }
                if (sameSize && PannedOnly(_key, key, out int dx, out int dy) && Math.Abs(dx) < w * 3 / 4 && Math.Abs(dy) < h * 3 / 4)
                {
                    // Panning: the old pixels moved, and only the strip that came into view is drawn from the picture.
                    if (!Shift(frame, m, left, top, w, h, dx, dy)) return false;
                    _key = key;
                    _lastKey = key;
                    _exact = false;
                    Shifts++;
                }
                else
                {
                    // A drag preview is a sure sign that the picture under it will not change; otherwise (a rubber band, the pointer moving about) wait for
                    // a second frame with the same picture and view (or the same view moved by whole pixels, a pan). A frame whose view just moved makes one at once: it costs about
                    // what drawing the picture does, and the next frames of the gesture can then move or stretch it instead of drawing the picture.
                    bool again = _lastKey.Equals(key) || PannedOnly(_lastKey, key, out _, out _);
                    _lastKey = key;
                    if (frame.Preview is null && !again && !frame.ViewMoved && !frame.Settled) return false;
                    if (!Build(frame, m, left, top, w, h)) return false;
                    _key = key;
                    Rebuilds++;
                }
                _left = left;
                _top = top;
            }
            canvas.Save();
            canvas.SetMatrix(SKMatrix.Identity);   // device pixels; the clip set before stays where it was
            canvas.DrawImage(_image, _left, _top, Nearest);
            canvas.Restore();
        }
        return true;
    }

    /// <summary>
    /// When <paramref name="b"/> is <paramref name="a"/> zoomed in (up to 4 times) so that the whole control rectangle lies inside what the old bitmap holds: the matrix
    /// that draws the old bitmap (its pixel (0, 0) at device pixel (left, top)) as the new view would show it.
    /// </summary>
    private static bool Magnification(in Key a, in Key b, int left, int top, SKMatrix m, CanvasFrame frame, out SKMatrix matrix)
    {
        matrix = default;
        // The picture itself is not compared: it is recorded again when the zoom crosses a half octave (pictures are drawn from another copy then), which is no reason to
        // draw a picture again for a few frames that are replaced by a sharp one in a moment.
        if (!a.Device.Equals(b.Device) || a.Width != b.Width || a.Height != b.Height || a.Pasteboard != b.Pasteboard || a.Page != b.Page || a.Checker != b.Checker) return false;
        var va = a.View; var vb = b.View;
        if (va.SkewX != 0 || va.SkewY != 0 || vb.SkewX != 0 || vb.SkewY != 0 || va.ScaleX <= 0 || va.ScaleY <= 0) return false;
        double ax = vb.ScaleX / va.ScaleX, ay = vb.ScaleY / va.ScaleY;
        if (ax < 1.0005 || ay < 1.0005 || ax > MaxMagnification || ay > MaxMagnification) return false;
        // device = m(view(doc)): new = a * old + b on each axis
        double bx = m.ScaleX * vb.TransX + m.TransX - ax * (m.ScaleX * va.TransX + m.TransX);
        double by = m.ScaleY * vb.TransY + m.TransY - ay * (m.ScaleY * va.TransY + m.TransY);
        // The new control rectangle, seen in old device pixels, must lie inside the old one (the old bitmap is only valid there), one pixel in.
        double nl = m.TransX, nr = m.TransX + frame.Width * m.ScaleX, nt = m.TransY, nb = m.TransY + frame.Height * m.ScaleY;
        double ol = (nl - bx) / ax, or = (nr - bx) / ax, ot = (nt - by) / ay, ob = (nb - by) / ay;
        double vl = Math.Floor(m.TransX + 0.5), vr = Math.Floor(nr + 0.5), vt = Math.Floor(m.TransY + 0.5), vbm = Math.Floor(nb + 0.5);
        if (ol < vl + 1 || or > vr - 1 || ot < vt + 1 || ob > vbm - 1) return false;
        matrix = new SKMatrix((float)ax, 0, (float)(ax * left + bx), 0, (float)ay, (float)(ay * top + by), 0, 0, 1);
        return true;
    }

    /// <summary>The largest stretch of a kept bitmap while zooming in; beyond it the picture is drawn again (it would look too blurred).</summary>
    public const double MaxMagnification = 4;

    /// <summary>The same matrix the canvas has, moved so that device pixel (left, top) is the bitmap's first pixel.</summary>
    private static SKMatrix BitmapMatrix(SKMatrix canvasMatrix, int left, int top) => canvasMatrix.PostConcat(SKMatrix.CreateTranslation(-left, -top));

    private static SKSurface? NewSurface(int w, int h) => SKSurface.Create(new SKImageInfo(w, h, SKImageInfo.PlatformColorType, SKAlphaType.Premul));

    private bool Build(CanvasFrame frame, SKMatrix m, int left, int top, int w, int h)
    {
        using var surface = NewSurface(w, h);
        if (surface is null) return false;
        var c = surface.Canvas;
        // Clipped to the control rectangle exactly as the canvas is, so every pixel is what drawing the picture there would give (a path that crosses
        // the clip is antialiased a little differently from one that does not).
        c.SetMatrix(BitmapMatrix(m, left, top));
        c.ClipRect(SKRect.Create(frame.Width, frame.Height));
        frame.DrawBase(c);
        _image?.Dispose();
        _image = surface.Snapshot();
        _exact = true;
        return true;
    }

    private bool Shift(CanvasFrame frame, SKMatrix m, int left, int top, int w, int h, int dx, int dy)
    {
        using var surface = NewSurface(w, h);
        if (surface is null || _image is null) return false;
        var c = surface.Canvas;
        c.DrawImage(_image, dx, dy, Nearest);
        // What shows is the control's rectangle as the canvas rounds it. The old pixels cover the part of it that lay inside the old rectangle too;
        // what they do not cover is a band along each side they moved away from.
        int rl = (int)Math.Floor(m.TransX - left + 0.5), rt = (int)Math.Floor(m.TransY - top + 0.5);
        int rr = (int)Math.Floor(m.TransX - left + frame.Width * m.ScaleX + 0.5), rb = (int)Math.Floor(m.TransY - top + frame.Height * m.ScaleY + 0.5);
        int cl = Math.Max(rl, rl + dx), ct = Math.Max(rt, rt + dy), cr = Math.Min(rr, rr + dx), cb = Math.Min(rb, rb + dy);
        var strips = new List<SKRectI>(4);
        if (ct > rt) strips.Add(new SKRectI(rl, rt, rr, ct));
        if (cb < rb) strips.Add(new SKRectI(rl, cb, rr, rb));
        if (cl > rl) strips.Add(new SKRectI(rl, ct, cl, cb));
        if (cr < rr) strips.Add(new SKRectI(cr, ct, rr, cb));
        var full = BitmapMatrix(m, left, top);
        var control = SKRect.Create((float)(m.TransX - left), (float)(m.TransY - top), frame.Width * m.ScaleX, frame.Height * m.ScaleY);
        foreach (var strip in strips)
        {
            c.Save();
            c.SetMatrix(SKMatrix.Identity);
            var clip = SKRect.Create(strip.Left, strip.Top, strip.Width, strip.Height);
            clip.Intersect(control);
            c.ClipRect(clip);
            c.SetMatrix(full);
            frame.DrawBase(c);
            c.Restore();
        }        _image.Dispose();
        _image = surface.Snapshot();
        return true;
    }

    /// <summary>How many times the bitmap was made (tests, profiling).</summary>
    public int Rebuilds { get; private set; }

    /// <summary>How many times the bitmap was moved for a pan instead of made again (tests, profiling).</summary>
    public int Shifts { get; private set; }

    /// <summary>How many frames were drawn from the last sharp bitmap stretched, during a zoom (tests, profiling).</summary>
    public int Magnified { get; private set; }

    /// <summary>How many times a bitmap that had been moved was made again exactly once the pan was over (tests, profiling).</summary>
    public int Settles { get; private set; }

    public void Dispose()
    {
        lock (_gate)
        {
            _image?.Dispose();
            _image = null;
        }
    }
}

/// <summary>Builds <see cref="CanvasFrame"/>s for an <see cref="EditorSession"/>. One per canvas, UI thread only.</summary>
public sealed class CanvasFrameBuilder(EditorSession session) : IDisposable
{
    private readonly SceneRenderer _scene = new(session.Document);
    private readonly OverlayRenderer _overlay = new();
    private readonly StaticLayerCache _staticLayer = new();

    public double LastRecordMs => _scene.LastRecordMs;

    /// <summary>The bitmap of the still part of the canvas during drags (<see cref="StaticLayerCache"/>). On by default; switched off to compare or to find a drawing problem.</summary>
    public bool UseStaticLayer { get; set; } = true;

    /// <summary>The cache the frames use, for tests and profiling.</summary>
    public StaticLayerCache StaticLayer => _staticLayer;

    /// <summary>The colors for the next frames (R19): Dark Canvas on or off. Set by the canvas control before each frame (the page is drawn where <see cref="EditorSession.PageBounds"/> says, also while its border is dragged).</summary>
    public CanvasTheme Theme { get; set; } = CanvasTheme.Light;

    /// <summary>Show Transparency (R27): draw the page as a checkerboard. Set by the canvas control before each frame; changing it makes the kept bitmap of the still part draw again.</summary>
    public bool ShowTransparency { get; set; }

    /// <summary>How long the view must stand still before a frame counts as settled after a pan.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>The clock the settle delay is measured with (ticks of <see cref="System.Diagnostics.Stopwatch"/>); tests replace it.</summary>
    public Func<long> Clock { get; set; } = System.Diagnostics.Stopwatch.GetTimestamp;

    /// <summary>Two view changes closer than this are one gesture (a wheel turning, a pan).</summary>
    public static readonly TimeSpan GestureGap = TimeSpan.FromMilliseconds(100);

    private ViewState _lastView;
    private long _viewChangedAt = long.MinValue / 4;
    private bool _panPending;

    /// <summary>
    /// True after the view was panned or zoomed until a frame has been built with the view standing still for <see cref="SettleDelay"/>.
    /// The canvas asks for one more frame after the delay when this is set, so that the settled picture is drawn exactly.
    /// </summary>
    public bool SettleFramePending => UseStaticLayer && _panPending;

    public CanvasFrame Build(float width, float height)
    {
        var view = session.Viewport;
        long now = Clock();
        bool settled = false, moved = false, gesture = false;
        if (!view.State.Equals(_lastView))
        {
            moved = view.State.Zoom >= _lastView.Zoom;     // a bitmap is only worth making where a pan or a zoom in can use it later; zooming out shows what it does not hold
            gesture = (now - _viewChangedAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency < GestureGap.TotalMilliseconds;
            _panPending = true;
            _lastView = view.State;
            _viewChangedAt = now;
        }
        else if (_panPending && (now - _viewChangedAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= SettleDelay.TotalMilliseconds)
        {
            settled = true;
            _panPending = false;
        }
        var preview = session.Preview;
        _scene.Wireframe = !session.ShowFillColors;
        _scene.ViewScale = view.Zoom;
        _scene.Theme = Theme;
        var docPicture = _scene.GetDocumentPicture(preview is { Copy: false } ? preview.Objects : null);
        var previewPicture = preview is null ? null : _scene.RecordPreview(preview.Objects, preview.WorldDelta);
        var overlay = _overlay.Record(session.BuildOverlay(), view, width, height, Theme);
        return new CanvasFrame
        {
            Width = width,
            Height = height,
            View = view.DocToScreen.ToSK(),
            PageScreenRect = session.PageBounds.Transform(view.DocToScreen).ToSK(),
            Document = docPicture,
            Preview = previewPicture,
            Overlay = overlay,
            Theme = Theme,
            Checker = ShowTransparency,
            StaticLayer = UseStaticLayer ? _staticLayer : null,
            Settled = settled,
            ViewMoved = moved,
            InGesture = gesture,
        };
    }

    public void Dispose()
    {
        _scene.Dispose();
        _overlay.Dispose();
        _staticLayer.Dispose();
    }
}

/// <summary>Draws selection boxes, handles, marquees and draft outlines in screen space (constant pixel size).</summary>
public sealed class OverlayRenderer : IDisposable
{
    private static readonly SKColor Accent = new(0x00, 0x78, 0xD7);
    public static readonly SKColor GuideColor = CanvasTheme.Light.Guide;
    private const float HandleSize = 7;

    private readonly SKPaint _line = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = Accent };
    private readonly SKPaint _dash = new()
    {
        IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.Black,
        PathEffect = SKPathEffect.CreateDash([4, 3], 0),
    };
    private readonly SKPaint _deleteFill = new() { Style = SKPaintStyle.Fill, Color = new SKColor(0xE5, 0x39, 0x35, 0x28) };
    private readonly SKPaint _smallMark = new() { Style = SKPaintStyle.Fill, Color = new SKColor(255, 0, 0) };
    private readonly SKPaint _zoomDash = new()
    {
        IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.Red,
        PathEffect = SKPathEffect.CreateDash([2, 2], 0),
    };
    // Ruler guides: dotted, blue (distinct from the white workspace and the grey page border); the selected one red.
    private readonly SKPaint _guide = new()
    {
        Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = GuideColor,
        PathEffect = SKPathEffect.CreateDash([2, 3], 0),
    };
    private readonly SKPaint _guideSelected = new()
    {
        Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.Red,
        PathEffect = SKPathEffect.CreateDash([2, 3], 0),
    };
    // Selection marks invert whatever is underneath (white with a Difference blend), like XOR handles: black over
    // white, white over black, so they stay visible on dark artwork (user, 2026-10-12). Drawn straight onto the frame
    // canvas after the artwork, so the blend sees the real pixels.
    private readonly SKPaint _handleFill = new() { Style = SKPaintStyle.Fill, Color = SKColors.White, BlendMode = SKBlendMode.Difference };
    private readonly SKPaint _centerMark = new()
    {
        IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.White, BlendMode = SKBlendMode.Difference,
    };
    private readonly SKPaint _marqueeFill = new() { Style = SKPaintStyle.Fill, Color = Accent.WithAlpha(0x22) };

    private CanvasTheme _theme = CanvasTheme.Light;

    public SharedPicture Record(IReadOnlyList<OverlayItem> items, Viewport view, float width, float height, CanvasTheme? theme = null)
    {
        _theme = theme ?? CanvasTheme.Light;
        _dash.Color = _theme.Marquee;
        _guide.Color = _theme.Guide;
        using var rec = new SKPictureRecorder();
        var c = rec.BeginRecording(SKRect.Create(width, height));
        var m = view.DocToScreen;
        foreach (var item in items)
        {
            switch (item)
            {
                case SelectionOverlay s:
                {
                    // Classic style (user, 2026-10-11): no frame around the selection, just eight solid black handle squares
                    // and a small x at the center. Non-interactive selections (mid-drag, other tools) show the same marks.
                    foreach (var h in SelectionHandles.All)
                    {
                        var p = view.ToScreen(SelectionHandles.Position(s.Bounds, h));
                        var hr = Snap(SKRect.Create((float)p.X - HandleSize / 2, (float)p.Y - HandleSize / 2, HandleSize, HandleSize));
                        c.DrawRect(hr, _handleFill);
                    }
                    var center = view.ToScreen(s.Bounds.Center);
                    float cx = MathF.Round((float)center.X) + 0.5f, cy = MathF.Round((float)center.Y) + 0.5f;
                    using (var x = new SKPath())       // one path, so the crossing pixel isn't inverted twice
                    {
                        x.MoveTo(cx - 3, cy - 3); x.LineTo(cx + 3, cy + 3);
                        x.MoveTo(cx - 3, cy + 3); x.LineTo(cx + 3, cy - 3);
                        c.DrawPath(x, _centerMark);
                    }
                    break;
                }
                case MarqueeOverlay mq:
                {
                    var r = Snap(mq.Bounds.Transform(m).ToSK());
                    // The zoom window is drawn as a red dotted rectangle.
                    if (mq.Kind == MarqueeKind.Select) c.DrawRect(r, _marqueeFill);
                    else if (mq.Kind == MarqueeKind.Delete) c.DrawRect(r, _deleteFill);   // a box that deletes what it holds: red, with a faint red tint
                    c.DrawRect(r, mq.Kind is MarqueeKind.Zoom or MarqueeKind.Delete ? _zoomDash : _dash);
                    break;
                }
                case OutlineOverlay o:
                {
                    using var path = o.Path.Transform(m).ToSKPath();
                    c.DrawPath(path, _line);
                    break;
                }
                case CropOverlay crop:
                    DrawCrop(c, crop, view);
                    break;
                case GuideOverlay g:
                    DrawGuide(c, view.ToScreen(g.Origin), g, width, height);
                    break;
                case VectorEditOverlay v:
                    DrawVectors(c, v, view);
                    break;
                case SmallVectorMarksOverlay sv:
                {
                    // View > Highlight Small Vectors: a red 5 x 5 square at the end of each small vector.
                    foreach (var end in sv.Ends)
                    {
                        var q = view.ToScreen(end);
                        c.DrawRect(SKRect.Create(MathF.Floor((float)q.X) - 2, MathF.Floor((float)q.Y) - 2, 5, 5), _smallMark);
                    }
                    break;
                }
                case LoopEditOverlay lo:
                    DrawLoops(c, lo, view);
                    break;
                case TextEditOverlay te:
                    DrawTextEdit(c, te, view);
                    break;
                case LetterDotsOverlay dots:
                    DrawLetterDots(c, dots, view);
                    break;
                case GradientOverlay gradient:
                    DrawGradientControls(c, gradient, view);
                    break;
                case PointHandlesOverlay handles:
                    DrawPointHandles(c, handles, view);
                    break;
                case TrackingHandleOverlay th:
                {
                    // Larger than the 7px scale handles so it reads as its own tool.
                    var p = view.ToScreen(th.Position);
                    var r = Snap(SKRect.Create((float)p.X - 6, (float)p.Y - 6, 12, 12));
                    using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = th.Active ? SKColors.White : Accent };
                    c.DrawRect(r, fill);
                    c.DrawRect(r, _line);
                    break;
                }
            }
        }
        return new SharedPicture(rec.EndRecording());
    }

    /// <summary>
    /// Vector mode: the nodes as solid black squares (small crosses for smooth nodes), then the loops across them, one pixel wide and inverting\n    /// what is under them (border blue, interior red, broken black on white), the vector under the cursor dashed, and handles.
    /// </summary>
    private void DrawVectors(SKCanvas c, VectorEditOverlay v, Viewport view)
    {
        var m = view.DocToScreen;
        // The nodes go down first, solid black, so the outline drawn after them runs across them (which is why the small ones look lighter
        // in the middle). Same pixel size at any zoom: a loop's start/end point is the big 10 px square, an ordinary node a 6 px square, a
        // node exactly where an earlier one was drawn a 4 px square (inside the first one's, so no different to look at), a node where the
        // two vectors meet tangentially a small cross (arms 4 px), a selected node an 8 px square.
        using var nodeFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Black };
        using var cross = new SKPath();
        foreach (var n in v.Nodes)
        {
            var p = view.ToScreen(n.Position);
            float ix = MathF.Floor((float)p.X), iy = MathF.Floor((float)p.Y);
            float x = ix + 0.5f, y = iy + 0.5f;
            if (n.Selected)
                c.DrawRect(SKRect.Create(ix - 4, iy - 4, 8, 8), nodeFill);
            else if (n.Start)
                c.DrawRect(SKRect.Create(ix - 5, iy - 5, 10, 10), nodeFill);
            else if (!n.Corner)
            {
                cross.MoveTo(x - 4, y); cross.LineTo(x + 5, y);
                cross.MoveTo(x, y - 4); cross.LineTo(x, y + 5);
            }
            else if (n.Coincident)
                c.DrawRect(SKRect.Create(ix - 2, iy - 2, 4, 4), nodeFill);
            else
                c.DrawRect(SKRect.Create(ix - 3, iy - 3, 6, 6), nodeFill);
        }
        using (var crossPaint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.Black })
            c.DrawPath(cross, crossPaint);   // one path, so crossing pixels are not drawn twice
        // The outline inverts what is under it, one pixel wide and not smoothed (border loops come out blue on white, interior loops red,
        // broken ones black), so it stays visible on any background and over the black node marks.
        using var loopPaint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1, BlendMode = SKBlendMode.Difference };
        foreach (var loop in v.Loops)
        {
            var shown = CanvasTheme.Light.ColorOf(loop.Kind);
            loopPaint.Color = new SKColor((byte)(255 - shown.Red), (byte)(255 - shown.Green), (byte)(255 - shown.Blue));
            using var path = loop.Path.Transform(m).ToSKPath();
            path.Transform(SKMatrix.CreateTranslation(0.5f, 0.5f));   // a point lands on the pixel that contains it
            c.DrawPath(path, loopPaint);
        }
        if (v.Highlight is { } hl)
        {
            using var dashed = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = SKColors.Red,
                PathEffect = SKPathEffect.CreateDash([4, 3], 0),
            };
            using var path = hl.Transform(m).ToSKPath();
            c.DrawPath(path, dashed);
        }
        // The selected vector is dotted red and heavier.
        if (v.SelectedVector is { } sel)
        {
            using var dotted = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = SKColors.Red,
                PathEffect = SKPathEffect.CreateDash([2, 3], 0),
            };
            using var path = sel.Transform(m).ToSKPath();
            c.DrawPath(path, dotted);
        }
        using var handleLine = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.Gray };
        using var handleDot = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = Accent };
        foreach (var (node, handle) in v.Handles)
        {
            var a = view.ToScreen(node).ToSK();
            var b = view.ToScreen(handle).ToSK();
            c.DrawLine(a, b, handleLine);
            c.DrawCircle(b, 3.5f, handleDot);
        }
    }
    /// <summary>
    /// Loop Edit tool: loops in colors, the selected ones dotted red, a black square at each loop's start/end point,
    /// optional loop numbers, and the dotted red preview of a loop being copied.
    /// </summary>
    private void DrawLoops(SKCanvas c, LoopEditOverlay l, Viewport view)
    {
        var m = view.DocToScreen;
        using var loopPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        foreach (var loop in l.Loops)
        {
            loopPaint.Color = _theme.ColorOf(loop.Kind);
            using var path = loop.Path.Transform(m).ToSKPath();
            c.DrawPath(path, loopPaint);
        }
        using var hover = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = SKColors.Red.WithAlpha(0xA0),
            PathEffect = SKPathEffect.CreateDash([4, 3], 0),
        };
        foreach (var g in l.Hover)
        {
            using var path = g.Transform(m).ToSKPath();
            c.DrawPath(path, hover);
        }
        using var dotted = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = SKColors.Red,
            PathEffect = SKPathEffect.CreateDash([2, 3], 0),
        };
        foreach (var g in l.Selected.Concat(l.Preview))
        {
            using var path = g.Transform(m).ToSKPath();
            c.DrawPath(path, dotted);
        }
        foreach (var s in l.StartMarks)
        {
            var p = view.ToScreen(s);
            float x = MathF.Floor((float)p.X) + 0.5f, y = MathF.Floor((float)p.Y) + 0.5f;
            c.DrawRect(SKRect.Create(x - 3.5f, y - 3.5f, 8, 8), _handleFill);
        }
        if (l.Numbers.Count > 0)
        {
            using var font = new SKFont(SKTypeface.Default, 11);
            using var text = new SKPaint { IsAntialias = true, Color = _theme.Label };
            using var halo = new SKPaint { IsAntialias = true, Color = _theme.LabelHalo, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
            foreach (var (pos, number) in l.Numbers)
            {
                var p = view.ToScreen(pos);
                string s = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                c.DrawText(s, (float)p.X + 6, (float)p.Y - 5, font, halo);
                c.DrawText(s, (float)p.X + 6, (float)p.Y - 5, font, text);
            }
        }
    }

    /// <summary>Crop tool: everything outside the box dimmed, the box outlined (white over dashed black, visible on any picture) and its handles.</summary>
    private void DrawCrop(SKCanvas c, CropOverlay crop, Viewport view)
    {
        static SKPath Quad(Vec2[] corners, Viewport view)
        {
            var p = new SKPath();
            p.MoveTo(view.ToScreen(corners[0]).ToSK());
            for (int i = 1; i < corners.Length; i++) p.LineTo(view.ToScreen(corners[i]).ToSK());
            p.Close();
            return p;
        }

        using var frame = Quad(crop.Frame, view);
        using var dim = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(0, 0, 0, 0x90), IsAntialias = true };
        using var edge = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.White, IsAntialias = true };
        if (crop.Box is not { } corners)
        {
            c.DrawPath(frame, edge);
            return;
        }
        using var box = Quad(corners, view);
        using var outside = new SKPath { FillType = SKPathFillType.EvenOdd };
        outside.AddPath(frame);
        outside.AddPath(box);
        c.DrawPath(outside, dim);
        c.DrawPath(box, edge);
        c.DrawPath(box, _dash);
        foreach (var h in crop.Handles)
        {
            var p = view.ToScreen(h);
            var r = Snap(SKRect.Create((float)p.X - HandleSize / 2, (float)p.Y - HandleSize / 2, HandleSize, HandleSize));
            c.DrawRect(r, _handleFill);
            c.DrawRect(r, _line);
        }
    }

    /// <summary>Text edit mode: the selected letters tinted, and a caret line.</summary>
    private void DrawTextEdit(SKCanvas c, TextEditOverlay t, Viewport view)
    {
        foreach (var quad in t.Highlights)
        {
            using var path = new SKPath();
            path.MoveTo(view.ToScreen(quad[0]).ToSK());
            for (int i = 1; i < quad.Length; i++) path.LineTo(view.ToScreen(quad[i]).ToSK());
            path.Close();
            c.DrawPath(path, _marqueeFill);
        }
        var a = view.ToScreen(t.CaretTop);
        var b = view.ToScreen(t.CaretBottom);
        using var caret = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = _theme.Caret };
        c.DrawLine(a.ToSK(), b.ToSK(), caret);
    }

    /// <summary>
    /// Gradient Blend: the control line (and the circle or ellipse outline) in white over black so it shows on any art, with a square handle
    /// per color filled with that color; the picked color's handle is larger, a highlight's is a diamond. The second axis of an ellipse is a hollow handle.
    /// </summary>
    private void DrawGradientControls(SKCanvas c, GradientOverlay g, Viewport view)
    {
        var start = view.ToScreen(g.Start).ToSK();
        var end = view.ToScreen(g.End).ToSK();
        using var casing = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = SKColors.Black.WithAlpha(0xB0) };
        using var inner = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.White };
        using var dashed = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.White, PathEffect = SKPathEffect.CreateDash([4, 3], 0) };

        if (g.Shape is { } shape)
        {
            using var outline = shape.Transform(view.DocToScreen).ToSKPath();
            c.DrawPath(outline, casing);
            c.DrawPath(outline, dashed);
        }
        c.DrawLine(start, end, casing);
        c.DrawLine(start, end, inner);
        if (g.Axis2End is { } a2)
        {
            var p2 = view.ToScreen(a2).ToSK();
            c.DrawLine(start, p2, casing);
            c.DrawLine(start, p2, dashed);
        }

        using var edge = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.Black };
        using var edgeWhite = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.White };
        using var fill = new SKPaint { Style = SKPaintStyle.Fill };
        foreach (var mark in g.Marks)
        {
            var p = view.ToScreen(mark.Position);
            float half = mark.Current ? 7 : 5;
            var box = Snap(SKRect.Create((float)p.X - half, (float)p.Y - half, half * 2, half * 2));
            if (mark.Color is null)
            {
                fill.Color = SKColors.White;
                c.DrawRect(box, fill);
                c.DrawRect(box, edge);
                continue;
            }
            fill.Color = new SKColor(mark.Color.Value.R, mark.Color.Value.G, mark.Color.Value.B);
            if (mark.Highlight)
            {
                using var diamond = new SKPath();
                diamond.MoveTo(box.MidX, box.Top - 1); diamond.LineTo(box.Right + 1, box.MidY);
                diamond.LineTo(box.MidX, box.Bottom + 1); diamond.LineTo(box.Left - 1, box.MidY); diamond.Close();
                c.DrawPath(diamond, fill);
                c.DrawPath(diamond, edgeWhite);
                c.DrawPath(diamond, edge);
            }
            else
            {
                c.DrawRect(box, fill);
                c.DrawRect(SKRect.Inflate(box, 1, 1), edgeWhite);
                c.DrawRect(box, edge);
            }
        }
    }

    /// <summary>Effect points (bevel light, shadow vanishing/cast point): white over black lines and dashed ring so they show on any art, round handles that fill white while dragged.</summary>
    private void DrawPointHandles(SKCanvas c, PointHandlesOverlay h, Viewport view)
    {
        using var casing = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = SKColors.Black.WithAlpha(0xB0) };
        using var inner = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.White };
        using var dashed = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.White, PathEffect = SKPathEffect.CreateDash([4, 3], 0) };
        if (h.Ring is { } ring)
        {
            using var outline = ring.Transform(view.DocToScreen).ToSKPath();
            c.DrawPath(outline, casing);
            c.DrawPath(outline, dashed);
        }
        foreach (var (from, to) in h.Lines)
        {
            var a = view.ToScreen(from).ToSK();
            var b = view.ToScreen(to).ToSK();
            c.DrawLine(a, b, casing);
            c.DrawLine(a, b, inner);
        }
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.Black };
        foreach (var mark in h.Points)
        {
            var p = view.ToScreen(mark.Position).ToSK();
            float r = mark.Active ? 7 : 6;
            fill.Color = mark.Active ? SKColors.White : Accent;
            c.DrawCircle(p, r + 1, inner);
            c.DrawCircle(p, r, fill);
            c.DrawCircle(p, r, edge);
        }
    }

    /// <summary>Live-text marks: a small dot (half its former size, <see cref="LetterDotsOverlay.RadiusPx"/>) in every gap between letters; the one being dragged is filled.</summary>
    private void DrawLetterDots(SKCanvas c, LetterDotsOverlay d, Viewport view)
    {
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = Accent };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.White };
        using var active = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = Accent };
        float radius = LetterDotsOverlay.RadiusPx;
        for (int i = 0; i < d.Dots.Count; i++)
        {
            var p = view.ToScreen(d.Dots[i]).ToSK();
            c.DrawCircle(p, radius, i == d.Active ? active : fill);
            c.DrawCircle(p, radius, ring);
        }
    }

    /// <summary>A guide is an infinite line: draw it across the whole canvas, crisp when it is horizontal or vertical.</summary>
    private void DrawGuide(SKCanvas c, Vec2 p, GuideOverlay g, float width, float height)
    {
        var paint = g.Selected ? _guideSelected : _guide;
        switch (Guide.Orient(g.AngleDegrees))
        {
            case GuideOrientation.Horizontal:
                float y = MathF.Floor((float)p.Y) + 0.5f;
                c.DrawLine(0, y, width, y, paint);
                break;
            case GuideOrientation.Vertical:
                float x = MathF.Floor((float)p.X) + 0.5f;
                c.DrawLine(x, 0, x, height, paint);
                break;
            default:
                var d = Guide.DirectionOf(g.AngleDegrees);
                double len = width + height;
                paint.IsAntialias = true;
                c.DrawLine((float)(p.X - d.X * len), (float)(p.Y - d.Y * len), (float)(p.X + d.X * len), (float)(p.Y + d.Y * len), paint);
                paint.IsAntialias = false;
                break;
        }
    }

    /// <summary>Aligns a rectangle to half-pixels so 1px lines stay crisp at 100% scaling.</summary>
    private static SKRect Snap(SKRect r) =>
        new(MathF.Floor(r.Left) + 0.5f, MathF.Floor(r.Top) + 0.5f, MathF.Floor(r.Right) + 0.5f, MathF.Floor(r.Bottom) + 0.5f);

    public void Dispose()
    {
        _line.Dispose();
        _dash.Dispose();
        _zoomDash.Dispose();
        _deleteFill.Dispose();
        _smallMark.Dispose();
        _guide.Dispose();
        _guideSelected.Dispose();
        _handleFill.Dispose();
        _centerMark.Dispose();
        _marqueeFill.Dispose();
    }
}
