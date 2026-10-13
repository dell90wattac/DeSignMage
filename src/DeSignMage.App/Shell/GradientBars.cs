using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Shell;

/// <summary>
/// Gradient Blend Context Bar. First row: the gradient type (Flat, Linear, Vertical, Horizontal, Circle, Ellipse), Alternate Colors and Reflect, and
/// which color is picked. Second row: the saved gradients with Add, Update and Delete, Remove (back to a plain fill) and Done. Only what works now
/// is shown (R26): without a gradient there are no flags and no Remove; Update and Delete need a saved gradient picked; with nothing suitable
/// selected only the hint and Done are left.
/// </summary>
public sealed class GradientContextBar : ContextBarBase, IRefreshable
{
    private readonly EditorSession _session;
    private readonly GradientTool _tool;
    private readonly GradientPresets _presets;
    private readonly ComboBox _type, _presetBox;
    private readonly CheckBox _alternate, _reflect;
    private readonly TextBlock _node = PathBarParts.Value(), _hint;
    private readonly Control _typeGroup, _presetGroup;
    private readonly Button _add, _update, _delete, _remove, _done;
    private bool _refreshing, _presetsDirty = true;

    public GradientContextBar(EditorSession session, GradientTool tool, GradientPresets? presets = null)
    {
        _session = session;
        _tool = tool;
        _presets = presets ?? GradientPresets.Default;

        _type = new ComboBox { ItemsSource = new[] { "Flat", "Linear", "Vertical", "Horizontal", "Circle", "Ellipse" }, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_type, "How the colors blend. Flat is one plain color. Vertical and Horizontal are Linear with the line locked to an axis. Circle and Ellipse blend outward from a center.");
        _type.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _type.SelectedIndex < 0) return;
            if (_type.SelectedIndex == 0) tool.Remove();
            else tool.SetKind((GradientKind)(_type.SelectedIndex - 1));
            ContextBars.FocusCanvas?.Invoke();
        };
        _alternate = PathBarParts.Check("Alternate Colors", "Blend the long way round the color wheel (a rainbow) instead of the short way", v => { if (!_refreshing) tool.SetAlternateColors(v); });
        _reflect = PathBarParts.Check("Reflect", "Run the colors to the middle of the line and back", v => { if (!_refreshing) tool.SetReflect(v); });

        _presetBox = new ComboBox
        {
            VerticalAlignment = VerticalAlignment.Center, MinWidth = 120, PlaceholderText = "Saved gradients",
            ItemTemplate = new FuncDataTemplate<GradientPaint>((g, _) =>
                g is null ? new Panel() : new Border { Width = 96, Height = 16, Background = ColorUi.PreviewBrush(g), BorderBrush = AppTheme.Brush("Ui.SwatchBorder"), BorderThickness = new Avalonia.Thickness(1) }),
        };
        ToolTip.SetTip(_presetBox, "Saved gradients: pick one to put it on the selected object");
        _presetBox.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _presetBox.SelectedItem is not GradientPaint g) return;
            tool.SetGradient(g);
            ContextBars.FocusCanvas?.Invoke();
        };
        _add = PathBarParts.Action("Add", "Save this gradient in the list", () =>
        {
            if (tool.Gradient is { } g) { _presets.Add(g); _presetBox.SelectedIndex = _presets.Items.Count - 1; }
        });
        _update = PathBarParts.Action("Update", "Replace the picked saved gradient with this one", () =>
        {
            if (tool.Gradient is { } g && _presetBox.SelectedIndex >= 0) { int at = _presetBox.SelectedIndex; _presets.Update(at, g); _presetBox.SelectedIndex = at; }
        });
        _delete = PathBarParts.Action("Delete", "Remove the picked gradient from the saved list", () =>
        {
            if (_presetBox.SelectedIndex >= 0) _presets.Delete(_presetBox.SelectedIndex);
        });
        _remove = PathBarParts.Action("Remove", "Take the gradient off: the fill becomes its first color", () => { tool.Remove(); ContextBars.FocusCanvas?.Invoke(); });
        _done = PathBarParts.Action("✓ Done", "Leave the Gradient Blend tool (Esc)", () => { session.SetTool("select"); ContextBars.FocusCanvas?.Invoke(); });

        _hint = ContextBars.Hint("Select one shape or text, then pick a type to blend its fill.");
        _typeGroup = ContextBars.Group(ContextBars.Label("Gradient"), _type);
        _presetGroup = ContextBars.Group(_presetBox);

        foreach (var c in new Control[] { _typeGroup, _alternate, _reflect, ContextBars.Separator(), _node, _hint })
            Row1.Children.Add(c);
        foreach (var c in new Control[] { _presetGroup, _add, _update, _delete, ContextBars.Separator(), _remove, _done })
            Row2.Children.Add(c);

        _presets.Changed += OnPresetsChanged;
        DetachedFromVisualTree += (_, _) => _presets.Changed -= OnPresetsChanged;
        EffectBarParts.Bind(this, session, Refresh, h => tool.Changed += h, h => tool.Changed -= h);
        Refresh();
    }

    private void OnPresetsChanged(object? sender, EventArgs e)
    {
        _presetsDirty = true;
        Refresh();
    }

    public void Refresh()
    {
        _refreshing = true;
        var g = _tool.Gradient;
        bool target = _tool.Target is not null;

        ContextBars.Valid(_typeGroup, target);
        _type.SelectedIndex = g is null ? 0 : 1 + (int)g.Kind;
        ContextBars.Valid(_alternate, g is not null);
        ContextBars.Valid(_reflect, g is not null);
        _alternate.IsChecked = g?.AlternateColors == true;
        _reflect.IsChecked = g?.Reflect == true;

        _node.IsVisible = g is not null;
        _node.Text = g is null ? "" : $"Color {Math.Clamp(_tool.CurrentNode, 0, g.Stops.Count - 1) + 1} of {g.Stops.Count}";
        _hint.IsVisible = g is null;
        _hint.Text = target ? "Pick a type to blend this fill." : "Select one shape or text, then pick a type to blend its fill.";

        if (_presetsDirty)
        {
            _presetsDirty = false;
            int picked = Math.Min(_presetBox.SelectedIndex, _presets.Items.Count - 1);
            _presetBox.ItemsSource = _presets.Items.ToList();
            _presetBox.SelectedIndex = picked;
        }
        ContextBars.Valid(_presetGroup, target && _presets.Items.Count > 0);
        ContextBars.Valid(_add, g is not null);
        ContextBars.Valid(_update, g is not null && _presetBox.SelectedIndex >= 0);
        ContextBars.Valid(_delete, _presetBox.SelectedIndex >= 0 && target);
        ContextBars.Valid(_remove, g is not null);
        _refreshing = false;
    }

    // ---- For tests ----

    internal ComboBox TypeCombo => _type;
    internal CheckBox Alternate => _alternate;
    internal CheckBox Reflect => _reflect;
    internal ComboBox PresetCombo => _presetBox;
    internal Button AddPreset => _add;
    internal Button UpdatePreset => _update;
    internal Button DeletePreset => _delete;
    internal Button RemoveButton => _remove;
    internal Button DoneButton => _done;
    internal TextBlock NodeText => _node;
}
