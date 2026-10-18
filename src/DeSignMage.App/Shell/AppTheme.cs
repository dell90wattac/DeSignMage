using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using DeSignMage.Feedback;
using DeSignMage.Rendering;

namespace DeSignMage.App.Shell;

/// <summary>
/// Dark mode (requirement R19): two independent switches.
/// <list type="bullet">
/// <item><b>Dark Menus</b>: all of the program's own chrome (top row, Main Toolbar, Context Bar, Toolbox, Dock, Info Line, rulers, Project Color
/// Bar, status line, dialogs, drop-downs) turns dark. It switches Avalonia's theme variant, so every standard control follows, and recolors the
/// program's own brushes (<see cref="Brush"/>).</item>
/// <item><b>Dark Canvas</b>: what the canvas draws around and under the design turns dark (<see cref="Canvas"/>). The design's own colors
/// are never touched, and neither are exports, saved files, thumbnails or the clipboard.</item>
/// </list>
/// Both are remembered between runs. All the greys are neutral (R = G = B), so judging a design's colors is not skewed.
///
/// How colors reach the screen: every themed color of the chrome is one named brush here (<c>Ui.*</c>), registered as an application resource and
/// recolored in place when the mode changes, so XAML (<c>{DynamicResource Ui.Chrome}</c>) and code (<c>AppTheme.Brush("Ui.Chrome")</c>) both follow live.
/// Swatches never use them: a swatch shows its true color.
/// </summary>
public static class AppTheme
{
    public const string DarkMenusSetting = "ui.darkMenus", DarkCanvasSetting = "ui.darkCanvas", ShowTransparencySetting = "ui.showTransparency";

    /// <summary>Light (the classic look) and dark value of every themed brush.</summary>
    private static readonly (string Key, string Light, string Dark)[] Entries =
    [
        // surfaces
        ("Ui.WindowBack", "#FFFFFF", "#2E2E2E"),
        ("Ui.Chrome", "#F0F0F0", "#333333"),          // Main Toolbar, Context Bar, Toolbox, Palette Strip, Project Color Bar, status line
        ("Ui.ChromeBorder", "#C8C8C8", "#4A4A4A"),
        ("Ui.Panel", "#F7F7F7", "#3C3C3C"),            // the Dock's panels, and the open Dock Tab
        ("Ui.Splitter", "#D8D8D8", "#454545"),
        ("Ui.PopupBack", "#FFFFFF", "#2F2F2F"),
        ("Ui.PopupBorder", "#808080", "#5A5A5A"),
        // Dock Tabs
        ("Ui.TabBack", "#E6E6E6", "#2B2B2B"),
        ("Ui.TabBorder", "#C6C6C6", "#4A4A4A"),
        ("Ui.TabHoverBack", "#DCE9F7", "#454545"),
        ("Ui.TabHoverBorder", "#8DB6E2", "#7A7A7A"),
        ("Ui.TabText", "#505050", "#BEBEBE"),
        ("Ui.TabTextOpen", "#1A1A1A", "#F0F0F0"),
        // text
        ("Ui.Muted", "#808080", "#A0A0A0"),
        ("Ui.Caption", "#6B6B6B", "#A6A6A6"),
        ("Ui.Label", "#444444", "#D0D0D0"),
        ("Ui.Unit", "#7A7A7A", "#9C9C9C"),
        ("Ui.Error", "#B22222", "#FF7B72"),
        ("Ui.TabItemText", "#707070", "#A6A6A6"),
        ("Ui.TabItemTextHover", "#303030", "#E0E0E0"),
        ("Ui.TabItemTextSelected", "#202020", "#F0F0F0"),
        // text buttons (Button.act) and on/off toolbar buttons (Button.tb.on)
        ("Ui.ActBack", "#FBFBFB", "#424242"),
        ("Ui.ActBorder", "#C6C6C6", "#5C5C5C"),
        ("Ui.ActHoverBack", "#E6EFFA", "#505050"),
        ("Ui.ActHoverBorder", "#8DB6E2", "#8A8A8A"),
        ("Ui.ActPressedBack", "#CFE3F7", "#5E5E5E"),
        ("Ui.ActDisabledBack", "#F4F4F4", "#383838"),
        ("Ui.ActDisabledBorder", "#DEDEDE", "#484848"),
        ("Ui.ActDisabledText", "#A3A3A3", "#7A7A7A"),
        ("Ui.OnBack", "#CFE3F7", "#5A5A5A"),
        ("Ui.OnBorder", "#7AB0E0", "#8C8C8C"),
        ("Ui.OnHoverBack", "#BFD9F2", "#666666"),
        // lines, notices
        ("Ui.Separator", "#C4C4C4", "#555555"),
        ("Ui.NoticeBack", "#FFF1CC", "#5A4A1C"),
        ("Ui.NoticeBorder", "#D9B13B", "#A8862A"),
        // rulers
        ("Ui.RulerBack", "#F3F3F3", "#3A3A3A"),
        ("Ui.RulerTick", "#000000", "#D0D0D0"),
        ("Ui.RulerEdge", "#808080", "#5A5A5A"),
        ("Ui.RulerMarker", "#1F3FFF", "#7F9BFF"),
        // drawn icons, and the thin border around every swatch (so black shows in dark mode and white in light mode)
        ("Ui.Icon", "#000000", "#E6E6E6"),
        ("Ui.IconAccent", "#0000FF", "#6C8CFF"),
        ("Ui.IconInverse", "#FFFFFF", "#333333"),
        ("Ui.SwatchBorder", "#808080", "#8C8C8C"),
    ];

