using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Shell;

/// <summary>Shared pieces of the two path editing bars: buttons that never take the keyboard focus, and refreshing while the tool changes.</summary>
internal static class PathBarParts
{
    public static Button Action(string text, string tip, Action run) => ContextBars.Action(text, run, tip);

    public static CheckBox Check(string text, string tip, Action<bool> set)
    {
        var c = new CheckBox { Content = text, Focusable = false, VerticalAlignment = VerticalAlignment.Center, Margin = new(2, 0, 0, 0) };
        ToolTip.SetTip(c, tip);
        c.IsCheckedChanged += (_, _) => set(c.IsChecked == true);
        return c;
    }

    public static TextBlock Value() => new() { VerticalAlignment = VerticalAlignment.Center };

    public static BarSeparator Gap() => ContextBars.Separator();

    /// <summary>The bar re-reads the tool's state whenever the canvas redraws (a node was selected, a vector hovered, ...) while it is on screen.</summary>
    public static void RefreshWhileVisible(Control bar, EditorSession session, Action refresh)
    {
        void OnRedraw(object? s, EventArgs e) => refresh();
        bar.AttachedToVisualTree += (_, _) => session.RedrawRequested += OnRedraw;
        bar.DetachedFromVisualTree += (_, _) => session.RedrawRequested -= OnRedraw;
    }
}

/// <summary>
/// Vector Edit Context Bar: X/Y of the selected node (editable), readouts for the selected vector
/// (length, radius, angle, tangency) and a button per command.
/// </summary>
public sealed class VectorEditContextBar : ContextBarBase, IRefreshable
{
    private readonly EditorSession _session;
    private readonly VectorEditTool _tool;
    private readonly TextBox _x, _y;
    private readonly TextBlock _length = PathBarParts.Value(), _radius = PathBarParts.Value(), _angle = PathBarParts.Value(),
        _tangency = PathBarParts.Value(), _node = PathBarParts.Value();
    private readonly Button _insert, _line, _arc, _bezier, _sharp, _corner, _smooth, _symmetric, _break, _setStart, _join, _delete;
    private bool _refreshing;
    private string _shownX = "", _shownY = "";   // what the fields were last set to: anything else was typed by the user

    public VectorEditContextBar(EditorSession session, VectorEditTool tool)
    {
        _session = session;
        _tool = tool;
        _x = ContextBars.NumberBox(Apply);
        _y = ContextBars.NumberBox(Apply);

        _insert = PathBarParts.Action("Insert", "Insert a node on the vector (Ins)", tool.Insert);
        _line = PathBarParts.Action("Line", "Make the vector a line (L)", tool.ToLine);
        _arc = PathBarParts.Action("Arc", "Make the vector an arc (A)", tool.ToArc);
        _bezier = PathBarParts.Action("Bezier", "Make the vector a Bézier curve (B)", tool.ToBezier);
        _sharp = PathBarParts.Action("Sharp", "Turn the selected arc into a sharp corner", tool.MakeSharp);
        _corner = PathBarParts.Action("Corner", "Make the selected nodes corners: their handles are pulled in", tool.MakeCorner);
        _smooth = PathBarParts.Action("Smooth", "Make the selected nodes smooth: the vectors on both sides leave along one line", tool.MakeSmooth);
        _symmetric = PathBarParts.Action("Symmetric", "Make the selected nodes smooth with equal handles", tool.MakeSymmetric);
        _break = PathBarParts.Action("Break", "Break the loop open at the selected node (O)", tool.BreakLoop);
        _setStart = PathBarParts.Action("Set Start/End", "Make the selected node the loop's start/end point (E)", tool.SetStartEnd);
        _join = PathBarParts.Action("Close/Join", "Close the open loop, or join the two selected end nodes (J)", tool.JoinStartEnd);
        _delete = PathBarParts.Action("Delete", "Delete the selected nodes or vector (Del)", tool.Delete);

        // Row 1: the selected node's position and the readouts; row 2: a button per command.
        foreach (var c in new Control[] { ContextBars.Field("X", _x, "in"), ContextBars.Field("Y", _y, "in"), PathBarParts.Gap(), _node, _length, _radius, _angle, _tangency })
            Row1.Children.Add(c);
        foreach (var c in new Control[] { _insert, _line, _arc, _bezier, _sharp, PathBarParts.Gap(), _corner, _smooth, _symmetric, PathBarParts.Gap(), _break, _setStart, _join, _delete })
            Row2.Children.Add(c);
        PathBarParts.RefreshWhileVisible(this, session, Refresh);
        Refresh();
    }

    public void Refresh()
    {
        _refreshing = true;
        var anchor = _tool.AnchorPosition();
        _x.IsEnabled = _y.IsEnabled = anchor is not null;
        if (anchor is { } a)
        {
            var (ux, uy) = Units.ToUser(a, _session.Document.Page);
            if (!_x.IsFocused) _x.Text = _shownX = ContextBars.Fmt(ux);
            if (!_y.IsFocused) _y.Text = _shownY = ContextBars.Fmt(uy);
        }
        else if (!_x.IsFocused && !_y.IsFocused) _x.Text = _y.Text = _shownX = _shownY = "";

        _node.Text = _tool.SelectedNodeKind() is { } k ? $"Node: {k}" : _tool.SelectedNodeCount > 1 ? $"{_tool.SelectedNodeCount} nodes" : "";
        var r = _tool.Readout();
        _length.Text = r is { } v ? $"Length: {Inches(v.Length)}" : "";
        _radius.Text = r?.Radius is { } rad ? $"Radius: {Inches(rad)}" : "";
        _angle.Text = r?.Angle is { } ang ? $"Angle: {ang.ToString("0.##", CultureInfo.InvariantCulture)}°" : "";
        _tangency.Text = r?.Tangency is { } t ? $"Tangency: {t.ToString("0.##", CultureInfo.InvariantCulture)}°" : "";
        foreach (var readout in new[] { _node, _length, _radius, _angle, _tangency }) readout.IsVisible = !string.IsNullOrEmpty(readout.Text);   // an empty readout would still take a gap

        _insert.IsEnabled = _tool.CanInsert;
        _line.IsEnabled = _tool.CanToLine;
        _arc.IsEnabled = _tool.CanToArc;
        _bezier.IsEnabled = _tool.CanToBezier;
        _sharp.IsEnabled = _tool.CanMakeSharp;
        _corner.IsEnabled = _tool.CanMakeCorner;
        _smooth.IsEnabled = _tool.CanMakeSmooth;
        _symmetric.IsEnabled = _tool.CanMakeSmooth;
        _break.IsEnabled = _tool.CanBreakLoop;
        _setStart.IsEnabled = _tool.CanSetStartEnd;
        _join.IsEnabled = _tool.CanJoinStartEnd;
        _delete.IsEnabled = _tool.CanDelete;
        _refreshing = false;
    }

