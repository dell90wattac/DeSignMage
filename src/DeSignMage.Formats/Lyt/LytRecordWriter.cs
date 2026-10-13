namespace DeSignMage.Formats.Lyt;

/// <summary>
/// Turns parts and groups into the bytes of their records (without the leading link to the next record, which the file assembly adds).
/// Every field the format has is written; values nothing has been found to depend on are the ones real files hold most often
/// (the "templates" in <see cref="Defaults"/>). The layout is the one <see cref="LytParser"/> reads.
/// </summary>
internal static class LytRecordWriter
{
    private const byte ObjectTag = 0xBB;
    public const int StyleMagic = 0x11176;            // 70006, the style block generation of the current file format
    public const ushort FillVersion = 0x1786;         // 6022, the fill block generation written with it

    /// <summary>Values copied from records of real files for the fields that are not understood yet.</summary>
    private static class Defaults
    {
        public const uint FloatOne = 0x3F800000, FloatHalf = 0x3F000000, Nan = 0xFFC00000;

        public const uint FlagsMain = 0x24000000, FlagsMore = 0x1040, FlagsShape = 0x80000000, FlagsExtra = 0x8, FlagsLast = 0x80040020;
        public const uint FlagsPicture = 0x1200;
        public const string PictureProfile = "sRGB IEC61966-2.1";

        /// <summary>The effect parameter groups of a fill block, as found in blocks of plain and gradient fills.</summary>
        public static readonly uint[] EffectA = [0, 1008981770, 1017370378, 1, 1036831949, 1061997773, 1065353216];
        public static readonly uint[] EffectB = [1061997773, 1065353216];
        public static readonly uint[] EffectC = [2, 1022739087, 0, 0, 1065353216, 1065353216, 1065353216];
        public static readonly uint[] EffectD = [4, 1022739087, 1025758986, 3173242634, 1065353216];
        public static readonly uint[] EffectE = [2, 0, 0, 0];

        /// <summary>The same groups in blocks of picture fills.</summary>
        public static readonly uint[] PictureEffectA = [0, 1008981770, 1017370378, 1, 1031127695, 1061997773, 1065353216];
        public static readonly uint[] PictureEffectC = [2, 0, 0, 0, 1065353216, 1065353216, 1065353216];
        public static readonly uint[] PictureEffectD = [4, 0, 1032805417, 3180289065, 1065353216];

        /// <summary>Parameters after the effect groups of a bitmap fill (the last four hold 1.0 and 256.0 in real files).</summary>
        public static readonly uint[] BitmapParams = [0, 0, 0, 0, 0, FloatOne, 0x43800000, 0, 0x43800000];

        public const uint PictureParameter = 1052938076;
    }

    // ---------------------------------------------------------------- common header

    private static void WriteHeader(ByteWriter w, string name, int count, double orgX, double orgY, double sizeW, double sizeH,
        double left, double top, double right, double bottom, double scale, double xScale, double rotation, uint type, uint type2, int parent)
    {
        w.U8(ObjectTag);
        w.U32((uint)count);
        var nameBytes = LytEncoding.Ansi.GetBytes(name);
        w.U32((uint)nameBytes.Length + 1);
        w.Bytes(nameBytes);
        w.U8(0);
        w.F32(orgX); w.F32(orgY); w.F32(sizeW); w.F32(sizeH);
        w.F32(left); w.F32(top); w.F32(right); w.F32(bottom);
        w.F32(scale); w.F32(xScale); w.F32(rotation);
        w.F32(0);                                            // italic (slant)
        w.F32(0);                                            // shear
        w.U32(type); w.U32(type2);
        w.U16((ushort)parent);
    }

    // ---------------------------------------------------------------- groups

    public static byte[] WriteGroup(GroupRecord g)
    {
        var w = new ByteWriter();
        double width = Math.Max(g.Right - g.Left, 0), height = Math.Max(g.Top - g.Bottom, 0);
        WriteHeader(w, g.Name, g.Count, g.Left, g.Bottom, width, height, 0, height, width, 0, 1, 1, 0, 2, 0, g.ParentId);
        w.U32(0);                                            // group flags
        w.U16((ushort)g.Id);
        return w.ToArray();
    }

    // ---------------------------------------------------------------- parts

    public static byte[] WritePart(PartRecord p, uint linkStamp, uint linkCounter)
    {
        var w = new ByteWriter();
        WriteHeader(w, p.Name, p.Nodes.Count, p.OrgX, p.OrgY, p.SizeW, p.SizeH, p.ActualLeft, p.ActualTop, p.ActualRight, p.ActualBottom,
            p.Scale, p.XScale, p.Rotation, p.Type, p.Type2, p.ParentGroup);
        WriteBody(w, p, linkStamp, linkCounter);
        foreach (var n in p.Nodes) n.Write(w);
        return w.ToArray();
    }

