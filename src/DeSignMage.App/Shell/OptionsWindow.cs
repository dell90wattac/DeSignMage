using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace DeSignMage.App.Shell;

/// <summary>
/// One section ("page") of Tools ▸ Options. <see cref="Create"/> builds the page's content when the page is first shown; the content
/// should apply its choices as they are made (there is no OK/Cancel) and remember them in the settings store.
/// </summary>
public sealed record OptionsPage(string Id, string Title, Func<Control> Create);

/// <summary>
/// The sections of Tools ▸ Options. To add a section, add one <see cref="OptionsPage"/> to <see cref="Pages"/> (or call
/// <see cref="Register"/> at startup) with a title and a function that builds its content; the window lists it automatically.
/// Pages are shown in this order.
/// </summary>
public static class OptionsPages
{
    public const string AccessibilityId = "accessibility";
    public const string PerformanceId = "performance";
    public const string VectorId = "vector";

    private static readonly List<OptionsPage> Pages =
    [
        new(AccessibilityId, "Accessibility", () => new AccessibilityOptionsPage()),
        new(VectorId, "Vector editing", () => new VectorOptionsPage()),
        new(PerformanceId, "Performance & memory", () => new PerformanceOptionsPage()),
        // Add further sections here.
    ];

    public static IReadOnlyList<OptionsPage> All => Pages;

    internal static void Unregister(string id) => Pages.RemoveAll(p => p.Id == id);

    /// <summary>Adds (or replaces, by id) a section. Call before the window is opened.</summary>
    public static void Register(OptionsPage page)
    {
        int i = Pages.FindIndex(p => p.Id == page.Id);
        if (i >= 0) Pages[i] = page; else Pages.Add(page);
    }
}

/// <summary>Tools ▸ Options: a list of sections on the left, the chosen section on the right.</summary>
public sealed class OptionsWindow : Window
{
    private readonly ListBox _sections = new() { Name = "OptionsSections" };
    private readonly ContentControl _host = new() { Name = "OptionsPageHost" };
    private readonly Dictionary<string, Control> _built = [];

