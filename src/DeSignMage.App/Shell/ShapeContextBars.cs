using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Shell;

/// <summary>
/// A numeric Context Bar field with a caption and a unit. It applies what was typed on Enter or when it loses the focus, and only when the
/// text really differs from what the bar last showed (Avalonia raises <c>TextChanged</c> late, so comparing texts is what is reliable).
/// </summary>
internal sealed class ShapeNumberField
{
    private readonly TextBox _box;
    private readonly Func<double> _read;
    private readonly Action<double> _write;
    private readonly string _format;
    private string _shown = "";

    public ShapeNumberField(string caption, string? unit, string format, Func<double> read, Action<double> write)
    {
        _read = read;
        _write = write;
        _format = format;
        _box = ContextBars.NumberBox(Commit);
        Panel = ContextBars.Field(caption, _box, unit);
    }

    public StackPanel Panel { get; }

    public void Refresh()
    {
        if (!_box.IsFocused) _box.Text = _shown = _read().ToString(_format, CultureInfo.InvariantCulture);
    }

    private void Commit()
    {
        if ((_box.Text ?? "") == _shown) return;
        if (!ContextBars.TryParse(_box, out double v)) { Refresh(); return; }
        _write(v);
        _shown = _box.Text ?? "";
        Refresh();
    }

    internal TextBox Box => _box;
}

/// <summary>The settings of a polygon or star: number of sides, star yes/no and the star's inner radius. Bound to a tool, or to the selected polygons.</summary>
internal sealed class PolygonFields : StackPanel
{
    private readonly ShapeNumberField _sides, _inner;
    private readonly CheckBox _star;
    private readonly Func<bool> _readStar;
    private bool _refreshing;

    public PolygonFields(Func<int> sides, Action<int> setSides, Func<bool> star, Action<bool> setStar, Func<double> inner, Action<double> setInner)
    {
        Orientation = Orientation.Horizontal;
        this.Spaced(SpacingProperty, ContextBars.RowSpacing);
        _readStar = star;
        _sides = new ShapeNumberField("Sides", null, "0", () => sides(), v => setSides((int)Math.Round(v)));
        _star = PathBarParts.Check("Star", "Make a star: the corners alternate between the outer and the inner radius", v => { if (!_refreshing) setStar(v); });
        _inner = new ShapeNumberField("Inner radius", "%", "0.#", () => inner() * 100, v => setInner(v / 100));
        Children.Add(_sides.Panel);
        Children.Add(_star);
        Children.Add(_inner.Panel);
        Refresh();
    }

    internal ShapeNumberField Sides => _sides;
    internal ShapeNumberField Inner => _inner;
    internal CheckBox StarBox => _star;

    public void Refresh()
    {
        _refreshing = true;
        _sides.Refresh();
        _inner.Refresh();
        _star.IsChecked = _readStar();
        ContextBars.Valid(_inner.Panel, _star.IsChecked == true);   // R26: the inner radius only means something for a star
        _refreshing = false;
    }
}

/// <summary>The proportions of an arrow: head length (of the arrow's length), head width (of its length) and shaft width (of the head's width).</summary>
internal sealed class ArrowFields : StackPanel
{
    private readonly ShapeNumberField _head, _width, _shaft;

    public ArrowFields(Func<double> head, Action<double> setHead, Func<double> width, Action<double> setWidth, Func<double> shaft, Action<double> setShaft)
    {
        Orientation = Orientation.Horizontal;
        this.Spaced(SpacingProperty, ContextBars.RowSpacing);
        _head = new ShapeNumberField("Head length", "%", "0.#", () => head() * 100, v => setHead(v / 100));
        _width = new ShapeNumberField("Head width", "%", "0.#", () => width() * 100, v => setWidth(v / 100));
        _shaft = new ShapeNumberField("Shaft width", "%", "0.#", () => shaft() * 100, v => setShaft(v / 100));
        Children.Add(_head.Panel);
        Children.Add(_width.Panel);
        Children.Add(_shaft.Panel);
        Refresh();
    }

    internal ShapeNumberField Head => _head;
    internal ShapeNumberField HeadWidth => _width;
    internal ShapeNumberField Shaft => _shaft;

    public void Refresh()
    {
        _head.Refresh();
        _width.Refresh();
        _shaft.Refresh();
    }
}

/// <summary>Edits of the selected polygons and arrows, one undo step each; shared by the tool bars and the Layout tool's bar.</summary>
internal static class ShapeEdits
{
    public static void Sides(EditorSession s, IEnumerable<PolygonObject> p, int n) =>
        s.History.Execute(new SetPropertyCommand<PolygonObject, int>("Polygon Sides", p, o => o.Sides, (o, v) => o.Sides = v, PolygonObject.ClampSides(n)));

