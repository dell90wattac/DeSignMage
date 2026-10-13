using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Shell;

/// <summary>
/// A small dialog to choose one plain color away from the design: the hue ring and square of the Color Mixer, a box for the hex code and a row of
/// ready colors (white, black, grays and the colors the design uses). Used where a color is needed that belongs to no object, such as the background of
/// a JPEG export.
/// </summary>
internal sealed class ColorChoiceDialog : Window
{
    private readonly ColorWheel _wheel = new();
    private readonly TextBox _hex = new TextBox().ScaledWidth(96);
    private readonly Border _big = new Border { BorderThickness = new Thickness(1), BorderBrush = AppTheme.Brush("Ui.SwatchBorder") }.ScaledWidth(44).ScaledHeight(44);
    private readonly TextBlock _error = new() { Foreground = AppTheme.Brush("Ui.Error") };
    private bool _updating;
    private DocColor _color;

    internal TextBox HexBox => _hex;
    internal ColorWheel Wheel => _wheel;
    internal Button OkButton { get; } = new Button { Content = "OK", IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(84);
    internal Button CancelButton { get; } = new Button { Content = "Cancel", IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(84);
    internal List<Button> Presets { get; } = [];

    /// <summary>The color chosen with OK; null when the dialog was cancelled.</summary>
    public DocColor? Result { get; private set; }

    public ColorChoiceDialog(string title, DocColor start, IEnumerable<DocColor> designColors)
    {
        Title = title;
        _color = Plain(start);
        DialogScale.Attach(this, 420);
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _wheel.Changed += (_, _) =>
        {
            var (r, g, b) = ColorMath.HsvToRgb(_wheel.Hue, _wheel.Saturation, _wheel.Value);
            Show(new DocColor(r, g, b), fromWheel: true);
        };
        _hex.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty || _updating) return;
            if (TryParse(_hex.Text, out var parsed))
            {
                _error.Text = "";
                OkButton.IsEnabled = true;
                Show(parsed, fromHex: true);
            }
            else
            {
                _error.Text = "Enter a color as six hex digits, like #1A2B3C.";
                OkButton.IsEnabled = false;
            }
        };
        OkButton.Click += (_, _) => { Result = _color; Close(); };
        CancelButton.Click += (_, _) => Close();

        var presetRow = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in new[] { DocColor.White, new DocColor(230, 230, 230), new DocColor(128, 128, 128), new DocColor(40, 40, 40), DocColor.Black }.Concat(designColors.Select(Plain)).Distinct().Take(20))
        {
            var color = c;
            var b = new Button { Content = ColorUi.Swatch(new SolidPaint(color), 22), Padding = new Thickness(2), Margin = new Thickness(0, 0, UiScale.Sp(4), UiScale.Sp(4)), Focusable = false };
            ToolTip.SetTip(b, Hex(color));
            b.Click += (_, _) => Show(color);
            Presets.Add(b);
            presetRow.Children.Add(b);
        }

        var side = new StackPanel { Spacing = UiScale.Sp(8), VerticalAlignment = VerticalAlignment.Center };
        side.Children.Add(_big);
        side.Children.Add(_hex);
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(18), Children = { _wheel, side } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(8), HorizontalAlignment = HorizontalAlignment.Right, Children = { CancelButton, OkButton } };
        var panel = new StackPanel { Margin = UiScale.Space(18), Spacing = UiScale.Sp(12) };
        panel.Children.Add(top);
        panel.Children.Add(presetRow);
        panel.Children.Add(_error);
        panel.Children.Add(buttons);
        Content = DialogScale.Scrollable(panel);
        Show(_color);
    }

    /// <summary>Only the color counts: no name, no alpha.</summary>
    private static DocColor Plain(DocColor c) => new(c.R, c.G, c.B);

    internal static string Hex(DocColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    internal static bool TryParse(string? text, out DocColor color)
    {
        color = default;
        var s = (text ?? "").Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = new DocColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    private void Show(DocColor c, bool fromWheel = false, bool fromHex = false)
    {
        _color = Plain(c);
        _updating = true;
        _big.Background = new SolidColorBrush(ColorUi.ToAvalonia(_color));
        if (!fromHex) _hex.Text = Hex(_color);
        if (!fromWheel)
        {
            var (h, s, v) = ColorMath.RgbToHsv(_color.R, _color.G, _color.B);
            _wheel.Set(h, s, v);
        }
        _updating = false;
        OkButton.IsEnabled = true;
        _error.Text = "";
    }
}