    private static void WriteBody(ByteWriter w, PartRecord p, uint linkStamp, uint linkCounter)
    {
        bool picture = p.Kind == PartKind.Picture;
        bool text = p.Kind == PartKind.Text;

        w.U32(StyleMagic);
        w.Fixed(p.FontFile, 128);
        w.F32(p.CharSpacing);
        w.F32(100);                                          // word spacing, percent
        w.F32(0.5f);                                         // line spacing
        w.I16(p.Align);
        w.U32(p.LayerId);
        w.U32(linkStamp); w.U32(linkCounter);                // link id
        w.Zeros(8);                                          // source id
        w.Zeros(8);
        w.U32(0); w.U32(0); w.U32(3); w.U32(0x42200000);   // 40.0f
        w.NoColor();                                         // alternate color
        w.U32(0); w.U32(0); w.U32(0); w.U32(0);
        if (p.Color.IsValid) w.Color(p.Color); else w.NoColor();

        // Effect, shadow and bevel parameters that no feature of this writer uses: zero apart from one block of unit values.
        w.Zeros(12); w.F32(0.2f); w.F32(0.5f); w.F32(0.5f); w.U32(0);   // 28 bytes
        w.Zeros(40);
        w.Zeros(16);
        w.Zeros(8);
        w.Zeros(16);
        w.Zeros(50);

        // The 334-byte block that repeats the font and two colors.
        w.U32(8191);
        w.Fixed("", 50);
        w.Fixed(text ? p.FontFile : "", 128);
        w.Zeros(32);
        w.U16(0);
        w.U32(0);
        if (text) w.NoColor(); else w.Color(new LytColor("", "", LytColor.ModelRgb, 0, 0, 0, 0));
        if (p.Color.IsValid) w.Color(p.Color); else w.Color(new LytColor("", "", LytColor.ModelRgb, 0, 0, 0, 0));

        ushort flags72b = (ushort)((p.Fill is not null ? 0x1000 : 0) | (text ? 0x4000 : 0));
        w.U16(flags72b);
        if (p.Fill is not null) WriteFill(w, p.Fill);

        w.U32(Defaults.FlagsMain);
        w.U32(Defaults.FlagsMore);
        w.U32(picture ? Defaults.FlagsPicture : Defaults.FlagsShape);
        if (picture)
        {
            w.Fixed(Defaults.PictureProfile, 260);
        }
        else
        {
            w.U32(Defaults.FlagsExtra);                      // flags 6f75: nothing beyond the default
        }
        w.U32(Defaults.FlagsLast);
        w.U32(0);
    }

    // ---------------------------------------------------------------- fill block

    private static void WriteFill(ByteWriter w, FillSpec f)
    {
        bool picture = f.Type == 4;
        w.U16(FillVersion);
        w.U32(f.Type);
        w.U32(f.TransType);
        w.U32(150);

        for (int i = 0; i < 4; i++)
        {
            if (f.Colors[i].IsValid) w.Color(f.Colors[i]); else w.NoColor();
        }
        for (int i = 0; i < 8; i++) WriteFloatOrNan(w, f.Points[i], picture);
        for (int i = 0; i < 8; i++) WriteFloatOrNan(w, f.TransPoints[i], picture);
        w.U8(0);
        w.F32(f.TransMin);
        w.U32(0);
        w.U32(picture ? Defaults.PictureParameter : 0);
        w.U32((uint)f.NodeCount);

        for (int i = 0; i < 16; i++)
        {
            if (i < f.Extras.Count)
            {
                var n = f.Extras[i];
                w.F32(n.X); w.F32(n.Y); w.U32(0); w.U32(0);
                if (n.Color.IsValid) w.Color(n.Color); else w.NoColor();
                w.U16(n.Flag);
            }
            else
            {
                if (picture) { w.U32(Defaults.Nan); w.U32(Defaults.Nan); w.U32(Defaults.Nan); w.U32(Defaults.Nan); }
                else { w.F32(f.Points[6]); w.F32(0); w.U32(0); w.U32(0); }
                w.NoColor();
                w.U16(0);
            }
        }
        for (int i = 0; i < 20; i++) w.U32(Defaults.FloatHalf);
        w.U32(0); w.U32(0);
        w.U32(f.TransSwap ? 1u : 0u);
        w.F32(f.TransMax);
        w.U32(0);
        w.U32(Defaults.FloatOne); w.U32(Defaults.FloatOne); w.U32(0); w.U32(0);
        w.Zeros(260);                                        // source file paths
        w.Zeros(260);
        w.U32(0); w.U32(0); w.Zeros(8); w.U32(0);
        w.U16(0);                                            // source file is not embedded

        // effect parameters
        var a = picture ? Defaults.PictureEffectA : Defaults.EffectA;
        var c = picture ? Defaults.PictureEffectC : Defaults.EffectC;
        var d = picture ? Defaults.PictureEffectD : Defaults.EffectD;
        foreach (var v in a) w.U32(v);
        w.Color(new LytColor("255,255,255", "", LytColor.ModelRgb, 255, 255, 255, 0));
        foreach (var v in Defaults.EffectB) w.U32(v);
        w.Color(new LytColor("0,0,0", "", LytColor.ModelRgb, 0, 0, 0, 0));
        foreach (var v in c) w.U32(v);
        w.NoColor(); w.NoColor(); w.NoColor();
        foreach (var v in d) w.U32(v);
        w.NoColor();
        foreach (var v in Defaults.EffectE) w.U32(v);
        w.U32(0);
        w.NoColor();

        w.U32(Defaults.FloatOne); w.U32(0); w.Zeros(8);
        if (picture)
        {
            foreach (var v in Defaults.BitmapParams) w.U32(v);
            var image = f.Image ?? [];
            w.U32((uint)image.Length);
            w.U8(1);                                         // colour order
            w.Bytes(image);
        }
        else
        {
            w.Zeros(260);
        }
        w.U32(0);
    }

    private static void WriteFloatOrNan(ByteWriter w, float v, bool nan)
    {
        if (nan) w.U32(Defaults.Nan);
        else w.F32(v);
    }
}
