using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Shell;

/// <summary>
/// Soft Shadow and Glow Context Bar (one class, the fields that apply to the mode): Hs and Vs, Opacity, Softness, Inset or Thickness, the cast shadow's Slant
/// and Height, the color, Remove and Done. The effect shows on the page as the fields change (the tool re-targets one undo step).
/// Percent fields are the legacy program's fractions: Softness 50 % is 0.068 × 50 % × the source's height of blur.
/// </summary>
public sealed class SoftEffectContextBar : ContextBarBase, IRefreshable
{
    internal static readonly (string Name, DocColor Color)[] Colors =
    [
        ("Black", DocColor.Black), ("Dark gray", DocColor.FromHex("#404040")), ("Gray", DocColor.FromHex("#808080")),
        ("Light gray", DocColor.FromHex("#C0C0C0")), ("White", DocColor.White), ("Yellow", DocColor.FromHex("#FFD400")),
        ("Orange", DocColor.FromHex("#FF8A00")), ("Red", DocColor.FromHex("#E02020")), ("Blue", DocColor.FromHex("#2060E0")),
    ];

    private readonly EditorSession _session;
    private readonly SoftEffectTool _tool;
    private readonly EffectBarParts.Field _hs, _vs, _opacity, _softness, _inset, _thickness, _slant, _height;
    private readonly ComboBox _color;
    private readonly Button _remove;
    private readonly DoneCancelButtons _buttons;
    private readonly Control _hsGroup, _vsGroup, _insetGroup, _thicknessGroup, _slantGroup, _heightGroup;
    private readonly TextBlock _title = PathBarParts.Value();
    private bool _refreshing;

    public SoftEffectContextBar(EditorSession session, SoftEffectTool tool)
    {
        _session = session;
        _tool = tool;
        _hs = new EffectBarParts.Field(v => tool.Update(s => s with { ShiftX = Units.FromInches(v) }));
        _vs = new EffectBarParts.Field(v => tool.Update(s => s with { ShiftY = -Units.FromInches(v) }));   // the bar counts up, the document counts down
        _opacity = new EffectBarParts.Field(v => tool.Update(s => s with { Opacity = Math.Clamp(v, 0, 100) / 100 }));
        _softness = new EffectBarParts.Field(v => tool.Update(s => s with { Softness = Math.Clamp(v, 0, 500) / 100 }));
        _inset = new EffectBarParts.Field(v => tool.Update(s => s with { Inset = v / 100 }));
        _thickness = new EffectBarParts.Field(v => tool.Update(s => s with { Thickness = Math.Max(v, 0) / 100 }));
        _slant = new EffectBarParts.Field(v => tool.Update(s => s with { Slant = v }));
        _height = new EffectBarParts.Field(v => tool.Update(s => s with { HeightScale = Math.Max(v, 1) / 100 }));

        _color = new ComboBox { ItemsSource = Colors.Select(c => c.Name).ToArray(), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_color, "The color of the soft shadow or glow");
        _color.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _color.SelectedIndex < 0) return;
            var color = Colors[_color.SelectedIndex].Color;
            tool.Update(s => s with { Color = color });
            ContextBars.FocusCanvas?.Invoke();
        };
        _remove = PathBarParts.Action("Remove", "Remove the effect (undoable)", () => { tool.Remove(); ContextBars.FocusCanvas?.Invoke(); });
        _buttons = new DoneCancelButtons(session, "Keep the effect (Enter)", "Take back what this tool did (Esc)");

        ToolTip.SetTip(_hs.Box, "Hs: how far the shadow moves to the right");
        ToolTip.SetTip(_vs.Box, "Vs: how far the shadow moves up (negative: down)");
        ToolTip.SetTip(_opacity.Box, "Opacity: 0 = nothing, 100 = the full color. (Ctrl+wheel in the legacy program)");
        ToolTip.SetTip(_softness.Box, "Softness: how far the edge is blurred, as a share of the source's height. About 50 % looks best; more is slower, not more realistic");
        ToolTip.SetTip(_inset.Box, "Inset: pulls the shadow's outline in (negative: pushes it out), as a share of the height");
        ToolTip.SetTip(_thickness.Box, "Thickness: how far the glow's shape grows before it is blurred");
        ToolTip.SetTip(_slant.Box, "Slant: a cast shadow, leaning this many degrees from vertical");
        ToolTip.SetTip(_height.Box, "Height: a cast shadow's height compared to the source (100 % = the same)");

        _hsGroup = ContextBars.Field("Hs", _hs.Box, "in");
        _vsGroup = ContextBars.Field("Vs", _vs.Box, "in");
        _insetGroup = ContextBars.Field("Inset", _inset.Box, "%");
        _thicknessGroup = ContextBars.Field("Thickness", _thickness.Box, "%");
        _slantGroup = ContextBars.Field("Slant", _slant.Box, "°");
        _heightGroup = ContextBars.Field("Height", _height.Box, "%");
        foreach (var c in new Control[]
                 {
                     _title, _hsGroup, _vsGroup, ContextBars.Field("Opacity", _opacity.Box, "%"), ContextBars.Field("Softness", _softness.Box, "%"),
                     _insetGroup, _thicknessGroup,
                 })
            Row1.Children.Add(c);
        foreach (var c in new Control[]
                 {
                     _slantGroup, _heightGroup, ContextBars.Group(ContextBars.Label("Color"), _color),
                     PathBarParts.Gap(), _remove,
                 })
            Row2.Children.Add(c);
        _buttons.AddTo(Row2);
        EffectBarParts.Bind(this, session, Refresh, h => tool.Changed += h, h => tool.Changed -= h);
        Refresh();
    }

    public void Refresh()
    {
        _refreshing = true;
        var s = _tool.Settings;
        bool glow = _tool.Kind == SoftKind.Glow;
        _title.Text = SoftEffect.NameOf(_tool.Kind);
        _hs.Show(ContextBars.Fmt(Units.ToInches(s.ShiftX)));
        _vs.Show(ContextBars.Fmt(Units.ToInches(s.Vs)));
        _opacity.Show(ContextBars.Fmt(s.Opacity * 100));
        _softness.Show(ContextBars.Fmt(s.Softness * 100));
        _inset.Show(ContextBars.Fmt(s.Inset * 100));
        _thickness.Show(ContextBars.Fmt(s.Thickness * 100));
        _slant.Show(ContextBars.Fmt(s.Slant));
        _height.Show(ContextBars.Fmt(s.HeightScale * 100));
        // a field that does not apply is not shown (R26): a glow has no shift, inset, slant or height; a soft shadow has no thickness
        foreach (var (box, group, on) in new[]
                 {
                     (_hs.Box, _hsGroup, !glow), (_vs.Box, _vsGroup, !glow), (_inset.Box, _insetGroup, !glow), (_slant.Box, _slantGroup, !glow),
                     (_height.Box, _heightGroup, !glow), (_thickness.Box, _thicknessGroup, glow),
                 })
            box.IsEnabled = group.IsVisible = on;
        _color.SelectedIndex = Array.FindIndex(Colors, c => c.Color.R == s.Color.R && c.Color.G == s.Color.G && c.Color.B == s.Color.B);
        ContextBars.Valid(_remove, _tool.Effects.Count > 0);
        _buttons.Refresh();
        _refreshing = false;
    }

    // ---- For tests ----

    internal TextBox OpacityBox => _opacity.Box;
    internal TextBox Softness => _softness.Box;
    internal TextBox Hs => _hs.Box;
    internal TextBox Vs => _vs.Box;
    internal TextBox Thickness => _thickness.Box;
    internal Control ThicknessGroup => _thicknessGroup;
    internal Control HsGroup => _hsGroup;
    internal ComboBox Color => _color;
    internal Button Remove => _remove;
    internal Button Done => _buttons.Done;
    internal Button Cancel => _buttons.Cancel;
}

