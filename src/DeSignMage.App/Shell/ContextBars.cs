using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Shell;

/// <summary>A panel that re-reads its values from the session when the document or selection changes.</summary>
public interface IRefreshable
{
    void Refresh();
}

/// <summary>
/// The Context Bar: the row under the Main Toolbar that changes with the active tool and the selection. One bar class per tool.
/// A bar is two <see cref="BarRow"/>s (see <see cref="ContextBarBase"/>): the fields on the first, the buttons and options on the second,
/// flowing left to right in groups divided by thin separators. A row only wraps when the window is too narrow for it, and a bar's
/// height follows its content.
/// </summary>
public static class ContextBars
{
    public static Control Create(Tool tool, EditorSession session) => tool switch
    {
        SelectTool => new SelectionContextBar(session),
        RectangleTool r => new RectangleContextBar(session, r),
        RoundedRectangleTool rr => new RoundedRectangleContextBar(session, rr),
        PolygonTool pg => new PolygonContextBar(session, pg),
        ArrowTool ar => new ArrowContextBar(session, ar),
        RoundCornersTool rc => new RoundCornersContextBar(session, rc),
        TextTool => new TextToolContextBar(session),
        CropTool c => new CropContextBar(session, c),
        VectorEditTool v => new VectorEditContextBar(session, v),
        LoopEditTool l => new LoopEditContextBar(session, l),
        OutlineTool o => new OutlineContextBar(session, o),
        CookieCutterTool c2 => new CookieCutterContextBar(session, c2),
        KnifeTool k => new KnifeContextBar(session, k),
        GradientTool gt => new GradientContextBar(session, gt),
        ShadowTool sh => new ShadowContextBar(session, sh),
        BevelTool bv => new BevelContextBar(session, bv),
        SoftEffectTool se => new SoftEffectContextBar(session, se),
        FeatherTool ft => new FeatherContextBar(session, ft),
        TextPathTool tp => new TextPathContextBar(session, tp),
        _ => new ToolNameContextBar(tool),
    };

    /// <summary>Hands the keyboard back to the canvas after a bar control was used (the shell sets this), so typing continues on the canvas.</summary>
    public static Action? FocusCanvas { get; set; }

    /// <summary>Spacing between the items of a bar row; groups use <see cref="GroupSpacing"/> inside.</summary>
    public const double RowSpacing = 8, GroupSpacing = 4, LineSpacing = 4;

    /// <summary>Height of one Context Bar row at 100% interface text (it grows with the text size, never with the content).</summary>
    public const double RowHeightBase = 30;

    /// <summary>Height of the whole Context Bar now: two rows, the gap between them and the bar's own padding (6 px above and below at 100%; gaps grow at a third of the text rate). Constant for a given text size.</summary>
    public static double BarHeight => 2 * RowHeightBase * UiScale.Scale + UiScale.Sp(LineSpacing + 12);

    /// <summary>A row for a Context Bar (or one section of it): a fixed height, so a bar's height never depends on what is in it.</summary>
    internal static BarRow NewRow(params Control[] items)
    {
        var row = new BarRow().Spaced(BarRow.SpacingProperty, RowSpacing).Spaced(BarRow.LineSpacingProperty, LineSpacing).ScaledHeight(RowHeightBase);
        foreach (var item in items) row.Children.Add(item);
        return row;
    }

