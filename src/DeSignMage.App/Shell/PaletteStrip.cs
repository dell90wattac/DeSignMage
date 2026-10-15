using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Shell;

/// <summary>One cell of the strip: a built-in color.</summary>
public sealed record SwatchCell(DocColor Color);

/// <summary>
/// The far-right vertical Palette Strip: the red X (No Color) on top, then a small built-in set of plain colors in a scrollable
/// column. On a swatch, left click sets the fill and right click sets the stroke; the canvas keeps the right button for zoom.
/// Hovering shows the color's name and values.
/// </summary>
public static class PaletteStrip
{
    public const double CellSize = 18;

    /// <summary>The built-in colors: black, white, greys and basic hues (plain RGB values).</summary>
    public static IReadOnlyList<DocColor> BuiltInColors { get; } = new[]
    {
        ("Black", "#000000"), ("Dark Grey", "#404040"), ("Grey", "#808080"), ("Light Grey", "#C0C0C0"), ("White", "#FFFFFF"),
        ("Red", "#FF0000"), ("Orange", "#FF8000"), ("Yellow", "#FFFF00"), ("Lime", "#80FF00"), ("Green", "#00A000"),
        ("Cyan", "#00FFFF"), ("Blue", "#0000FF"), ("Purple", "#8000FF"), ("Magenta", "#FF00FF"), ("Pink", "#FF80C0"),
        ("Brown", "#804000"),
    }.Select(c => DocColor.FromHex(c.Item2, c.Item1)).ToArray();

    public static void Populate(ContentControl host, EditorSession session)
    {
        var items = new ItemsControl
        {
            Name = "PaletteStripItems",
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel { Orientation = Orientation.Vertical }),
            ItemTemplate = new FuncDataTemplate<SwatchCell>((cell, _) => cell is null ? new Panel() : MakeCell(cell, session)),
        };
        var scroll = new ScrollViewer
        {
            Name = "PaletteStripScroll",
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = items,
        };
        // A fixed red X above the strip: removes all color, fill and stroke, from the selection.
        var noColor = new Button
        {
            Name = "PaletteStripNoColor",
            Content = new TextBlock { Text = "✕", Foreground = Avalonia.Media.Brushes.Red, FontWeight = Avalonia.Media.FontWeight.Bold },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Avalonia.Thickness(0, 1),
            Margin = new Avalonia.Thickness(1, 1, 1, 2),
        };
        ToolTip.SetTip(noColor, "Remove all color (fill and stroke)");
        noColor.Click += (_, _) => session.ApplyNoColor();
        var dock = new DockPanel();
        DockPanel.SetDock(noColor, Dock.Top);
        dock.Children.Add(noColor);
        dock.Children.Add(scroll);
        host.Content = dock;

        items.ItemsSource = BuiltInColors.Select(c => new SwatchCell(c)).ToList();
    }

    private static Control MakeCell(SwatchCell cell, EditorSession session)
    {
        Paint? paint = cell.Color is { } c ? new SolidPaint(c) : null;
        var swatch = ColorUi.Swatch(paint, CellSize);
        swatch.Margin = new Avalonia.Thickness(1, 1, 1, 0);
        ToolTip.SetTip(swatch, ColorUi.Describe(cell.Color) + "\nLeft click: fill   Right click: stroke");
        swatch.PointerPressed += (_, e) =>
        {
            var kind = e.GetCurrentPoint(swatch).Properties.PointerUpdateKind;
            if (kind == PointerUpdateKind.LeftButtonPressed) session.ApplyFill(paint);
            else if (kind == PointerUpdateKind.RightButtonPressed) session.ApplyStrokePaint(paint);
            e.Handled = true;
        };
        return swatch;
    }
}
