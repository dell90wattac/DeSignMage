using DeSignMage.Core.Geometry;
using SkiaSharp;

namespace DeSignMage.Rendering;

/// <summary>
/// The colors of what the program itself draws on the editing canvas (R19): the area around the page, the page, the page edge, plain vector
/// outlines, guides, the marquee and so on. The design's own colors are never part of this. <see cref="Light"/> is the classic look (a white
/// page on a white workspace); <see cref="Dark"/> is the Dark Canvas look. Only the on-screen canvas uses a theme: exports, saved files,
/// thumbnails and the clipboard always draw with <see cref="Light"/>.
/// </summary>
public sealed record CanvasTheme
{
    /// <summary>The workspace around the page.</summary>
    public required SKColor Pasteboard { get; init; }
    /// <summary>The page itself; null leaves it the same as the workspace (the classic look).</summary>
    public SKColor? Page { get; init; }
    public required SKColor PageEdge { get; init; }
    /// <summary>The two squares of the transparency checkerboard (Show Transparency, R27): neutral greys, lighter or darker with the canvas.</summary>
    public required SKColor CheckerA { get; init; }
    public required SKColor CheckerB { get; init; }
    /// <summary>Outline view: loops of the three kinds (outer edge, hole, open path).</summary>
    public required SKColor Border { get; init; }
    public required SKColor Interior { get; init; }
    public required SKColor Open { get; init; }
    public required SKColor Guide { get; init; }
    /// <summary>The dashed rectangle of a marquee and a crop box.</summary>
    public required SKColor Marquee { get; init; }
    public required SKColor Caret { get; init; }
    /// <summary>Loop numbers: the digits and the halo behind them.</summary>
    public required SKColor Label { get; init; }
    public required SKColor LabelHalo { get; init; }

    public static readonly CanvasTheme Light = new()
    {
        Pasteboard = SKColors.White,
        Page = null,
        PageEdge = new SKColor(0x60, 0x60, 0x60),
        CheckerA = new SKColor(0xFF, 0xFF, 0xFF),
        CheckerB = new SKColor(0xCC, 0xCC, 0xCC),
        Border = new SKColor(0x00, 0x00, 0xFF),
        Interior = new SKColor(0xFF, 0x00, 0x00),
        Open = new SKColor(0x00, 0x00, 0x00),
        Guide = new SKColor(0x1F, 0x3F, 0xFF),
        Marquee = SKColors.Black,
        Caret = SKColors.Black,
        Label = SKColors.Black,
        LabelHalo = SKColors.White,
    };

    /// <summary>
    /// Neutral greys only (R = G = B), so judging a design's colors is not skewed: the workspace is the darker grey and the page a bit
    /// lighter. Everything the program draws stays visible on it: plain outlines turn light, blue and red are lifted so they do not sink into the
    /// grey, and the selection handles keep inverting whatever lies under them.
    /// </summary>
    public static readonly CanvasTheme Dark = new()
    {
        Pasteboard = new SKColor(0x2A, 0x2A, 0x2A),
        Page = new SKColor(0x42, 0x42, 0x42),
        PageEdge = new SKColor(0x9A, 0x9A, 0x9A),
        CheckerA = new SKColor(0x4C, 0x4C, 0x4C),
        CheckerB = new SKColor(0x38, 0x38, 0x38),
        Border = new SKColor(0x6A, 0x88, 0xFF),
        Interior = new SKColor(0xFF, 0x4A, 0x4A),
        Open = new SKColor(0xE6, 0xE6, 0xE6),
        Guide = new SKColor(0x7F, 0x9B, 0xFF),
        Marquee = new SKColor(0xEE, 0xEE, 0xEE),
        Caret = new SKColor(0xF2, 0xF2, 0xF2),
        Label = new SKColor(0xF2, 0xF2, 0xF2),
        LabelHalo = new SKColor(0x20, 0x20, 0x20),
    };

    /// <summary>The outline-view color of a loop of this kind.</summary>
    public SKColor ColorOf(LoopKind kind) => kind switch
    {
        LoopKind.Border => Border,
        LoopKind.Interior => Interior,
        _ => Open,
    };
}
