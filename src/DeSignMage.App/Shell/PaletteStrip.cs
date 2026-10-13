using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Shell;

/// <summary>One cell of the strip: a palette color.</summary>
public sealed record SwatchCell(DocColor Color);

/// <summary>
/// The far-right vertical palette strip ("resource color palette"): the active palette's colors in a scrollable
/// column. On a swatch, left click sets the fill and right click sets the stroke; the
/// canvas keeps the right button for zoom. Hovering shows the color's name and values.
/// Palette colors keep their name, vendor and model when applied.
/// </summary>
public static class PaletteStrip
{
    public const double CellSize = 18;

    public static void Populate(ContentControl host, EditorSession session, ColorUiState state)
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

        void Fill()
        {
            var cells = new List<SwatchCell>(state.ActivePalette.Colors.Count);
            cells.AddRange(state.ActivePalette.Colors.Select(c => new SwatchCell(c)));
            items.ItemsSource = cells;
            scroll.Offset = default;
        }

        Fill();
        state.PaletteChanged += (_, _) => Fill();
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
