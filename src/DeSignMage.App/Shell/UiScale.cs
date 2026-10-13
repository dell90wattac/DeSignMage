using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using DeSignMage.Feedback;

namespace DeSignMage.App.Shell;

/// <summary>
/// Interface text size (requirement R12): one number (100% ... 250%) that makes all of the program's own chrome larger (menus,
/// toolbars, Context Bars, side panels, status line, dialogs, tooltips); the design on the canvas is never touched.
///
/// How it works: the scale lives in application resources. Text follows through the theme's font-size resources
/// (<c>ControlContentThemeFontSize</c> and friends, which menus, combo lists, tooltips and other popups also read) and through
/// <c>Ui.*</c> resources used by our own styles. Pixel sizes that must grow with the text (field widths, icon boxes, dialog sizes) are
/// bound to the scale with <see cref="UiScaleExtensions.Scaled"/>, so a change reaches everything that is already on screen.
/// Not a LayoutTransform: popups and menus live in their own windows and would ignore one.
///
/// "Scale icons with text" decides whether icons (toolbar glyphs, Toolbox art) follow: <see cref="IconScale"/> is the scale
/// they use (1 when switched off).
/// </summary>
public static class UiScale
{
    public const double MinScale = 1.0, MaxScale = 2.5;
    public static readonly double[] Presets = [1.0, 1.25, 1.5, 1.75, 2.0];

    public const string ScaleKey = "Ui.Scale", IconScaleKey = "Ui.IconScale", SpaceScaleKey = "Ui.SpaceScale";
    private const string SettingScale = "ui.textScale", SettingIcons = "ui.scaleIcons";

    /// <summary>Base text sizes at 100% (the look the program had before this option existed).</summary>
    public const double BaseFontSize = 12, BaseSmallFontSize = 11, BaseGlyphSize = 15;

    /// <summary>Theme resources (double) that scale with the text. A key the theme doesn't define is skipped.</summary>
    private static readonly (string Key, double Fallback, bool Icon)[] ThemeKeys =
    [
        ("ControlContentThemeFontSize", 14, false), ("ToolTipContentThemeFontSize", 14, false),
        ("TabItemHeaderFontSize", 14, false), ("GroupBoxHeaderFontSize", 14, false),
        ("ScrollBarButtonArrowIconFontSize", 8, false), ("ScrollBarSize", 16, false),
        ("SliderHorizontalHeight", 32, false), ("SliderHorizontalThumbWidth", 20, false), ("SliderHorizontalThumbHeight", 20, false),
        ("SliderVerticalThumbWidth", 20, false), ("SliderVerticalThumbHeight", 20, false), ("SliderTrackThemeHeight", 4, false),
        ("CheckBoxMinHeight", 20, false), ("RadioButtonMinHeight", 20, false),
        ("ComboBoxMinHeight", 24, false), ("TextControlThemeMinHeight", 24, false), ("TabItemMinHeight", 24, false), ("MenuBarHeight", 24, false),
    ];

    private static readonly Dictionary<string, double> ThemeBase = [];
    private static double _scale = 1;
    private static bool _scaleIcons = true;
    private static SettingsStore? _store;

    /// <summary>The interface text scale: 1.0 = 100%.</summary>
    public static double Scale => _scale;
    public static bool ScaleIcons => _scaleIcons;
    /// <summary>
    /// The scale of padding, margins and gaps: they grow at one third the rate of the text (200% text gives about 133% spacing), so large
    /// text does not waste room on air. Text, icons and control sizes keep following <see cref="Scale"/> / <see cref="IconScale"/>.
    /// </summary>
    public static double SpaceScale => 1 + (_scale - 1) / 3;

    /// <summary>A gap, padding or margin of <paramref name="basePx"/> at 100%, at the current spacing scale.</summary>
    public static double Sp(double basePx) => basePx * SpaceScale;

    public static Thickness Space(double all) => new(Sp(all));
    public static Thickness Space(double horizontal, double vertical) => new(Sp(horizontal), Sp(vertical));
    public static Thickness Space(double left, double top, double right, double bottom) => new(Sp(left), Sp(top), Sp(right), Sp(bottom));

    /// <summary>The scale icons and icon boxes use: the text scale, or 1 when "Scale icons with text" is off.</summary>
    public static double IconScale => _scaleIcons ? _scale : 1;

    /// <summary>Raised after the scale or the icon option changed and the resources were updated.</summary>
    public static event Action? Changed;

    /// <summary>Where the choice is remembered; null keeps it for this run only.</summary>
    public static SettingsStore? Store
    {
        get => _store;
        set => _store = value;
    }

    /// <summary>Reads the remembered choice from <paramref name="store"/> and applies it (call once at startup, before the main window).</summary>
    public static void Load(SettingsStore? store)
    {
        _store = store;
        _scale = Clamp(store?.Get(SettingScale, 1.0) ?? 1.0);
        _scaleIcons = store?.Get(SettingIcons, true) ?? true;
        Apply();
    }

    public static double Clamp(double scale) =>
        double.IsNaN(scale) ? 1 : Math.Round(Math.Clamp(scale, MinScale, MaxScale), 2);

    public static void SetScale(double scale, bool remember = true)
    {
        scale = Clamp(scale);
        if (Math.Abs(scale - _scale) < 1e-9) return;
        _scale = scale;
        if (remember) _store?.Set(SettingScale, scale);
        Apply();
    }