    internal static TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text };
        t.Classes.Add("label");
        return t;
    }

    /// <summary>A line of gray explanatory text (the tool's hint).</summary>
    internal static TextBlock Hint(string text) => new() { Text = text, Foreground = AppTheme.Brush("Ui.Muted"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };

    /// <summary>A muted caption (small, gray) that names a group: the same look in every bar and panel.</summary>
    internal static TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text };
        t.Classes.Add("caption");
        return t;
    }

    /// <summary>A small gray unit caption ("in", "pt", "%") that goes after a field.</summary>
    internal static TextBlock Unit(string text)
    {
        var t = new TextBlock { Text = text };
        t.Classes.Add("unit");
        return t;
    }

    /// <summary>Controls that always stay on one line together (a caption and its field).</summary>
    internal static StackPanel Group(params Control[] items)
    {
        var g = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }.Spaced(StackPanel.SpacingProperty, GroupSpacing);
        foreach (var item in items) g.Children.Add(item);
        return g;
    }

    /// <summary>"X [ 1.25 ] in": a caption, a compact numeric field and its unit, kept together.</summary>
    internal static StackPanel Field(string caption, TextBox box, string? unit = null) =>
        unit is null ? Group(Label(caption), box) : Group(Label(caption), box, Unit(unit));

    /// <summary>
    /// The Context Bar shows only valid choices (R26): a control that would do nothing for the current selection, tool or state is hidden
    /// completely, never just greyed. Call this wherever a control follows a command's can-execute state. It keeps <c>IsEnabled</c> in step
    /// as well, so the control is exactly as usable as it was; a hidden one takes no room (rows close the gap and never keep a stray separator).
    /// </summary>
    internal static void Valid(Control control, bool valid)
    {
        control.IsEnabled = valid;
        control.IsVisible = valid;
    }

    /// <summary>A thin vertical divider between groups of a bar; the row hides it where a line would start or end with it.</summary>
    internal static BarSeparator Separator() => new();

    /// <summary>A text button of a bar (Combine, Done, ...): never takes the keyboard focus, so typing continues on the canvas.</summary>
    internal static Button Action(string text, Action run, string? tip = null)
    {
        var b = new Button { Content = text, Focusable = false };
        b.Classes.Add("act");
        if (tip is not null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => run();
        return b;
    }

    internal static TextBox NumberBox(Action onCommit)
    {
        var box = new TextBox();
        box.Classes.Add("num");
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            onCommit();
            e.Handled = true;
        };
        box.LostFocus += (_, _) => onCommit();
        return box;
    }

    internal static string Fmt(double inches) => inches.ToString("0.####", CultureInfo.InvariantCulture);

    internal static bool TryParse(TextBox box, out double value) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
}

/// <summary>The two rows every Context Bar is made of; a bar adds its controls to <see cref="Row1"/> and <see cref="Row2"/>.</summary>
public abstract class ContextBarBase : StackPanel
{
    protected BarRow Row1 { get; } = ContextBars.NewRow();
    protected BarRow Row2 { get; } = ContextBars.NewRow();

    protected ContextBarBase()
    {
        this.Spaced(SpacingProperty, ContextBars.LineSpacing);
        Children.Add(Row1);
        Children.Add(Row2);
    }
}

/// <summary>The bar of a tool that has no options: its name, and its hint below.</summary>
public sealed class ToolNameContextBar : ContextBarBase
{
    public ToolNameContextBar(Tool tool)
    {
        Row1.Children.Add(ContextBars.Label(tool.DisplayName));
        Row2.Children.Add(ContextBars.Hint(tool.Hint));
    }
}

/// <summary>Select tool: position and size of the selection in inches (x from page left, y up from page bottom), then the group buttons.</summary>
public sealed class SelectionContextBar : ContextBarBase, IRefreshable
{
    private readonly EditorSession _session;
    private readonly TextBox _x, _y, _w, _h;
    private readonly Control[] _fields;   // the X, Y, W and H fields with their captions: shown only while something is selected
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Ui.Muted") };
    private bool _refreshing, _edited;
    private readonly Button _combine, _breakApart, _group, _ungroup;
    private readonly (AlignMode Mode, Button Button)[] _align;   // Arrange > Align: Left, Center, Right, Top, Middle, Bottom
    private readonly Button _centerLayout, _mirrorH, _mirrorV, _lock;
    private readonly TextFormatPanel _text;
    private readonly ImageContextRow _image;   // pixel size, dpi and the Image menu buttons while a bitmap is selected
    private readonly ShapeSelectionFields _shapeFields;

    internal TextFormatPanel TextPanel => _text;

