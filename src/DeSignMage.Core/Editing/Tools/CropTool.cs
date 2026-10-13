using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// Image â–¸ Crop Tool. Modal: drag a box over the picture (it cannot leave the picture), adjust it by dragging inside it (move)
/// or its handles (resize), then Enter, a double-click or the Context Bar's check mark crops; Esc cancels.
/// Dragging outside the box starts a new one. The box lives in the picture's own frame (points), so a rotated or scaled
/// picture crops correctly. Cropping removes pixels and is one undo step.
/// </summary>
public sealed class CropTool : Tool
{
    private enum Mode { None, Drawing, Moving, Resizing }

    private ImageObject? _target;
    private Rect2? _box;            // frame coordinates (points, 0..Width x 0..Height)
    private Mode _mode;
    private Vec2 _anchor;           // Drawing: the corner where the drag began; Moving: pointer offset into the box
    private Handle _handle;
    private Rect2? _boxAtStart;

    public override string Id => "crop";
    public override string DisplayName => "Crop Tool";
    public override string Hint => _box is null
        ? "To CROP: Drag a box over the picture. Enter crops, Esc cancels."
        : "To CROP: Enter or double-click crops, Esc cancels. Drag inside the box to move it, drag its handles to resize.";
    public override CursorKind Cursor => CursorKind.Cross;

    /// <summary>The picture being cropped, or null while the tool is idle.</summary>
    public ImageObject? Target => _target;

    /// <summary>The box to keep, in the picture's frame (points); null until one was dragged.</summary>
    public Rect2? Box => _box;

    public bool HasBox => _box is not null;

    /// <summary>The picture's frame in points, or null without a target.</summary>
    private Rect2 Frame => _target is { } t ? new Rect2(0, 0, t.Width, t.Height) : Rect2.Empty;

    /// <summary>Starts cropping <paramref name="image"/> (the session activates the tool and sets this).</summary>
    internal void SetTarget(ImageObject image)
    {
        _target = image;
        _box = null;
        _mode = Mode.None;
        Redraw();
    }

    /// <summary>The box in whole pixels (as it will be cropped), or null without a box or pixel size.</summary>
    public (int X, int Y, int Width, int Height)? BoxPixels
    {
        get
        {
            if (_target is not { HasPixelSize: true } t || _box is not { } b) return null;
            return Imaging.ImageFrames.PixelRect(b, t.Width, t.Height, t.PixelWidth, t.PixelHeight);
        }
    }

    /// <summary>Sets the box size in pixels (Context Bar Px W / Px H), keeping its top-left corner where it is, or the picture's if there is no box yet.</summary>
    public void SetBoxPixelSize(int width, int height)
    {
        if (_target is not { HasPixelSize: true } t) return;
        double ppx = t.Width / t.PixelWidth, ppy = t.Height / t.PixelHeight;
        var origin = _box?.TopLeft ?? Vec2.Zero;
        double w = Math.Clamp(width, 1, t.PixelWidth) * ppx, h = Math.Clamp(height, 1, t.PixelHeight) * ppy;
        double x = Math.Min(origin.X, t.Width - w), y = Math.Min(origin.Y, t.Height - h);
        _box = Rect2.FromXYWH(Math.Max(0, x), Math.Max(0, y), w, h);
        Redraw();
    }

    /// <summary>Crops to the box (does nothing without one) and returns to the select tool.</summary>
    public bool Apply()
    {
        if (_target is not { } image || !Session.Document.Contains(image) || BoxPixels is not { } r) return false;
        var done = Session.CropImage(image, r.X, r.Y, r.Width, r.Height);
        Session.SetTool("select");
        return done;
    }

    public override void Deactivate()
    {
        base.Deactivate();
        _target = null;
        _box = null;
    }

    public override void Cancel()
    {
        if (_mode != Mode.None)
        {
            _mode = Mode.None;
            _box = _boxAtStart;
        }
        Redraw();
    }

    // ---- Pointer ----

    private Vec2 ToFrame(Vec2 doc) => _target!.GetWorldTransform().Invert().Transform(doc);

    private Vec2 Clamp(Vec2 p) => new(Math.Clamp(p.X, 0, _target!.Width), Math.Clamp(p.Y, 0, _target!.Height));