    public static void SetScaleIcons(bool on)
    {
        if (on == _scaleIcons) return;
        _scaleIcons = on;
        _store?.Set(SettingIcons, on);
        Apply();
    }

    /// <summary>True when the scale is one of the presets (the View menu checks it).</summary>
    public static bool IsPreset(double scale, double current) => Math.Abs(scale - current) < 0.005;

    /// <summary>Pixels at 100% to pixels now (for code that builds sizes once, e.g. a dialog's starting size).</summary>
    public static double Px(double basePx) => basePx * _scale;
    public static double IconPx(double basePx) => basePx * IconScale;

    /// <summary>Pushes the current scale into the application resources; everything bound to them updates.</summary>
    public static void Apply()
    {
        if (Application.Current is { } app)
        {
            var r = app.Resources;
            double s = _scale, i = IconScale;
            r[ScaleKey] = s;
            r[IconScaleKey] = i;
            r[SpaceScaleKey] = SpaceScale;
            r["Ui.DockTabHeight"] = 26 * s;
            r["Ui.ListItemPadding"] = Space(8, 3);
            r["Ui.ActPadding"] = Space(9, 1);
            r["Ui.FontSize"] = BaseFontSize * s;
            r["Ui.SmallFontSize"] = BaseSmallFontSize * s;
            r["Ui.GlyphFontSize"] = BaseGlyphSize * i;
            r["Ui.ToolButtonSize"] = 26 * i;
            r["Ui.NumWidth"] = 70 * s;
            r["Ui.NumMinHeight"] = 22 * s;
            r["Ui.TabMinHeight"] = 24 * s;
            r["Ui.SliderThumb"] = 12 * s;
            foreach (var (key, fallback, icon) in ThemeKeys)
            {
                if (!ThemeBase.TryGetValue(key, out var baseValue))
                {
                    // Read the theme's own value once, before our override hides it.
                    baseValue = app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is double d ? d : double.NaN;
                    ThemeBase[key] = baseValue;
                }
                if (double.IsNaN(baseValue)) continue;
                r[key] = baseValue * (icon ? i : s);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Test hook: back to 100% with icons scaling, without touching the store.</summary>
    internal static void Reset()
    {
        _scale = 1;
        _scaleIcons = true;
        Apply();
    }
}

public static class UiScaleExtensions
{
    /// <summary>
    /// Binds a size property to <paramref name="basePx"/> times the interface scale (or the icon scale), so the control follows live
    /// changes. Use for fixed widths/heights of fields, buttons and icons; text itself scales through font-size resources.
    /// </summary>
    public static T Scaled<T>(this T control, AvaloniaProperty<double> property, double basePx, bool icon = false) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(icon ? UiScale.IconScaleKey : UiScale.ScaleKey,
            o => basePx * (o is double d ? d : 1.0)));
        return control;
    }

    /// <summary>
    /// Binds a gap (a spacing, a line spacing) to <paramref name="basePx"/> times the spacing scale, so it follows live changes.
    /// </summary>
    public static T Spaced<T>(this T control, AvaloniaProperty<double> property, double basePx) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(UiScale.SpaceScaleKey, o => basePx * (o is double d ? d : 1.0)));
        return control;
    }

    /// <summary>Binds a margin or padding (<paramref name="property"/>) to the given thickness at 100%, times the spacing scale.</summary>
    public static T SpacedThickness<T>(this T control, AvaloniaProperty<Thickness> property, Thickness basePx) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(UiScale.SpaceScaleKey, o =>
        {
            double k = o is double d ? d : 1.0;
            return new Thickness(basePx.Left * k, basePx.Top * k, basePx.Right * k, basePx.Bottom * k);
        }));
        return control;
    }

    public static T ScaledWidth<T>(this T control, double basePx, bool icon = false) where T : Control => control.Scaled(Layoutable.WidthProperty, basePx, icon);
    public static T ScaledHeight<T>(this T control, double basePx, bool icon = false) where T : Control => control.Scaled(Layoutable.HeightProperty, basePx, icon);
    public static T ScaledMinWidth<T>(this T control, double basePx, bool icon = false) where T : Control => control.Scaled(Layoutable.MinWidthProperty, basePx, icon);
    public static T ScaledMinHeight<T>(this T control, double basePx, bool icon = false) where T : Control => control.Scaled(Layoutable.MinHeightProperty, basePx, icon);
    public static T ScaledMaxHeight<T>(this T control, double basePx, bool icon = false) where T : Control => control.Scaled(Layoutable.MaxHeightProperty, basePx, icon);

    /// <summary>The control's text size follows the interface scale (base size at 100%).</summary>
    public static T ScaledFont<T>(this T control, double baseSize, bool icon = false) where T : Control
        => control.Scaled(TextElement.FontSizeProperty, baseSize, icon);
}



/// <summary>Drawn icons are built at their 100% size; <see cref="Box"/> makes them follow the icon scale.</summary>
public static class UiIcons
{
    /// <summary>Wraps an icon that has an explicit Width and Height so it is drawn at that size times the icon scale.</summary>
    public static Control Box(Control icon) => Box(icon, icon.Width, icon.Height);

    public static Control Box(Control icon, double width, double height) =>
        new Viewbox { Child = icon, Stretch = Stretch.Uniform }.ScaledWidth(width, icon: true).ScaledHeight(height, icon: true);
}
