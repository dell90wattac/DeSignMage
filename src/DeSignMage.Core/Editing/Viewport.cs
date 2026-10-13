using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Editing;

public readonly record struct ViewState(double Zoom, Vec2 Pan);

/// <summary>
/// Maps document points to screen DIPs: <c>screen = doc * Zoom + Pan</c>. Holds the right-button zoom stack
/// (requirement R1): marquee zoom pushes the current view, right-click pops it.
/// </summary>
public sealed class Viewport
{
    public const double MinZoom = 0.002;
    public const double MaxZoom = 1000;

    private readonly Stack<ViewState> _zoomStack = new();
    private ViewState? _step;      // the view the last marquee zoom or zoom-out step produced
    private bool _wheelAdjusted;   // the wheel has changed the view since then

    /// <summary>Screen DIPs per document point. See <see cref="ZoomPercent"/> for the user-facing value.</summary>
    public double Zoom { get; private set; } = 1;

    /// <summary>Screen position (DIPs) of the document origin.</summary>
    public Vec2 Pan { get; private set; }

    /// <summary>Size of the canvas control in DIPs.</summary>
    public Vec2 ViewSize { get; private set; } = new(800, 600);

    /// <summary>Fraction of the view kept free on each side by marquee zoom and zoom-to-fit.</summary>
    public double FitMargin { get; set; } = 0.05;

    /// <summary>Zoom expressed so that 100% shows a document inch as a 96-DIP screen inch.</summary>
    public double ZoomPercent => Zoom * 72.0 / 96.0 * 100.0;

    public int ZoomStackDepth => _zoomStack.Count;
    public ViewState State => new(Zoom, Pan);

    public event EventHandler? Changed;

    public Affine2 DocToScreen => new(Zoom, 0, 0, Zoom, Pan.X, Pan.Y);
    public Vec2 ToScreen(Vec2 doc) => doc * Zoom + Pan;
    public Vec2 ToDoc(Vec2 screen) => (screen - Pan) / Zoom;
    public double ToDocLength(double screenLength) => screenLength / Zoom;

    public Rect2 VisibleDocRect => Rect2.FromPoints(ToDoc(Vec2.Zero), ToDoc(ViewSize));

    public void SetViewSize(Vec2 size)
    {
        if (size == ViewSize) return;
        // Keep the center of the view fixed while resizing.
        var center = ToDoc(ViewSize / 2);
        ViewSize = size;
        Pan = size / 2 - center * Zoom;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Restore(ViewState s) => Set(s.Zoom, s.Pan);

    public void Set(double zoom, Vec2 pan)
    {
        Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        Pan = pan;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void PanBy(Vec2 screenDelta) => Set(Zoom, Pan + screenDelta);

    /// <summary>Zooms by <paramref name="factor"/> keeping the document point under <paramref name="screenPoint"/> fixed.</summary>
    public void ZoomAt(Vec2 screenPoint, double factor)
    {
        var doc = ToDoc(screenPoint);
        double z = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        Set(z, screenPoint - doc * z);
    }

    /// <summary>Centers <paramref name="docRect"/> and fits it in the view with <see cref="FitMargin"/> on each side.</summary>
    public void ZoomToFit(Rect2 docRect)
    {
        if (docRect.IsEmpty) return;
        double availW = ViewSize.X * (1 - 2 * FitMargin), availH = ViewSize.Y * (1 - 2 * FitMargin);
        double w = Math.Max(docRect.Width, 1e-6), h = Math.Max(docRect.Height, 1e-6);
        double z = Math.Clamp(Math.Min(availW / w, availH / h), MinZoom, MaxZoom);
        Set(z, ViewSize / 2 - docRect.Center * z);
    }

    /// <summary>Right-drag marquee: remember the current view, then fit the marquee.</summary>
    public void PushAndZoomTo(Rect2 docRect)
    {
        // A wheel-adjusted view is never saved: the stack only holds marquee steps (requirement R1).
        _zoomStack.Push(_wheelAdjusted && _step is { } s ? s : State);
        ZoomToFit(docRect);
        _step = State;
        _wheelAdjusted = false;
    }

    /// <summary>Like <see cref="PushAndZoomTo"/>, but to an exact zoom (DIPs per document point) with <paramref name="docCenter"/> in the middle of the view.</summary>
    public void PushAndSetZoom(double zoom, Vec2 docCenter)
    {
        _zoomStack.Push(_wheelAdjusted && _step is { } s ? s : State);
        double z = Math.Clamp(zoom, MinZoom, MaxZoom);
        Set(z, ViewSize / 2 - docCenter * z);
        _step = State;
        _wheelAdjusted = false;
    }

    /// <summary>Mouse wheel zoom about the cursor. Not pushed on the zoom stack.</summary>
    public void WheelZoom(Vec2 screenPoint, double factor)
    {
        ZoomAt(screenPoint, factor);
        _wheelAdjusted = true;
    }

    /// <summary>
    /// Right-click. If the wheel changed the view since the last step, first return to that step
    /// (the user: right-click goes back "to the zoom state that was never affected by a scroll zoom");
    /// otherwise pop the previous marquee step. Returns false when there is nothing to go back to.
    /// </summary>
    public bool PopZoom()
    {
        if (_wheelAdjusted && _step is { } step)
        {
            _wheelAdjusted = false;
            Restore(step);
            return true;
        }
        if (_zoomStack.Count == 0) return false;
        Restore(_zoomStack.Pop());
        _step = State;
        _wheelAdjusted = false;
        return true;
    }

    public void ClearZoomStack()
    {
        _zoomStack.Clear();
        _step = null;
        _wheelAdjusted = false;
    }
}