    /// <summary>Star on or off (<paramref name="inner"/> is the ratio a star gets).</summary>
    public static void Star(EditorSession s, IEnumerable<PolygonObject> p, bool star, double inner) =>
        s.History.Execute(new SetPropertyCommand<PolygonObject, double>(star ? "Make Star" : "Make Polygon", p, o => o.InnerRatio, (o, v) => o.InnerRatio = v,
            star ? Math.Clamp(inner, 0.05, PolygonObject.MaxInnerRatio) : 0));

    public static void Inner(EditorSession s, IEnumerable<PolygonObject> p, double inner) =>
        s.History.Execute(new SetPropertyCommand<PolygonObject, double>("Star Inner Radius", p, o => o.InnerRatio, (o, v) => o.InnerRatio = v,
            Math.Clamp(inner, 0.05, PolygonObject.MaxInnerRatio)));

    public static void HeadLength(EditorSession s, IEnumerable<ArrowObject> a, double fraction) =>
        s.History.Execute(new SetPropertyCommand<ArrowObject, double>("Arrow Head Length", a, o => o.HeadFraction, (o, v) => o.HeadFraction = v, fraction));

    public static void ShaftWidth(EditorSession s, IEnumerable<ArrowObject> a, double fraction) =>
        s.History.Execute(new SetPropertyCommand<ArrowObject, double>("Arrow Shaft Width", a, o => o.ShaftFraction, (o, v) => o.ShaftFraction = v, fraction));

    /// <summary>The head width is kept as a share of each arrow's own length, so undo gives each its own width back.</summary>
    public static void HeadWidth(EditorSession s, IEnumerable<ArrowObject> a, double ratio) =>
        s.History.Execute(new SetPropertyCommand<ArrowObject, double>("Arrow Head Width", a,
            o => o.Length > 0 ? o.HeadWidth / o.Length : 0, (o, v) => o.HeadWidth = v * o.Length, Math.Max(0.02, ratio)));

    public static double WidthRatio(ArrowObject a) => a.Length > 0 ? a.HeadWidth / a.Length : ArrowTool.DefaultWidthRatio;
}

/// <summary>Rounded Rectangle tool: the corner radius for new rounded rectangles (also applied to selected rectangles).</summary>
public sealed class RoundedRectangleContextBar : ContextBarBase, IRefreshable
{
    private readonly ShapeNumberField _radius;

    public RoundedRectangleContextBar(EditorSession session, RoundedRectangleTool tool)
    {
        _radius = new ShapeNumberField("Corner radius", "in", "0.####", () => Units.ToInches(tool.CornerRadius), v =>
        {
            double pts = Units.FromInches(Math.Max(0, v));
            tool.CornerRadius = pts;
            var rects = session.Selection.Items.OfType<RectangleObject>().ToArray();
            if (rects.Length > 0)
                session.History.Execute(new SetPropertyCommand<RectangleObject, double>("Corner Radius", rects, o => o.CornerRadius, (o, r) => o.CornerRadius = r, pts));
        });
        Row1.Children.Add(_radius.Panel);
        Row2.Children.Add(ContextBars.Hint(tool.Hint));
        Refresh();
    }

    public void Refresh() => _radius.Refresh();
}

/// <summary>Polygon tool: sides and star settings for new polygons; they also reshape the selected polygons.</summary>
public sealed class PolygonContextBar : ContextBarBase, IRefreshable
{
    private readonly PolygonFields _fields;

    public PolygonContextBar(EditorSession session, PolygonTool tool)
    {
        List<PolygonObject> Selected() => session.Selection.Items.OfType<PolygonObject>().ToList();
        _fields = new PolygonFields(
            () => tool.Sides, n =>
            {
                tool.Sides = PolygonObject.ClampSides(n);
                if (Selected() is { Count: > 0 } p) ShapeEdits.Sides(session, p, n);
            },
            () => tool.Star, on =>
            {
                tool.Star = on;
                if (Selected() is { Count: > 0 } p) ShapeEdits.Star(session, p, on, tool.InnerRatio);
                Fields.Refresh();
            },
            () => tool.InnerRatio, r =>
            {
                tool.InnerRatio = Math.Clamp(r, 0.05, PolygonObject.MaxInnerRatio);
                if (tool.Star && Selected() is { Count: > 0 } p) ShapeEdits.Inner(session, p, r);
            });
        Row1.Children.Add(_fields);
        Row2.Children.Add(ContextBars.Hint(tool.Hint));
    }

    internal PolygonFields Fields => _fields;
    public void Refresh() => _fields.Refresh();
}

/// <summary>Arrow tool: head and shaft proportions for new arrows; they also reshape the selected arrows.</summary>
public sealed class ArrowContextBar : ContextBarBase, IRefreshable
{
    private readonly ArrowFields _fields;

