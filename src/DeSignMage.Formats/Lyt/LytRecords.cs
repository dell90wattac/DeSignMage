namespace DeSignMage.Formats.Lyt;

/// <summary>One path node as it is written: the mirror image of <see cref="LytNode"/>, with the flag bits already combined.</summary>
internal readonly record struct OutNode(uint Flags, float X, float Y, float Cx, float Cy, double Sweep, float C1X, float C1Y, bool Bezier)
{
    public static OutNode Point(uint flags, float x, float y) => new(flags, x, y, 0, 0, 0, 0, 0, false);

    public static OutNode Arc(uint flags, float x, float y, float cx, float cy, double sweep) => new(flags, x, y, cx, cy, sweep, 0, 0, false);

    /// <summary>A cubic: the file stores the second control point where an arc keeps its centre and the first where it keeps its sweep.</summary>
    public static OutNode Cubic(uint flags, float x, float y, float c1x, float c1y, float c2x, float c2y) =>
        new(flags | LytNode.FlagBezier, x, y, c2x, c2y, 0, c1x, c1y, true);

    /// <summary>Per-character marker of a text record: x on the baseline, the lowest point of the glyph (<paramref name="bottom"/>), the em height, the ink width.</summary>
    public static OutNode Marker(float x, float bottom, float em, double inkWidth) => new(LytNode.FlagMarker, x, 0, bottom, em, inkWidth, 0, 0, false);

    public bool IsMarker => Flags == LytNode.FlagMarker;

    public void Write(ByteWriter w)
    {
        w.U32(Flags);
        w.F32(X); w.F32(Y); w.F32(Cx); w.F32(Cy);
        if (Bezier) { w.F32(C1X); w.F32(C1Y); }
        else w.F64(Sweep);
    }
}

/// <summary>What kind of record a part is; it decides the template the style block is filled from.</summary>
internal enum PartKind { Shape, Text, Picture }

/// <summary>The gradient, fade or picture block of a part (a record's fill block, version 6022).</summary>
internal sealed class FillSpec
{
    /// <summary>0 = none (a fade over the plain color), 1 linear, 2 circle, 3 ellipse, 5 horizontal, 6 vertical, 4 = picture.</summary>
    public uint Type;

    /// <summary>0 none, 2 linear, 3 circle, 4 ellipse, 0xC horizontal, 0xD vertical.</summary>
    public uint TransType;

    public LytColor[] Colors { get; } = new LytColor[4];
    public float[] Points { get; } = new float[8];
    public float[] TransPoints { get; } = new float[8];
    public float TransMin, TransMax = 1;
    public bool TransSwap;
    public int NodeCount = 2;

    /// <summary>Nodes beyond the first four (index 4 up): point, color, flag (1 = highlight).</summary>
    public List<LytBlendNode> Extras { get; } = [];

    /// <summary>The complete image file of a picture fill.</summary>
    public byte[]? Image;
}

/// <summary>A part (shape, text, picture) ready to be written. Lengths are inches, angles radians, nodes are in the part's own units (y up).</summary>
internal sealed class PartRecord
{
    public PartKind Kind;
    public string Name = "";
    public List<OutNode> Nodes { get; } = [];

    public double OrgX, OrgY;
    public double Scale = 1, XScale = 1, Rotation;
    public double SizeW, SizeH;
    public double ActualLeft, ActualTop, ActualRight, ActualBottom;

    public uint Type = 1, Type2 = 0x4000;
    public int ParentGroup;
    public uint LayerId;

    /// <summary>The object's color (its fill); <see cref="LytColor.IsValid"/> false writes none (cut lines).</summary>
    public LytColor Color;

    public string FontFile = "";
    public float CharSpacing = 1;
    public short Align;
    public FillSpec? Fill;

    /// <summary>Page-space bounds in inches (origin bottom-left, y up), for the extents of the groups around the part.</summary>
    public double PageLeft, PageBottom, PageRight, PageTop;
}

/// <summary>A group record: membership only. Groups come after the parts in the file, each naming its parent.</summary>
internal sealed class GroupRecord
{
    public int Id;
    public int ParentId;
    public string Name = "Group";

    /// <summary>Direct children (parts and groups) that were written.</summary>
    public int Count;

    public double Left = double.PositiveInfinity, Bottom = double.PositiveInfinity, Right = double.NegativeInfinity, Top = double.NegativeInfinity;

    public void Include(double left, double bottom, double right, double top)
    {
        Left = Math.Min(Left, left); Bottom = Math.Min(Bottom, bottom);
        Right = Math.Max(Right, right); Top = Math.Max(Top, top);
    }
}
