using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
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
/// flowing left to right in groups divided by thin separators. The two share one line whenever they fit side by side, and only a bar
/// that is too long for the window takes a second line (<see cref="TwoLinePanel"/>), so small screens keep more room for the canvas.
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
        VectorDrawTool vd => new VectorDrawContextBar(session, vd),
        _ => new ToolNameContextBar(tool),
    };

    /// <summary>Hands the keyboard back to the canvas after a bar control was used (the shell sets this), so typing continues on the canvas.</summary>
    public static Action? FocusCanvas { get; set; }

    /// <summary>Spacing between the items of a bar row; groups use <see cref="GroupSpacing"/> inside.</summary>
    public const double RowSpacing = 6, GroupSpacing = 4, LineSpacing = 4;

    /// <summary>Height of one Context Bar row at 100% interface text (it grows with the text size, never with the content).</summary>
    public const double RowHeightBase = 30;

    /// <summary>The Context Bar's own padding above and below its rows at 100% (gaps grow at a third of the text rate).</summary>
    public const double BarPaddingY = 2;

    /// <summary>Height of the Context Bar when its rows share one line: one row, the bar's padding and its one-pixel bottom line.</summary>
    public static double OneLineHeight => RowHeightBase * UiScale.Scale + UiScale.Sp(2 * BarPaddingY) + 1;

    /// <summary>Height of the Context Bar when its rows need two lines: two rows, the gap between them, the bar's padding and its bottom line.</summary>
    public static double TwoLineHeight => 2 * RowHeightBase * UiScale.Scale + UiScale.Sp(LineSpacing + 2 * BarPaddingY) + 1;

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

/// <summary>
/// The <b>Done</b> and <b>Cancel</b> buttons that end a mode (<see cref="IModeTool"/>): the same two text buttons, in that order, at the right end of the
/// tool's own controls on every bar that has them. Done keeps everything and goes to the Layout tool; Cancel takes everything back to how the design was
/// when the mode began and goes to the Layout tool (<see cref="EditorSession.DoneMode"/>, <see cref="EditorSession.CancelMode"/>). They show only while
/// the mode is on (the bar calls <see cref="Refresh"/>), and Done only when there is something to keep. No keys are bound to them here.
/// </summary>
internal sealed class DoneCancelButtons
{
    private readonly EditorSession _session;

    public DoneCancelButtons(EditorSession session, string? doneTip = null, string? cancelTip = null)
    {
        _session = session;
        Done = ContextBars.Action("Done", () => { _ = session.DoneMode(); ContextBars.FocusCanvas?.Invoke(); }, doneTip ?? "Keep what you did and go back to the Layout tool");
        Cancel = ContextBars.Action("Cancel", () => { session.CancelMode(); ContextBars.FocusCanvas?.Invoke(); }, cancelTip ?? "Take back everything you did here and go back to the Layout tool");
    }

    public Button Done { get; }
    public Button Cancel { get; }

    /// <summary>Adds a divider and the two buttons to the end of <paramref name="row"/>.</summary>
    public void AddTo(BarRow row)
    {
        row.Children.Add(ContextBars.Separator());
        row.Children.Add(Done);
        row.Children.Add(Cancel);
    }

    /// <summary>Shows Cancel while the mode is on and Done while it also has something to keep (and <paramref name="doneValid"/>, for a bar with a further condition).</summary>
    public void Refresh(bool doneValid = true)
    {
        ContextBars.Valid(Cancel, _session.InMode);
        ContextBars.Valid(Done, doneValid && _session.CanFinishMode);
    }
}

/// <summary>
/// Two bar rows that share one line, divided by a separator, whenever they fit side by side in the width the bar has, and stack on two
/// lines when they don't. The rows themselves never wrap: a row that is longer than the bar even on its own line scrolls sideways with
/// the bar. Inside the Context Bar's sideways scrolling the width offered is unlimited, so the panel compares with the width of the
/// scroll viewer it sits in; the window asks it to look again when that width changes.
/// </summary>
public class TwoLinePanel : Panel
{
    private readonly BarSeparator _join = ContextBars.Separator();
    private BarRow? _first, _second;

