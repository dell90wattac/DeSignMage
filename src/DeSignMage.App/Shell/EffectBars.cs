using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Shell;

/// <summary>Small helpers the three effect bars share (numeric fields that only act when the user typed, ✓ / ✕ buttons).</summary>
internal static class EffectBarParts
{
    /// <summary>A number box that remembers what it was last set to, so committing an untouched field does nothing.</summary>
    public sealed class Field
    {
        /// <summary>How long a typed value waits before a live field acts on it (a keystroke later it is replaced).</summary>
        public const int LiveDelayMs = 50;

        private readonly Avalonia.Threading.DispatcherTimer? _live;

        /// <param name="live">True: the value is applied while it is typed (after <see cref="LiveDelayMs"/> without another keystroke), not only on Enter or when the box is left (R31).
        /// Applying only changes what the tool shows; nothing is committed until the tool's own Done, so there is no undo step per keystroke.</param>
        public Field(Action<double> apply, bool live = false)
        {
            Box = ContextBars.NumberBox(() =>
            {
                _live?.Stop();
                if (Refreshing || (Box!.Text ?? "") == _applied) return;
                if (ContextBars.TryParse(Box, out var v)) { _applied = Box.Text ?? ""; apply(v); }
                else Show(Shown);   // not a number: put the old value back
            });
            if (!live) return;
            _live = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LiveDelayMs) };
            _live.Tick += (_, _) =>
            {
                _live.Stop();
                if (Refreshing || (Box.Text ?? "") == _applied) return;
                if (ContextBars.TryParse(Box, out var v)) { _applied = Box.Text ?? ""; apply(v); }   // half-typed ("0.", "-") is just waited for
            };
            Box.TextChanged += (_, _) => { if (!Refreshing) { _live.Stop(); _live.Start(); } };
        }

        private string _applied = "";   // what the tool was last given from this box (or shown in it): typing back to it changes nothing

        public TextBox Box { get; }
        public string Shown { get; private set; } = "";
        public bool Refreshing { get; set; }

        public void Show(string text)
        {
            if (Box.IsFocused) return;   // never overwrite what the user is typing
            Refreshing = true;
            Box.Text = Shown = _applied = text;
            Refreshing = false;
        }
    }

    /// <summary>Refreshes <paramref name="bar"/> while it is on screen: when the tool says it changed and whenever the canvas redraws (selection changes).</summary>
    public static void Bind(Control bar, EditorSession session, Action refresh, Action<EventHandler> add, Action<EventHandler> remove)
    {
        void On(object? s, EventArgs e) => refresh();
        bar.AttachedToVisualTree += (_, _) => { add(On); session.RedrawRequested += On; };
        bar.DetachedFromVisualTree += (_, _) => { remove(On); session.RedrawRequested -= On; };
    }
}

/// <summary>
/// Outline / Inline Context Bar: the distance as inches or % of the selection's
/// height, the optional 2nd ring, the corner type (Auto with OAT, Miter, Round), Contour, Leave Original and Preview, then Done.
/// </summary>
public sealed class OutlineContextBar : ContextBarBase, IRefreshable
{
    private readonly EditorSession _session;
    private readonly OutlineTool _tool;
    private readonly EffectBarParts.Field _first, _firstPct, _second, _secondPct, _oat;
    private readonly ComboBox _corners;
    private readonly CheckBox _contour, _leave, _preview;
    private readonly DoneCancelButtons _buttons;
    private readonly Control _firstPctGroup, _secondPctGroup, _oatGroup;   // shown only while they apply
    private readonly TextBlock _title = PathBarParts.Value();
    private bool _refreshing;

