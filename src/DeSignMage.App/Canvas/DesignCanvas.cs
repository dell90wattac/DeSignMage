using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using DeSignMage.App.Shell;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Rendering;

namespace DeSignMage.App.Canvas;

/// <summary>
/// The drawing area. Converts Avalonia input to <see cref="PointerInput"/> for the <see cref="EditorSession"/>,
/// and draws with Skia: each frame's pictures are recorded here on the UI thread (<see cref="CanvasFrameBuilder"/>)
/// and replayed on the render thread by <see cref="SkiaDrawOperation"/>.
/// </summary>
public sealed class DesignCanvas : Control
{
    private readonly Dictionary<CursorKind, Cursor> _cursors = [];
    private readonly Dictionary<double, Cursor> _drawCursors = [];   // the Vector Draw cursor per display scale
    private readonly Dictionary<double, Cursor> _hitCursors = [];   // the select-hit cursor per display scale
    private EditorSession? _session;
    private CanvasFrameBuilder? _frames;
    private bool _fittedOnce;
    private DispatcherTimer? _settleTimer;

    public DesignCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>Last pointer position in document coordinates (for rulers and the status bar).</summary>
    public Vec2? PointerDoc { get; private set; }

    public event EventHandler? PointerDocChanged;

    /// <summary>Raised after every redraw request, e.g. to refresh the help bar text.</summary>
    public event EventHandler? Redrawn;

    /// <summary>Render-thread time of the last frame in ms (written from the render thread).</summary>
    public double LastDrawMs => SkiaDrawOperation.LastDrawMs;
    public double LastRecordMs => _frames?.LastRecordMs ?? 0;

