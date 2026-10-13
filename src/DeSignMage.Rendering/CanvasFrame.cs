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
        if (Theme.Page is { } pageColor)
        {
            using var page = new SKPaint { Color = pageColor };
            canvas.DrawRect(PageScreenRect, page);
        }

        canvas.Save();
        canvas.Concat(View);
        canvas.DrawPicture(Document.Picture);
        canvas.Restore();
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

    /// <summary>How many times the bitmap was made (tests, profiling).</summary>
    public int Rebuilds { get; private set; }

    private readonly record struct Key(uint Picture, SKMatrix Device, SKMatrix View, float Width, float Height, SKColor Pasteboard, SKColor? Page, SKRect PageRect);

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

        var key = new Key(picture.UniqueId, m, frame.View, frame.Width, frame.Height, frame.Theme.Pasteboard, frame.Theme.Page, frame.PageScreenRect);
        lock (_gate)
        {
            if (_image is null || !_key.Equals(key) || _image.Width != w || _image.Height != h)
            {
                // A drag preview is a sure sign that the picture under it will not change; otherwise (a rubber band, the pointer moving about) wait for
                // a second frame with the same picture and view, so panning and zooming, where every frame is new, never pay for a bitmap.
                bool again = _lastKey.Equals(key);
                _lastKey = key;
                if (frame.Preview is null && !again) return false;
                _image?.Dispose();
                _image = null;
                using var surface = SKSurface.Create(new SKImageInfo(w, h, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
                if (surface is null) return false;
                var c = surface.Canvas;
                // The same matrix the canvas has, moved so that device pixel (left, top) is this bitmap's first pixel.
                c.SetMatrix(m.PostConcat(SKMatrix.CreateTranslation(-left, -top)));
                c.ClipRect(SKRect.Create(frame.Width, frame.Height));
                frame.DrawBase(c);
                _image = surface.Snapshot();
                _key = key;
                _left = left;
                _top = top;
                Rebuilds++;
            }
            canvas.Save();
            canvas.SetMatrix(SKMatrix.Identity);   // device pixels; the clip set before stays where it was
            canvas.DrawImage(_image, _left, _top, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
            canvas.Restore();
        }
        return true;
    }

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

    public CanvasFrame Build(float width, float height)
    {
        var view = session.Viewport;
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
            StaticLayer = UseStaticLayer ? _staticLayer : null,
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
                    c.DrawRect(r, mq.Kind == MarqueeKind.Zoom ? _zoomDash : _dash);
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
                case LoopEditOverlay lo:
                    DrawLoops(c, lo, view);
                    break;
                case TextEditOverlay te:
                    DrawTextEdit(c, te, view);
                    break;
                case LetterDotsOverlay dots:
                    DrawLetterDots(c, dots, view);
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
    /// Vector mode: loops in colors (border blue, interior red, broken black), the vector under the cursor
    /// dashed, handles, and the nodes: big filled squares for corners, small crosses for smooth nodes.
    /// </summary>
    private void DrawVectors(SKCanvas c, VectorEditOverlay v, Viewport view)
    {
        var m = view.DocToScreen;
        using var loopPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        foreach (var loop in v.Loops)
        {
            loopPaint.Color = _theme.ColorOf(loop.Kind);
            using var path = loop.Path.Transform(m).ToSKPath();
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
        // Nodes: corners are small solid squares (a loop's start point a little bigger), smooth nodes are crosses.
        // Unselected marks invert what is under them like the selection handles do; selected nodes are filled with the accent color.
        using var nodeFill = new SKPaint { Style = SKPaintStyle.Fill, Color = Accent };
        using var cross = new SKPath();
        foreach (var n in v.Nodes)
        {
            var p = view.ToScreen(n.Position);
            float x = MathF.Floor((float)p.X) + 0.5f, y = MathF.Floor((float)p.Y) + 0.5f;
            float half = n.Start ? 3.5f : 2.5f;
            if (n.Selected)
            {
                c.DrawRect(SKRect.Create(x - 4, y - 4, 8, 8), nodeFill);
            }
            else if (n.Corner)
            {
                c.DrawRect(SKRect.Create(x - half, y - half, half * 2 + 1, half * 2 + 1), _handleFill);
            }
            else
            {
                cross.MoveTo(x - 3, y); cross.LineTo(x + 4, y);
                cross.MoveTo(x, y - 3); cross.LineTo(x, y + 4);
            }
        }
        c.DrawPath(cross, _centerMark);   // one path, so crossing pixels aren't inverted twice
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
        _guide.Dispose();
        _guideSelected.Dispose();
        _handleFill.Dispose();
        _centerMark.Dispose();
        _marqueeFill.Dispose();
    }
}
