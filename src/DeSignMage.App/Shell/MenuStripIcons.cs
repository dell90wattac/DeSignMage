using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using AvCanvas = Avalonia.Controls.Canvas;
using AvPath = Avalonia.Controls.Shapes.Path;

namespace DeSignMage.App.Shell;

/// <summary>
/// The icons of the Context Bar's menu strip (R25), one per command, drawn here as simple vector shapes in a 20 x 20 box, in the same plain
/// style as the Toolbox icons: ink-colored strokes and fills, one accent color for the part that does the work. They use the themed icon
/// brushes, so they follow Dark Menus, and <see cref="UiIcons.Box"/> makes them follow the icon scale. The three tool commands reuse the
/// Toolbox's own icons.
/// </summary>
internal static class MenuStripIcons
{
    private static IBrush Ink => AppTheme.Brush("Ui.Icon");
    private static IBrush Accent => AppTheme.Brush("Ui.IconAccent");
    private static IBrush Paper => AppTheme.Brush("Ui.IconInverse");

    /// <summary>The command ids that have a drawn icon.</summary>
    public static IReadOnlyCollection<string> Ids => Drawn.Keys;

    /// <summary>The icon of a command at <paramref name="size"/> pixels (at 100%), or null when none is drawn (the strip then shows initials).</summary>
    public static Control? For(string commandId, double size = 20)
    {
        if (commandId.StartsWith("tool.", StringComparison.Ordinal))
        {
            string toolId = commandId["tool.".Length..].ToLowerInvariant();
            if (Toolbox.Buttons.FirstOrDefault(b => b.ToolId == toolId) is { } spec) return UiIcons.Box(spec.Icon(), size, size);
        }
        return Drawn.TryGetValue(commandId, out var make) ? UiIcons.Box(make(), size, size) : null;
    }

