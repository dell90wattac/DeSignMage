using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Vectorize;

public enum VectorizeMode
{
    /// <summary>Reduce to <see cref="VectorizeOptions.Colors"/> colors; one filled part per color region.</summary>
    Color,
    /// <summary>Threshold to black and white; only the black shapes are produced (the white is the background).</summary>
    BlackAndWhite,
    /// <summary>Like black and white, but the shapes are produced as unfilled outlines.</summary>
    Outline,
}

public enum VectorizeLayering
{
    /// <summary>
    /// Each part extends underneath the parts painted on top of it, so there are no gaps or hairline seams between
    /// neighbors and holes appear only where something below really shows through. Parts overlap.
    /// </summary>
    Stacked,
    /// <summary>Parts abut exactly and do not overlap (what a vinyl cutter wants). Neighbors share the same outline.</summary>
    CutOut,
}

/// <summary>User-facing tracing settings. Defaults suit a typical logo or clipart; photos want more colors and a larger speckle size.</summary>
public sealed record VectorizeOptions
{
    public VectorizeMode Mode { get; init; } = VectorizeMode.Color;
    public VectorizeLayering Layering { get; init; } = VectorizeLayering.Stacked;

    /// <summary>Number of colors to reduce to (2-256). Color mode only.</summary>
    public int Colors { get; init; } = 16;

    /// <summary>Black/white cut-off 1-255 for the black-and-white and outline modes; 0 picks one automatically (Otsu).</summary>
    public int Threshold { get; init; }

    /// <summary>Regions smaller than this many pixels (at working resolution) are merged into their closest neighbor. The noise filter.</summary>
    public int SpeckleSize { get; init; } = 8;

    /// <summary>Passes of a 3x3 median filter applied before the colors are chosen (0-3). Removes photo grain and JPEG noise.</summary>
    public int Denoise { get; init; }

    /// <summary>Curve smoothness 0-1. 0 follows the pixels closely (more nodes); 1 gives few, flowing curves.</summary>
    public double Smoothness { get; init; } = 0.5;

    /// <summary>A bend sharper than this many degrees is kept as a corner instead of being rounded off (10-170).</summary>
    public double CornerAngle { get; init; } = 50;

    /// <summary>Largest side, in pixels, the image is traced at. Bigger images are shrunk first (and the result scaled back).</summary>
    public int MaxWorkingSize { get; init; } = 1400;

    /// <summary>Images smaller than this are enlarged (smoothly) before tracing, which gives smoother curves on small logos.</summary>
    public int MinWorkingSize { get; init; } = 1000;

    /// <summary>Do not produce the largest region touching the image border (typically the background).</summary>
    public bool OmitBackground { get; init; }

    /// <summary>Stroke width in output pixels for the outline mode.</summary>
    public double OutlineWidth { get; init; } = 1;

    public VectorizeOptions Normalized() => this with
    {
        Colors = Math.Clamp(Colors, 2, 256),
        Threshold = Math.Clamp(Threshold, 0, 255),
        SpeckleSize = Math.Clamp(SpeckleSize, 0, 100_000),
        Denoise = Math.Clamp(Denoise, 0, 3),
        Smoothness = Math.Clamp(Smoothness, 0, 1),
        CornerAngle = Math.Clamp(CornerAngle, 10, 170),
        MaxWorkingSize = Math.Clamp(MaxWorkingSize, 32, 8192),
        MinWorkingSize = Math.Clamp(MinWorkingSize, 0, 4096),
        OutlineWidth = Math.Max(0.01, OutlineWidth),
    };
}

/// <summary>One traced part: a closed path (several subpaths where it has holes, filled even-odd) and its colors.</summary>
public sealed record VectorShape(PathGeometry Path, DocColor? Fill, DocColor? Stroke);

/// <summary>The traced picture. Coordinates are in pixels of the <i>source</i> image (x right, y down); shapes are listed bottom to top.</summary>
public sealed class VectorizeResult
{
    public VectorizeResult(int width, int height, IReadOnlyList<VectorShape> shapes, IReadOnlyList<DocColor> palette, double strokeWidth)
    {
        Width = width;
        Height = height;
        Shapes = shapes;
        Palette = palette;
        StrokeWidth = strokeWidth;
    }

    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<VectorShape> Shapes { get; }
    /// <summary>The colors the image was reduced to (some may not appear if their regions were merged away or omitted).</summary>
    public IReadOnlyList<DocColor> Palette { get; }
    /// <summary>Stroke width, in source pixels, for shapes that have a stroke color.</summary>
    public double StrokeWidth { get; }

    public int NodeCount => Shapes.Sum(s => s.Path.Verbs.Count(v => v is PathVerb.CubicTo or PathVerb.LineTo));
}