    public OutlineContextBar(EditorSession session, OutlineTool tool)
    {
        _session = session;
        _tool = tool;
        // The four amounts are live: the preview follows the digits as they are typed (R31).
        _first = new EffectBarParts.Field(v => tool.Update(s => s with { First = Units.FromInches(v) }), live: true);
        _firstPct = new EffectBarParts.Field(v => { if (tool.HasPercent) tool.Update(s => s with { First = v / 100 * tool.Height }); }, live: true);
        _second = new EffectBarParts.Field(v => tool.Update(s => s with { Second = Units.FromInches(v) }), live: true);
        _secondPct = new EffectBarParts.Field(v => { if (tool.HasPercent) tool.Update(s => s with { Second = v / 100 * tool.Height }); }, live: true);
        _oat = new EffectBarParts.Field(v => tool.Update(s => s with { Oat = Math.Clamp(v, 0, 120) }));   // 0..120: what the legacy Miter / Round buttons write

        _corners = new ComboBox { ItemsSource = new[] { "Auto (OAT)", "Miter", "Round" }, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_corners, "Corner type. Auto rounds corners sharper than the OAT angle and keeps the others sharp; Miter keeps every corner sharp; Round rounds every corner.");
        _corners.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _corners.SelectedIndex < 0) return;
            var style = _corners.SelectedIndex switch { 1 => CornerStyle.Miter, 2 => CornerStyle.Round, _ => CornerStyle.Auto };
            tool.Update(s => s with { Corners = style });
            ContextBars.FocusCanvas?.Invoke();
        };
        _contour = PathBarParts.Check("Contour", "Outer loops only: holes are left out", v => { if (!_refreshing) tool.Update(s => s with { ContourOnly = v }); });
        _leave = PathBarParts.Check("Leave Original", "Keep the original. Off: the result takes its place.", v => { if (!_refreshing) tool.Update(s => s with { LeaveOriginal = v }); });
        _preview = PathBarParts.Check("Preview", "Show the result while you change the fields", v => { if (!_refreshing) tool.SetPreviewEnabled(v); });
        _buttons = new DoneCancelButtons(session, "Make the new object (Enter)", "Leave without changing anything");

        _firstPctGroup = ContextBars.Field("%", _firstPct.Box);
        _secondPctGroup = ContextBars.Field("%", _secondPct.Box);
        _oatGroup = ContextBars.Field("OAT", _oat.Box);
        foreach (var c in new Control[]
                 {
                     _title,
                     ContextBars.Field("1st", _first.Box, "in"), _firstPctGroup,
                     PathBarParts.Gap(),
                     ContextBars.Field("2nd", _second.Box, "in"), _secondPctGroup,
                 })
            Row1.Children.Add(c);
        foreach (var c in new Control[]
                 {
                     ContextBars.Group(ContextBars.Label("Corners"), _corners), _oatGroup,
                     PathBarParts.Gap(), _contour, _leave, _preview,
                 })
            Row2.Children.Add(c);
        _buttons.AddTo(Row2);
        EffectBarParts.Bind(this, session, Refresh, h => tool.Changed += h, h => tool.Changed -= h);
        Refresh();
    }

    internal Button Done => _buttons.Done;
    internal Button Cancel => _buttons.Cancel;

    public void Refresh()
    {
        _refreshing = true;
        var s = _tool.Settings;
        _title.Text = _tool.Kind == OutlineKind.Inline ? "Inline" : "Outline";
        _first.Show(ContextBars.Fmt(Units.ToInches(s.First)));
        _second.Show(ContextBars.Fmt(Units.ToInches(s.Second)));
        _firstPct.Show(_tool.HasPercent ? ContextBars.Fmt(_tool.FirstPercent) : "");
        _secondPct.Show(_tool.HasPercent ? ContextBars.Fmt(_tool.SecondPercent) : "");
        // a field that does not apply is not shown (R26): percentages need a selection to take a height from; OAT belongs to Auto corners
        _firstPct.Box.IsEnabled = _secondPct.Box.IsEnabled = _firstPctGroup.IsVisible = _secondPctGroup.IsVisible = _tool.HasPercent;
        _oat.Show(ContextBars.Fmt(s.Oat));
        _oat.Box.IsEnabled = _oatGroup.IsVisible = s.Corners == CornerStyle.Auto;
        _corners.SelectedIndex = s.Corners switch { CornerStyle.Miter => 1, CornerStyle.Round => 2, _ => 0 };
        _contour.IsChecked = s.ContourOnly;
        _leave.IsChecked = s.LeaveOriginal;
        _preview.IsChecked = _tool.PreviewEnabled;
        _buttons.Refresh(!_session.EffectBusy);
        _refreshing = false;
    }
}