    public OptionsWindow(string? pageId = null)
    {
        Title = "Options";
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DialogScale.Attach(this, 780, 540, 560, 380);

        _sections.ItemsSource = OptionsPages.All.Select(p => p.Title).ToList();
        _sections.SelectionChanged += (_, _) => ShowPage(_sections.SelectedIndex);
        _sections.ScaledWidth(190);

        var close = new Button { Name = "OptionsCloseButton", Content = "Close", IsDefault = true, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(90);
        close.Click += (_, _) => Close();

        var scroll = new ScrollViewer
        {
            Content = _host, Margin = UiScale.Space(14, 0, 0, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var body = new DockPanel { Margin = UiScale.Space(14) };
        DockPanel.SetDock(close, Dock.Bottom);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = UiScale.Space(0, 12, 0, 0);
        DockPanel.SetDock(_sections, Dock.Left);
        body.Children.Add(close);
        body.Children.Add(_sections);
        body.Children.Add(scroll);
        Content = body;

        int index = OptionsPages.All.ToList().FindIndex(p => p.Id == pageId);
        _sections.SelectedIndex = index >= 0 ? index : 0;
    }

    /// <summary>The page being shown (for tests).</summary>
    internal Control? CurrentPage => _host.Content as Control;
    internal ListBox Sections => _sections;

    public void ShowPage(string id)
    {
        int i = OptionsPages.All.ToList().FindIndex(p => p.Id == id);
        if (i >= 0) _sections.SelectedIndex = i;
    }

    private void ShowPage(int index)
    {
        if (index < 0 || index >= OptionsPages.All.Count) return;
        var page = OptionsPages.All[index];
        if (!_built.TryGetValue(page.Id, out var content)) _built[page.Id] = content = page.Create();
        _host.Content = content;
    }
}

/// <summary>
/// Options ▸ Accessibility: the interface text size (presets, a fine slider, a preview line) and "Scale icons with text".
/// Presets and the checkbox apply at once. The slider applies when released (or at once from the keyboard), because the window
/// itself grows with the text and would otherwise move away from under the mouse; the preview line follows the slider while dragging.
/// </summary>
public sealed class AccessibilityOptionsPage : StackPanel
{
    private readonly RadioButton[] _presetButtons;
    private readonly Slider _slider;
    private readonly TextBlock _percent = new() { Name = "TextSizePercent", MinWidth = 60, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
    private readonly CheckBox _icons = new() { Name = "ScaleIconsCheck", Content = "Scale icons with text" };
    private readonly TextBlock _previewText = new() { Name = "TextSizePreviewText", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _previewGlyph = new() { VerticalAlignment = VerticalAlignment.Center };
    private bool _syncing, _dragging;

    public AccessibilityOptionsPage()
    {
        Spacing = UiScale.Sp(10);

        _presetButtons = UiScale.Presets.Select(p =>
        {
            int pct = (int)Math.Round(p * 100);
            var r = new RadioButton { Name = $"TextSize{pct}", GroupName = "textSize", Content = pct == 100 ? "100% (standard)" : $"{pct}%", Tag = p, Focusable = true };
            r.IsCheckedChanged += (_, _) =>
            {
                if (_syncing || r.IsChecked != true) return;
                UiScale.SetScale(p);
            };
            return r;
        }).ToArray();

        _slider = new Slider
        {
            Name = "TextSizeSlider", Minimum = UiScale.MinScale * 100, Maximum = UiScale.MaxScale * 100,
            TickFrequency = 5, IsSnapToTickEnabled = true, SmallChange = 5, LargeChange = 25, Value = UiScale.Scale * 100,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
        };
        _slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _syncing) return;
            UpdatePreview(_slider.Value / 100);
            if (!_dragging) UiScale.SetScale(_slider.Value / 100);
        };
        _slider.AddHandler(PointerPressedEvent, (_, _) => _dragging = true, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        void Release()
        {
            if (!_dragging) return;
            _dragging = false;
            UiScale.SetScale(_slider.Value / 100);
        }
        _slider.AddHandler(PointerReleasedEvent, (_, _) => Release(), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        _slider.AddHandler(PointerCaptureLostEvent, (_, _) => Release(), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

        _icons.IsCheckedChanged += (_, _) =>
        {
            if (!_syncing) UiScale.SetScaleIcons(_icons.IsChecked == true);
        };

        var reset = new Button { Name = "TextSizeReset", Content = "Back to 100%" };
        reset.Click += (_, _) => UiScale.SetScale(1.0);

        var sliderRow = new DockPanel();
        DockPanel.SetDock(_percent, Dock.Right);
        sliderRow.Children.Add(_percent);
        sliderRow.Children.Add(_slider);

        var preview = new Border
        {
            Name = "TextSizePreview", BorderBrush = AppTheme.Brush("Ui.PopupBorder"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = UiScale.Space(10, 6), Background = AppTheme.Brush("Ui.PopupBack"),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(10),
                Children = { _previewGlyph, _previewText },
            },
        };
        _previewGlyph.Classes.Add("glyph");
        _previewGlyph.Text = "";   // the Save icon, as on the toolbar

        Children.Add(new TextBlock { Text = "Interface text size", FontWeight = FontWeight.Bold }.ScaledFont(15));
        Children.Add(new TextBlock
        {
            Text = "Makes the menus, toolbars, panels, dialogs and tooltips larger. Your design on the canvas is not changed.",
            TextWrapping = TextWrapping.Wrap,
        });
        var presetList = new StackPanel { Spacing = UiScale.Sp(2) };
        foreach (var b in _presetButtons) presetList.Children.Add(b);
        Children.Add(presetList);
        Children.Add(new TextBlock { Text = "Or choose any size:" });
        Children.Add(sliderRow);
        Children.Add(new TextBlock { Text = "Preview" });
        Children.Add(preview);
        Children.Add(_icons);
        Children.Add(reset);

        UiScale.Changed += OnScaleChanged;
        DetachedFromVisualTree += (_, _) => UiScale.Changed -= OnScaleChanged;
        AttachedToVisualTree += (_, _) => { UiScale.Changed -= OnScaleChanged; UiScale.Changed += OnScaleChanged; Sync(); };
        Sync();
    }

    internal Slider SizeSlider => _slider;
    internal RadioButton[] Presets => _presetButtons;
    internal CheckBox IconsCheck => _icons;
    internal TextBlock PreviewText => _previewText;

    private void OnScaleChanged() => Sync();

    /// <summary>Shows the current setting in all the controls.</summary>
    private void Sync()
    {
        _syncing = true;
        foreach (var r in _presetButtons) r.IsChecked = UiScale.IsPreset((double)r.Tag!, UiScale.Scale);
        if (!_dragging) _slider.Value = UiScale.Scale * 100;
        _icons.IsChecked = UiScale.ScaleIcons;
        UpdatePreview(_dragging ? _slider.Value / 100 : UiScale.Scale);
        _syncing = false;
    }

    /// <summary>The preview line is drawn at the size being chosen, even while the slider is still moving.</summary>
    private void UpdatePreview(double scale)
    {
        _percent.Text = $"{Math.Round(scale * 100):0}%";
        _previewText.Text = $"File  Edit  View   Font: Arial   {Math.Round(scale * 100):0}%";
        _previewText.FontSize = UiScale.BaseFontSize * scale;
        _previewGlyph.FontSize = UiScale.BaseGlyphSize * (UiScale.ScaleIcons ? scale : 1);
    }
}
