using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Shell;

/// <summary>
/// The measurement fields of the Info Line (R38), which used to sit on the Context Bar: the position and size of the selection in inches (X from
/// the page's left edge, Y up from its bottom edge, W and H), typed values applied with Enter or by leaving the field, as one undo step each
/// (<see cref="EditorSession.SetSelectionBounds"/>). With nothing selected the same place shows the layout's own width and height
/// (<see cref="EditorSession.SetPageSize"/>, keeping its bottom-left corner). They belong to the Layout tool, as they did on its bar: with any
/// other tool, or while a ruler guide is selected, they are hidden; so are the selection's fields while it is locked (a locked selection
/// can't be moved or resized). A field only acts when something was typed in it, because it shows rounded values.
/// </summary>
public sealed class MeasureFields : IRefreshable
{
    private readonly EditorSession _session;
    private readonly TextBox _x, _y, _w, _h, _pageW, _pageH;
    private readonly Control _selectionFields;   // X, Y, W and H: shown while something can be moved or resized
    private readonly Control _pageFields;        // the layout's width and height: shown while nothing is selected
    private bool _refreshing;
    private readonly string[] _shown = ["", "", "", "", "", ""];   // what the fields were last given or acted on (x, y, w, h, layout w, layout h)

    public MeasureFields(Panel host, EditorSession session)
    {
        _session = session;
        _x = ContextBars.NumberBox(Apply);
        _y = ContextBars.NumberBox(Apply);
        _w = ContextBars.NumberBox(Apply);
        _h = ContextBars.NumberBox(Apply);
        _pageW = ContextBars.NumberBox(ApplyPageSize);
        _pageH = ContextBars.NumberBox(ApplyPageSize);
        _x.Name = "MeasureX"; _y.Name = "MeasureY"; _w.Name = "MeasureW"; _h.Name = "MeasureH";
        _pageW.Name = "MeasureLayoutW"; _pageH.Name = "MeasureLayoutH";
        ToolTip.SetTip(_x, "Position from the left edge of the layout, in inches");
        ToolTip.SetTip(_y, "Position from the bottom edge of the layout, in inches");
        ToolTip.SetTip(_w, "Width of the selection, in inches");
        ToolTip.SetTip(_h, "Height of the selection, in inches");
        ToolTip.SetTip(_pageW, "Width of the layout, in inches");
        ToolTip.SetTip(_pageH, "Height of the layout, in inches");

        _selectionFields = Row(ContextBars.Field("X", _x), ContextBars.Field("Y", _y), ContextBars.Field("W", _w), ContextBars.Field("H", _h), ContextBars.Unit("in"));
        _pageFields = Row(ContextBars.Caption("Layout"), ContextBars.Field("W", _pageW), ContextBars.Field("H", _pageH), ContextBars.Unit("in"));
        host.Children.Add(_selectionFields);
        host.Children.Add(_pageFields);
        Refresh();
    }

    private static StackPanel Row(params Control[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }.Spaced(StackPanel.SpacingProperty, ContextBars.RowSpacing);
        foreach (var item in items) row.Children.Add(item);
        return row;
    }

    public void Refresh()
    {
        _refreshing = true;
        bool layoutTool = _session.ActiveTool is SelectTool && _session.SelectedGuide is null;
        var b = _session.Selection.GetBounds();
        bool has = layoutTool && !b.IsEmpty && !_session.SelectionLocked;   // a locked selection can't be moved or resized: no fields
        foreach (var t in new[] { _x, _y, _w, _h }) t.IsEnabled = has;
        ContextBars.Valid(_selectionFields, has);
        void Put(int i, TextBox box, string text)
        {
            if (box.IsFocused) return;   // never overwrite what is being typed
            box.Text = text;
            _shown[i] = text;
        }
        if (has)
        {
            var (ux, uy) = Units.ToUser(new Vec2(b.Left, b.Bottom), _session.Document.Page);
            Put(0, _x, ContextBars.Fmt(ux));
            Put(1, _y, ContextBars.Fmt(uy));
            Put(2, _w, ContextBars.Fmt(Units.ToInches(b.Width)));
            Put(3, _h, ContextBars.Fmt(Units.ToInches(b.Height)));
        }
        else
        {
            _x.Text = _y.Text = _w.Text = _h.Text = "";
            _shown[0] = _shown[1] = _shown[2] = _shown[3] = "";
        }

        bool none = layoutTool && _session.Selection.IsEmpty;
        foreach (var t in new[] { _pageW, _pageH }) t.IsEnabled = none;
        ContextBars.Valid(_pageFields, none);
        if (none)
        {
            var page = _session.Document.Page;
            Put(4, _pageW, ContextBars.Fmt(Units.ToInches(page.Width)));
            Put(5, _pageH, ContextBars.Fmt(Units.ToInches(page.Height)));
        }
        _refreshing = false;
    }

    /// <summary>True when one of the boxes (x, y, w, h, layout w, layout h by index) holds other text than it was last given or acted on.</summary>
    private bool Differs(params int[] indexes)
    {
        TextBox[] boxes = [_x, _y, _w, _h, _pageW, _pageH];
        return indexes.Any(i => (boxes[i].Text ?? "") != _shown[i]);
    }

    /// <summary>Notes the text the boxes hold now as dealt with, so leaving a field again does not do it twice.</summary>
    private void Acknowledge(params int[] indexes)
    {
        TextBox[] boxes = [_x, _y, _w, _h, _pageW, _pageH];
        foreach (int i in indexes) _shown[i] = boxes[i].Text ?? "";
    }

    private void Apply()
    {
        // The fields show rounded values, so only act when the user actually typed something (the text differs from what was shown).
        if (_refreshing || !Differs(0, 1, 2, 3) || _session.Selection.IsEmpty) return;
        if (!ContextBars.TryParse(_x, out var x) || !ContextBars.TryParse(_y, out var y) ||
            !ContextBars.TryParse(_w, out var w) || !ContextBars.TryParse(_h, out var h) || w <= 0 || h <= 0)
        {
            Refresh();
            return;
        }
        var bottomLeft = Units.FromUser(x, y, _session.Document.Page);
        double wp = Units.FromInches(w), hp = Units.FromInches(h);
        var target = Rect2.FromXYWH(bottomLeft.X, bottomLeft.Y - hp, wp, hp);
        Acknowledge(0, 1, 2, 3);
        _session.SetSelectionBounds(target);
    }

    /// <summary>Layout W / H typed with nothing selected: resizes the page (Layout Properties), keeping its bottom-left corner.</summary>
    private void ApplyPageSize()
    {
        if (_refreshing || !Differs(4, 5) || !_session.Selection.IsEmpty) return;
        if (!ContextBars.TryParse(_pageW, out var w) || !ContextBars.TryParse(_pageH, out var h) || w <= 0 || h <= 0)
        {
            Refresh();
            return;
        }
        _session.SetPageSize(Units.FromInches(w), Units.FromInches(h));
        // Show the size the page really has (it may have been clamped), even in the field that still has the keyboard.
        _refreshing = true;
        _pageW.Text = ContextBars.Fmt(Units.ToInches(_session.Document.Page.Width));
        _pageH.Text = ContextBars.Fmt(Units.ToInches(_session.Document.Page.Height));
        Acknowledge(4, 5);
        _refreshing = false;
    }

    // ---- For tests ----

    internal TextBox X => _x;
    internal TextBox Y => _y;
    internal TextBox W => _w;
    internal TextBox H => _h;
    internal TextBox PageW => _pageW;
    internal TextBox PageH => _pageH;
    internal Control SelectionFields => _selectionFields;
    internal Control PageFields => _pageFields;
}
