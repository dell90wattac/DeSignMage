using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Shell;

/// <summary>
/// Context Bar while a ruler guide is selected (requirement R4, guide bar): X and Y of the guide
/// (the one that doesn't apply is greyed out), its angle, horizontal / vertical / diagonal buttons, lock,
/// "+" (center on the page along its axis), snap objects to guides, snap guides to graphics, blue X = remove this guide,
/// red X = remove all guides. A locked guide can't be dragged, but its numbers and buttons still work.
/// </summary>
public sealed class GuideContextBar : ContextBarBase, IRefreshable
{
    private readonly EditorSession _session;
    private readonly TextBox _x, _y, _angle;
    private readonly Button _horizontal, _vertical, _diagonal, _lock, _center, _snap, _snapGraphics, _removeOne, _removeAll;
    private readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Ui.Muted") };
    private readonly Control _xField, _yField, _angleField, _kinds, _tools;   // groups shown only while they apply to the selected guide
    private bool _refreshing, _edited;

    public GuideContextBar(EditorSession session)
    {
        _session = session;

        _x = ContextBars.NumberBox(Apply);
        _y = ContextBars.NumberBox(Apply);
        _angle = ContextBars.NumberBox(Apply);
        foreach (var box in new[] { _x, _y, _angle })
            box.TextChanged += (_, _) => { if (!_refreshing) _edited = true; };
        _x.Name = "GuideX";
        _y.Name = "GuideY";
        _angle.Name = "GuideAngle";

        _horizontal = IconButton("GuideHorizontal", "┄", "Horizontal guide (angle 0°)", () => SetAngle(0));
        _vertical = IconButton("GuideVertical", "┆", "Vertical guide (angle 90°)", () => SetAngle(90));
        _diagonal = IconButton("GuideDiagonal", "╲", "Angled guide (45°; edit the angle field for other angles)", () => SetAngle(45));
        _lock = IconButton("GuideLock", "", "Lock guide (a locked guide can't be dragged)", ToggleLock, glyph: true);
        _center = IconButton("GuideCenter", "+", "Center on the page along its axis", () => { if (Current is { } g) _session.CenterGuideOnPage(g); });
        _snap = IconButton("GuideSnap", "⌖", "Snap objects to guides", () => _session.SnapToGuides = !_session.SnapToGuides);
        _snapGraphics = IconButton("GuideSnapGraphics", "▣", "Snap guides to graphics: a guide jumps onto the edges, centers, baselines and cap heights of objects", () => _session.SnapGuidesToGraphics = !_session.SnapGuidesToGraphics);
        _removeOne = IconButton("GuideRemoveOne", "✕", "Remove this guide", () => { if (Current is { } g) _session.RemoveGuide(g); });
        _removeAll = IconButton("GuideRemoveAll", "✕", "Remove all guides", () => _session.RemoveAllGuides());
        ((TextBlock)_removeOne.Content!).Foreground = AppTheme.Brush("Ui.RulerMarker");
        ((TextBlock)_removeAll.Content!).Foreground = Brushes.Red;
        foreach (var b in new[] { _removeOne, _removeAll }) ((TextBlock)b.Content!).FontWeight = FontWeight.Bold;

        _xField = ContextBars.Field("X", _x, "in");
        _yField = ContextBars.Field("Y", _y, "in");
        _angleField = ContextBars.Field("Angle", _angle, "°");
        _kinds = ContextBars.Group(_horizontal, _vertical, _diagonal);
        _tools = ContextBars.Group(_lock, _center, _snap, _snapGraphics);
        foreach (var c in new Control[]
                 {
                     _xField, _yField, _angleField,
                     ContextBars.Separator(), _kinds,
                 })
            Row1.Children.Add(c);
        foreach (var c in new Control[]
                 {
                     _tools, ContextBars.Separator(),
                     ContextBars.Group(_removeOne, _removeAll), _info,
                 })
            Row2.Children.Add(c);
        Refresh();
    }

    private Guide? Current => _session.SelectedGuide;