/// <summary>Cookie Cutter Context Bar: Outline, Remain Active, Apply.</summary>
public sealed class CookieCutterContextBar : ContextBarBase, IRefreshable
{
    private readonly EditorSession _session;
    private readonly CookieCutterTool _tool;
    private readonly EffectBarParts.Field _outline;
    private readonly CheckBox _remain;
    private readonly Button _apply;
    private bool _refreshing;

    public CookieCutterContextBar(EditorSession session, CookieCutterTool tool)
    {
        _session = session;
        _tool = tool;
        _outline = new EffectBarParts.Field(v => tool.SetOutline(Units.FromInches(v)));
        _remain = PathBarParts.Check("Remain Active", "The cutter follows the pointer and every click cuts; the tool stays on until Esc", v => { if (!_refreshing) tool.SetRemainActive(v); });
        _apply = PathBarParts.Action("✓ Apply", "Cut everything below the cutter (Enter)", () => { _ = tool.Apply(); ContextBars.FocusCanvas?.Invoke(); });
        var cancel = PathBarParts.Action("✕ Done", "Leave the tool (Esc)", () => { session.SetTool("select"); ContextBars.FocusCanvas?.Invoke(); });

        foreach (var c in new Control[] { ContextBars.Label("Cookie Cutter"), ContextBars.Field("Outline", _outline.Box, "in"), PathBarParts.Gap(), _remain })
            Row1.Children.Add(c);
        foreach (var c in new Control[]
                 {
                     _apply, cancel, PathBarParts.Gap(),
                     ContextBars.Hint("The selected object is the cutter: everything beneath it is cut. Drag the cutter's edge to set the outline."),
                 })
            Row2.Children.Add(c);
        EffectBarParts.Bind(this, session, Refresh, h => tool.Changed += h, h => tool.Changed -= h);
        Refresh();
    }

    public void Refresh()
    {
        _refreshing = true;
        _outline.Show(ContextBars.Fmt(Units.ToInches(_tool.Outline)));
        _remain.IsChecked = _tool.RemainActive;
        ContextBars.Valid(_apply, _tool.CanApply && !_session.EffectBusy && !_tool.RemainActive);
        _refreshing = false;
    }
}

/// <summary>Knife Context Bar: Gap or Overlap with their amounts, Selected Only, Simple Cut.</summary>
public sealed class KnifeContextBar : ContextBarBase, IRefreshable
{
    private readonly KnifeTool _tool;
    private readonly EffectBarParts.Field _gap, _overlap;
    private readonly RadioButton _gapMode, _overlapMode;
    private readonly CheckBox _selectedOnly, _simple;
    private readonly Control _gapBox, _gapUnit, _overlapBox, _overlapUnit;   // the amount of the mode that is on; the other one is not shown
    private bool _refreshing;

