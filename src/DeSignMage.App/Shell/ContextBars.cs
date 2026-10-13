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
        TextTool => new TextToolContextBar(session),
        CropTool c => new CropContextBar(session, c),
        VectorEditTool v => new VectorEditContextBar(session, v),
        LoopEditTool l => new LoopEditContextBar(session, l),
        OutlineTool o => new OutlineContextBar(session, o),
        CookieCutterTool c2 => new CookieCutterContextBar(session, c2),
        KnifeTool k => new KnifeContextBar(session, k),
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
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Ui.Muted") };
    private bool _refreshing, _edited;
    private readonly Button _combine, _breakApart, _group, _ungroup;
    private readonly TextFormatPanel _text;
    private readonly ImageContextRow _image;   // pixel size, dpi and the Image menu buttons while a bitmap is selected

    internal TextFormatPanel TextPanel => _text;

    public SelectionContextBar(EditorSession session)
    {
        _session = session;
        _text = new TextFormatPanel(session, twoRows: false) { IsVisible = false };
        _image = new ImageContextRow(session);
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
        Row1.Children.Add(ContextBars.Field("X", _x, "in"));
        Row1.Children.Add(ContextBars.Field("Y", _y, "in"));
        Row1.Children.Add(ContextBars.Field("W", _w, "in"));
        Row1.Children.Add(ContextBars.Field("H", _h, "in"));
        Row1.Children.Add(ContextBars.Separator());
        Row1.Children.Add(_count);
        Row2.Children.Add(_combine);
        Row2.Children.Add(_breakApart);
        Row2.Children.Add(_group);
        Row2.Children.Add(_ungroup);
        Row2.Children.Add(ContextBars.Separator());
        Row2.Children.Add(_image);   // shown while a bitmap is selected
        Row2.Children.Add(ContextBars.Separator());
        Row2.Children.Add(_text);    // font controls: shown while the selection holds live text (R6)
        Refresh();
    }

    public void Refresh()
    {
        _refreshing = true;
        _text.IsVisible = _session.SelectedTexts().Count > 0;
        if (_text.IsVisible) _text.Refresh();
        _image.Refresh();
        var b = _session.Selection.GetBounds();
        bool has = !b.IsEmpty;
        foreach (var t in new[] { _x, _y, _w, _h }) t.IsEnabled = has;
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
        _combine.IsEnabled = _session.CanCombine;
        _breakApart.IsEnabled = _session.CanBreakApart;
        _group.IsEnabled = _session.CanGroup;
        _ungroup.IsEnabled = _session.CanUngroup;
        _count.Text = _session.Selection.Count switch
        {
            0 => "Nothing selected",
            1 => _session.Selection.Items[0].KindName + " selected",
            int n => $"{n} objects selected",
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
