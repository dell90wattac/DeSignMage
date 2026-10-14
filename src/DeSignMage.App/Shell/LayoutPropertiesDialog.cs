using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Model;
using static DeSignMage.App.Shell.DialogParts;

namespace DeSignMage.App.Shell;

/// <summary>What the user chose in Layout Properties.</summary>
internal sealed record LayoutPropertiesResult(double WidthInches, double HeightInches, bool ShowGuides, bool SnapToGuides, bool UseAsDefaultSize);

/// <summary>
/// Layout ▸ Layout Properties...: the page size by number (inches), the two guide switches (show, snap) and whether the size is the one new
/// layouts start with. Built like the Export dialog: small section labels over their rows, labels in one column and fields in the next. The
/// page keeps its bottom-left corner and nothing in the design moves; that is said in the dialog.
/// </summary>
internal sealed class LayoutPropertiesDialog : Window
{
    private static readonly double MinInches = Units.ToInches(PageBorder.MinSize), MaxInches = Units.ToInches(PageBorder.MaxSize);

    private readonly bool _wasDefaultSize;
    private bool _updating;

    internal TextBox WidthBox { get; } = SizeField();
    internal TextBox HeightBox { get; } = SizeField();
    internal CheckBox ShowGuidesBox { get; } = new() { Content = "Show guidelines" };
    internal CheckBox SnapBox { get; } = new() { Content = "Snap objects to guidelines" };
    internal CheckBox DefaultBox { get; } = new() { Content = "Use this size for new layouts" };
    internal TextBlock MetricNote { get; } = Note();
    internal TextBlock ErrorText { get; } = new() { Foreground = AppTheme.Brush("Ui.Error"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    internal Button OkButton { get; } = new Button { Content = "OK", IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(96);
    internal Button CancelButton { get; } = new Button { Content = "Cancel", IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(96);

    /// <summary>What to apply when the user pressed OK; null otherwise.</summary>
    public LayoutPropertiesResult? Result { get; private set; }

    public LayoutPropertiesDialog(PageSetup page, bool showGuides, bool snapToGuides, bool isDefaultSize)
    {
        _wasDefaultSize = isDefaultSize;
        Title = "Layout Properties";
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        ShowGuidesBox.IsChecked = showGuides;
        SnapBox.IsChecked = snapToGuides;
        DefaultBox.IsChecked = isDefaultSize;

        var size = Section("Page size", Stack(
            Row("Width", WithUnit(WidthBox, "in")),
            Row("Height", WithUnit(HeightBox, "in")),
            Row("", MetricNote),
            Row("", Note("The page keeps its bottom-left corner. Nothing in the design moves."))));
        var guides = Section("Guidelines", Stack(ShowGuidesBox, SnapBox));
        var newLayouts = Section("New layouts", DefaultBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = UiScale.Sp(8) };
        buttons.Children.Add(OkButton);
        buttons.Children.Add(CancelButton);

        var form = new StackPanel { Margin = UiScale.Space(18), Spacing = UiScale.Sp(16) };
        form.Children.Add(size);
        form.Children.Add(guides);
        form.Children.Add(newLayouts);
        form.Children.Add(ErrorText);
        form.Children.Add(buttons);
        Content = DialogScale.Scrollable(form);
        DialogScale.Attach(this, 440);

        _updating = true;
        WidthBox.Text = Fmt(Units.ToInches(page.Width));
        HeightBox.Text = Fmt(Units.ToInches(page.Height));
        _updating = false;
        // The Text property itself is watched (not TextChanged, which can arrive later), so OK and the note follow at once.
        WidthBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Validate(); };
        HeightBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Validate(); };
        OkButton.Click += (_, _) => Accept();
        CancelButton.Click += (_, _) => Close();
        Validate();
    }

    private static TextBox SizeField() => new TextBox { HorizontalContentAlignment = HorizontalAlignment.Right }.ScaledWidth(110);

    private static string Fmt(double inches) => inches.ToString("0.####", CultureInfo.InvariantCulture);

    private static bool TryParse(TextBox box, out double value) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    /// <summary>Checks both numbers: OK is off, with a reason, until they are a usable page size.</summary>
    private void Validate()
    {
        if (_updating) return;
        bool wOk = TryParse(WidthBox, out var w), hOk = TryParse(HeightBox, out var h);
        string? problem = null;
        if (!wOk || !hOk) problem = "Enter the width and the height as numbers.";
        else if (w < MinInches || h < MinInches) problem = $"The page must be at least {Fmt(MinInches)} inch each way.";
        else if (w > MaxInches || h > MaxInches) problem = $"The page can be at most {Fmt(Math.Floor(MaxInches))} inches each way.";
        ErrorText.Text = problem ?? "";
        ErrorText.IsVisible = problem is not null;
        MetricNote.Text = wOk && hOk
            ? string.Create(CultureInfo.InvariantCulture, $"= {w * 25.4:0.##} x {h * 25.4:0.##} mm")
            : "";
        OkButton.IsEnabled = problem is null;
    }

    private void Accept()
    {
        if (!OkButton.IsEnabled || !TryParse(WidthBox, out var w) || !TryParse(HeightBox, out var h)) return;
        Result = new LayoutPropertiesResult(w, h, ShowGuidesBox.IsChecked == true, SnapBox.IsChecked == true, DefaultBox.IsChecked == true);
        Close();
    }

    /// <summary>True when the "new layouts" box was ticked when the dialog opened (so unticking it means "stop using this size").</summary>
    internal bool WasDefaultSize => _wasDefaultSize;
}
