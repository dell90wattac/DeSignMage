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
            b.Click += (_, _) => { session.SetTool(spec.ToolId); b.IsChecked = session.ActiveTool.Id == spec.ToolId; };   // a click on the tool that is already active must not leave its button looking released
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

        // One-click guides (user, 2026-10-18): three command buttons in a group of their own, apart from the tools. They are commands, not
        // tools, so they are plain buttons that never stay pressed and the Tools strip (which mirrors the tools) does not list them.
        var guideButtons = new List<(Button Button, GuidePlace Place)>();
        foreach (var (name, place, tip, edge) in GuideButtonSpecs)
        {
            var g = new Button { Name = name, Content = UiIcons.Box(GuideEdgeIcon(edge), 20, 20), Focusable = false }
                .ScaledWidth(30, icon: true).ScaledHeight(30, icon: true);
            g.Classes.Add("tb");
            if (guideButtons.Count == 0) g.Margin = new Avalonia.Thickness(0, 6, 0, 0);
            ToolTip.SetTip(g, tip);
            g.Click += (_, _) => session.AddGuideOnSelection(place);
            host.Children.Add(g);
            guideButtons.Add((g, place));
        }

        void SyncGuideButtons()
        {
            foreach (var (b, place) in guideButtons) b.IsEnabled = session.CanGuideSelection(place);   // nothing selected: nothing to put the guide on
        }
        session.Selection.Changed += (_, _) => SyncGuideButtons();
        session.Document.Changed += (_, _) => SyncGuideButtons();
        SyncGuideButtons();

        void Sync()
        {
            foreach (var (b, id) in buttons) b.IsChecked = session.ActiveTool.Id == id;
            colorMode.IsChecked = !session.ShowFillColors;   // pressed = vector mode
        }
        session.ToolChanged += (_, _) => Sync();
        session.DisplayChanged += (_, _) => Sync();
        Sync();
    }

    private enum GuideEdge { Top, Center, Bottom, Side }

    private static readonly (string Name, GuidePlace Place, string Tip, GuideEdge Edge)[] GuideButtonSpecs =
    [
        ("ToolboxGuideTop", GuidePlace.TopEdge, "Guide at top: put a horizontal guide on the top edge of the selected object or group", GuideEdge.Top),
        ("ToolboxGuideCenter", GuidePlace.HorizontalCenter, "Guide at center: put a horizontal guide through the middle of the selected object or group", GuideEdge.Center),
        ("ToolboxGuideBottom", GuidePlace.BottomEdge, "Guide at bottom: put a horizontal guide on the bottom edge of the selected object or group", GuideEdge.Bottom),
        ("ToolboxGuideSide", GuidePlace.LeftEdge, "Guide at side: put a vertical guide on the left edge of the selected object or group", GuideEdge.Side),
    ];

    /// <summary>Our own icon for the one-click guides: a box with a dashed guide line along one of its edges.</summary>
    private static Control GuideEdgeIcon(GuideEdge edge)
    {
        var c = new Avalonia.Controls.Canvas { Width = 18, Height = 18 };
        var box = new Rectangle { Width = 10, Height = 10, Stroke = AppTheme.Brush("Ui.Icon"), StrokeThickness = 1.3 };
        Avalonia.Controls.Canvas.SetLeft(box, 5);
        Avalonia.Controls.Canvas.SetTop(box, 4);
        c.Children.Add(box);
        string line = edge switch { GuideEdge.Top => "M0,4 H18", GuideEdge.Center => "M0,9 H18", GuideEdge.Bottom => "M0,14 H18", _ => "M5,0 V18" };
        c.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse(line), Stroke = AppTheme.Brush("Ui.IconAccent"), StrokeThickness = 1.4,
            StrokeDashArray = [2.5, 1.5],
        });
        return c;
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
