using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;
using TextAlignment = DeSignMage.Core.Model.TextAlignment;

namespace DeSignMage.App.Shell;

/// <summary>A family in the font picker. <see cref="Missing"/> marks a name the document asks for that isn't installed.</summary>
public sealed record FontItem(string Name, bool Missing)
{
    public override string ToString() => Missing ? $"{Name} (missing)" : Name;
}

/// <summary>
/// Font controls for live text (family, style, size, spacing, alignment, Convert to Curves). They act on the text in
/// the selection (also text inside selected groups) as one undo step, or set what new text starts with when no text
/// is selected. A requested font that isn't installed shows as "(missing)" in red while the object keeps its name.
/// With <c>twoRows</c> the font, size and spacing are on the first row and the alignment, Convert to Curves and the note on the
/// second; without it everything flows in one row (inside the selection bar's second row).
/// </summary>
public sealed class TextFormatPanel : StackPanel, IRefreshable
{
    private readonly EditorSession _session;
    private readonly FontPicker _family = new();
    private readonly ComboBox _style = new ComboBox { Padding = new(6, 1) }.ScaledWidth(120).ScaledMinHeight(22);
    private readonly TextBox _size, _tracking;
    private readonly ToggleButton _left, _center, _right;
    private readonly Button _convert;
    private readonly TextBlock _note = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Ui.Error"), Margin = new(6, 0) };
    private bool _refreshing, _edited;

    internal FontPicker FamilyBox => _family;
    internal ComboBox StyleBox => _style;
    internal TextBox SizeBox => _size;

    public TextFormatPanel(EditorSession session, bool twoRows = true)
    {
        _session = session;
        this.Spaced(SpacingProperty, ContextBars.LineSpacing);

        // The list shows each font in its own font; with text selected, that text in each font (R12).
        _family.SampleProvider = () => _session.SelectedTexts().FirstOrDefault()?.Text;
        _family.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _family.SelectedItem is not FontItem { Missing: false } f) return;
            Apply(new TextFormatChange(Family: f.Name));
            ContextBars.FocusCanvas?.Invoke();
        };
        _style.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<FontStyleInfo>((s, _) => new TextBlock { Text = s.Name });
        _style.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _style.SelectedItem is not FontStyleInfo s) return;
            Apply(new TextFormatChange(Weight: s.Weight, Italic: s.Italic));
            ContextBars.FocusCanvas?.Invoke();
        };

        _size = ContextBars.NumberBox(ApplyNumbers);
        _tracking = ContextBars.NumberBox(ApplyNumbers);
        foreach (var box in new[] { _size, _tracking }) box.TextChanged += (_, _) => { if (!_refreshing) _edited = true; };

        _left = AlignButton("", "Align left", TextAlignment.Left);
        _center = AlignButton("", "Center", TextAlignment.Center);
        _right = AlignButton("", "Align right", TextAlignment.Right);

        _convert = ContextBars.Action("Convert to Curves", () => _session.ConvertSelectionToCurves(),
            "Turn the text into ordinary vector shapes (the font can't be changed afterwards)");

        var top = ContextBars.NewRow();
        var bottom = twoRows ? ContextBars.NewRow() : top;
        top.Children.Add(ContextBars.Group(ContextBars.Label("Font"), _family, _style));
        top.Children.Add(ContextBars.Field("Size", _size, "pt"));
        top.Children.Add(ContextBars.Field("Spacing", _tracking, "%"));
        if (!twoRows) top.Children.Add(ContextBars.Separator());
        bottom.Children.Add(ContextBars.Group(_left, _center, _right));
        bottom.Children.Add(ContextBars.Separator());
        bottom.Children.Add(_convert);
        bottom.Children.Add(_note);
        Children.Add(top);
        if (twoRows) Children.Add(bottom);
    }
    private ToggleButton AlignButton(string glyph, string tip, TextAlignment alignment)
    {
        var g = new TextBlock { Text = glyph };
        g.Classes.Add("glyph");
        var b = new ToggleButton { Content = g, Focusable = false }.ScaledMinWidth(26, icon: true).ScaledMinHeight(24, icon: true);
        b.Classes.Add("tb");
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) =>
        {
            if (!_refreshing) Apply(new TextFormatChange(Alignment: alignment));
            Refresh();
        };
        return b;
    }

    private bool Apply(TextFormatChange change)
    {
        bool changed = _session.ApplyTextFormat(change);
        Refresh();
        return changed;
    }

    private void ApplyNumbers()
    {
        if (_refreshing || !_edited) return;
        _edited = false;
        double? size = null, tracking = null;
        if (ContextBars.TryParse(_size, out var s) && s > 0.1) size = s;
        if (ContextBars.TryParse(_tracking, out var k)) tracking = k / 100;
        Apply(new TextFormatChange(Size: size, Tracking: tracking));
    }

    /// <summary>What the controls show: the first selected text, or the defaults for new text.</summary>
    private (string Family, int Weight, bool Italic, double Size, double Tracking, TextAlignment Alignment, bool Missing, string Resolved) Current()
    {
        var texts = _session.SelectedTexts();
        if (texts.Count > 0)
        {
            var t = texts[0];
            return (t.FontFamily, t.FontWeight, t.Italic, t.FontSize, t.Tracking, t.Alignment, t.IsFontMissing, t.ResolvedFontFamily);
        }
        var d = _session.TextDefaults;
        var r = TextEngine.Fonts.Resolve(d.Family, d.Weight, d.Italic);
        return (d.Family, d.Weight, d.Italic, d.Size, d.Tracking, d.Alignment, r.Missing, r.Family);
    }

    public void Refresh()
    {
        _refreshing = true;
        var cur = Current();
        var fonts = TextEngine.Fonts;

        // The font list itself is built when the drop-down opens; here only the box is set. A name that isn't an installed family
        // (missing: shown in red; or a legacy name like "Segoe UI Semibold" that resolves to one) is listed first when it opens.
        _family.SelectedItem = new FontItem(cur.Family, cur.Missing);

        var styles = fonts.StylesOf(cur.Missing ? cur.Resolved : cur.Family);
        if (!ReferenceEquals(_style.ItemsSource, styles)) _style.ItemsSource = styles;
        _style.SelectedItem = styles.Count == 0 ? null
            : styles.OrderBy(s => (s.Italic == cur.Italic ? 0 : 1000) + Math.Abs(s.Weight - cur.Weight)).First();

        if (!_size.IsFocused) _size.Text = cur.Size.ToString("0.##", CultureInfo.InvariantCulture);
        if (!_tracking.IsFocused) _tracking.Text = (cur.Tracking * 100).ToString("0.##", CultureInfo.InvariantCulture);
        _left.IsChecked = cur.Alignment is TextAlignment.Left or TextAlignment.Justify;
        _center.IsChecked = cur.Alignment == TextAlignment.Center;
        _right.IsChecked = cur.Alignment == TextAlignment.Right;
        _convert.IsEnabled = _session.CanConvertToCurves;
        _note.Text = cur.Missing ? $"Font not installed: showing {cur.Resolved}" : "";
        _edited = false;
        _refreshing = false;
    }
}

/// <summary>The text tool's Context Bar: the font controls on two rows (the status line explains how to type).</summary>
public sealed class TextToolContextBar : StackPanel, IRefreshable
{
    private readonly TextFormatPanel _panel;

    public TextToolContextBar(EditorSession session)
    {
        _panel = new TextFormatPanel(session);
        Children.Add(_panel);
        _panel.Refresh();
    }

    internal TextFormatPanel Panel => _panel;

    public void Refresh() => _panel.Refresh();
}