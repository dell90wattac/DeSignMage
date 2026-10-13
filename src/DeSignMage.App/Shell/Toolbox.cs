using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using DeSignMage.Core.Editing;

namespace DeSignMage.App.Shell;

/// <summary>One button on the left Toolbox. Icons are simple original drawings (no third-party art).</summary>
public sealed record ToolButtonSpec(string ToolId, string ToolTip, Func<Control> Icon);

/// <summary>The vertical Toolbox, built from <see cref="Buttons"/>.</summary>
public static class Toolbox
{
    public static readonly ToolButtonSpec[] Buttons =
    [
        new("select", "Layout tool (1)", () => new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse("M 3,1 L 3,16 L 7,12.5 L 9.7,18 L 12,17 L 9.4,11.6 L 14,11.2 Z"),
            Fill = AppTheme.Brush("Ui.Icon"), Width = 16, Height = 18, Stretch = Stretch.Uniform,
        }),
        new("vector", "Vector Edit tool (2): double-click a vector for its menu", () => new Avalonia.Controls.Canvas
        {
            Width = 18, Height = 16,
            Children =
            {
                new Avalonia.Controls.Shapes.Path
                {
                    Data = Avalonia.Media.Geometry.Parse("M 2,13 C 4,2 13,1 16,6"),
                    Stroke = AppTheme.Brush("Ui.IconAccent"), StrokeThickness = 1.3,
                },
                NodeMark(0, 11), NodeMark(14, 4),
            },
        }),
        new("vectordraw", "Vector Draw tool: click to place nodes, drag for a curve, double-click the first node to close", () => new Avalonia.Controls.Canvas
        {
            Width = 18, Height = 20,
            Children =
            {
                // the regular arrow with a small square node at its tip (the same art as the cursor)
                new Avalonia.Controls.Shapes.Path
                {
                    Data = Avalonia.Media.Geometry.Parse("M 4,3 L 4,17 L 7.5,14 L 10,19 L 12,18 L 9.6,13 L 14,12.6 Z"),
                    Fill = AppTheme.Brush("Ui.Icon"),
                },
                NodeSquare(),
            },
        }),        new("text", "Text tool: click to type, double-click text with the Layout tool to edit it", () => new TextBlock
        {
            Text = "A", FontSize = 17, FontWeight = FontWeight.Bold, FontFamily = new FontFamily("Times New Roman"),
            Foreground = AppTheme.Brush("Ui.Icon"), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        }),
        new("rectangle", "Rectangle (Ctrl+Shift+G)", () => new Rectangle
        {
            Width = 16, Height = 12, Stroke = AppTheme.Brush("Ui.Icon"), StrokeThickness = 1.5,
        }),
        new("ellipse", "Ellipse (Ctrl+Shift+E)", () => new Ellipse
        {
            Width = 17, Height = 12, Stroke = AppTheme.Brush("Ui.Icon"), StrokeThickness = 1.5,
        }),
    ];

    private static Control NodeSquare()
    {
        var r = new Rectangle { Width = 5, Height = 5, Fill = AppTheme.Brush("Ui.IconInverse"), Stroke = AppTheme.Brush("Ui.Icon"), StrokeThickness = 1 };
        Avalonia.Controls.Canvas.SetLeft(r, 1.5);
        Avalonia.Controls.Canvas.SetTop(r, 0.5);
        return r;
    }

    private static Control NodeMark(double x, double y)
    {
        var r = new Rectangle { Width = 4, Height = 4, Fill = AppTheme.Brush("Ui.Icon") };
        Avalonia.Controls.Canvas.SetLeft(r, x);
        Avalonia.Controls.Canvas.SetTop(r, y);
        return r;
    }

    public static void Populate(Panel host, EditorSession session)
    {
        var buttons = new List<(ToggleButton Button, string Id)>();
        foreach (var spec in Buttons)
        {
            // The icon is built at 18 px; the Viewbox makes it (and the button) follow the icon scale.
            var b = new ToggleButton { Content = UiIcons.Box(spec.Icon(), 20, 20), Focusable = false }.ScaledWidth(30, icon: true).ScaledHeight(30, icon: true);
            b.Classes.Add("tb");
            ToolTip.SetTip(b, spec.ToolTip);
            b.Click += (_, _) => session.SetTool(spec.ToolId);
            host.Children.Add(b);
            buttons.Add((b, spec.ToolId));
        }

        // Color mode (user, 2026-10-12): the Show Fill Colors setting in the Toolbox. PRESSED = vector mode (colors hidden,
        // everything as plain outlines, pictures as frames); released = colors shown, which is how the app starts. Same setting
        // as View ▸ Show Fill Colors and F3 (the Main Toolbar no longer repeats it).
        var colorMode = new ToggleButton { Name = "ToolboxColorMode", Content = UiIcons.Box(ColorModeIcon(), 20, 20), Focusable = false }
            .ScaledWidth(30, icon: true).ScaledHeight(30, icon: true);
        colorMode.Classes.Add("tb");
        colorMode.Margin = new Avalonia.Thickness(0, 6, 0, 0);
        ToolTip.SetTip(colorMode, "Vector mode: hide colors and show plain vectors (press again to show colors)");
        colorMode.Click += (_, _) => session.ShowFillColors = !session.ShowFillColors;
        host.Children.Add(colorMode);

        void Sync()
        {
            foreach (var (b, id) in buttons) b.IsChecked = session.ActiveTool.Id == id;
            colorMode.IsChecked = !session.ShowFillColors;   // pressed = vector mode
        }
        session.ToolChanged += (_, _) => Sync();
        session.DisplayChanged += (_, _) => Sync();
        Sync();
    }

    /// <summary>Our own color-mode icon: three color bars (red, green, blue).</summary>
    private static Control ColorModeIcon()
    {
        var c = new Avalonia.Controls.Canvas { Width = 18, Height = 18 };
        var colors = new[] { Brushes.Red, Brushes.LimeGreen, Brushes.RoyalBlue };
        for (int i = 0; i < 3; i++)
        {
            var bar = new Rectangle { Width = 5, Height = 14, Fill = colors[i], Stroke = AppTheme.Brush("Ui.Icon"), StrokeThickness = 0.5 };
            Avalonia.Controls.Canvas.SetLeft(bar, 1.5 + i * 5);
            Avalonia.Controls.Canvas.SetTop(bar, 2);
            c.Children.Add(bar);
        }
        return c;
    }
}
