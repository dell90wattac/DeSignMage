using System.Globalization;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Lyt;

/// <summary>
/// Packed 57-byte color record: a name, a vendor/series, four 16-bit components and a model
/// (1 = RGB 0..255, 2 = CMYK in 1/100 percent). Unnamed colors carry their values as text in <see cref="Name"/>.
/// </summary>
public readonly record struct LytColor(string Name, string Vendor, byte Model, ushort V0, ushort V1, ushort V2, ushort V3)
{
    /// <summary>Model 4 is a gray: black ink only, in the last component (0 to 10000 = 0 to 100 %). "SILVER METAL" is 27 %, which the reference program shows as gray 185.</summary>
    public const byte ModelRgb = 1, ModelCmyk = 2, ModelGray = 4;

    public bool IsValid => Model is ModelRgb or ModelCmyk or ModelGray;

    /// <summary>Display color. CMYK uses the plain device conversion; own ICC-managed look will differ slightly.</summary>
    public DocColor ToDocColor()
    {
        byte r, g, b;
        switch (Model)
        {
            case ModelRgb:
                r = Clamp(V0); g = Clamp(V1); b = Clamp(V2);
                break;
            case ModelCmyk:
                double c = V0 / 10000.0, m = V1 / 10000.0, y = V2 / 10000.0, k = V3 / 10000.0;
                r = ToByte((1 - c) * (1 - k)); g = ToByte((1 - m) * (1 - k)); b = ToByte((1 - y) * (1 - k));
                break;
            case ModelGray:
                r = g = b = ToByte(1 - V3 / 10000.0);
                break;
            default:
                r = g = b = 0;
                break;
        }
        return new DocColor(r, g, b, 255, DisplayName);
    }

    /// <summary>"3M 12 Black", "ARLON 83 Bright Orange"; null for colors that are only numbers.</summary>
    public string? DisplayName
    {
        get
        {
            var n = string.Join(' ', Name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (n.Length == 0 || n.All(ch => char.IsDigit(ch) || ch is ',' or '.' or '-')) return null;
            var v = Vendor.Trim();
            return v.Length == 0 ? n : $"{v} {n}";
        }
    }

    private static byte Clamp(ushort v) => (byte)Math.Min(v, (ushort)255);
    private static byte ToByte(double unit) => (byte)Math.Round(Math.Clamp(unit, 0, 1) * 255);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{(Model == ModelCmyk ? "cmyk" : "rgb")}({V0},{V1},{V2},{V3}) '{Name}'");
}

public enum LytNodeKind : byte { Move, Line, Arc, Char, Bezier }

/// <summary>
/// One 28-byte path node, in the object's own units with y up (node space). The node ends a segment at (X, Y); what
/// the segment is depends on <see cref="Kind"/>:
/// <list type="bullet">
/// <item><see cref="LytNodeKind.Arc"/>: circular, <see cref="Cx"/>/<see cref="Cy"/> is the centre and <see cref="Sweep"/> the
/// signed sweep in radians (positive = counter-clockwise) from the previous point.</item>
/// <item><see cref="LytNodeKind.Bezier"/>: cubic from the previous point with control points
/// (<see cref="C1X"/>, <see cref="C1Y"/>) and (<see cref="Cx"/>, <see cref="Cy"/>). Flag 0x400. The file stores
/// the second control point where an arc keeps its centre and the first where an arc keeps its sweep.</item>
/// <item><see cref="LytNodeKind.Char"/>: per-character marker in text objects (not geometry).</item>
/// </list>
/// </summary>
public readonly record struct LytNode(uint Flags, LytNodeKind Kind, float X, float Y, float Cx, float Cy, double Sweep, float C1X = 0, float C1Y = 0)
{
    public const uint FlagMove = 0x1, FlagArc = 0x4, FlagClose = 0x10, FlagHole = 0x20, FlagOuter = 0x40, FlagMarker = 0x80, FlagBezier = 0x400;

    /// <summary>The last node of a closed contour.</summary>
    public bool Closes => Kind != LytNodeKind.Char && (Flags & FlagClose) != 0;

    /// <summary>Second control point of a Bézier node.</summary>
    public float C2X => Cx;
    public float C2Y => Cy;

    public static LytNode Decode(ReadOnlySpan<byte> b)
    {
        uint flags = BitConverter.ToUInt32(b);
        float x = BitConverter.ToSingle(b[4..]), y = BitConverter.ToSingle(b[8..]);
        float cx = BitConverter.ToSingle(b[12..]), cy = BitConverter.ToSingle(b[16..]);
        if (flags != FlagMarker && (flags & FlagBezier) != 0 && (flags & FlagArc) == 0)
        {
            float c1x = BitConverter.ToSingle(b[20..]), c1y = BitConverter.ToSingle(b[24..]);
            return new LytNode(flags, LytNodeKind.Bezier, x, y, cx, cy, 0, c1x, c1y);
        }
        double sweep = BitConverter.ToDouble(b[20..]);
        var kind = flags == FlagMarker ? LytNodeKind.Char
            : (flags & FlagArc) != 0 ? LytNodeKind.Arc
            : (flags & FlagMove) != 0 ? LytNodeKind.Move
            : LytNodeKind.Line;
        return new LytNode(flags, kind, x, y, cx, cy, sweep);
    }
}

/// <summary>An image or gradient/effect fill. Only what the importer needs is kept.</summary>
public sealed class LytFill
{
    /// <summary>The fill block's own version (4006 / 6008 / 6022 ...).</summary>
    public int Version { get; init; }

    /// <summary>0 = solid, 4 = bitmap, 9 = multi-bitmap; other values are gradients/effects.</summary>
    public uint Type { get; init; }

    /// <summary>Embedded image file (JPEG or PNG) for bitmap fills.</summary>
    public byte[]? Image { get; init; }

    /// <summary>"image/jpeg" or "image/png".</summary>
    public string? ImageMediaType { get; init; }

    /// <summary>Path of a linked (not embedded) image.</summary>
    public string? LinkedImagePath { get; init; }

    /// <summary>The gradient and transparency part of the block (null for the oldest block versions).</summary>
    public LytBlend? Blend { get; init; }
}

/// <summary>One of the blend's colors beyond the first four: its control point, color and node flag (1 = highlight).</summary>
public readonly record struct LytBlendNode(float X, float Y, LytColor Color, ushort Flag);

/// <summary>
/// The gradient (GradType) and transparency (TransType) fields of a fill block, as stored: control points in the part's node space (y up),
/// the first four colors and nodes, the remaining ones in <see cref="ExtraNodes"/>. GradType: 0 none, 1 linear, 2 circle, 3 ellipse, 5 horizontal,
/// 6 vertical, 7 flat. TransType: 0 none, 1 flat, 2 linear, 3 circle, 4 ellipse, 0xC horizontal, 0xD vertical.
/// </summary>
public sealed class LytBlend
{
    public uint GradType { get; init; }
    public uint TransType { get; init; }
    public LytColor[] Colors { get; } = new LytColor[4];
    public float[] Points { get; } = new float[8];
    public float[] TransPoints { get; } = new float[8];
    public float TransMin { get; set; }
    public float TransMax { get; set; } = 1;
    public bool TransSwap { get; set; }
    public int NodeCount { get; set; } = 2;
    public List<LytBlendNode> ExtraNodes { get; } = [];
}

/// <summary>Style data of a part (a drawn shape or text).</summary>
public sealed class LytStyle
{
    public int BodyMagic { get; init; }

    /// <summary>"HELVM.FNT" (a legacy outline font), "_Arial.ptt" (a TrueType font) or empty for non-text.</summary>
    public string FontFile { get; init; } = "";

    public float CharSpacing { get; init; }
    public float WordSpacing { get; init; }
    public float LineSpacing { get; init; }

    /// <summary>0 = left, 1 = centre, 2 = right.</summary>
    public short Align { get; init; }

    /// <summary>Matches <see cref="LytLayer.Id"/>.</summary>
    public uint LayerId { get; init; }

    /// <summary>The object's color (its fill). Unset (model 0) for bitmaps and cut lines.</summary>
    public LytColor Color { get; init; }

    /// <summary>A second color record. Two early files keep the fill here when <see cref="Color"/> is unset.</summary>
    public LytColor AlternateColor { get; init; }

    public ushort Flags { get; init; }
    public bool HasOutline => (Flags & 0x800) != 0;
    public LytFill? Fill { get; init; }

    /// <summary>Why the rest of the style block was not read, if it was not.</summary>
    public string? NotDecoded { get; init; }
}

/// <summary>A part (shape/text) or a group record.</summary>
public sealed class LytObject
{
    public int Offset { get; init; }
    public int Length { get; init; }
    public bool IsGroup { get; init; }
    public string Name { get; init; } = "";

    /// <summary>Path node count for parts; direct child count for groups.</summary>
    public int NodeCount { get; init; }

    /// <summary>Position in inches, page space (origin bottom-left, y up).</summary>
    public double OrgX { get; init; }
    public double OrgY { get; init; }
    public double SizeW { get; init; }
    public double SizeH { get; init; }

    /// <summary>Node units to inches.</summary>
    public double Scale { get; init; }

    /// <summary>Horizontal stretch (1 = none): width = nodes × <see cref="Scale"/> × this, height = nodes × <see cref="Scale"/>.</summary>
    public double XScale { get; init; }

    /// <summary>Radians, counter-clockwise.</summary>
    public double Rotation { get; init; }

    public double Italic { get; init; }
    public double Shear { get; init; }
    public uint Type { get; init; }
    public uint Type2 { get; init; }

    /// <summary>Id of the group that contains this object; 0 = none.</summary>
    public int ParentGroup { get; init; }

    /// <summary>Groups only: this group's id (16-bit in the file).</summary>
    public int GroupId { get; init; }

    public LytStyle? Style { get; init; }
    public LytNode[] Nodes { get; init; } = [];

    /// <summary>The path of a "text on a path" effect stored with this text record (null for every other record).</summary>
    public LytTextOnPath? TextOnPath { get; init; }

    /// <summary>Reasons parts of this object were skipped.</summary>
    public List<string> Notes { get; } = [];

    public bool IsText => Style is { FontFile.Length: > 0 };
}

public sealed record LytGuide(double X, double Y, double AngleRadians);

public sealed record LytLayer(uint Id, string Name, LytColor Color, uint Flags);

/// <summary>A parsed .lyt file.</summary>
public sealed class LytFile
{
    /// <summary>0x40/0x41 = first generation, 0x42..0x49 = second, 0x4A = current.</summary>
    public int FormatVersion { get; init; }
    public string VersionName { get; init; } = "";
    public int DeclaredObjectCount { get; init; }
    public string Title { get; init; } = "";

    /// <summary>Page size in inches.</summary>
    public double PageWidth { get; init; }
    public double PageHeight { get; init; }

    public int HeaderSubVersion { get; set; }
    public string? ColorProfile { get; set; }
    public string? BackgroundImage { get; set; }
    public List<LytGuide> Guides { get; } = [];
    public List<LytColor> DocumentColors { get; } = [];
    public List<LytLayer> Layers { get; } = [];
    public List<LytObject> Objects { get; } = [];

    /// <summary>Embedded preview, a complete JPEG or BMP file (null if the file has none).</summary>
    public byte[]? Thumbnail { get; init; }

    /// <summary>Everything that was skipped or looked wrong while reading.</summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// A text-on-a-path effect record: the path as nodes (in the text record's own node space, not yet y-negated; the last node carries the "last" flag,
/// which here does NOT close anything) and the curve compensation fraction.
/// </summary>
public sealed record LytTextOnPath(IReadOnlyList<LytNode> Nodes, double CurveCompensation);