    public SelectionContextBar(EditorSession session)
    {
        _session = session;
        _text = new TextFormatPanel(session, twoRows: false) { IsVisible = false };
        _image = new ImageContextRow(session);
        _shapeFields = new ShapeSelectionFields(session);
        _x = ContextBars.NumberBox(Apply);
        _y = ContextBars.NumberBox(Apply);
        _w = ContextBars.NumberBox(Apply);
        _h = ContextBars.NumberBox(Apply);
        foreach (var box in new[] { _x, _y, _w, _h })
            box.TextChanged += (_, _) => { if (!_refreshing) _edited = true; };

        // Group buttons (Combine, Break Apart, Group, Ungroup).
        _combine = ContextBars.Action("Combine", () => _session.CombineSelection());
        _breakApart = ContextBars.Action("Break Apart (K)", () => _session.BreakApartSelection());
        _group = ContextBars.Action("Group", () => _session.GroupSelection());
        _ungroup = ContextBars.Action("Ungroup (U)", () => _session.UngroupSelection());

        // Row 1: where the selection is and how big; row 2: what can be done with it (the picture and text controls join it when they apply).
        _fields = [ContextBars.Field("X", _x, "in"), ContextBars.Field("Y", _y, "in"), ContextBars.Field("W", _w, "in"), ContextBars.Field("H", _h, "in")];
        foreach (var field in _fields) Row1.Children.Add(field);
        Row1.Children.Add(ContextBars.Separator());
        Row1.Children.Add(_count);
        Row2.Children.Add(_combine);
        Row2.Children.Add(_breakApart);
        Row2.Children.Add(_group);
        Row2.Children.Add(_ungroup);
        Row2.Children.Add(ContextBars.Separator());

        // Arrange: Align (and center on the layout), Mirror and Lock, as small icon buttons at the end of row 2 (after the picture and text
        // controls, which keep their places). Each shows only when its command would work (R26).
        _align =
        [
            (AlignMode.Left, ArrangeButton("arrange.alignLeft", "Align Left (L)", () => _session.Align(AlignMode.Left))),
            (AlignMode.Center, ArrangeButton("arrange.alignCenter", "Align Center (N)", () => _session.Align(AlignMode.Center))),
            (AlignMode.Right, ArrangeButton("arrange.alignRight", "Align Right (R)", () => _session.Align(AlignMode.Right))),
            (AlignMode.Top, ArrangeButton("arrange.alignTop", "Align Top Edge", () => _session.Align(AlignMode.Top))),
            (AlignMode.Middle, ArrangeButton("arrange.alignMiddle", "Align Vertical Center", () => _session.Align(AlignMode.Middle))),
            (AlignMode.Bottom, ArrangeButton("arrange.alignBottom", "Align Bottom Edge", () => _session.Align(AlignMode.Bottom))),
        ];
        _centerLayout = ArrangeButton("arrange.centerBoth", "Center of Layout: center the selection on the page", () => _session.CenterOnPage(PageCentering.Both));
        _mirrorH = ArrangeButton("arrange.mirrorH", "Horizontal Mirror", () => _session.Mirror(horizontal: true));
        _mirrorV = ArrangeButton("arrange.mirrorV", "Vertical Mirror", () => _session.Mirror(horizontal: false));
        _lock = ArrangeButton("arrange.lock", "Locked", () => _session.ToggleLockSelection());
        Row2.Children.Add(_image);   // shown while a bitmap is selected
        Row2.Children.Add(ContextBars.Separator());
        Row2.Children.Add(_text);    // font controls: shown while the selection holds live text (R6)
        Row2.Children.Add(_shapeFields);   // polygon / star and arrow settings: shown while only those are selected
        foreach (var (_, button) in _align) Row2.Children.Add(button);
        Row2.Children.Add(_centerLayout);
        Row2.Children.Add(ContextBars.Separator());
        Row2.Children.Add(_mirrorH);
        Row2.Children.Add(_mirrorV);
        Row2.Children.Add(ContextBars.Separator());
        Row2.Children.Add(_lock);
        Refresh();
    }