    private static Button IconButton(string name, string text, string tip, Action run, bool glyph = false)
    {
        var t = new TextBlock { Text = text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (glyph) t.Classes.Add("glyph");
        else t.ScaledFont(14);
        var b = new Button { Name = name, Content = t, Focusable = false, MinWidth = 24, MinHeight = 22, Padding = new Thickness(2) };   // keep keyboard focus on the canvas
        b.Classes.Add("tb");
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => run();
        return b;
    }

    private static void Pressed(Button b, bool on)
    {
        if (on) b.Classes.Add("on");
        else b.Classes.Remove("on");
    }

    private void SetAngle(double degrees)
    {
        if (Current is not { } g) return;
        // The diagonal button only turns a straight guide diagonal; an angled guide keeps its own angle.
        if (degrees == 45 && g.Orientation == GuideOrientation.Angled) return;
        _session.SetGuide(g, g.Origin, degrees);
    }

    private void ToggleLock()
    {
        if (Current is { } g) _session.SetGuideLocked(g, !g.Locked);
    }

    public void Refresh()
    {
        _refreshing = true;
        var g = Current;
        var page = _session.Document.Page;
        bool has = g is not null;
        bool locked = g?.Locked ?? false;
        var kind = g?.Orientation ?? GuideOrientation.Horizontal;

        // What does not apply is not shown (R26): a horizontal guide has no X, a vertical one no Y. A locked guide only refuses to be
        // dragged ("prevents it from being moved accidentally"); typing its numbers and the buttons still work.
        _x.IsEnabled = _xField.IsVisible = has && kind != GuideOrientation.Horizontal;
        _y.IsEnabled = _yField.IsVisible = has && kind != GuideOrientation.Vertical;
        _angle.IsEnabled = _angleField.IsVisible = has;
        if (g is not null)
        {
            var (ux, uy) = Units.ToUser(g.Origin, page);
            if (!_x.IsFocused) _x.Text = ContextBars.Fmt(ux);
            if (!_y.IsFocused) _y.Text = ContextBars.Fmt(uy);
            if (!_angle.IsFocused) _angle.Text = ContextBars.Fmt(g.AngleDegrees);
        }
        else
        {
            _x.Text = _y.Text = _angle.Text = "";
        }

        _horizontal.IsEnabled = _vertical.IsEnabled = _diagonal.IsEnabled = _kinds.IsVisible = has;
        ContextBars.Valid(_center, has);
        ContextBars.Valid(_lock, has);
        ContextBars.Valid(_removeOne, has);
        ContextBars.Valid(_removeAll, _session.Document.Guides.Count > 0);
        Pressed(_horizontal, has && kind == GuideOrientation.Horizontal);
        Pressed(_vertical, has && kind == GuideOrientation.Vertical);
        Pressed(_diagonal, has && kind == GuideOrientation.Angled);
        Pressed(_lock, locked);
        Pressed(_snap, _session.SnapToGuides);
        Pressed(_snapGraphics, _session.SnapGuidesToGraphics);
        ((TextBlock)_lock.Content!).Text = locked ? "" : "";   // closed / open padlock
        _info.Text = $"{_session.Document.Guides.Count} guide{(_session.Document.Guides.Count == 1 ? "" : "s")}" + (locked ? "  (locked)" : "");
        _edited = false;
        _refreshing = false;
    }

    private void Apply()
    {
        // Fields show rounded values, so only act when the user actually typed something.
        if (_refreshing || !_edited || Current is not { } g) return;
        _edited = false;
        if (!ContextBars.TryParse(_x, out var x) || !ContextBars.TryParse(_y, out var y) || !ContextBars.TryParse(_angle, out var angle))
        {
            Refresh();
            return;
        }
        var page = _session.Document.Page;
        // Take a coordinate from the fields only if it differs from what they showed (they're rounded).
        var (ux, uy) = Units.ToUser(g.Origin, page);
        var typed = Units.FromUser(Math.Abs(x - ux) > 5e-5 ? x : ux, Math.Abs(y - uy) > 5e-5 ? y : uy, page);
        var origin = g.Orientation switch
        {
            GuideOrientation.Horizontal => new Vec2(g.Origin.X, typed.Y),
            GuideOrientation.Vertical => new Vec2(typed.X, g.Origin.Y),
            _ => typed,
        };
        _session.SetGuide(g, origin, Guide.NormalizeAngle(angle));
        Refresh();
    }
}
