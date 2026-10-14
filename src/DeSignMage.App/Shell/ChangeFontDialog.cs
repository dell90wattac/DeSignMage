using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Shell;

/// <summary>
/// Text ▸ Change Font...: one font for every live text in the selection (also inside groups). It shows the app's font list
/// (<see cref="FontPicker"/>: each row in its own font, or your own text in that font, with the size slider) and, below it, a true
/// preview of the text in the font that is picked. It says how many texts will change and how many selected objects are left alone
/// (shapes, and text already converted to curves, which can no longer change font). Size, style, spacing, kerning and alignment of each
/// text stay as they are. OK stays disabled until a font is picked; the dialog only chooses, <see cref="EditorSession.ChangeFont"/> applies.
/// </summary>
internal sealed class ChangeFontDialog : Window
{
    private readonly string _sample;

    internal FontPicker Picker { get; } = new(inline: true);
    internal Button OkButton { get; } = new Button { Content = "OK", IsDefault = true, IsEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(80);
    internal Button CancelButton { get; } = new Button { Content = "Cancel", IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(80);
    internal TextBlock SummaryText { get; } = new() { TextWrapping = TextWrapping.Wrap };
    internal TextBlock SkippedText { get; } = new() { TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Brush("Ui.Muted") };
    internal TextBlock PreviewText { get; } = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    internal TextBlock PreviewName { get; } = new() { Foreground = AppTheme.Brush("Ui.Muted"), TextTrimming = TextTrimming.CharacterEllipsis };

    /// <summary>The family picked when the user pressed OK; null otherwise.</summary>
    public string? Result { get; private set; }

    /// <param name="scope">What the selection holds (<see cref="EditorSession.GetFontScope"/>).</param>
    /// <param name="sample">The beginning of one of the texts, shown in every row and in the preview; null for a standard line.</param>
    /// <param name="current">The font all the texts share, or null when they differ.</param>
    public ChangeFontDialog(TextFontScope scope, string? sample, FontItem? current)
    {
        _sample = FontPicker.Sample1(sample) ?? "The quick brown fox";
        SummaryText.Text = scope.Texts == 1
            ? "The font of the selected text will change. Its size, style, spacing and alignment stay as they are."
            : $"The font of {scope.Texts} texts will change. Their sizes, styles, spacing and alignment stay as they are.";
        SkippedText.Text = scope.Others switch
        {
            0 => "",
            1 => "1 other selected object is left as it is: it is not live text (a shape, or text already converted to curves).",
            _ => $"{scope.Others} other selected objects are left as they are: they are not live text (shapes, or text already converted to curves).",
        };
        SkippedText.IsVisible = SkippedText.Text.Length > 0;

        Picker.SampleProvider = () => sample;
        Picker.SelectedItem = current;
        Picker.Open();
        Picker.SelectionChanged += (_, _) => OnPicked();

        PreviewText.Text = _sample;
        PreviewText.FontSize = 30 * UiScale.Scale;
        PreviewName.Text = current is null ? "Pick a font from the list." : $"Now: {current}";
        var preview = new Border
        {
            BorderBrush = AppTheme.Brush("Ui.PopupBorder"), BorderThickness = new Thickness(1), Background = AppTheme.Brush("Ui.PopupBack"),
            Padding = UiScale.Space(10, 6), Child = new StackPanel { Children = { PreviewText, PreviewName } },
        }.ScaledMinHeight(86);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = UiScale.Sp(8) };
        buttons.Children.Add(OkButton);
        buttons.Children.Add(CancelButton);

        var panel = new StackPanel { Margin = UiScale.Space(16), Spacing = UiScale.Sp(10) };
        panel.Children.Add(SummaryText);
        panel.Children.Add(SkippedText);
        panel.Children.Add(Picker);
        panel.Children.Add(preview);
        panel.Children.Add(buttons);

        Title = "Change Font";
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = DialogScale.Scrollable(panel);
        DialogScale.Attach(this, 460);

        OkButton.Click += (_, _) => Accept();
        CancelButton.Click += (_, _) => Close();
        Picker.List.DoubleTapped += (_, _) => Accept();   // a double click on a row is "pick it and press OK"
    }

    private bool _picked;

    private void OnPicked()
    {
        if (Picker.SelectedItem is not { Missing: false } font) return;
        _picked = true;
        OkButton.IsEnabled = true;
        PreviewText.FontFamily = FontCatalog.IsSymbol(font.Name) ? FontFamily.Default : FontCatalog.FamilyFor(font.Name);
        PreviewName.Text = font.Name;
    }

    private void Accept()
    {
        if (!_picked || Picker.SelectedItem is not { Missing: false } font) return;
        Result = font.Name;
        Close();
    }
}