    public TwoLinePanel() => Children.Add(_join);

    /// <summary>Sets the rows once, from the constructor of the bar; <paramref name="second"/> may be null for a single row.</summary>
    protected void SetRows(BarRow first, BarRow? second)
    {
        _first = first;
        _second = second;
        Children.Add(first);
        if (second is not null) Children.Add(second);
    }

    /// <summary>True while the rows are on two lines (the bar did not fit on one).</summary>
    public bool Stacked { get; private set; }

    /// <summary>The width the rows have to fit in: the width offered, or the scroll viewer's when the offer is unlimited.</summary>
    private double FitWidth(Size available)
    {
        if (!double.IsInfinity(available.Width)) return available.Width;
        if (this.FindAncestorOfType<ScrollViewer>() is { } scroll)
        {
            double w = scroll.Viewport.Width > 0 ? scroll.Viewport.Width : scroll.Bounds.Width;
            if (w > 0) return w;
        }
        return double.PositiveInfinity;
    }

    private double Gap => ContextBars.RowSpacing * UiScale.SpaceScale;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_first is null) return default;
        _first.Measure(Size.Infinity);
        _second?.Measure(Size.Infinity);
        _join.Measure(Size.Infinity);
        double w1 = _first.DesiredSize.Width, w2 = _second?.DesiredSize.Width ?? 0;
        double h1 = _first.DesiredSize.Height, h2 = _second?.DesiredSize.Height ?? 0;
        bool both = w1 > 0.5 && w2 > 0.5;
        double oneLine = both ? w1 + Gap + _join.DesiredSize.Width + Gap + w2 : Math.Max(w1, w2);
        Stacked = both && oneLine > FitWidth(availableSize) + 0.5;
        return Stacked
            ? new Size(Math.Max(w1, w2), h1 + UiScale.Sp(ContextBars.LineSpacing) + h2)
            : new Size(oneLine, Math.Max(h1, h2));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_first is null) return finalSize;
        double w1 = _first.DesiredSize.Width, w2 = _second?.DesiredSize.Width ?? 0;
        double h1 = _first.DesiredSize.Height, h2 = _second?.DesiredSize.Height ?? 0;
        if (Stacked)
        {
            _first.Arrange(new Rect(0, 0, w1 + 1, h1));
            _second?.Arrange(new Rect(0, h1 + UiScale.Sp(ContextBars.LineSpacing), w2 + 1, h2));
            _join.Arrange(default);
            return finalSize;
        }
        double h = Math.Max(h1, h2), x = 0;
        _first.Arrange(new Rect(0, 0, w1 + 1, h));
        if (w1 > 0.5 && w2 > 0.5)
        {
            x = w1 + Gap;
            _join.Arrange(new Rect(x, 0, _join.DesiredSize.Width, h));
            x += _join.DesiredSize.Width + Gap;
        }
        else _join.Arrange(default);
        _second?.Arrange(new Rect(x, 0, w2 + 1, h));
        return finalSize;
    }
}

/// <summary>The two rows every Context Bar is made of; a bar adds its controls to <see cref="Row1"/> and <see cref="Row2"/>.</summary>
public abstract class ContextBarBase : TwoLinePanel
{
    protected BarRow Row1 { get; } = ContextBars.NewRow();
    protected BarRow Row2 { get; } = ContextBars.NewRow();

    protected ContextBarBase() => SetRows(Row1, Row2);
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

/// <summary>
/// Select tool (the Layout tool): only actions (R38). Row 1 is what the selection is and can be turned into (Combine, Break Apart, Group, Ungroup,
/// the picture buttons, the font controls of a text, the settings of a polygon or arrow); row 2 is Arrange (the Align drop-down, Mirror H and V,
/// Lock). The two share one line when they fit. The position and size fields and "N objects selected" moved to the Info Line
/// (<see cref="MeasureFields"/>, the counts), which also says what is selected.
/// </summary>
public sealed class SelectionContextBar : ContextBarBase, IRefreshable
{
    private readonly EditorSession _session;
    private readonly Button _combine, _breakApart, _group, _ungroup;
    private readonly Button _align;   // the Align drop-down button: icon, the word Align, a chevron
    private readonly ContextMenu _alignMenu;
    private readonly (AlignMode Mode, MenuItem Item)[] _alignItems;   // Arrange > Align: Left, Center, Right, Top, Middle, Bottom
    private readonly MenuItem _centerItem;   // Center of Layout
    private readonly Separator _alignDivider = new();
    private readonly Button _mirrorH, _mirrorV, _lock;
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

