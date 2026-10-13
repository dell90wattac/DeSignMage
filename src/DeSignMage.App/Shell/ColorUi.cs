using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using DeSignMage.Core.Model;
using AvCanvas = Avalonia.Controls.Canvas;
using SPath = Avalonia.Controls.Shapes.Path;

namespace DeSignMage.App.Shell;

/// <summary>Helpers shared by the color UI.</summary>
internal static class ColorUi
{
    public static Color ToAvalonia(DocColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>
    /// A swatch box; null paint draws a red "no color" cross. The box keeps the paint in its <see cref="Control.Tag"/>
    /// so the UI can be inspected without reading pixels.
    /// </summary>
    public static Control Swatch(Paint? paint, double size)
    {
        var grid = new Grid { Tag = paint }.ScaledWidth(size, icon: true).ScaledHeight(size, icon: true);
        grid.Children.Add(new Border
        {
            Background = paint is SolidPaint sp ? new SolidColorBrush(ToAvalonia(sp.Color)) : Brushes.White,
            BorderBrush = AppTheme.Brush("Ui.SwatchBorder"),
            BorderThickness = new Thickness(1),
        });
        if (paint is null)
        {
            // Drawn to fill the box (not at fixed coordinates), so the cross follows the swatch when icons scale.
            grid.Children.Add(new SPath { Data = Avalonia.Media.Geometry.Parse("M0,0 L1,1 M1,0 L0,1"), Stretch = Stretch.Fill, Margin = new Thickness(2), Stroke = Brushes.Red, StrokeThickness = 1.5 });
        }
        return grid;
    }

    /// <summary>The paint of the object's fill or stroke, whichever the target names.</summary>
    public static Paint? PaintOf(ObjectStyle style, ColorTarget target) =>
        target == ColorTarget.Fill ? style.Fill : style.Stroke?.Paint;

    /// <summary>Tooltip text for a color: its name and vendor, its values and the model it was specified in.</summary>
    public static string Describe(DocColor c)
    {
        string values = c.Model == ColorModel.Cmyk && c.Cmyk is { } k
            ? $"C {k.C:0.##}  M {k.M:0.##}  Y {k.Y:0.##}  K {k.K:0.##}  (CMYK)"
            : $"R {c.R}  G {c.G}  B {c.B}  {c.ToHex()}";
        return c.DisplayName is { } n ? $"{n}\n{values}" : values;
    }

    /// <summary>A small flat button that can look pressed (class "on").</summary>
    public static Button IconButton(Control icon, string tip, Action click)
    {
        var b = new Button { Content = icon, Focusable = false };
        b.Classes.Add("tb");
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A small flat button with a text label.</summary>
    public static Button TextButton(string text, string tip, Action click)
    {
        var b = new Button { Content = new TextBlock { Text = text }.ScaledFont(11), Focusable = false };
        b.Classes.Add("tb");
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    public static void SetOn(Button b, bool on) => b.Classes.Set("on", on);

    public static Control Cross(double size = 16)
    {
        return new SPath { Data = Avalonia.Media.Geometry.Parse("M0,0 L1,1 M1,0 L0,1"), Stretch = Stretch.Fill, Margin = new Thickness(3), Stroke = Brushes.Red, StrokeThickness = 2.5 }
            .ScaledWidth(size, icon: true).ScaledHeight(size, icon: true);
    }
}

/// <summary>Original icons for the stroke corner (join) and line-end (cap) buttons, drawn as vector paths.</summary>
internal static class StrokeIcons
{
    public static Control Join(LineJoin join)
    {
        var path = new SPath
        {
            Data = Avalonia.Media.Geometry.Parse("M4,17 L4,5 L16,5"),
            Stroke = AppTheme.Brush("Ui.Icon"),
            StrokeThickness = 6,
            StrokeJoin = join switch { LineJoin.Round => PenLineJoin.Round, LineJoin.Bevel => PenLineJoin.Bevel, _ => PenLineJoin.Miter },
            StrokeLineCap = PenLineCap.Flat,
        };
        return UiIcons.Box(new AvCanvas { Width = 20, Height = 20, Children = { path } });
    }

    public static Control Cap(LineCap cap)
    {
        var line = new SPath
        {
            Data = Avalonia.Media.Geometry.Parse("M6,10 L14,10"),
            Stroke = AppTheme.Brush("Ui.Icon"),
            StrokeThickness = 7,
            StrokeLineCap = cap switch { LineCap.Round => PenLineCap.Round, LineCap.Square => PenLineCap.Square, _ => PenLineCap.Flat },
        };
        var guide = new SPath { Data = Avalonia.Media.Geometry.Parse("M6,2 L6,18 M14,2 L14,18"), Stroke = AppTheme.Brush("Ui.Muted"), StrokeThickness = 1, StrokeDashArray = [2, 2] };   // where the line ends
        return UiIcons.Box(new AvCanvas { Width = 20, Height = 20, Children = { guide, line } });
    }
}

