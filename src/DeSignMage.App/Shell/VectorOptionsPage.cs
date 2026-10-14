using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DeSignMage.Core.Editing;

namespace DeSignMage.App.Shell;

/// <summary>
/// Options ▸ Vector editing: the small-vector threshold (what the Info Line counts and View ▸ Highlight Small Vectors marks), the Tangency
/// setting (how close two vectors' directions must be for a node to show as a cross) and what a box does in Vector Edit. Applies as you
/// type (Enter or leaving a field).
/// </summary>
public sealed class VectorOptionsPage : StackPanel
{
    private readonly TextBox _threshold = new() { Name = "SmallVectorThresholdBox" };
    private readonly TextBox _tangency = new() { Name = "TangencyBox" };
    private readonly CheckBox _marquee = new() { Name = "MarqueeShiftSelectsBox", Content = "Marquee with Shift selects vectors" };
    private bool _syncing;

    public VectorOptionsPage()
    {
        Spacing = UiScale.Sp(10);
        _threshold.ScaledWidth(90);
        _tangency.ScaledWidth(90);
        foreach (var box in new[] { _threshold, _tangency })
        {
            var b = box;
            b.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            b.LostFocus += (_, _) => Commit();
        }
        _marquee.IsCheckedChanged += (_, _) =>
        {
            if (!_syncing) VectorEditSettings.SetMarqueeShiftSelects(_marquee.IsChecked == true);
        };

        Children.Add(new TextBlock { Text = "Small vectors", FontWeight = FontWeight.Bold }.ScaledFont(15));
        Children.Add(new TextBlock
        {
            Text = "A vector whose two end points are closer than this is small, whatever its kind. The Info Line counts the small vectors of the open graphic, " +
                   "and View ▸ Highlight Small Vectors marks the end of each one with a red square.",
            TextWrapping = TextWrapping.Wrap,
        });
        Children.Add(Row("Small vector threshold:", _threshold, "in"));
        Children.Add(new TextBlock
        {
            Text = $"Between {SmallVectors.MinInches:0.##} and {SmallVectors.MaxInches:0.##} inches. The standard is 0.25.",
            Foreground = AppTheme.Brush("Ui.Muted"),
        });

        Children.Add(new TextBlock { Text = "Vector Edit", FontWeight = FontWeight.Bold, Margin = UiScale.Space(0, 8, 0, 0) }.ScaledFont(15));
        Children.Add(Row("Tangency:", _tangency, "degrees"));
        Children.Add(new TextBlock
        {
            Text = "Where the two vectors at a node differ in direction by less than this, the node shows as a small cross instead of a square.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppTheme.Brush("Ui.Muted"),
        });
        Children.Add(_marquee);
        Children.Add(new TextBlock
        {
            Text = "On (the standard): a box dragged in Vector Edit deletes the nodes inside it, and Shift+box selects them. Off: the two are swapped. " +
                   "A box that holds a loop's start/end point deletes nothing and beeps.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppTheme.Brush("Ui.Muted"),
        });

        AttachedToVisualTree += (_, _) => Sync();
        Sync();
    }

    private static StackPanel Row(string caption, Control field, string unit)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(8) };
        row.Children.Add(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(field);
        row.Children.Add(new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Ui.Muted") });
        return row;
    }

    internal TextBox ThresholdBox => _threshold;
    internal TextBox TangencyBox => _tangency;
    internal CheckBox MarqueeBox => _marquee;

    private static bool TryParse(TextBox box, out double v) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ||
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out v);

    private void Commit()
    {
        if (_syncing) return;
        if (TryParse(_threshold, out double inches) && inches.ToString("0.####", CultureInfo.InvariantCulture) != ThresholdText)
            VectorEditSettings.SetSmallThresholdInches(inches);
        if (TryParse(_tangency, out double degrees) && degrees.ToString("0.##", CultureInfo.InvariantCulture) != TangencyText)
            VectorEditSettings.SetTangencyDegrees(degrees);
        Sync();   // shows what was accepted (the old value again for text that is not a number)
    }

    private static string ThresholdText => SmallVectors.ThresholdInches.ToString("0.####", CultureInfo.InvariantCulture);
    private static string TangencyText => VectorEditOptions.TangencyDegrees.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Shows the settings that are in force (also after a value was rejected).</summary>
    private void Sync()
    {
        _syncing = true;
        _threshold.Text = ThresholdText;
        _tangency.Text = TangencyText;
        _marquee.IsChecked = VectorEditOptions.MarqueeShiftSelects;
        _syncing = false;
    }
}
