using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Shell;

/// <summary>
/// Image ▸ Resample...: sets a placed picture's real pixel size, by pixel width, pixel height or dpi. Its printed size on the page does
/// not change (the handles do that); only the number of pixels does, and with it the dpi. The fields update each other live.
/// It only states the numbers: it never judges the resolution.
/// </summary>
internal sealed class ResampleDialog : Window
{
    private readonly ResampleSizing _sizing;
    private readonly string _printedText;
    private bool _updating;

    internal TextBox WidthBox { get; } = Field();
    internal TextBox HeightBox { get; } = Field();
    internal TextBox DpiBox { get; } = Field();
    internal CheckBox LockBox { get; } = new() { Content = "Keep proportions", IsChecked = true };
    internal Button OkButton { get; } = new Button { Content = "OK", IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(80);
    internal Button CancelButton { get; } = new Button { Content = "Cancel", IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(80);
    internal TextBlock CurrentText { get; } = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    internal TextBlock ResultText { get; } = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    /// <summary>The new pixel size when the user pressed OK; null otherwise.</summary>
    public (int Width, int Height)? Result { get; private set; }

    internal ResampleSizing Sizing => _sizing;

    public ResampleDialog(ImageObject image)
    {
        _sizing = ResampleSizing.For(image);
        var (pw, ph) = (_sizing.PrintedWidthInches, _sizing.PrintedHeightInches);
        _printedText = string.Create(CultureInfo.InvariantCulture, $"{pw:0.##} x {ph:0.##} in");
        CurrentText.Text = string.Create(CultureInfo.InvariantCulture,
            $"Now: {image.PixelWidth} x {image.PixelHeight} pixels, printed {_printedText}, {Dpi(_sizing.CurrentDpiX, _sizing.CurrentDpiY)}");
        CurrentText.Foreground = AppTheme.Brush("Ui.Muted");
        ResultText.Foreground = AppTheme.Brush("Ui.Muted");

        var grid = new Grid { RowSpacing = UiScale.Sp(8), ColumnSpacing = UiScale.Sp(8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        void Row(int row, string label, TextBox box, string unit)
        {
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            var u = new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(l, row); Grid.SetColumn(l, 0);
            Grid.SetRow(box, row); Grid.SetColumn(box, 1);
            var unitHolder = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(12) };
            unitHolder.Children.Add(u);
            Grid.SetRow(unitHolder, row); Grid.SetColumn(unitHolder, 2);
            grid.Children.Add(l); grid.Children.Add(box); grid.Children.Add(unitHolder);
            if (row == 1) unitHolder.Children.Add(LockBox);   // the lock sits between the two sides
        }
        Row(0, "Width:", WidthBox, "pixels");
        Row(1, "Height:", HeightBox, "pixels");
        Row(2, "Resolution:", DpiBox, "dpi");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = UiScale.Sp(8) };
        buttons.Children.Add(OkButton);
        buttons.Children.Add(CancelButton);
        var panel = new StackPanel { Margin = UiScale.Space(16), Spacing = UiScale.Sp(12) };
        panel.Children.Add(new TextBlock
        {
            Text = "Set how many pixels make up the picture. Its size on the page stays the same; the resolution follows.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        panel.Children.Add(CurrentText);
        panel.Children.Add(grid);
        panel.Children.Add(ResultText);
        panel.Children.Add(buttons);

        Title = "Resample";
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = DialogScale.Scrollable(panel);
        DialogScale.Attach(this, 440);

        // The Text property itself is watched (not the TextChanged event, which can arrive later), so the fields follow each other at once.
        void Watch(TextBox box, Edited which) => box.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) OnEdited(which); };
        Watch(WidthBox, Edited.Width);
        Watch(HeightBox, Edited.Height);
        Watch(DpiBox, Edited.Dpi);
        LockBox.IsCheckedChanged += (_, _) => _sizing.LockAspect = LockBox.IsChecked == true;
        foreach (var box in new[] { WidthBox, HeightBox, DpiBox }) box.LostFocus += (_, _) => ShowAll();
        OkButton.Click += (_, _) => Accept();
        CancelButton.Click += (_, _) => Close();

        ShowAll();
    }

    private enum Edited { Width, Height, Dpi }

    private static TextBox Field() => new TextBox { HorizontalContentAlignment = HorizontalAlignment.Right }.ScaledWidth(100);

    private static string Dpi(double x, double y) =>
        Math.Abs(x - y) < 0.5 ? ImageSizing.Dpi(x) + " dpi" : $"{ImageSizing.Dpi(x)} x {ImageSizing.Dpi(y)} dpi";

    /// <summary>One of the three fields was typed in: the model takes it, the other fields follow.</summary>
    private void OnEdited(Edited which)
    {
        if (_updating) return;
        var box = which switch { Edited.Width => WidthBox, Edited.Height => HeightBox, _ => DpiBox };
        bool ok = false;
        if (which == Edited.Dpi)
        {
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var dpi) && dpi > 0) { _sizing.SetDpi(dpi); ok = true; }
        }
        else if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0)
        {
            if (which == Edited.Width) _sizing.SetWidth(n); else _sizing.SetHeight(n);
            ok = true;
        }
        if (!ok)
        {
            OkButton.IsEnabled = false;   // empty or not a number: nothing to apply until it is one
            ResultText.Text = "Enter a number.";
            return;
        }
        ShowAll(except: box);
    }

    /// <summary>Writes the model into the fields (not the one being typed in) and the summary line.</summary>
    private void ShowAll(TextBox? except = null)
    {
        _updating = true;
        if (!ReferenceEquals(except, WidthBox)) WidthBox.Text = _sizing.Width.ToString(CultureInfo.InvariantCulture);
        if (!ReferenceEquals(except, HeightBox)) HeightBox.Text = _sizing.Height.ToString(CultureInfo.InvariantCulture);
        if (!ReferenceEquals(except, DpiBox)) DpiBox.Text = ImageSizing.Dpi(_sizing.DpiX);
        _updating = false;

        if (_sizing.Problem is { } problem)
            ResultText.Text = problem;
        else
            ResultText.Text = string.Create(CultureInfo.InvariantCulture,
                $"New: {_sizing.Width} x {_sizing.Height} pixels, {Dpi(_sizing.DpiX, _sizing.DpiY)}, {_sizing.Megabytes:0.0} MB. Printed size stays {_printedText}.");
        OkButton.IsEnabled = _sizing.CanApply;
    }

    private void Accept()
    {
        if (!_sizing.CanApply) return;
        Result = (_sizing.Width, _sizing.Height);
        Close();
    }
}