    public EditorSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null) _session.RedrawRequested -= OnRedrawRequested;
            _frames?.Dispose();
            _session = value;
            _frames = value is null ? null : new CanvasFrameBuilder(value);
            _fittedOnce = false;
            if (_session is not null) _session.RedrawRequested += OnRedrawRequested;
            // A canvas that is already laid out gets no size change when its session is replaced (File > New / Open),
            // so size the view and fit it here instead of waiting for OnSizeChanged.
            if (_session is not null && Bounds.Width > 0 && Bounds.Height > 0)
            {
                _session.Viewport.SetViewSize(new Vec2(Bounds.Width, Bounds.Height));
                _fittedOnce = true;
                _session.ZoomToContent();
            }
            InvalidateVisual();
        }
    }

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

    private void OnRedrawRequested(object? sender, EventArgs e)
    {
        InvalidateVisual();
        UpdateCursor();
        Redrawn?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_session is null) return;
        _session.Viewport.SetViewSize(new Vec2(e.NewSize.Width, e.NewSize.Height));
        if (!_fittedOnce && e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            _fittedOnce = true;
            _session.ZoomToContent();   // open at the base ("fully zoomed out") view
        }
    }

    public override void Render(DrawingContext context)
    {
        if (_frames is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        _frames.Theme = AppTheme.Canvas;   // Dark Canvas (R19): only what is drawn on screen, never an export
        var frame = _frames.Build((float)Bounds.Width, (float)Bounds.Height);
        context.Custom(new SkiaDrawOperation(new Rect(Bounds.Size), frame));
        if (_frames.SettleFramePending) RestartSettleTimer();
    }

    /// <summary>
    /// While the view is panned the picture is drawn from moved pixels; once the view has stood still for a moment, one more frame draws it
    /// exactly. Each panned or zoomed frame pushes that frame back, so it comes only after the last one.
    /// </summary>
    private void RestartSettleTimer()
    {
        if (_settleTimer is null)
        {
            _settleTimer = new DispatcherTimer { Interval = CanvasFrameBuilder.SettleDelay + TimeSpan.FromMilliseconds(40) };
            _settleTimer.Tick += (_, _) => { _settleTimer.Stop(); InvalidateVisual(); };
        }
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    // ---- Input ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_session is null) return;
        Focus();
        var pt = e.GetCurrentPoint(this);
        var button = pt.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => PointerButton.Left,
            PointerUpdateKind.MiddleButtonPressed => PointerButton.Middle,
            PointerUpdateKind.RightButtonPressed => PointerButton.Right,
            _ => PointerButton.None,
        };
        if (button == PointerButton.None) return;
        e.Pointer.Capture(this);
        _session.PointerDown(MakeInput(pt.Position, button, e.KeyModifiers, e.ClickCount));
        e.Handled = true;
        UpdateCursor();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_session is null) return;
        var input = MakeInput(e.GetPosition(this), PointerButton.None, e.KeyModifiers);
        PointerDoc = input.Doc;
        PointerDocChanged?.Invoke(this, EventArgs.Empty);
        _session.PointerMove(input);
        UpdateCursor();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_session is null) return;
        var button = e.InitialPressMouseButton switch
        {
            MouseButton.Left => PointerButton.Left,
            MouseButton.Middle => PointerButton.Middle,
            MouseButton.Right => PointerButton.Right,
            _ => PointerButton.None,
        };
        _session.PointerUp(MakeInput(e.GetPosition(this), button, e.KeyModifiers));
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed && !props.IsMiddleButtonPressed && !props.IsRightButtonPressed)
            e.Pointer.Capture(null);
        e.Handled = true;
        UpdateCursor();
    }

    /// <summary>Typed characters go to the text object being edited (R6); letter shortcuts never see them.</summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_session is not null && !string.IsNullOrEmpty(e.Text) && _session.TextInput(e.Text))
            e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_session is null) return;
        var p = e.GetPosition(this);
        _session.Wheel(new Vec2(p.X, p.Y), e.Delta.Y);
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _session?.PointerExited();
        PointerDoc = null;
        PointerDocChanged?.Invoke(this, EventArgs.Empty);
    }

    private PointerInput MakeInput(Point p, PointerButton button, KeyModifiers mods, int clicks = 1)
    {
        var screen = new Vec2(p.X, p.Y);
        return new PointerInput(screen, _session!.Viewport.ToDoc(screen), button, ToModifiers(mods), clicks);
    }

    public static ModifierKeys ToModifiers(KeyModifiers m) =>
        ((m & KeyModifiers.Shift) != 0 ? ModifierKeys.Shift : 0) |
        ((m & KeyModifiers.Control) != 0 ? ModifierKeys.Control : 0) |
        ((m & KeyModifiers.Alt) != 0 ? ModifierKeys.Alt : 0);

    private void UpdateCursor()
    {
        if (_session is null) return;
        var kind = _session.CurrentCursor;
        if (kind == CursorKind.SelectHit)
        {
            double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            if (!_hitCursors.TryGetValue(scale, out var hit)) _hitCursors[scale] = hit = CursorArt.CreateSelectHit(scale);
            Cursor = hit;
            return;
        }
        if (kind == CursorKind.VectorDraw)
        {
            double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            if (!_drawCursors.TryGetValue(scale, out var draw)) _drawCursors[scale] = draw = CursorArt.CreateVectorDraw(scale);
            Cursor = draw;
            return;
        }
        if (!_cursors.TryGetValue(kind, out var cursor))
        {
            cursor = new Cursor(kind switch
            {
                CursorKind.Cross => StandardCursorType.Cross,
                CursorKind.Move => StandardCursorType.SizeAll,
                CursorKind.Copy => StandardCursorType.DragCopy,
                CursorKind.Slant => StandardCursorType.SizeWestEast,
                CursorKind.Rotate => StandardCursorType.Hand,   // TODO: custom rotate cursor
                CursorKind.SizeNS => StandardCursorType.SizeNorthSouth,
                CursorKind.SizeWE => StandardCursorType.SizeWestEast,
                CursorKind.SizeNWSE => StandardCursorType.TopLeftCorner,
                CursorKind.SizeNESW => StandardCursorType.TopRightCorner,
                CursorKind.Hand or CursorKind.Grabbing => StandardCursorType.Hand,
                CursorKind.ZoomIn => StandardCursorType.Cross,
                CursorKind.IBeam => StandardCursorType.Ibeam,
                CursorKind.Eyedropper => StandardCursorType.Cross,
                _ => StandardCursorType.Arrow,
            });
            _cursors[kind] = cursor;
        }
        Cursor = cursor;
    }
}

/// <summary>Replays a <see cref="CanvasFrame"/> onto Avalonia's Skia canvas (render thread).</summary>
internal sealed class SkiaDrawOperation(Rect bounds, CanvasFrame frame) : ICustomDrawOperation
{
    private static long _lastDrawTicks;

    public static double LastDrawMs => Interlocked.Read(ref _lastDrawTicks) * 1000.0 / Stopwatch.Frequency;

    private static int _firstFrameDone;

    /// <summary>Raised once, on the render thread, after the first frame was drawn (startup timing).</summary>
    public static event Action? FirstFrameDrawn;

    public Rect Bounds { get; } = bounds;

    public bool HitTest(Point p) => Bounds.Contains(p);
    public bool Equals(ICustomDrawOperation? other) => false;

    public void Render(ImmediateDrawingContext context)
    {
        var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (lease is null) return;
        long t0 = Stopwatch.GetTimestamp();
        using (var api = lease.Lease())
            frame.Draw(api.SkCanvas);
        Interlocked.Exchange(ref _lastDrawTicks, Stopwatch.GetTimestamp() - t0);
        if (Interlocked.Exchange(ref _firstFrameDone, 1) == 0) FirstFrameDrawn?.Invoke();
    }

    public void Dispose() => frame.Dispose();
}