/// <summary>Feather Context Bar: Feather and Inset (percent), Remove, Done. The edge fades on the page as the fields change.</summary>
public sealed class FeatherContextBar : ContextBarBase, IRefreshable
{
    private readonly FeatherTool _tool;
    private readonly EffectBarParts.Field _amount, _inset;
    private readonly Button _remove;
    private readonly DoneCancelButtons _buttons;

    public FeatherContextBar(EditorSession session, FeatherTool tool)
    {
        _tool = tool;
        _buttons = new DoneCancelButtons(session, "Keep the feather (Enter)", "Take back what this tool did (Esc)");
        _amount = new EffectBarParts.Field(v => tool.Update(f => f with { Amount = Math.Clamp(v, 0, 100) / 100 }));
        _inset = new EffectBarParts.Field(v => tool.Update(f => f with { Inset = Math.Clamp(v, 0, 200) / 100 }));
        ToolTip.SetTip(_amount.Box, "Feather: how wide the soft edge is, as a share of the object's smaller side (a text's height). 0 removes the feather; more than 20 % is slow");
        ToolTip.SetTip(_inset.Box, "Inset: moves the fade in from the edge, so a hard-edged picture loses its edge");
        _remove = PathBarParts.Action("Remove", "Remove the feather (undoable)", () => { tool.Remove(); ContextBars.FocusCanvas?.Invoke(); });
        foreach (var c in new Control[] { ContextBars.Label("Feather"), ContextBars.Field("Feather", _amount.Box, "%"), ContextBars.Field("Inset", _inset.Box, "%") })
            Row1.Children.Add(c);
        Row2.Children.Add(_remove);
        _buttons.AddTo(Row2);
        foreach (var c in new Control[] { PathBarParts.Gap(), ContextBars.Hint("Drag on the object to set the feather from how far you move.") })
            Row2.Children.Add(c);
        EffectBarParts.Bind(this, session, Refresh, h => tool.Changed += h, h => tool.Changed -= h);
        Refresh();
    }

    public void Refresh()
    {
        _amount.Show(ContextBars.Fmt(_tool.Settings.Amount * 100));
        _inset.Show(ContextBars.Fmt(_tool.Settings.Inset * 100));
        ContextBars.Valid(_remove, _tool.IsActiveOnTarget);
        _buttons.Refresh();
    }

    // ---- For tests ----

    internal TextBox Amount => _amount.Box;
    internal TextBox Inset => _inset.Box;
    internal Button Remove => _remove;
    internal Button Done => _buttons.Done;
    internal Button Cancel => _buttons.Cancel;
}
