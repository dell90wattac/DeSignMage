using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

/// <summary>An eyedropper click: the painted object under the cursor and its style.</summary>
public sealed class ColorPickedEventArgs(DocObject source, ObjectStyle style) : EventArgs
{
    public DocObject Source { get; } = source;
    public ObjectStyle Style { get; } = style;
}

/// <summary>
/// Fill and stroke editing (task C.3). Every change goes to the selection (groups and combined objects pass it on to all
/// members, see <see cref="StyleTargets"/>), or becomes the default style of new objects when nothing is selected.
/// A drag on a slider or the color wheel brackets its changes with <see cref="BeginStyleGesture"/> /
/// <see cref="EndStyleGesture"/>: the document updates live and the whole drag is ONE undo step.
/// </summary>
public sealed partial class EditorSession
{
    // New shapes start with no color at all: no fill and no stroke (user, 2026-10-12). They show as plain vector outlines until
    // a color is chosen, so nothing looks pre-colored in a fresh session.
    private ObjectStyle _defaultStyle = ObjectStyle.None;

    /// <summary>What a stroke looks like when one is created from "none" (the last width, join and cap the user chose).</summary>
    private StrokeStyle _strokeMemory = StrokeStyle.Solid(DocColor.Black, 1);

    private SetStyleCommand? _liveStyle;
    private bool _styleGesture;

    /// <summary>Style given to newly drawn shapes; what the color UI edits while nothing is selected.</summary>
    public ObjectStyle DefaultStyle
    {
        get => _defaultStyle;
        set
        {
            if (_defaultStyle == value) return;
            _defaultStyle = value;
            DefaultStyleChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? DefaultStyleChanged;

    private JobPalette? _jobPalette;

    /// <summary>The colors in use in this document (the Job color palette), cached per document revision.</summary>
    public JobPalette JobPalette => _jobPalette ??= new JobPalette(Document);

    /// <summary>The style the color UI shows: the first selected object's (a group's top member), else the default for new objects.</summary>
    public ObjectStyle CurrentStyle => Selection.IsEmpty ? DefaultStyle : StyleTargets.Representative(Selection.Items[0]);

    // ---- Gestures ----

    public bool InStyleGesture => _styleGesture;

    /// <summary>Starts a continuous edit (mouse down on a slider/wheel). Changes until <see cref="EndStyleGesture"/> make one undo step.</summary>
    public void BeginStyleGesture()
    {
        _styleGesture = true;
        _liveStyle = null;
    }

    public void EndStyleGesture()
    {
        _styleGesture = false;
        _liveStyle = null;
    }

    // ---- Edits ----

    /// <summary>Sets the fill of the selection, or of new objects when nothing is selected. Null = no fill.</summary>
    public void ApplyFill(Paint? fill) => ApplyStyleChange("Fill Color", s => s.WithFill(fill));

    /// <summary>"No Fill" button (R11): removes the fill of the selection (groups and combined objects included), one undo step.</summary>
    public void ApplyNoFill() => ApplyStyleChange("No Fill", s => s.WithFill(null));

    /// <summary>"No Stroke" button: removes the stroke of the selection, one undo step. Width, join and cap are remembered for the next stroke.</summary>
    public void ApplyNoStroke() => ApplyStyleChange("No Stroke", s => s.WithStroke(null));

    /// <summary>
    /// The red X at the top of the Palette Strip (user, 2026-10-12): removes ALL color, fill and stroke, from the selection in one
    /// undo step. Uncolored objects show as their plain vector outline on screen.
    /// </summary>
    public void ApplyNoColor() => ApplyStyleChange("No Color", _ => ObjectStyle.None);

    /// <summary>Sets the stroke paint (keeping width, join and cap), or removes the stroke when null.</summary>
    public void ApplyStrokePaint(Paint? paint) => ApplyStyleChange("Stroke Color", s =>
        s.WithStroke(paint is null ? null : (s.Stroke ?? DefaultStrokeForNew()) with { Paint = paint }));

    /// <summary>Stroke width in points. Zero or less removes the stroke. Creates a black stroke when there was none.</summary>
    public void ApplyStrokeWidth(double points)
    {
        if (points > 0) _strokeMemory = _strokeMemory with { Width = points };
        ApplyStyleChange("Stroke Width", s => s.WithStroke(
            points <= 0 ? null : (s.Stroke ?? DefaultStrokeForNew()) with { Width = points }));
    }

    /// <summary>Corner style of strokes. Objects without a stroke are left alone; new strokes use it from now on.</summary>
    public void ApplyStrokeJoin(LineJoin join)
    {
        _strokeMemory = _strokeMemory with { Join = join };
        ApplyStyleChange("Stroke Join", s => s.Stroke is null ? s : s.WithStroke(s.Stroke with { Join = join }));
    }

    /// <summary>Line-end style of strokes. Objects without a stroke are left alone; new strokes use it from now on.</summary>
    public void ApplyStrokeCap(LineCap cap)
    {
        _strokeMemory = _strokeMemory with { Cap = cap };
        ApplyStyleChange("Stroke Cap", s => s.Stroke is null ? s : s.WithStroke(s.Stroke with { Cap = cap }));
    }

    private StrokeStyle DefaultStrokeForNew() => _strokeMemory;

    /// <summary>
    /// Applies <paramref name="change"/> to the selection as one undo step (or, inside a gesture, as the gesture's single
    /// step), or to <see cref="DefaultStyle"/> when nothing is selected.
    /// </summary>
    public void ApplyStyleChange(string description, Func<ObjectStyle, ObjectStyle> change)
    {
        if (Selection.IsEmpty)
        {
            DefaultStyle = change(DefaultStyle);
            return;
        }
        if (_styleGesture && _liveStyle is { } live && History.IsTop(live))
        {
            live.Update(change);
            History.NotifyTopEdited(live);
            return;
        }
        var cmd = new SetStyleCommand(description, Selection.Items, change);
        if (cmd.IsNoOp) return;
        History.Execute(cmd);
        if (_styleGesture) _liveStyle = cmd;
    }

    // ---- Eyedropper ----

    private string? _eyedropperReturnTool;

    /// <summary>Raised when the eyedropper picked an object; the shell reads the color it needs from <see cref="ColorPickedEventArgs.Style"/>.</summary>
    public event EventHandler<ColorPickedEventArgs>? ColorPicked;

    /// <summary>Switches to the eyedropper; after one pick (or Escape) the previous tool comes back.</summary>
    public void BeginEyedropper()
    {
        if (ActiveTool is not EyedropperTool) _eyedropperReturnTool = ActiveTool.Id;
        SetTool("eyedropper");
    }

    public void CancelEyedropper()
    {
        var back = _eyedropperReturnTool ?? "select";
        _eyedropperReturnTool = null;
        if (ActiveTool is EyedropperTool) SetTool(back);
    }

    internal void RaiseColorPicked(DocObject source)
    {
        var style = source.Style;
        var back = _eyedropperReturnTool ?? "select";
        _eyedropperReturnTool = null;
        SetTool(back);
        ColorPicked?.Invoke(this, new ColorPickedEventArgs(source, style));
    }
}