    public KnifeContextBar(EditorSession session, KnifeTool tool)
    {
        _tool = tool;
        _gap = new EffectBarParts.Field(v =>
        {
            double points = Units.FromInches(v);
            if (points < Units.FromInches(0.001)) points = KnifeSettings.MinGap;   // anything under 0.001 in becomes 0.002 in
            tool.Update(s => s with { Gap = points });
        });
        _overlap = new EffectBarParts.Field(v => { if (v > 0) tool.Update(s => s with { Overlap = Units.FromInches(v) }); });
        _gapMode = new RadioButton { Content = "Knife Gap", GroupName = "knife-mode", Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        _overlapMode = new RadioButton { Content = "Overlap", GroupName = "knife-mode", Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_gapMode, "The blade has a width: a strip this wide is cut away along the line");
        ToolTip.SetTip(_overlapMode, "No strip is cut away: the two pieces overlap by this much (for vinyl that must not leave a gap)");
        _gapMode.IsCheckedChanged += (_, _) => { if (!_refreshing && _gapMode.IsChecked == true) tool.Update(s => s with { OverlapMode = false }); };
        _overlapMode.IsCheckedChanged += (_, _) => { if (!_refreshing && _overlapMode.IsChecked == true) tool.Update(s => s with { OverlapMode = true }); };
        _selectedOnly = PathBarParts.Check("Selected Only", "Only the selected objects are cut (Shift: for one cut). Off: everything the line crosses", v => { if (!_refreshing) tool.Update(s => s with { SelectedOnly = v }); });
        _simple = PathBarParts.Check("Simple Cut", "Cut exactly along the line you drag. Off: the line is extended across the whole shape", v => { if (!_refreshing) tool.Update(s => s with { SimpleCut = v }); });
        var done = PathBarParts.Action("✓ Done", "Leave the Knife tool (Esc)", () => { session.SetTool("select"); ContextBars.FocusCanvas?.Invoke(); });

        _gapBox = _gap.Box;
        _overlapBox = _overlap.Box;
        _gapUnit = ContextBars.Unit("in");
        _overlapUnit = ContextBars.Unit("in");
        foreach (var c in new Control[]
                 {
                     ContextBars.Group(_gapMode, _gapBox, _gapUnit), PathBarParts.Gap(),
                     ContextBars.Group(_overlapMode, _overlapBox, _overlapUnit),
                 })
            Row1.Children.Add(c);
        foreach (var c in new Control[] { _selectedOnly, _simple, PathBarParts.Gap(), done })
            Row2.Children.Add(c);
        EffectBarParts.Bind(this, session, Refresh, h => tool.Changed += h, h => tool.Changed -= h);
        Refresh();
    }

    public void Refresh()
    {
        _refreshing = true;
        var s = _tool.Settings;
        _gap.Show(ContextBars.Fmt(Units.ToInches(s.Gap)));
        _overlap.Show(ContextBars.Fmt(Units.ToInches(s.Overlap)));
        _gapMode.IsChecked = !s.OverlapMode;
        _overlapMode.IsChecked = s.OverlapMode;
        // only the amount of the mode that is on is shown (R26); the radio buttons stay, they are how the mode is chosen
        _gapBox.IsEnabled = _gapBox.IsVisible = _gapUnit.IsVisible = !s.OverlapMode;
        _overlapBox.IsEnabled = _overlapBox.IsVisible = _overlapUnit.IsVisible = s.OverlapMode;
        ContextBars.Valid(_selectedOnly, _tool.CanSelectedOnly);
        _selectedOnly.IsChecked = s.SelectedOnly && _tool.CanSelectedOnly;
        _simple.IsChecked = s.SimpleCut;
        ContextBars.Valid(_simple, !s.OverlapMode);   // overlap mode always runs the line across the whole shape
        _refreshing = false;
    }
}

/// <summary>
/// Shadow Context Bar: the shadow's form (Flat, Extruded, Perspective, Cast) and type (Drop or Relief), its shift (Hs, Vs), outline (inches or % of
/// the selection's height), 3D % (Perspective only), corners, color and Apply (the OAT angle and the Preview switch are kept in the tool, not on the bar, to keep it narrow). For a Cast shadow Hs is how far the top edge is pushed
/// sideways and Vs is the change of the shadow's height (a negative Vs larger than the height flips it under the object: a floor shadow).
/// </summary>
public sealed class ShadowContextBar : ContextBarBase, IRefreshable
{
    private static readonly (string Name, DocColor? Color)[] Colors =
    [
        ("Black", DocColor.Black), ("Gray", DocColor.FromHex("#808080")), ("Dark gray", DocColor.FromHex("#404040")),
        ("Light gray", DocColor.FromHex("#C0C0C0")), ("White", DocColor.White), ("Same as object", null),
    ];

    private readonly EditorSession _session;
    private readonly ShadowTool _tool;
    private readonly EffectBarParts.Field _hs, _vs, _outline, _outlinePct, _depth, _vpX, _vpY;
    private readonly ComboBox _type, _form, _corners, _color;
    private readonly Button _vpCenter;
    private readonly DoneCancelButtons _buttons;
    private readonly Control _outlinePctGroup, _depthGroup, _vpGroup;
    private bool _refreshing;

