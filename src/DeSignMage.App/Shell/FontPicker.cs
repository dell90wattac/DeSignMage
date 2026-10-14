using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace DeSignMage.App.Shell;

/// <summary>
/// The font family drop-down of the text Context Bar (R12). A box showing the current font; its list opens in a popup with a size
/// slider on top. Every row shows the font's name drawn in that font (symbol fonts also get a plain-font label), larger than menu text
/// (default 22 px, 12-48 with the slider, remembered, and relative to the interface text size). When text is selected, each row
/// shows the beginning of that text in the font instead, with the font's name in small print.
/// The list is virtualized (only the rows on screen exist) and the font names come from <see cref="FontCatalog"/>, so it opens at
/// once however many fonts are installed. Choosing a row raises <see cref="SelectionChanged"/>; looking never changes anything.
/// </summary>
public sealed class FontPicker : Grid
{
    public const double MinListSize = 12, MaxListSize = 48, DefaultListSize = 22;
    private const string SizeSetting = "ui.fontListSize";

    private readonly Button _button = new() { Name = "FontPickerButton", HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 1) };
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Popup _popup;
    private readonly ListBox _list = new() { Name = "FontList", SelectionMode = SelectionMode.Single };
    private readonly Slider _sizeSlider;
    private readonly TextBlock _sizeText = new() { Name = "FontListSizeText", VerticalAlignment = VerticalAlignment.Center, MinWidth = 48, TextAlignment = TextAlignment.Right };
    private FontItem? _selected;
    private string? _sample;
    private double _listSize;
    private readonly bool _inline;

    /// <summary>Raised after the user picked a row (not when the program sets <see cref="SelectedItem"/>).</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Gives the text to show in each row (the selected text), or null for "show the font names". Asked each time the list opens.</summary>
    public Func<string?>? SampleProvider { get; set; }

    /// <param name="inline">
    /// Shows the list (with its size slider) directly in the control instead of behind the drop-down box, for dialogs such as
    /// Text ▸ Change Font. Call <see cref="Open"/> once to fill it; picking a row raises <see cref="SelectionChanged"/> and nothing closes.
    /// </param>
    public FontPicker(bool inline = false)
    {
        _inline = inline;
        if (!inline) this.ScaledWidth(210);
        _button.ScaledMinHeight(22);
        _button.HorizontalAlignment = HorizontalAlignment.Stretch;
        var arrow = new TextBlock { Text = "▾", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Foreground = AppTheme.Brush("Ui.Muted") };
        var content = new DockPanel();
        DockPanel.SetDock(arrow, Dock.Right);
        content.Children.Add(arrow);
        content.Children.Add(_label);
        _button.Content = content;
        _button.Click += (_, _) => { if (_popup!.IsOpen) _popup.IsOpen = false; else Open(); };

        _listSize = Math.Clamp(AppSettings.Store.Get(SizeSetting, DefaultListSize), MinListSize, MaxListSize);
        _sizeSlider = new Slider
        {
            Name = "FontListSizeSlider", Minimum = MinListSize, Maximum = MaxListSize, Value = _listSize, SmallChange = 1, LargeChange = 4,
            TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center,
        }.ScaledMinWidth(160);
        _sizeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _listSize = _sizeSlider.Value;
            AppSettings.Store.Set(SizeSetting, _listSize);
            ApplyRowSize();
        };

        var header = new DockPanel { Margin = new Thickness(8, 6, 8, 4), LastChildFill = true };
        var caption = new TextBlock { Text = "List size", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(caption, Dock.Left);
        DockPanel.SetDock(_sizeText, Dock.Right);
        header.Children.Add(caption);
        header.Children.Add(_sizeText);
        header.Children.Add(_sizeSlider);

        // A tight row: the row's own Border sets the height, so the list can lay rows out without measuring them one by one.
        _list.Classes.Add("fontlist");
        _list.Padding = new Thickness(0);
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem is FontItem f) { Choose(f); e.Handled = true; }
            else if (e.Key == Key.Escape) { _popup!.IsOpen = false; e.Handled = true; }
        };
        _list.ScaledHeight(360);

        var root = new Border
        {
            Background = AppTheme.Brush("Ui.PopupBack"), BorderBrush = AppTheme.Brush("Ui.PopupBorder"), BorderThickness = new Thickness(1),
            Child = new DockPanel { Children = { header, _list } },
        }.ScaledMinWidth(380);
        DockPanel.SetDock(header, Dock.Top);

        _popup = new Popup
        {
            PlacementTarget = _button, Placement = PlacementMode.BottomEdgeAlignedLeft, IsLightDismissEnabled = true, Child = inline ? null : root,
        };
        _popup.Closed += (_, _) => ContextBars.FocusCanvas?.Invoke();

        if (inline)
        {
            Children.Add(root);
        }
        else
        {
            Children.Add(_button);
            Children.Add(_popup);
        }
        ShowCurrent();
        ApplyRowSize();
        UiScale.Changed += OnScaleChanged;
        DetachedFromVisualTree += (_, _) => UiScale.Changed -= OnScaleChanged;
        AttachedToVisualTree += (_, _) => { UiScale.Changed -= OnScaleChanged; UiScale.Changed += OnScaleChanged; };
    }

    private void OnScaleChanged() { if (_popup.IsOpen || _inline) ApplyRowSize(); }

    // ---- Public surface (what the text bar and the tests use) ----

    /// <summary>The font shown in the box. Set by the program (no event) and by <see cref="Choose"/>.</summary>
    public FontItem? SelectedItem
    {
        get => _selected;
        set
        {
            _selected = value;
            ShowCurrent();
        }
    }

    public bool IsOpen => _popup.IsOpen;
    internal ListBox List => _list;
    internal Slider SizeSlider => _sizeSlider;
    internal Button Button => _button;
    internal Popup Popup => _popup;
    /// <summary>The text shown in the rows, or null when they show font names (valid while the list is open).</summary>
    internal string? Sample => _sample;
    /// <summary>The rows' font size in px right now.</summary>
    internal double RowFontSize => _listSize * UiScale.Scale;
    public double ListSize => _listSize;

    /// <summary>The fonts the list shows right now (the extra name first, then the installed ones).</summary>
    public IReadOnlyList<FontItem> Items => _list.ItemsSource as IReadOnlyList<FontItem> ?? FontCatalog.Installed;

    public void Open()
    {
        _sample = Sample1(SampleProvider?.Invoke());
        var installed = FontCatalog.Installed;
        // A name the document uses that is not an installed family (a missing font, or a legacy name like "Segoe UI Semibold") is listed first.
        IReadOnlyList<FontItem> items = _selected is { } s && !FontCatalog.IsListed(s.Name) ? [s, .. installed] : installed;
        _list.ItemsSource = items;
        ApplyRowSize();
        if (!_inline) _popup.IsOpen = true;
        var current = items.FirstOrDefault(f => string.Equals(f.Name, _selected?.Name, StringComparison.OrdinalIgnoreCase));
        _list.SelectedItem = current;
        if (current is not null)
            Dispatcher.UIThread.Post(() => _list.ScrollIntoView(current), DispatcherPriority.Loaded);
    }

    public void Close() => _popup.IsOpen = false;

    /// <summary>Picks a font as if the user clicked its row.</summary>
    public void Choose(FontItem item)
    {
        _selected = item;
        ShowCurrent();
        _popup.IsOpen = false;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Rows ----

    /// <summary>The first ~20 characters of the first line, or null if there is no usable text.</summary>
    internal static string? Sample1(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Split('\n', 2)[0].Trim('\r', ' ', '\t');
        if (line.Length == 0) return null;
        return line.Length > 20 ? line[..20] + "…" : line;
    }

    private void ShowCurrent()
    {
        var item = _selected;
        _label.Text = item?.ToString() ?? "";
        _label.FontStyle = item is { Missing: true } ? FontStyle.Italic : FontStyle.Normal;
        if (item is { Missing: true }) _label.Foreground = AppTheme.Brush("Ui.Error"); else _label.ClearValue(TextBlock.ForegroundProperty);
        _label.FontFamily = item is { Missing: false } && !FontCatalog.IsSymbol(item.Name) ? FontCatalog.FamilyFor(item.Name) : FontFamily.Default;
    }

    private void ApplyRowSize()
    {
        _sizeText.Text = $"{Math.Round(RowFontSize):0} px";
        // A new template object makes the list rebuild the rows that are on screen (a handful), at the new size.
        double fontPx = RowFontSize, smallPx = UiScale.BaseSmallFontSize * UiScale.Scale;
        bool preview = _sample is not null;
        double height = Math.Ceiling(fontPx * 1.35 + (preview ? smallPx * 1.4 : 0) + 6);
        _list.ItemTemplate = new FuncDataTemplate<FontItem>((item, _) => item is null ? new Panel() : BuildRow(item, fontPx, smallPx, height));
    }

    private Control BuildRow(FontItem item, double fontPx, double smallPx, double height)
    {
        var row = new Border { Height = height, Padding = new Thickness(8, 0), Background = Brushes.Transparent };
        row.Tapped += (_, e) => { Choose(item); e.Handled = true; };
        var family = item.Missing ? FontFamily.Default : FontCatalog.FamilyFor(item.Name);
        bool symbol = !item.Missing && FontCatalog.IsSymbol(item.Name);

        if (_sample is { } sample && !item.Missing)
        {
            // Your own text in this font, with the font's name below in small print (in the normal font, so it is always readable).
            row.Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = sample, FontFamily = family, FontSize = fontPx, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = item.Name, FontSize = smallPx, Foreground = AppTheme.Brush("Ui.Muted"), TextTrimming = TextTrimming.CharacterEllipsis },
                },
            };
        }
        else if (item.Missing)
        {
            row.Child = new TextBlock { Text = item.ToString(), FontSize = fontPx, FontStyle = FontStyle.Italic, Foreground = AppTheme.Brush("Ui.Error"), VerticalAlignment = VerticalAlignment.Center };
        }
        else if (symbol)
        {
            // Symbol fonts: the name set in the font (pictures), and the name again in the normal font so it can be read.
            row.Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = item.Name, FontFamily = family, FontSize = fontPx, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = item.Name, FontSize = Math.Max(smallPx, fontPx * 0.6), Foreground = AppTheme.Brush("Ui.Muted"), VerticalAlignment = VerticalAlignment.Center },
                },
            };
        }
        else
        {
            row.Child = new TextBlock { Text = item.Name, FontFamily = family, FontSize = fontPx, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        }
        return row;
    }
}
