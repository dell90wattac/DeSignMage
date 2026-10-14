using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DeSignMage.App.Shell;

/// <summary>
/// The pieces the Export and Import dialogs are made of, so they look the same: small section labels over their rows, labels in one column and
/// fields in the next, muted notes, units after fields, one spacing scale (which follows the interface text size).
/// </summary>
internal static class DialogParts
{
    public static TextBlock Note(string? text = null) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Brush("Ui.Muted") };

    public static TextBlock Unit(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Ui.Unit") };

    /// <summary>A field with its unit right after it ("px").</summary>
    public static Control WithUnit(Control field, string unit)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(8) };
        p.Children.Add(field);
        p.Children.Add(Unit(unit));
        return p;
    }

    public static TextBlock SectionLabel(string text) => new()
    {
        Text = text, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.Brush("Ui.Caption"), Margin = UiScale.Space(0, 0, 0, 6),
    };

    public static StackPanel Stack(params Control[] items)
    {
        var p = new StackPanel { Spacing = UiScale.Sp(6) };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    public static Control Section(string label, Control content)
    {
        var p = new StackPanel();
        p.Children.Add(SectionLabel(label));
        p.Children.Add(content);
        return p;
    }

    /// <summary>A label in a column of its own and the field next to it, so all fields of the dialog line up.</summary>
    public static Control Row(string label, Control field)
    {
        var grid = new Grid { ColumnSpacing = UiScale.Sp(10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Ui.Label") }.ScaledMinWidth(92);
        Grid.SetColumn(l, 0);
        Grid.SetColumn(field, 1);
        field.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(l);
        grid.Children.Add(field);
        return grid;
    }
}