    /// <summary>The short word shown on a strip button (the tooltip keeps the full command name).</summary>
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["image.vectorize"] = "Vectorize", ["image.crop"] = "Crop", ["image.resample"] = "Resample",
        ["image.rotateCW"] = "Rotate R", ["image.rotateCCW"] = "Rotate L", ["image.flipH"] = "Flip H", ["image.flipV"] = "Flip V",
        ["image.invert"] = "Invert", ["image.zoom11"] = "1:1",
        ["effects.outline"] = "Outline", ["effects.inline"] = "Inline", ["effects.knife"] = "Knife", ["effects.weld"] = "Weld",
        ["effects.commonWeld"] = "C. Weld", ["effects.cookieCutter"] = "Cookie",
        ["effects.gradientBlend"] = "Gradient",
        ["effects.softShadow"] = "Soft Sh.", ["effects.glow"] = "Glow", ["effects.feather"] = "Feather",
        ["effects.easyBevel"] = "Bevel", ["effects.shadow"] = "Drop",["effects.shadowExtruded"] = "Extruded", ["effects.shadowPerspective"] = "Persp.", ["effects.shadowCast"] = "Cast",
        ["tool.select"] = "Layout", ["tool.vector"] = "Vector", ["tool.vectorDraw"] = "Draw", ["tool.loop"] = "Loops", ["tools.showTransparency"] = "Transparency", ["tools.options"] = "Options",
    };

    /// <summary>The label of a command: its short word, or (for a command without one) the first word of its name.</summary>
    public static string LabelFor(AppCommand command)
    {
        if (Labels.TryGetValue(command.Id, out var label)) return label;
        var words = command.Header.Replace("_", "").Replace(".", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? command.Id : words[0];
    }

    /// <summary>The command ids that have a short label of their own.</summary>
    public static IReadOnlyCollection<string> LabeledIds => Labels.Keys;

    private static AvPath Stroke(string data, IBrush? brush = null, double width = 1.4, bool dashed = false) => new()
    {
        Data = Avalonia.Media.Geometry.Parse(data),
        Stroke = brush ?? Ink,
        StrokeThickness = width,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
        StrokeDashArray = dashed ? [2, 2] : null,
    };

    private static AvPath Fill(string data, IBrush? brush = null) => new() { Data = Avalonia.Media.Geometry.Parse(data), Fill = brush ?? Ink };

    private static AvCanvas Box(params Control[] parts)
    {
        var c = new AvCanvas { Width = 20, Height = 20 };
        foreach (var p in parts) c.Children.Add(p);
        return c;
    }

    private const string Circle5At7 = "M2,10 A5,5 0 1 1 12,10 A5,5 0 1 1 2,10 Z";    // radius 5, centered at (7, 10)
    private const string Circle5At13 = "M8,10 A5,5 0 1 1 18,10 A5,5 0 1 1 8,10 Z";   // radius 5, centered at (13, 10)

    private static readonly Dictionary<string, Func<Control>> Drawn = new()
    {
        // ---- Image ----
        // a few picture pixels, and a smooth vector curve with nodes drawn from them
        ["image.vectorize"] = () => Box(Fill("M2,2 H6 V6 H2 Z M6,6 H10 V10 H6 Z"), Stroke("M7,17 C9,9 14,19 18,7", Accent), Fill("M5.5,15.5 H8.5 V18.5 H5.5 Z M16.5,5.5 H19.5 V8.5 H16.5 Z", Accent)),
        // the two crop marks
        ["image.crop"] = () => Box(Stroke("M6,2 V14 H18"), Stroke("M2,6 H14 V18")),
        // a picture with a smaller block of pixels, and an arrow to the new size
        ["image.resample"] = () => Box(Stroke("M3,3 H17 V17 H3 Z"), Fill("M3,3 H10 V10 H3 Z"), Stroke("M11,11 L16,16 M12.5,16 H16 V12.5", Accent)),
        // a round arrow, clockwise; the counter-clockwise one is its mirror image
        ["image.rotateCW"] = () => Box(Stroke("M4.5,10 A5.5,5.5 0 1 1 10,15.5"), Stroke("M13,12.5 L10,15.5 L13,18.5")),
        ["image.rotateCCW"] = () =>
        {
            var c = Box(Stroke("M4.5,10 A5.5,5.5 0 1 1 10,15.5"), Stroke("M13,12.5 L10,15.5 L13,18.5"));
            c.RenderTransform = new ScaleTransform(-1, 1);
            c.RenderTransformOrigin = RelativePoint.Center;
            return c;
        },
        // two triangles, one the mirror image of the other across a dashed line; vertical is the same turned a quarter
        ["image.flipH"] = () => Box(Stroke("M10,2 V18", Accent, 1.2, dashed: true), Stroke("M8,5 V15 H2 Z"), Fill("M12,5 V15 H18 Z")),
        ["image.flipV"] = () =>
        {
            var c = Box(Stroke("M10,2 V18", Accent, 1.2, dashed: true), Stroke("M8,5 V15 H2 Z"), Fill("M12,5 V15 H18 Z"));
            c.RenderTransform = new RotateTransform(90);
            c.RenderTransformOrigin = RelativePoint.Center;
            return c;
        },
        // a disc, half of it flipped to the opposite tone
        ["image.invert"] = () => Box(Stroke("M3,10 A7,7 0 1 0 17,10 A7,7 0 1 0 3,10 Z"), Fill("M10,3 A7,7 0 0 0 10,17 Z")),
        ["image.zoom11"] = () => Box(Stroke("M1.5,4.5 H18.5 V15.5 H1.5 Z", Ink, 1.2), new TextBlock
        {
            Text = "1:1", FontSize = 9, FontWeight = FontWeight.Bold, Foreground = Ink,
            [AvCanvas.LeftProperty] = 3.5, [AvCanvas.TopProperty] = 5.0,
        }),

        // ---- Effects ----
        // a solid shape with an outline around it / a solid shape with a line inside it
        ["effects.outline"] = () => Box(Fill("M7,7 H13 V13 H7 Z"), Stroke("M3,3 H17 V17 H3 Z")),
        ["effects.inline"] = () => Box(Fill("M2,2 H18 V18 H2 Z"), Stroke("M6,6 H14 V14 H6 Z", Paper, 1.2)),
        // a shape and the cutting line through it
        ["effects.knife"] = () => Box(Stroke("M3,5 H17 V15 H3 Z"), Stroke("M1.5,18.5 L18.5,1.5", Accent, 1.8)),
        // two discs becoming one shape / only where two discs overlap
        ["effects.weld"] = () => Box(Fill(Circle5At7), Fill(Circle5At13)),
        ["effects.commonWeld"] = () => Box(Stroke(Circle5At7, Ink, 1.2), Stroke(Circle5At13, Ink, 1.2), Fill("M10,6 A5,5 0 0 1 10,14 A5,5 0 0 1 10,6 Z", Accent)),
        // a block with a round piece cut out of it
        ["effects.cookieCutter"] = () => Box(Fill("M2,3 H18 V17 H2 Z"), Fill("M5,10 A5,5 0 1 1 15,10 A5,5 0 1 1 5,10 Z", Paper), Stroke("M5,10 A5,5 0 1 1 15,10 A5,5 0 1 1 5,10 Z", Accent, 1.2, dashed: true)),

        // a solid shape with its offset shadow behind it / the shadow swept out to a moved copy / swept to a smaller one / a slanted flipped copy under it
        // a block that is solid at one end and fades to nothing at the other, and the control line over it
        ["effects.gradientBlend"] = () => Box(Fill("M2,3 H18 V17 H2 Z", Ink), Fill("M10,3 H18 V17 H10 Z", Paper), Stroke("M4,10 H16", Accent, 1.4), Fill("M3,8.5 H6 V11.5 H3 Z M14,8.5 H17 V11.5 H14 Z", Accent)),
        ["effects.shadow"] = () =>Box(Fill("M8,8 H18 V18 H8 Z", Accent), Fill("M2,2 H12 V12 H2 Z")),
        ["effects.shadowExtruded"] = () => Box(Fill("M2,8 L8,2 H18 V12 L12,18 H2 Z", Accent), Stroke("M2,8 H12 V18 M12,8 L18,2", Ink, 1.2), Fill("M2,8 H12 V18 H2 Z")),
        ["effects.shadowPerspective"] = () => Box(Fill("M8,4 L18,7 V13 L8,16 Z", Accent), Fill("M2,4 H8 V16 H2 Z")),
        ["effects.shadowCast"] = () => Box(Fill("M2,2 H10 V10 H2 Z"), Fill("M2,10 H10 L18,18 H10 Z", Accent)),
        // a solid shape with a blurry (stroked wide, dotted) shadow behind it / a shape with a halo around it / a shape whose edge fades (dotted rim)
        ["effects.softShadow"] = () => Box(Fill("M8,8 H18 V18 H8 Z", Accent), Stroke("M7,7 H19 V19 H7 Z", Accent, 1.2, dashed: true), Fill("M2,2 H12 V12 H2 Z")),
        ["effects.glow"] = () => Box(Stroke("M4,4 H16 V16 H4 Z", Accent, 3.2), Stroke("M2,2 H18 V18 H2 Z", Accent, 1.0, dashed: true), Fill("M6,6 H14 V14 H6 Z")),
        ["effects.feather"] = () => Box(Fill("M4,4 H16 V16 H4 Z"), Stroke("M2.5,2.5 H17.5 V17.5 H2.5 Z", Ink, 1.0, dashed: true), Stroke("M5,5 H15 V15 H5 Z", Paper, 1.2, dashed: true)),

        // a block with a light rim on its upper left and a dark one on its lower right
        ["effects.easyBevel"] = () => Box(Fill("M3,3 H17 V17 H3 Z"), Stroke("M3.5,16.5 V3.5 H16.5", Paper, 1.8), Stroke("M16.5,3.5 V16.5 H3.5", Accent, 1.8)),

        // ---- Tools (select, vector and vector draw come from the Toolbox) ----
        // a closed loop with its start point marked
        ["tool.loop"] = () => Box(Stroke("M3,10 A7,5 0 1 1 17,10 A7,5 0 1 1 3,10 Z"), Fill("M1,8.5 H4 V11.5 H1 Z", Accent)),
        // a little checkerboard: the page seen as transparent
        ["tools.showTransparency"] = () => Box(Stroke("M2,2 H18 V18 H2 Z", Ink, 1.2), Fill("M2,2 H10 V10 H2 Z M10,10 H18 V18 H10 Z")),
        // a cog: a ring with its teeth
        ["tools.options"] = () => Box(Stroke("M15,10 A5,5 0 1 1 5,10 A5,5 0 1 1 15,10 Z", Ink, 1.6), Fill("M8.5,8.5 H11.5 V11.5 H8.5 Z"),
            Stroke("M10,1.5 V4.5 M10,15.5 V18.5 M1.5,10 H4.5 M15.5,10 H18.5 M4,4 L6.1,6.1 M13.9,13.9 L16,16 M4,16 L6.1,13.9 M13.9,6.1 L16,4", Ink, 1.8)),
    };
}