    /// <summary>A small icon button of the Arrange group (Align, Mirror, Locked): the drawn icon, with the command's name as its tooltip.</summary>
    private static Button ArrangeButton(string commandId, string tip, Action run)
    {
        var b = new Button { Content = ArrangeIcons.For(commandId, 16), Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        b.Classes.Add("tb");
        b.MinWidth = b.MinHeight = 0;   // compact: ten of them share the row with the group buttons
        b.Padding = new Avalonia.Thickness(2);
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => run();
        return b;
    }

    /// <summary>True when the selection is one or more text objects selected directly, and nothing else.</summary>
    internal static bool IsOnlyText(EditorSession session) =>
        session.Selection.Items.Count > 0 && session.Selection.Items.All(o => o is TextObject);

    public void Refresh()
    {
        _refreshing = true;
        // Font controls only for a piece of text selected itself (R26): not a group that holds text, not text mixed with shapes.
        _text.IsVisible = IsOnlyText(_session);
        if (_text.IsVisible) _text.Refresh();
        _image.Refresh();
        _shapeFields.Refresh();
        var b = _session.Selection.GetBounds();
        bool locked = _session.SelectionLocked;
        bool has = !b.IsEmpty && !locked;   // a locked selection can't be moved or resized: no fields
        foreach (var t in new[] { _x, _y, _w, _h }) t.IsEnabled = has;
        foreach (var field in _fields) field.IsVisible = has;
        if (has)
        {
            var (ux, uy) = Units.ToUser(new Vec2(b.Left, b.Bottom), _session.Document.Page);
            if (!_x.IsFocused) _x.Text = ContextBars.Fmt(ux);
            if (!_y.IsFocused) _y.Text = ContextBars.Fmt(uy);
            if (!_w.IsFocused) _w.Text = ContextBars.Fmt(Units.ToInches(b.Width));
            if (!_h.IsFocused) _h.Text = ContextBars.Fmt(Units.ToInches(b.Height));
        }
        else
        {
            _x.Text = _y.Text = _w.Text = _h.Text = "";
        }
        _edited = false;
        // Each button shows exactly when its command would work (R26); otherwise it is not there at all.
        ContextBars.Valid(_combine, _session.CanCombine);
        ContextBars.Valid(_breakApart, _session.CanBreakApart);
        ContextBars.Valid(_group, _session.CanGroup);
        ContextBars.Valid(_ungroup, _session.CanUngroup);
        foreach (var (mode, button) in _align) ContextBars.Valid(button, _session.CanAlign(mode));
        ContextBars.Valid(_centerLayout, _session.CanCenterOnPage);
        ContextBars.Valid(_mirrorH, _session.CanMirror);
        ContextBars.Valid(_mirrorV, _session.CanMirror);
        ContextBars.Valid(_lock, _session.CanLock);
        _lock.Classes.Set("on", _session.SelectionAllLocked);   // pressed while the selection is locked: a click unlocks
        ToolTip.SetTip(_lock, _session.SelectionAllLocked ? "Locked (click to unlock)" : "Lock: a locked object can't be selected on the canvas or moved");
        _count.Text = _session.Selection.Count switch
        {
            0 => "Nothing selected",
            1 => _session.Selection.Items[0].KindName + (locked ? " selected (locked)" : " selected"),
            int n => $"{n} objects selected" + (locked ? " (locked)" : ""),
        };
        _refreshing = false;
    }

    private void Apply()
    {
        // Fields show rounded values, so only act when the user actually typed something.
        if (_refreshing || !_edited || _session.Selection.IsEmpty) return;
        if (!ContextBars.TryParse(_x, out var x) || !ContextBars.TryParse(_y, out var y) ||
            !ContextBars.TryParse(_w, out var w) || !ContextBars.TryParse(_h, out var h) || w <= 0 || h <= 0)
        {
            Refresh();
            return;
        }
        var bottomLeft = Units.FromUser(x, y, _session.Document.Page);
        double wp = Units.FromInches(w), hp = Units.FromInches(h);
        var target = Rect2.FromXYWH(bottomLeft.X, bottomLeft.Y - hp, wp, hp);
        _edited = false;
        _session.SetSelectionBounds(target);
    }

    // ---- For tests ----

    internal Button Combine => _combine;
    internal Button BreakApart => _breakApart;
    internal Button Group => _group;
    internal Button Ungroup => _ungroup;
    internal Button AlignButton(AlignMode mode) => _align.First(a => a.Mode == mode).Button;
    internal Button CenterLayout => _centerLayout;
    internal Button MirrorH => _mirrorH;
    internal Button MirrorV => _mirrorV;
    internal Button Lock => _lock;
    internal TextBox X => _x;
    internal TextBox Y => _y;
    internal TextBox W => _w;
    internal TextBox H => _h;
}

/// <summary>Rectangle tool: corner radius for new rectangles (also applied to selected rectangles).</summary>
public sealed class RectangleContextBar : ContextBarBase, IRefreshable
{
    private readonly EditorSession _session;
    private readonly RectangleTool _tool;
    private readonly TextBox _radius;

    public RectangleContextBar(EditorSession session, RectangleTool tool)
    {
        _session = session;
        _tool = tool;
        _radius = ContextBars.NumberBox(Apply);
        Row1.Children.Add(ContextBars.Field("Corner radius", _radius, "in"));
        Row2.Children.Add(ContextBars.Hint(tool.Hint));
        Refresh();
    }

    public void Refresh()
    {
        if (!_radius.IsFocused) _radius.Text = ContextBars.Fmt(Units.ToInches(_tool.CornerRadius));
    }

    private void Apply()
    {
        if (!ContextBars.TryParse(_radius, out var r) || r < 0) { Refresh(); return; }
        double pts = Units.FromInches(r);
        if (Math.Abs(pts - _tool.CornerRadius) < 1e-9) return;
        _tool.CornerRadius = pts;
        var rects = _session.Selection.Items.OfType<RectangleObject>().ToArray();
        if (rects.Length > 0)
            _session.History.Execute(new SetPropertyCommand<RectangleObject, double>(
                "Corner Radius", rects, o => o.CornerRadius, (o, v) => o.CornerRadius = v, pts));
    }
}
