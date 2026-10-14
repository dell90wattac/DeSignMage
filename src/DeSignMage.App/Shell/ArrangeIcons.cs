using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvCanvas = Avalonia.Controls.Canvas;
using AvPath = Avalonia.Controls.Shapes.Path;

namespace DeSignMage.App.Shell;

/// <summary>
/// The icons of Arrange ▸ Align, Mirror and Locked, drawn here in a 20 x 20 box in the same plain style as the menu strip's: ink strokes and
/// fills, the accent color for the line or edge the objects line up on. They use the themed brushes, so they follow Dark Menus.
/// Besides the Context Bar they are what an Arrange tab of the menu strips would show.
/// </summary>
internal static class ArrangeIcons
{
    private static IBrush Ink => AppTheme.Brush("Ui.Icon");
    private static IBrush Accent => AppTheme.Brush("Ui.IconAccent");

    /// <summary>The short word a strip button would show for each command.</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["arrange.alignLeft"] = "Left", ["arrange.alignCenter"] = "Center", ["arrange.alignRight"] = "Right",
        ["arrange.alignTop"] = "Top", ["arrange.alignMiddle"] = "Middle", ["arrange.alignBottom"] = "Bottom",
        ["arrange.centerH"] = "H Layout", ["arrange.centerV"] = "V Layout", ["arrange.centerBoth"] = "Layout",
        ["arrange.mirrorH"] = "Mirror H", ["arrange.mirrorV"] = "Mirror V",
        ["arrange.lock"] = "Lock", ["arrange.unlockAll"] = "Unlock All",
    };

    public static IReadOnlyCollection<string> Ids => Drawn.Keys.ToArray();

    /// <summary>The icon of a command at <paramref name="size"/> pixels (at 100%), or null when it has none here.</summary>
    public static Control? For(string commandId, double size = 20) =>
        Drawn.TryGetValue(commandId, out var make) ? UiIcons.Box(make(), size, size) : null;

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

    private static readonly Dictionary<string, Func<Control>> Drawn = new()
    {
        // two bars lined up on a line (the accent) at the left / center / right; the same turned for the top / middle / bottom
        ["arrange.alignLeft"] = () => Box(Stroke("M3,2 V18", Accent, 1.6), Fill("M5.5,4.5 H16 V8.5 H5.5 Z"), Fill("M5.5,11.5 H11 V15.5 H5.5 Z")),
        ["arrange.alignCenter"] = () => Box(Stroke("M10,2 V18", Accent, 1.6), Fill("M4,4.5 H16 V8.5 H4 Z"), Fill("M6.5,11.5 H13.5 V15.5 H6.5 Z")),
        ["arrange.alignRight"] = () => Box(Stroke("M17,2 V18", Accent, 1.6), Fill("M4,4.5 H14.5 V8.5 H4 Z"), Fill("M9,11.5 H14.5 V15.5 H9 Z")),
        ["arrange.alignTop"] = () => Box(Stroke("M2,3 H18", Accent, 1.6), Fill("M4.5,5.5 H8.5 V16 H4.5 Z"), Fill("M11.5,5.5 H15.5 V11 H11.5 Z")),
        ["arrange.alignMiddle"] = () => Box(Stroke("M2,10 H18", Accent, 1.6), Fill("M4.5,4 H8.5 V16 H4.5 Z"), Fill("M11.5,6.5 H15.5 V13.5 H11.5 Z")),
        ["arrange.alignBottom"] = () => Box(Stroke("M2,17 H18", Accent, 1.6), Fill("M4.5,4 H8.5 V14.5 H4.5 Z"), Fill("M11.5,9 H15.5 V14.5 H11.5 Z")),

        // the page (dashed frame) with a shape centered on it: across, up and down, or both
        ["arrange.centerH"] = () => Box(Stroke("M2,3 H18 V17 H2 Z", Ink, 1.1, dashed: true), Stroke("M10,2 V18", Accent, 1.2), Fill("M6,7.5 H14 V12.5 H6 Z")),
        ["arrange.centerV"] = () => Box(Stroke("M2,3 H18 V17 H2 Z", Ink, 1.1, dashed: true), Stroke("M1,10 H19", Accent, 1.2), Fill("M7,6 H13 V14 H7 Z")),
        ["arrange.centerBoth"] = () => Box(Stroke("M2,3 H18 V17 H2 Z", Ink, 1.1, dashed: true), Stroke("M10,2 V18 M1,10 H19", Accent, 1.2), Fill("M7,7 H13 V13 H7 Z")),

        // a shape and its mirror image across a dashed line; vertical is the same turned a quarter
        ["arrange.mirrorH"] = () => Box(Stroke("M10,2 V18", Accent, 1.2, dashed: true), Stroke("M8,5 V15 H2 Z"), Fill("M12,5 V15 H18 Z")),
        ["arrange.mirrorV"] = () =>
        {
            var c = Box(Stroke("M10,2 V18", Accent, 1.2, dashed: true), Stroke("M8,5 V15 H2 Z"), Fill("M12,5 V15 H18 Z"));
            c.RenderTransform = new RotateTransform(90);
            c.RenderTransformOrigin = RelativePoint.Center;
            return c;
        },

        // a padlock, shut; Unlock All has it open
        ["arrange.lock"] = () => Box(Stroke("M7,9 V6.5 A3,3 0 0 1 13,6.5 V9"), Fill("M5,9 H15 V17.5 H5 Z"), Fill("M9.2,12 H10.8 V14.8 H9.2 Z", AppTheme.Brush("Ui.IconInverse"))),
        ["arrange.unlockAll"] = () => Box(Stroke("M7,9 V6.5 A3,3 0 0 1 13,6.5"), Fill("M5,9 H15 V17.5 H5 Z", Accent), Fill("M9.2,12 H10.8 V14.8 H9.2 Z", AppTheme.Brush("Ui.IconInverse"))),
    };
}