    private static string Inches(double points) => $"{Units.ToInches(points).ToString("0.####", CultureInfo.InvariantCulture)} in";

    private void Apply()
    {
        if (_refreshing || _tool.AnchorPosition() is not { } now) return;
        // Fields show rounded values, so only the coordinate that was typed is applied; the other keeps its exact value.
        var (cx, cy) = Units.ToUser(now, _session.Document.Page);
        double x = cx, y = cy;
        bool xEdited = (_x.Text ?? "") != _shownX, yEdited = (_y.Text ?? "") != _shownY;
        if (!xEdited && !yEdited) return;
        if ((xEdited && !ContextBars.TryParse(_x, out x)) || (yEdited && !ContextBars.TryParse(_y, out y))) { Refresh(); return; }
        _tool.SetAnchorPosition(Units.FromUser(x, y, _session.Document.Page));
    }
}

/// <summary>Loop Edit Context Bar: width, height and length of the selected loop and a button per loop command.</summary>
public sealed class LoopEditContextBar : ContextBarBase, IRefreshable
{
    private readonly EditorSession _session;
    private readonly LoopEditTool _tool;
    private readonly TextBlock _size = PathBarParts.Value(), _length = PathBarParts.Value(), _which = PathBarParts.Value();
    private readonly Button _reverse, _open, _close, _circle, _delete, _breakOut, _reorder;
    private readonly CheckBox _numbers, _direction;
    private bool _refreshing;

    public LoopEditContextBar(EditorSession session, LoopEditTool tool)
    {
        _session = session;
        _tool = tool;

        _reverse = PathBarParts.Action("Reverse", "Reverse the loop's direction", tool.ReverseDirection);
        _open = PathBarParts.Action("Open Loop", "Open the loop at its start/end point", tool.OpenLoop);
        _close = PathBarParts.Action("Close Loop", "Close the open loop", tool.CloseLoop);
        _circle = PathBarParts.Action("To Circle", "Turn the loop into a circle", tool.ToCircle);
        _delete = PathBarParts.Action("Delete Loop", "Delete the loop (Del)", tool.Delete);
        _breakOut = PathBarParts.Action("Break Out", "Make the loop a graphic of its own", tool.BreakOut);
        _reorder = PathBarParts.Action("Reorder", "Reorder loops: outer clockwise, inner counter-clockwise and first", tool.ReorderLoops);
        _numbers = PathBarParts.Check("Loop numbers", "Show each loop's sequence number", v => { if (!_refreshing) tool.ShowNumbers = v; });
        _direction = PathBarParts.Check("Direction", "Show loop direction: clockwise blue, counter-clockwise red, broken black", v => { if (!_refreshing) tool.ShowDirection = v; });

        foreach (var c in new Control[] { _which, _size, _length })
            Row1.Children.Add(c);
        foreach (var c in new Control[] { _reverse, _open, _close, _circle, _delete, _breakOut, PathBarParts.Gap(), _reorder, _numbers, _direction })
            Row2.Children.Add(c);
        PathBarParts.RefreshWhileVisible(this, session, Refresh);
        Refresh();
    }

    public void Refresh()
    {
        _refreshing = true;
        if (_tool.Readout() is { } r)
        {
            _which.Text = $"Loop {r.Number} of {r.Total}{(r.Count > 1 ? $" (+{r.Count - 1})" : "")}  {(r.Closed ? (r.Clockwise ? "clockwise" : "counter-clockwise") : "open")}";
            _size.Text = $"Width: {Inches(r.Width)}  Height: {Inches(r.Height)}";
            _length.Text = $"Length: {Inches(r.Length)}";
        }
        else
        {
            _which.Text = _tool.HasTarget ? "Click a loop" : "Click an object";
            _size.Text = _length.Text = "";
        }
        _size.IsVisible = !string.IsNullOrEmpty(_size.Text);
        _length.IsVisible = !string.IsNullOrEmpty(_length.Text);
        _reverse.IsEnabled = _tool.CanReverse;
        _open.IsEnabled = _tool.CanOpen;
        _close.IsEnabled = _tool.CanClose;
        _circle.IsEnabled = _tool.CanToCircle;
        _delete.IsEnabled = _tool.CanDelete;
        _breakOut.IsEnabled = _tool.CanBreakOut;
        _reorder.IsEnabled = _tool.CanReorder;
        _numbers.IsChecked = _tool.ShowNumbers;
        _direction.IsChecked = _tool.ShowDirection;
        _refreshing = false;
    }

    private static string Inches(double points) => $"{Units.ToInches(points).ToString("0.####", CultureInfo.InvariantCulture)} in";
}