        // Group buttons (Combine, Break Apart, Group, Ungroup).
        _combine = ContextBars.Action("Combine", () => _session.CombineSelection());
        _breakApart = ContextBars.Action("Break Apart (K)", () => _session.BreakApartSelection());
        _group = ContextBars.Action("Group", () => _session.GroupSelection());
        _ungroup = ContextBars.Action("Ungroup (U)", () => _session.UngroupSelection());

        // Align: one drop-down for the six aligns and Center of Layout. Each item shows only when its command would work (R26), the button
        // only when at least one would. The keys shown are the ones the Arrange menu has.
        _alignItems =
        [
            (AlignMode.Left, AlignItem("arrange.alignLeft", "Align Left", Key.L, () => _session.Align(AlignMode.Left))),
            (AlignMode.Center, AlignItem("arrange.alignCenter", "Align Center", Key.N, () => _session.Align(AlignMode.Center))),
            (AlignMode.Right, AlignItem("arrange.alignRight", "Align Right", Key.R, () => _session.Align(AlignMode.Right))),
            (AlignMode.Top, AlignItem("arrange.alignTop", "Align Top Edge", null, () => _session.Align(AlignMode.Top))),
            (AlignMode.Middle, AlignItem("arrange.alignMiddle", "Align Vertical Center", null, () => _session.Align(AlignMode.Middle))),
            (AlignMode.Bottom, AlignItem("arrange.alignBottom", "Align Bottom Edge", null, () => _session.Align(AlignMode.Bottom))),
        ];
        _centerItem = AlignItem("arrange.centerBoth", "Center of Layout", null, () => _session.CenterOnPage(PageCentering.Both));
        _alignMenu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var (_, item) in _alignItems) _alignMenu.Items.Add(item);
        _alignMenu.Items.Add(_alignDivider);
        _alignMenu.Items.Add(_centerItem);
        _align = AlignDropDown(_alignMenu);

        _mirrorH = ArrangeButton("arrange.mirrorH", "Horizontal Mirror", () => _session.Mirror(horizontal: true));
        _mirrorV = ArrangeButton("arrange.mirrorV", "Vertical Mirror", () => _session.Mirror(horizontal: false));
        _lock = ArrangeButton("arrange.lock", "Locked", () => _session.ToggleLockSelection());