    public override void PointerDown(PointerInput e)
    {
        if (_target is not { } t || !Session.Document.Contains(t) || !t.GetWorldTransform().IsInvertible) return;
        var p = ToFrame(e.Doc);
        _boxAtStart = _box;

        if (_box is { } box)
        {
            if (e.ClickCount >= 2 && box.Contains(p)) { Apply(); return; }
            if (HandleAt(e.Screen) is { } h)
            {
                _mode = Mode.Resizing;
                _handle = h;
                return;
            }
            if (box.Contains(p))
            {
                _mode = Mode.Moving;
                _anchor = p - box.TopLeft;
                return;
            }
        }
        _mode = Mode.Drawing;
        _anchor = Clamp(p);
        _box = null;
        _drawStartedAt = e.Screen;
        Redraw();
    }

    private Vec2 _drawStartedAt;

    public override void PointerMove(PointerInput e)
    {
        if (_target is null || _mode == Mode.None) return;
        var p = Clamp(ToFrame(e.Doc));
        switch (_mode)
        {
            case Mode.Drawing:
                if (Vec2.Distance(_drawStartedAt, e.Screen) >= Session.ClickThresholdPx || _box is not null)
                    _box = Rect2.FromPoints(_anchor, p);
                break;
            case Mode.Moving:
            {
                var b = _box!.Value;
                double x = Math.Clamp(ToFrame(e.Doc).X - _anchor.X, 0, _target.Width - b.Width);
                double y = Math.Clamp(ToFrame(e.Doc).Y - _anchor.Y, 0, _target.Height - b.Height);
                _box = Rect2.FromXYWH(x, y, b.Width, b.Height);
                break;
            }
            case Mode.Resizing:
                _box = Resized(_boxAtStart!.Value, _handle, p);
                break;
        }
        Redraw();
    }

    public override void PointerUp(PointerInput e)
    {
        if (_mode == Mode.None) return;
        PointerMove(e);
        // A plain click (no drag) leaves no box behind.
        if (_mode == Mode.Drawing && _box is { } b && (b.Width < 1e-6 || b.Height < 1e-6)) _box = null;
        _mode = Mode.None;
        Redraw();
    }

    private static Rect2 Resized(Rect2 start, Handle h, Vec2 p)
    {
        double l = start.Left, t = start.Top, r = start.Right, b = start.Bottom;
        if (h is Handle.TopLeft or Handle.Left or Handle.BottomLeft) l = p.X;
        if (h is Handle.TopRight or Handle.Right or Handle.BottomRight) r = p.X;
        if (h is Handle.TopLeft or Handle.Top or Handle.TopRight) t = p.Y;
        if (h is Handle.BottomLeft or Handle.Bottom or Handle.BottomRight) b = p.Y;
        return new Rect2(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
    }

    public override bool KeyDown(EditorKey key, ModifierKeys modifiers)
    {
        switch (key)
        {
            case EditorKey.Enter:
                if (_mode != Mode.None) return true;
                Apply();
                return true;
            case EditorKey.Escape when _mode != Mode.None:
                Cancel();
                return true;
        }
        return false;   // Esc with no drag: the session returns to the select tool, which drops the crop
    }

    // ---- Overlay ----

    private Vec2[] ToDoc(Rect2 r)
    {
        var w = _target!.GetWorldTransform();
        return
        [
            w.Transform(new Vec2(r.Left, r.Top)), w.Transform(new Vec2(r.Right, r.Top)),
            w.Transform(new Vec2(r.Right, r.Bottom)), w.Transform(new Vec2(r.Left, r.Bottom)),
        ];
    }

    private IReadOnlyList<Vec2> HandlePositions(Rect2 b)
    {
        var w = _target!.GetWorldTransform();
        return SelectionHandles.All.Select(h => w.Transform(SelectionHandles.Position(b, h))).ToArray();
    }

    private Handle? HandleAt(Vec2 screen)
    {
        if (_box is not { } b || _target is null) return null;
        var w = _target.GetWorldTransform();
        Handle? best = null;
        double bestD = Session.HandleRadiusPx;
        foreach (var h in SelectionHandles.All)
        {
            double d = Vec2.Distance(Session.Viewport.ToScreen(w.Transform(SelectionHandles.Position(b, h))), screen);
            if (d <= bestD) { bestD = d; best = h; }
        }
        return best;
    }

    public override void BuildOverlay(List<OverlayItem> items)
    {
        if (_target is not { } t || !Session.Document.Contains(t)) return;
        items.Add(new CropOverlay(ToDoc(Frame), _box is { } b ? ToDoc(b) : null, _box is { } hb ? HandlePositions(hb) : []));
    }
}