    public ShadowContextBar(EditorSession session, ShadowTool tool)
    {
        _session = session;
        _tool = tool;
        _hs = new EffectBarParts.Field(v => tool.Update(s => s with { ShiftX = Units.FromInches(v) }));
        _vs = new EffectBarParts.Field(v => tool.Update(s => s with { ShiftY = -Units.FromInches(v) }));   // the bar counts up, the document counts down
        _outline = new EffectBarParts.Field(v => tool.Update(s => s with { Outline = Units.FromInches(v) }));
        _outlinePct = new EffectBarParts.Field(v => { if (tool.HasPercent) tool.Update(s => s with { Outline = v / 100 * tool.Height }); });
        _depth = new EffectBarParts.Field(v => tool.Update(s => s with { DepthPercent = Math.Clamp(v, 1, 1000) }));
        _vpX = new EffectBarParts.Field(v => tool.SetHandle(new Vec2(Units.FromInches(v), tool.VanishingPoint.Y)), live: true);
        _vpY = new EffectBarParts.Field(v => tool.SetHandle(new Vec2(tool.VanishingPoint.X, Units.FromInches(v))), live: true);

        _type = new ComboBox { ItemsSource = new[] { "Drop", "Relief" }, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_type, "Drop: the shadow includes the object (a bold, merged silhouette). Relief: the object is cut out of the shadow.");
        _type.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _type.SelectedIndex < 0) return;
            tool.Update(s => s with { Type = _type.SelectedIndex == 1 ? ShadowType.Relief : ShadowType.Drop });
            ContextBars.FocusCanvas?.Invoke();
        };
        _form = new ComboBox { ItemsSource = new[] { "Flat", "Extruded", "Perspective", "Cast" }, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_form, "Flat: the shadow is moved. Extruded: a solid sweep from the object to the moved copy. Perspective: extruded with the far end smaller (3D %). Cast: a copy changed in height and slanted.");
        _form.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _form.SelectedIndex < 0) return;
            tool.SetForm((ShadowForm)_form.SelectedIndex);
            ContextBars.FocusCanvas?.Invoke();
        };
        _corners = new ComboBox { ItemsSource = new[] { "Auto (OAT)", "Miter", "Round" }, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_corners, "Corner type of the shadow's outline. Auto rounds corners sharper than the OAT angle and keeps the others sharp; Miter keeps every corner sharp; Round rounds every corner.");
        _corners.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _corners.SelectedIndex < 0) return;
            var style = _corners.SelectedIndex switch { 1 => CornerStyle.Miter, 2 => CornerStyle.Round, _ => CornerStyle.Auto };
            tool.Update(s => s with { Corners = style });
            ContextBars.FocusCanvas?.Invoke();
        };
        _color = new ComboBox { ItemsSource = Colors.Select(c => c.Name).Append("Custom").ToArray(), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_color, "The shadow's color (black to begin with). \"Same as object\" makes the shadow a copy of the object's fill and stroke.");
        _color.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _color.SelectedIndex < 0 || _color.SelectedIndex >= Colors.Length) return;   // "Custom" only shows a color that is not in the list
            var color = Colors[_color.SelectedIndex].Color;
            tool.Update(s => s with { Color = color });
            ContextBars.FocusCanvas?.Invoke();
        };
        _buttons = new DoneCancelButtons(session, "Make the shadow (Enter)", "Leave without making a shadow");

        _outlinePctGroup = ContextBars.Field("%", _outlinePct.Box);
        _depthGroup = ContextBars.Field("3D %", _depth.Box);
        _vpCenter = PathBarParts.Action("↺", "Put the vanishing point back at the center of each object", () => { tool.ResetVanishingPoint(); ContextBars.FocusCanvas?.Invoke(); });
        // The bar is only so wide: the vanishing point shares one caption, the boxes are narrow and Corners has no caption (its list says what it is).
        _vpX.Box.Width = _vpY.Box.Width = _depth.Box.Width = 54;
        var vpLabel = ContextBars.Label("VP");
        ToolTip.SetTip(vpLabel, "Vanishing point (inches from the left and the top of the page): where the far end of the shadow shrinks toward. Drag the round point on the page too.");
        _vpGroup = ContextBars.Group(vpLabel, _vpX.Box, _vpY.Box, _vpCenter);
        ToolTip.SetTip(_vpX.Box, "Vanishing point, inches from the left of the page");
        ToolTip.SetTip(_vpY.Box, "Vanishing point, inches from the top of the page");
        ToolTip.SetTip(_hs.Box, "Hs: how far the shadow moves to the right (Cast: how far the top edge is pushed sideways)");
        ToolTip.SetTip(_vs.Box, "Vs: how far the shadow moves up (negative: down). Cast: the change of the shadow's height; a negative value larger than the height flips it under the object");
        // The first row is the shift and the outline; the second what kind of shadow, then Done and Cancel at the end. The two lists carry no captions to stay narrow.
        foreach (var c in new Control[]
                 {
                     ContextBars.Field("Hs", _hs.Box, "in"), ContextBars.Field("Vs", _vs.Box, "in"),
                     PathBarParts.Gap(),
                     ContextBars.Field("Outline", _outline.Box, "in"), _outlinePctGroup,
                 })
            Row1.Children.Add(c);
        foreach (var c in new Control[] { _form, _type, _color, _corners, _depthGroup, _vpGroup })
            Row2.Children.Add(c);
        _buttons.AddTo(Row2);
        EffectBarParts.Bind(this, session, Refresh, h => tool.Changed += h, h => tool.Changed -= h);
        Refresh();
    }

    public void Refresh()
    {
        _refreshing = true;
        var s = _tool.Settings;
        _hs.Show(ContextBars.Fmt(Units.ToInches(s.ShiftX)));
        _vs.Show(ContextBars.Fmt(Units.ToInches(s.Vs)));
        _outline.Show(ContextBars.Fmt(Units.ToInches(s.Outline)));
        _outlinePct.Show(_tool.HasPercent ? ContextBars.Fmt(_tool.OutlinePercent) : "");
        // a field that does not apply is not shown (R26): the percentage needs a selection to take a height from, 3D % belongs to Perspective
        _outlinePct.Box.IsEnabled = _outlinePctGroup.IsVisible = _tool.HasPercent;
        _depth.Show(ContextBars.Fmt(s.DepthPercent));
        _depth.Box.IsEnabled = _depthGroup.IsVisible = s.Form == ShadowForm.Perspective;
        var vp = _tool.VanishingPoint;
        _vpX.Show(ContextBars.Fmt(Units.ToInches(vp.X)));
        _vpY.Show(ContextBars.Fmt(Units.ToInches(vp.Y)));
        _vpGroup.IsVisible = _vpX.Box.IsEnabled = _vpY.Box.IsEnabled = s.Form == ShadowForm.Perspective && _tool.HasHandle;
        ContextBars.Valid(_vpCenter, s.Pivot is not null);
        _form.SelectedIndex = (int)s.Form;
        _type.SelectedIndex = (int)s.Type;
        _corners.SelectedIndex = s.Corners switch { CornerStyle.Miter => 1, CornerStyle.Round => 2, _ => 0 };
        int colorAt = Array.FindIndex(Colors, c => c.Color == s.Color);
        _color.SelectedIndex = colorAt >= 0 ? colorAt : Colors.Length;
        _buttons.Refresh(!_session.EffectBusy);
        _refreshing = false;
    }

    // ---- For tests ----

    internal Button Apply => _buttons.Done;   // Done makes the shadow
    internal Button Cancel => _buttons.Cancel;
    internal ComboBox Form => _form;
    internal ComboBox Type => _type;
    internal ComboBox Color => _color;
    internal Control DepthGroup => _depthGroup;
    internal Control OutlinePercentGroup => _outlinePctGroup;
    internal TextBox Hs => _hs.Box;
    internal TextBox Vs => _vs.Box;
    internal TextBox Outline => _outline.Box;
    internal TextBox Depth => _depth.Box;
    internal TextBox VanishingX => _vpX.Box;
    internal TextBox VanishingY => _vpY.Box;
    internal Control VanishingGroup => _vpGroup;
    internal Button VanishingCenter => _vpCenter;
}