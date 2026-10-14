using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Shell;

/// <summary>
/// Easy Bevel Context Bar: Done, Cancel and Remove; Inset, Width and Softness; the Light direction (degrees; also a round point on the page); a preset; the Highlight and Shadow strength (a Shadow of 0 leaves the shadow crescent
/// out); the two colors (hidden while Neutral, which makes them white and black); Neutral. The bevel shows on the page as the fields change (the tool re-targets
/// one undo step). All the percentages are shares of the object's own height.
/// </summary>
public sealed class BevelContextBar : ContextBarBase, IRefreshable
{
    private readonly EditorSession _session;
    private readonly BevelTool _tool;
    private readonly EffectBarParts.Field _inset, _width, _softness, _highlight, _shadow, _light;
    private readonly ComboBox _preset, _highlightColor, _shadowColor;
    private readonly CheckBox _neutral;
    private readonly Button _remove, _done;
    private bool _refreshing;

    public BevelContextBar(EditorSession session, BevelTool tool)
    {
        _session = session;
        _tool = tool;
        _inset = new EffectBarParts.Field(v => tool.Update(s => s with { Inset = v / 100 }));
        _width = new EffectBarParts.Field(v => tool.Update(s => s with { Width = v / 100 }));
        _softness = new EffectBarParts.Field(v => tool.Update(s => s with { Softness = v / 100 }));
        _highlight = new EffectBarParts.Field(v => tool.Update(s => s with { Highlight = v / 100 }));
        _shadow = new EffectBarParts.Field(v => tool.Update(s => s with { Shadow = v / 100, IncludeShadow = v > 0 }));
        _light = new EffectBarParts.Field(v => tool.Update(s => s with { LightAngle = v }), live: true);

        _preset = new ComboBox { ItemsSource = new[] { "Custom" }.Concat(BevelPresets.All.Select(p => p.Name)).ToArray(), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_preset, "A ready-made width, softness and strength");
        _preset.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _preset.SelectedIndex <= 0) return;
            var preset = BevelPresets.All[_preset.SelectedIndex - 1];
            tool.Update(s => preset.ApplyTo(s));
            ContextBars.FocusCanvas?.Invoke();
        };
        _highlightColor = ColorList("The highlight's color", c => tool.Update(s => s with { HighlightColor = c }));
        _shadowColor = ColorList("The shadow's color", c => tool.Update(s => s with { ShadowColor = c }));
        _neutral = PathBarParts.Check("Neutral", "The highlight is white and the shadow black, whatever colors are chosen", v => { if (!_refreshing) tool.Update(s => s with { Neutral = v }); });
        _remove = PathBarParts.Action("Remove", "Remove the bevel (undoable)", () => { tool.Remove(); ContextBars.FocusCanvas?.Invoke(); });
        _done = PathBarParts.Action("✓ Done", "Keep the bevel (Enter)", () => { _ = tool.Done(); ContextBars.FocusCanvas?.Invoke(); });
        var cancel = PathBarParts.Action("✕ Cancel", "Take back what this tool did (Esc)", () => { tool.Abandon(); ContextBars.FocusCanvas?.Invoke(); });

        // the bar is only so wide: narrower number boxes (three digits at most) leave room for the Light field, and Remove sits at the end of the second row
        foreach (var box in new[] { _inset.Box, _width.Box, _softness.Box, _light.Box, _highlight.Box, _shadow.Box }) box.Width = 54;
        ToolTip.SetTip(_inset.Box, "Inset: how far in from the edge the bevel starts, as a share of the height");
        ToolTip.SetTip(_width.Box, "Width: how wide the light and dark rims are, as a share of the height");
        ToolTip.SetTip(_softness.Box, "Softness: how soft the rims' edges are. 0 to about 20 % is useful");
        ToolTip.SetTip(_highlight.Box, "Highlight: how strong the light rim is (100 % = fully white)");
        ToolTip.SetTip(_shadow.Box, "Shadow: how strong the dark rim is. 0 leaves the shadow out");
        ToolTip.SetTip(_light.Box, "Light: the direction the light comes from, in degrees counter-clockwise from the right (90 = above, 135 = upper left). Drag the round point on the page to turn it");

        foreach (var c in new Control[]
                 {
                     _done, cancel, PathBarParts.Gap(),
                     ContextBars.Field("Inset", _inset.Box, "%"), ContextBars.Field("Width", _width.Box, "%"), ContextBars.Field("Softness", _softness.Box, "%"),
                     ContextBars.Field("Light", _light.Box, "°"),
                 })
            Row1.Children.Add(c);
        foreach (var c in new Control[]
                 {
                     _preset, ContextBars.Field("Highlight", _highlight.Box, "%"), ContextBars.Field("Shadow", _shadow.Box, "%"),
                     _highlightColor, _shadowColor, _neutral, _remove,
                 })
            Row2.Children.Add(c);
        EffectBarParts.Bind(this, session, Refresh, h => tool.Changed += h, h => tool.Changed -= h);
        Refresh();
    }

    private ComboBox ColorList(string tip, Action<DocColor> set)
    {
        var box = new ComboBox { ItemsSource = SoftEffectContextBar.Colors.Select(c => c.Name).Append("Custom").ToArray(), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(box, tip);
        box.SelectionChanged += (_, _) =>
        {
            if (_refreshing || box.SelectedIndex < 0 || box.SelectedIndex >= SoftEffectContextBar.Colors.Length) return;   // "Custom" only shows a color that is not in the list
            set(SoftEffectContextBar.Colors[box.SelectedIndex].Color);
            ContextBars.FocusCanvas?.Invoke();
        };
        return box;
    }

    private static int IndexOf(DocColor c)
    {
        int i = Array.FindIndex(SoftEffectContextBar.Colors, x => x.Color.R == c.R && x.Color.G == c.G && x.Color.B == c.B);
        return i >= 0 ? i : SoftEffectContextBar.Colors.Length;
    }

    public void Refresh()
    {
        _refreshing = true;
        var s = _tool.Settings;
        _inset.Show(ContextBars.Fmt(s.Inset * 100));
        _width.Show(ContextBars.Fmt(s.Width * 100));
        _softness.Show(ContextBars.Fmt(s.Softness * 100));
        _highlight.Show(ContextBars.Fmt(s.Highlight * 100));
        _shadow.Show(ContextBars.Fmt(s.IncludeShadow ? s.Shadow * 100 : 0));
        _light.Show(ContextBars.Fmt(s.LightAngle));
        var preset = BevelPresets.Find(s);
        _preset.SelectedIndex = preset is null ? 0 : BevelPresets.All.ToList().IndexOf(preset) + 1;
        _highlightColor.SelectedIndex = IndexOf(s.HighlightColor);
        _shadowColor.SelectedIndex = IndexOf(s.ShadowColor);
        _neutral.IsChecked = s.Neutral;
        // the colors do not apply while Neutral (R26); Remove only when there is a bevel to remove; Done only with something to keep
        ContextBars.Valid(_highlightColor, !s.Neutral);
        ContextBars.Valid(_shadowColor, !s.Neutral);
        ContextBars.Valid(_remove, _tool.CanRemove);
        ContextBars.Valid(_done, _tool.IsActiveOnSource);
        _refreshing = false;
    }

    // ---- For tests ----

    internal Button Done => _done;
    internal Button Remove => _remove;
    internal TextBox WidthBox => _width.Box;
    internal TextBox ShadowBox => _shadow.Box;
    internal TextBox SoftnessBox => _softness.Box;
    internal TextBox LightBox => _light.Box;
    internal ComboBox Preset => _preset;
    internal ComboBox HighlightColor => _highlightColor;
    internal ComboBox ShadowColor => _shadowColor;
    internal CheckBox Neutral => _neutral;
}