        Row1.Children.Add(_combine);
        Row1.Children.Add(_breakApart);
        Row1.Children.Add(_group);
        Row1.Children.Add(_ungroup);
        Row1.Children.Add(ContextBars.Separator());
        Row1.Children.Add(_image);   // shown while a bitmap is selected
        Row1.Children.Add(ContextBars.Separator());
        Row1.Children.Add(_text);    // font controls: shown while the selection holds live text (R6)
        Row1.Children.Add(_shapeFields);   // polygon / star and arrow settings: shown while only those are selected
        Row2.Children.Add(_align);
        Row2.Children.Add(_mirrorH);
        Row2.Children.Add(_mirrorV);
        Row2.Children.Add(ContextBars.Separator());
        Row2.Children.Add(_lock);
        Refresh();
    }

    /// <summary>A small icon button of the Arrange group (Mirror, Locked): the drawn icon, with the command's name as its tooltip.</summary>
    private static Button ArrangeButton(string commandId, string tip, Action run)
    {
        var b = new Button { Content = ArrangeIcons.For(commandId, 16), Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        b.Classes.Add("tb");
        b.MinWidth = b.MinHeight = 0;   // compact: they share the row with the Align button
        b.Padding = new Avalonia.Thickness(2);
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => run();
        return b;
    }

    /// <summary>One line of the Align drop-down: the command's drawn icon and name, and the key it has (shown only).</summary>
    private MenuItem AlignItem(string commandId, string name, Key? key, Action run)
    {
        var item = new MenuItem { Header = name, Icon = ArrangeIcons.For(commandId, 16), Name = "AlignItem_" + commandId.Replace("arrange.", "") };
        if (key is { } k) item.InputGesture = new KeyGesture(k);
        item.Click += (_, _) => { run(); ContextBars.FocusCanvas?.Invoke(); };
        return item;
    }

    /// <summary>The "Align" button: the align icon, the word, a small chevron; it opens <paramref name="menu"/> under itself.</summary>
    private static Button AlignDropDown(ContextMenu menu)
    {
        var chevron = new Avalonia.Controls.Shapes.Path { Data = Avalonia.Media.Geometry.Parse("M0,0 L3.5,3.5 L7,0"), StrokeThickness = 1.3, VerticalAlignment = VerticalAlignment.Center };
        chevron.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, chevron.GetResourceObservable("Ui.Icon"));
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }.Spaced(StackPanel.SpacingProperty, 5);
        content.Children.Add(ArrangeIcons.For("arrange.alignLeft", 16)!);
        content.Children.Add(new TextBlock { Text = "Align", VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(chevron);
        var b = new Button { Name = "AlignDropDown", Content = content, Focusable = false };
        b.Classes.Add("act");
        ToolTip.SetTip(b, "Align: line the selection up with its own edges or center, or with the layout");
        Avalonia.Automation.AutomationProperties.SetName(b, "Align");
        b.Click += (_, _) => menu.Open(b);
        return b;
    }

    /// <summary>True when the selection is one or more text objects selected directly, and nothing else.</summary>
    internal static bool IsOnlyText(EditorSession session) =>
        session.Selection.Items.Count > 0 && session.Selection.Items.All(o => o is TextObject);

    public void Refresh()
    {
        // Font controls only for a piece of text selected itself (R26): not a group that holds text, not text mixed with shapes.
        _text.IsVisible = IsOnlyText(_session);
        if (_text.IsVisible) _text.Refresh();
        _image.Refresh();
        _shapeFields.Refresh();
        // Each button shows exactly when its command would work (R26); otherwise it is not there at all.
        ContextBars.Valid(_combine, _session.CanCombine);
        ContextBars.Valid(_breakApart, _session.CanBreakApart);
        ContextBars.Valid(_group, _session.CanGroup);
        ContextBars.Valid(_ungroup, _session.CanUngroup);
        bool anyAlign = false;
        foreach (var (mode, item) in _alignItems)
        {
            bool can = _session.CanAlign(mode);
            ContextBars.Valid(item, can);
            anyAlign |= can;
        }
        bool canCenter = _session.CanCenterOnPage;
        ContextBars.Valid(_centerItem, canCenter);
        _alignDivider.IsVisible = anyAlign && canCenter;
        ContextBars.Valid(_align, anyAlign || canCenter);
        ContextBars.Valid(_mirrorH, _session.CanMirror);
        ContextBars.Valid(_mirrorV, _session.CanMirror);
        ContextBars.Valid(_lock, _session.CanLock);
        _lock.Classes.Set("on", _session.SelectionAllLocked);   // pressed while the selection is locked: a click unlocks
        ToolTip.SetTip(_lock, _session.SelectionAllLocked ? "Locked (click to unlock)" : "Lock: a locked object can't be selected on the canvas or moved");
    }

    // ---- For tests ----

    internal Button Combine => _combine;
    internal Button BreakApart => _breakApart;
    internal Button Group => _group;
    internal Button Ungroup => _ungroup;
    internal Button AlignDropDownButton => _align;
    internal ContextMenu AlignMenu => _alignMenu;
    internal MenuItem AlignItem(AlignMode mode) => _alignItems.First(a => a.Mode == mode).Item;
    internal MenuItem CenterLayoutItem => _centerItem;
    internal Button MirrorH => _mirrorH;
    internal Button MirrorV => _mirrorV;
    internal Button Lock => _lock;
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
