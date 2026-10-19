using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DeSignMage.App.Shell;

namespace DeSignMage.App.Recovery;

/// <summary>
/// Options ▸ Auto-save: switch the background recovery copies on or off, how many minutes apart they are made, and the biggest design that
/// gets one. Choices apply as they are made (Enter or leaving a field) and are remembered.
/// </summary>
public sealed class AutoSaveOptionsPage : StackPanel
{
    private readonly CheckBox _enabled = new() { Name = "AutoSaveEnabledBox", Content = "Make a recovery copy of my design in the background" };
    private readonly TextBox _minutes = new() { Name = "AutoSaveMinutesBox" };
    private readonly TextBox _largest = new() { Name = "AutoSaveLargestBox" };
    private readonly StackPanel _details = new() { Name = "AutoSaveDetails", Spacing = UiScale.Sp(10) };
    private bool _syncing;

    public AutoSaveOptionsPage()
    {
        Spacing = UiScale.Sp(10);
        _minutes.ScaledWidth(70);
        _largest.ScaledWidth(80);
        foreach (var box in new[] { _minutes, _largest })
        {
            var b = box;
            b.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            b.LostFocus += (_, _) => Commit();
        }
        _enabled.IsCheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            AutoSaveSettings.SetEnabled(_enabled.IsChecked == true);
            Sync();
        };

        _details.Children.Add(Row("Make a copy every", _minutes, "minutes (while there are unsaved changes)"));
        _details.Children.Add(Row("Don't copy designs bigger than", _largest, "MB"));

        Children.Add(new TextBlock { Text = "Auto-save", FontWeight = FontWeight.Bold }.ScaledFont(15));
        Children.Add(new TextBlock
        {
            Text = "While a design has changes you have not saved, DeSign Mage keeps a recovery copy of it. If the program or the computer stops before you save, " +
                   "the next start offers to restore it. The copy is written quietly in the background, so you can keep working. Your own file is never touched: " +
                   "the copy is removed when you save, or close the design and choose not to save.",
            TextWrapping = TextWrapping.Wrap,
        });
        Children.Add(_enabled);
        Children.Add(_details);
        Children.Add(new TextBlock
        {
            Text = "A design with many large pictures can be big; the limit keeps the recovery copies from using too much of the disk.",
            TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Brush("Ui.Muted"),
        });

        AttachedToVisualTree += (_, _) => Sync();
        Sync();
    }

    internal CheckBox EnabledBox => _enabled;
    internal TextBox MinutesBox => _minutes;
    internal TextBox LargestBox => _largest;
    internal StackPanel Details => _details;

    private static StackPanel Row(string caption, Control field, string unit)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(8) };
        row.Children.Add(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(field);
        row.Children.Add(new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Ui.Muted") });
        return row;
    }

    private void Commit()
    {
        if (_syncing) return;
        if (int.TryParse(_minutes.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes)) AutoSaveSettings.SetMinutes(minutes);
        if (int.TryParse(_largest.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mb)) AutoSaveSettings.SetLargestMb(mb);
        Sync();   // shows what was accepted (the old value again for text that is not a number, the nearest allowed value for one out of range)
    }

    private void Sync()
    {
        _syncing = true;
        _enabled.IsChecked = AutoSaveSettings.Enabled;
        _minutes.Text = AutoSaveSettings.Minutes.ToString(CultureInfo.InvariantCulture);
        _largest.Text = AutoSaveSettings.LargestMb.ToString(CultureInfo.InvariantCulture);
        _details.IsVisible = AutoSaveSettings.Enabled;   // nothing to choose while it is off
        _syncing = false;
    }
}
