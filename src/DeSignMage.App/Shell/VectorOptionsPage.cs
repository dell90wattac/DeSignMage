using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DeSignMage.Core.Editing;

namespace DeSignMage.App.Shell;

/// <summary>
/// Options ▸ Vector editing: how short a vector must be to count as small. Small vectors are drawn with small nodes in the Vector Edit
/// tool, counted in the Info Line, and a box drawn over small nodes deletes them. Applies as you type (Enter or leaving the field).
/// </summary>
public sealed class VectorOptionsPage : StackPanel
{
    private readonly TextBox _threshold = new() { Name = "SmallVectorThresholdBox" };

    public VectorOptionsPage()
    {
        Spacing = UiScale.Sp(10);
        _threshold.ScaledWidth(90);
        _threshold.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
        };
        _threshold.LostFocus += (_, _) => Commit();

        Children.Add(new TextBlock { Text = "Small vectors", FontWeight = FontWeight.Bold }.ScaledFont(15));
        Children.Add(new TextBlock
        {
            Text = "A vector shorter than this is small. In the Vector Edit tool the nodes that touch a small vector are drawn smaller than the others, " +
                   "the Info Line counts the small vectors, and a box drawn over small nodes deletes them (the box turns red as a warning).",
            TextWrapping = TextWrapping.Wrap,
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(8) };
        row.Children.Add(new TextBlock { Text = "Small vector threshold:", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(_threshold);
        row.Children.Add(new TextBlock { Text = "in", VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Ui.Muted") });
        Children.Add(row);
        Children.Add(new TextBlock
        {
            Text = $"Between {SmallVectors.MinInches:0.##} and {SmallVectors.MaxInches:0.##} inches. The standard is 0.25.",
            Foreground = AppTheme.Brush("Ui.Muted"),
        });

        AttachedToVisualTree += (_, _) => Sync();
        Sync();
    }

    internal TextBox ThresholdBox => _threshold;

    private void Commit()
    {
        if (double.TryParse(_threshold.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ||
            double.TryParse(_threshold.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
            VectorEditSettings.SetSmallThresholdInches(v);
        Sync();   // shows what was accepted (the old value again for text that is not a number)
    }

    /// <summary>Shows the threshold that is in force (also after a value was rejected).</summary>
    private void Sync() => _threshold.Text = SmallVectors.ThresholdInches.ToString("0.####", CultureInfo.InvariantCulture);
}