    public ArrowContextBar(EditorSession session, ArrowTool tool)
    {
        List<ArrowObject> Selected() => session.Selection.Items.OfType<ArrowObject>().ToList();
        _fields = new ArrowFields(
            () => tool.HeadFraction, v => { tool.HeadFraction = ArrowObject.ClampFraction(v); if (Selected() is { Count: > 0 } a) ShapeEdits.HeadLength(session, a, v); },
            () => tool.WidthRatio, v => { tool.WidthRatio = Math.Max(0.02, v); if (Selected() is { Count: > 0 } a) ShapeEdits.HeadWidth(session, a, v); },
            () => tool.ShaftFraction, v => { tool.ShaftFraction = ArrowObject.ClampFraction(v); if (Selected() is { Count: > 0 } a) ShapeEdits.ShaftWidth(session, a, v); });
        Row1.Children.Add(_fields);
        Row2.Children.Add(ContextBars.Hint(tool.Hint));
    }

    internal ArrowFields Fields => _fields;
    public void Refresh() => _fields.Refresh();
}

/// <summary>
/// Convert to Rounded Rectangle: the radius (previewed live on the canvas), then Done and Cancel. The radius and Done show only while there is a rectangle to
/// round; Cancel leaves without changing anything.
/// </summary>
public sealed class RoundCornersContextBar : ContextBarBase, IRefreshable
{
    private readonly RoundCornersTool _tool;
    private readonly ShapeNumberField _radius;
    private readonly DoneCancelButtons _buttons;
    private readonly TextBlock _hint;

    public RoundCornersContextBar(EditorSession session, RoundCornersTool tool)
    {
        _tool = tool;
        _radius = new ShapeNumberField("Corner radius", "in", "0.####", () => Units.ToInches(tool.Radius), v => tool.Radius = Units.FromInches(Math.Max(0, v)));
        _buttons = new DoneCancelButtons(session, "Round the corners of the selected rectangles (Enter)", "Leave without changing anything");
        _hint = ContextBars.Hint("Select a rectangle to round its corners");
        Row1.Children.Add(_radius.Panel);
        _buttons.AddTo(Row2);
        Row2.Children.Add(_hint);
        Refresh();
    }

    internal ShapeNumberField Radius => _radius;
    internal Button Done => _buttons.Done;
    internal Button Cancel => _buttons.Cancel;

    public void Refresh()
    {
        bool can = _tool.CanApply;
        ContextBars.Valid(_radius.Panel, can);
        _buttons.Refresh();
        ContextBars.Valid(_hint, !can);
        if (can) _radius.Refresh();
    }
}

/// <summary>
/// Shown in the Layout tool's bar while the selection is only polygons or only arrows: the same fields as the shape's tool bar, acting on
/// the selected shapes. Hidden otherwise (R26).
/// </summary>
internal sealed class ShapeSelectionFields : StackPanel, IRefreshable
{
    private readonly EditorSession _session;
    private readonly PolygonFields _polygon;
    private readonly ArrowFields _arrow;

    public ShapeSelectionFields(EditorSession session)
    {
        _session = session;
        Orientation = Orientation.Horizontal;
        this.Spaced(SpacingProperty, ContextBars.RowSpacing);
        _polygon = new PolygonFields(
            () => First<PolygonObject>()?.Sides ?? 5, n => ShapeEdits.Sides(session, All<PolygonObject>(), n),
            () => First<PolygonObject>()?.IsStar ?? false, on => { ShapeEdits.Star(session, All<PolygonObject>(), on, PolygonTool.DefaultInnerRatio); },
            () => First<PolygonObject>() is { IsStar: true } p ? p.InnerRatio : PolygonTool.DefaultInnerRatio, r => ShapeEdits.Inner(session, All<PolygonObject>(), r));
        _arrow = new ArrowFields(
            () => First<ArrowObject>()?.HeadFraction ?? 0, v => ShapeEdits.HeadLength(session, All<ArrowObject>(), v),
            () => First<ArrowObject>() is { } a ? ShapeEdits.WidthRatio(a) : 0, v => ShapeEdits.HeadWidth(session, All<ArrowObject>(), v),
            () => First<ArrowObject>()?.ShaftFraction ?? 0, v => ShapeEdits.ShaftWidth(session, All<ArrowObject>(), v));
        Children.Add(_polygon);
        Children.Add(_arrow);
        Refresh();
    }

    private T? First<T>() where T : DocObject => _session.Selection.Items.OfType<T>().FirstOrDefault();
    private List<T> All<T>() where T : DocObject => _session.Selection.Items.OfType<T>().ToList();

    /// <summary>True when every selected object is a polygon (or every one is an arrow).</summary>
    private bool Only<T>() where T : DocObject => _session.Selection.Items.Count > 0 && _session.Selection.Items.All(o => o is T);

    internal PolygonFields Polygon => _polygon;
    internal ArrowFields Arrow => _arrow;

    public void Refresh()
    {
        bool poly = Only<PolygonObject>(), arrow = Only<ArrowObject>();
        ContextBars.Valid(_polygon, poly);
        ContextBars.Valid(_arrow, arrow);
        IsVisible = poly || arrow;
        if (poly) _polygon.Refresh();
        if (arrow) _arrow.Refresh();
    }
}