    // Created on first use, on the UI thread (brushes belong to it); the color tables above can be read from any thread.
    private static Dictionary<string, SolidColorBrush>? _brushes;
    private static Dictionary<string, SolidColorBrush> Brushes => _brushes ??= Entries.ToDictionary(e => e.Key, e => new SolidColorBrush(Color.Parse(e.Light)));
    private static bool _darkMenus, _darkCanvas, _showTransparency;

    /// <summary>True while the chrome is dark (Dark Menus).</summary>
    public static bool DarkMenus => _darkMenus;

    /// <summary>True while the canvas is dark (Dark Canvas).</summary>
    public static bool DarkCanvas => _darkCanvas;

    /// <summary>
    /// Show Transparency (R27): the page is drawn as a checkerboard so whatever is not covered shows through. Like the dark-mode switches it only
    /// changes what is drawn on screen: exports, saved files and the clipboard never see it.
    /// </summary>
    public static bool ShowTransparency => _showTransparency;

    /// <summary>The colors the canvas draws with right now.</summary>
    public static CanvasTheme Canvas => _darkCanvas ? CanvasTheme.Dark : CanvasTheme.Light;

    /// <summary>Raised after either switch changed and the colors were updated.</summary>
    public static event Action? Changed;

    /// <summary>The themed brush with this key (for example <c>Ui.Muted</c>). The same object for the whole run; its color follows the mode.</summary>
    public static SolidColorBrush Brush(string key) => Brushes[key];

    /// <summary>The current color of a themed brush.</summary>
    public static Color ColorOf(string key) => Brushes[key].Color;

    /// <summary>The color a key has in the given mode (tests).</summary>
    public static Color ColorIn(string key, bool dark)
    {
        var (_, light, darkColor) = Entries.First(e => e.Key == key);
        return Color.Parse(dark ? darkColor : light);
    }

    public static IEnumerable<string> Keys => Entries.Select(e => e.Key);

    /// <summary>The dark theme's text color: a soft white, not the theme's pure white (the surfaces of the theme itself are neutral greys already).</summary>
    public static readonly Color DarkText = Color.Parse("#E4E4E4");

    /// <summary>What a focused text box is filled with in the dark theme (the unfocused ones are a see-through dark over the surface).</summary>
    public static readonly Color DarkField = Color.Parse("#262626");

    /// <summary>Puts the brushes into the application's resources (once per application) so styles and XAML can use them.</summary>
    public static void Register(Application app)
    {
        foreach (var (key, brush) in Brushes)
            if (!app.Resources.TryGetValue(key, out var existing) || !ReferenceEquals(existing, brush))
                app.Resources[key] = brush;
        // Every standard control (buttons, text boxes, menus, check boxes ...) takes its text color from this one brush of the theme's dark
        // variant, so one change there is enough; the light variant has its own brush and is left alone.
        if (app.TryGetResource("SystemControlForegroundBaseHighBrush", ThemeVariant.Dark, out var text) && text is SolidColorBrush darkText)
            darkText.Color = DarkText;
        // Likewise the theme's "alt high" brush, which a focused text box is filled with: pure black in the dark variant, here a soft dark grey.
        if (app.TryGetResource("SystemControlBackgroundAltHighBrush", ThemeVariant.Dark, out var field) && field is SolidColorBrush darkField)
            darkField.Color = DarkField;
    }

    /// <summary>Reads the remembered switches from <paramref name="store"/> and applies them (call once at startup, before the main window).</summary>
    public static void Load(SettingsStore? store)
    {
        _darkMenus = store?.Get(DarkMenusSetting, false) ?? false;
        _darkCanvas = store?.Get(DarkCanvasSetting, false) ?? false;
        _showTransparency = store?.Get(ShowTransparencySetting, false) ?? false;
        Apply();
    }

    public static void SetDarkMenus(bool on, bool remember = true)
    {
        if (on == _darkMenus) return;
        _darkMenus = on;
        if (remember) AppSettings.Store.Set(DarkMenusSetting, on);
        Apply();
    }

    public static void SetDarkCanvas(bool on, bool remember = true)
    {
        if (on == _darkCanvas) return;
        _darkCanvas = on;
        if (remember) AppSettings.Store.Set(DarkCanvasSetting, on);
        Apply();
    }

    public static void SetShowTransparency(bool on, bool remember = true)
    {
        if (on == _showTransparency) return;
        _showTransparency = on;
        if (remember) AppSettings.Store.Set(ShowTransparencySetting, on);
        Changed?.Invoke();   // nothing to recolor: the canvas just draws again
    }

    /// <summary>Recolors the brushes and the Avalonia theme for the current switches, then tells everything that draws by hand.</summary>
    public static void Apply()
    {
        foreach (var (key, light, dark) in Entries)
            Brushes[key].Color = Color.Parse(_darkMenus ? dark : light);
        if (Application.Current is { } app)
        {
            Register(app);
            app.RequestedThemeVariant = _darkMenus ? ThemeVariant.Dark : ThemeVariant.Light;
        }
        Changed?.Invoke();
    }

    /// <summary>Test hook: both switches off, without touching the store.</summary>
    internal static void Reset()
    {
        _darkMenus = false;
        _darkCanvas = false;
        _showTransparency = false;
        Apply();
    }
}
