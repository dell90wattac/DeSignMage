using System.Buffers.Binary;

namespace DeSignMage.Formats.Lyt;

/// <summary>
/// Reads legacy layout files (.lyt), format generations 0x40 to 0x4A.
/// Anything not understood is skipped and reported in <see cref="LytFile.Warnings"/>; only a structurally
/// unreadable file throws <see cref="LytFormatException"/>.
/// </summary>
public static class LytParser
{
    public const int NodeSize = 28;
    private const byte ObjectTag = 0xBB;
    private const int PrefixSize = 74;

    public static LytFile Parse(string path) => Parse(File.ReadAllBytes(path));

    public static LytFile Parse(byte[] data)
    {
        var (thumb, baseOffset) = SplitThumbnail(data);
        bool jpeg = thumb is { Length: > 1 } && thumb[0] == 0xFF;
        int docEnd = data.Length - (jpeg ? 4 : 0);
        if (docEnd - baseOffset < PrefixSize) throw new LytFormatException("The file is too short to be a layout.");

        var r = new ByteReader(data, baseOffset, docEnd);
        int version = r.I16();
        int count = (int)r.U32();
        int first = (int)r.U32();
        int last = (int)r.U32();
        r.Skip(4);
        string title = r.FixedString(40);
        double pageW = r.F32(), pageH = r.F32();
        r.Skip(8);
        _ = last;

        string? versionName = VersionName(version);
        if (versionName is null) throw new LytFormatException(UnsupportedMessage(version));

        var file = new LytFile
        {
            FormatVersion = version,
            VersionName = versionName,
            DeclaredObjectCount = count,
            Title = title,
            PageWidth = pageW,
            PageHeight = pageH,
            Thumbnail = thumb,
        };

        try { ReadHeaderBody(r, file, version >= 0x4A); }
        catch (LytFormatException e) { file.Warnings.Add($"Header settings (layers, guides) could not be read: {e.Message}"); }

        ReadObjects(data, baseOffset, docEnd, first, file);
        if (file.Objects.Count != count)
            file.Warnings.Add($"The header says {count} objects but {file.Objects.Count} were read.");
        return file;
    }

    private static string? VersionName(int v) => v switch
    {
        0x40 or 0x41 => "6.0",
        >= 0x42 and <= 0x49 => "6.5",
        0x4A => "7.0",
        _ => null,
    };

    private static string UnsupportedMessage(int v) => v switch
    {
        0x36 => "This layout was saved by an old version of the program that created it (format 0x36), which is not supported yet.",
        7 => "This is a clipart or template file (format 7), not a layout. Clipart/template import is not supported yet.",
        12 => "This is a font file (format 12), not a layout.",
        14 => "This layout uses an old format (14) that is not supported yet.",
        _ => $"Unknown layout format version 0x{v:X}.",
    };

    // ---------------------------------------------------------------- thumbnail

    /// <summary>JPEG thumbnails (current generation): the length is the u32 at the end of the file. BMP thumbnails (first generation): bfSize.</summary>
    internal static (byte[]? Thumbnail, int Length) SplitThumbnail(byte[] d)
    {
        if (d.Length > 8 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF && d[3] == 0xE0)
        {
            long n = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(d.Length - 4));
            if (n <= 4 || n >= d.Length || d[n - 2] != 0xFF || d[n - 1] != 0xD9) n = JpegEnd(d);
            if (n <= 0) throw new LytFormatException("The embedded JPEG preview is damaged.");
            return (d[..(int)n], (int)n);
        }
        if (d.Length > 14 && d[0] == (byte)'B' && d[1] == (byte)'M')
        {
            long n = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(2));
            if (n <= 14 || n >= d.Length) throw new LytFormatException("The embedded bitmap preview is damaged.");
            return (d[..(int)n], (int)n);
        }
        return (null, 0);
    }

    /// <summary>Offset just past the EOI marker of the JPEG at the start of <paramref name="d"/>, or 0.</summary>
    private static int JpegEnd(byte[] d)
    {
        int i = 2;
        while (i + 4 <= d.Length)
        {
            if (d[i] != 0xFF) return 0;
            byte m = d[i + 1];
            if (m == 0xD9) return i + 2;
            if (m is 0x01 or 0xD8 || (m >= 0xD0 && m <= 0xD7)) { i += 2; continue; }
            int seg = (d[i + 2] << 8) | d[i + 3];
            if (m == 0xDA)
            {
                int j = i + 2 + seg;
                while (true)
                {
                    j = Array.IndexOf(d, (byte)0xFF, j);
                    if (j < 0 || j + 1 >= d.Length) return 0;
                    if (d[j + 1] == 0 || (d[j + 1] >= 0xD0 && d[j + 1] <= 0xD7)) { j += 2; continue; }
                    break;
                }
                i = j;
                continue;
            }
            i += 2 + seg;
        }
        return 0;
    }

    // ---------------------------------------------------------------- header

    private static LytColor ReadColor(ByteReader r)
    {
        string name = r.FixedString(28), vendor = r.FixedString(20);
        ushort a = r.U16(), b = r.U16(), c = r.U16(), d = r.U16();
        byte model = r.U8();
        return new LytColor(name, vendor, model, a, b, c, d);
    }

    /// <summary>The header body: gated by a sub-version (601..611 seen). Reads up to and including the layer table.</summary>
    private static void ReadHeaderBody(ByteReader r, LytFile file, bool v7)
    {
        uint sub = r.U32();
        file.HeaderSubVersion = (int)sub;
        r.Skip(2);                                           // document flags
        if (sub > 600)
        {
            r.Skip(4);
            if (sub > 0x259) for (int i = 0; i < 4; i++) r.String16();   // info strings
        }
        if (sub > 0x25B)
        {
            file.BackgroundImage = NullIfEmpty(r.String16());
            r.Skip(12);
        }
        if (sub > 0x25C)
        {
            file.ColorProfile = NullIfEmpty(r.String16());
            r.Skip(4);
            if (sub > 0x25D)
            {
                r.Skip(4);
                if (!v7 && sub > 0x25E)
                {
                    r.Skip(4 + 16);
                    if (sub > 0x25F) r.Skip(4);
                }
            }
        }
        r.Skip(4 + 4 + 2);
        int n16 = r.U16();
        r.Skip(16 * n16);

        int guides = r.U16();
        for (int i = 0; i < guides; i++)
        {
            r.Skip(4);                                       // kind: 1 = horizontal, 2 = vertical (also implied by the angle)
            double x = r.F32(), y = r.F32(), angle = r.F64();
            file.Guides.Add(new LytGuide(x, y, angle));
        }

        r.Skip(57);                                          // current color
        int colors = r.I32();
        if (colors is < 0 or > 100_000) throw new LytFormatException($"Implausible color count {colors}.");
        for (int i = 0; i < colors; i++) file.DocumentColors.Add(ReadColor(r));

        int layers = r.I32();
        if (layers is < 0 or > 10_000) throw new LytFormatException($"Implausible layer count {layers}.");
        for (int i = 0; i < layers; i++)
        {
            var color = ReadColor(r);
            string name = r.FixedString(101);
            uint id = r.U32(), flags = r.U32();
            r.Skip(4);
            file.Layers.Add(new LytLayer(id, name, color, flags));
        }
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    // ---------------------------------------------------------------- objects

    private static void ReadObjects(byte[] data, int baseOffset, int docEnd, int first, LytFile file)
    {
        var seen = new HashSet<int>();
        int off = first;
        int docLength = docEnd - baseOffset;
        while (off != 0)
        {
            if (off < PrefixSize || off + 4 > docLength || !seen.Add(off))
                throw new LytFormatException($"Object chain is broken at offset {off}.");
            int next = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(baseOffset + off));
            int end = next != 0 ? next : docLength;
            if (end <= off || end > docLength) throw new LytFormatException($"Object at offset {off} has a bad length.");
            file.Objects.Add(ReadObject(data, baseOffset, off, end, file));
            off = next;
        }
    }

    private static LytObject ReadObject(byte[] data, int baseOffset, int off, int end, LytFile file)
    {
        var r = new ByteReader(data, baseOffset + off + 4, baseOffset + end);
        byte tag = r.U8();
        if (tag != ObjectTag) throw new LytFormatException($"Object at offset {off} does not start with its marker byte.");
        int nodeCount = (int)r.U32();
        int nameLen = (int)r.U32();
        if (nameLen < 0 || nameLen > 100_000) throw new LytFormatException($"Object at offset {off} has an implausible name length.");
        string name = r.StringWithNul(nameLen);
        double orgX = r.F32(), orgY = r.F32(), sizeW = r.F32(), sizeH = r.F32();
        r.Skip(16);                                          // actual bounds (left, top, right, bottom)
        double scale = r.F32(), xScale = r.F32(), rotation = r.F32(), italic = r.F32(), shear = r.F32();
        uint type = r.U32(), type2 = r.U32();
        int parent = r.U16();

        if ((type & 2) != 0)
        {
            r.Skip(4);                                       // group flags
            int groupId = r.U16();
            return new LytObject
            {
                Offset = off, Length = end - off, IsGroup = true, Name = name, NodeCount = nodeCount,
                OrgX = orgX, OrgY = orgY, SizeW = sizeW, SizeH = sizeH, Scale = scale, XScale = xScale, Rotation = rotation,
                Italic = italic, Shear = shear, Type = type, Type2 = type2, ParentGroup = parent, GroupId = groupId,
            };
        }

        // The nodes are normally the last bytes of the record, whatever the style block holds. A few old (6.0) files have
        // more data after them, so the position is checked and, if it does not look right, searched for.
        var notes = new List<string>();
        LytNode[] nodes = [];
        int bodyEnd = r.End;
        int nodesAt = LocateNodes(data, r.Position, r.End, nodeCount);
        if (nodesAt < 0)
        {
            notes.Add($"The outline ({nodeCount} nodes) could not be located in an older file variant; the shape was skipped.");
        }
        else
        {
            if (nodesAt != r.End - nodeCount * NodeSize) notes.Add("The outline was found by searching, not at its usual place.");
            bodyEnd = nodesAt;
            nodes = new LytNode[nodeCount];
            for (int i = 0; i < nodeCount; i++) nodes[i] = LytNode.Decode(data.AsSpan(nodesAt + i * NodeSize, NodeSize));
        }

        LytStyle? style = null;
        try
        {
            var body = new ByteReader(data, r.Position, bodyEnd);
            style = ReadStyle(body, file.FormatVersion >= 0x4A);
        }
        catch (LytFormatException e)
        {
            notes.Add($"Style could not be read: {e.Message}");
        }

        var obj = new LytObject
        {
            Offset = off, Length = end - off, IsGroup = false, Name = name, NodeCount = nodeCount,
            OrgX = orgX, OrgY = orgY, SizeW = sizeW, SizeH = sizeH, Scale = scale, XScale = xScale, Rotation = rotation,
            Italic = italic, Shear = shear, Type = type, Type2 = type2, ParentGroup = parent,
            Style = style, Nodes = nodes,
            TextOnPath = style is { FontFile.Length: > 0 } ? LytTextOnPathReader.Find(data, r.Position, bodyEnd) : null,
        };
        obj.Notes.AddRange(notes);
        if (style?.NotDecoded is { } nd) obj.Notes.Add(nd);
        return obj;
    }

    // ---------------------------------------------------------------- outline location

    /// <summary>Every flag bit seen in real files.</summary>
    private const uint KnownNodeFlags = 0x00A0A4F7;

    /// <summary>
    /// Start of the node run: at the end of the record if the nodes there look right, otherwise the nearest earlier
    /// position where a full run of plausible nodes starts. -1 if there is none.
    /// </summary>
    private static int LocateNodes(byte[] d, int bodyStart, int recordEnd, int count)
    {
        if (count < 0 || (long)count * NodeSize > recordEnd - bodyStart) return -1;
        if (count == 0) return recordEnd;
        int usual = recordEnd - count * NodeSize;
        if (PlausibleNodes(d, usual, count)) return usual;

        int lowest = Math.Max(bodyStart, usual - 4_000_000);
        for (int start = usual - 1; start >= lowest; start--)
            if (PlausibleFirstNode(d, start) && PlausibleNodes(d, start, count)) return start;
        return -1;
    }

    private static bool PlausibleFirstNode(byte[] d, int at)
    {
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at));
        return (flags & ~KnownNodeFlags) == 0 && (flags & LytNode.FlagMove) != 0 && flags != LytNode.FlagMarker;
    }

    /// <summary>Only known flag bits, finite coordinates, and the first real node starts a contour.</summary>
    private static bool PlausibleNodes(byte[] d, int start, int count)
    {
        bool sawGeometry = false;
        for (int i = 0; i < count; i++)
        {
            var b = d.AsSpan(start + i * NodeSize, NodeSize);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(b);
            if ((flags & ~KnownNodeFlags) != 0) return false;
            float x = BinaryPrimitives.ReadSingleLittleEndian(b[4..]), y = BinaryPrimitives.ReadSingleLittleEndian(b[8..]);
            if (!float.IsFinite(x) || !float.IsFinite(y) || Math.Abs(x) > 1e7f || Math.Abs(y) > 1e7f) return false;
            if (flags == LytNode.FlagMarker) continue;
            if (!sawGeometry)
            {
                if ((flags & LytNode.FlagMove) == 0) return false;
                sawGeometry = true;
            }
        }
        return sawGeometry;
    }

    // ---------------------------------------------------------------- style

    private sealed class NotDecodedException(string message) : Exception(message);

    /// <summary>
    /// Reads the style block as far as the fill. Everything after the fill (effects, shadows, bevels) is not needed
    /// yet. If a fill type is not understood the style is still returned, with <see cref="LytStyle.NotDecoded"/> set.
    /// </summary>
    private static LytStyle ReadStyle(ByteReader r, bool v7)
    {
        int magic = (int)r.U32();
        bool ok = v7 ? magic is >= 0x11171 and <= 0x11177 : magic is >= 0xEA61 and <= 0xEA66;
        if (!ok) throw new LytFormatException($"Unexpected style marker 0x{magic:X}.");

        string font = r.FixedString(128);
        float charSpacing = r.F32(), wordSpacing = r.F32(), lineSpacing = r.F32();
        short align = r.I16();
        uint layerId = r.U32();
        r.Skip(16);                                          // LinkID, SourceID
        if (v7) r.Skip(8);
        r.Skip(16);
        var alternate = ReadColor(r);
        r.Skip(16);
        var color = ReadColor(r);
        r.Skip(28 + 40 + 16 + 8 + 16 + 50 + 334);            // effect/shadow parameters not needed yet
        ushort flags = r.U16();
        if ((flags & 0x800) != 0) r.Skip(4 + 2 + 16);        // outline parameters

        LytFill? fill = null;
        string? notDecoded = null;
        if ((flags & 0x1000) != 0)
        {
            try { fill = ReadFill(r); }
            catch (NotDecodedException e) { notDecoded = e.Message; }
            catch (LytFormatException e) { notDecoded = $"fill block: {e.Message}"; }
        }

        // What follows the fill: the three flag words and the optional blocks they announce, of which only the Soft Shadow / Glow record is kept.
        // A block that is not understood ends the reading there (what comes after it cannot be found), without a warning: the part is complete without it.
        LytSoftShadow? soft = null;
        bool softHidden = false, softGlow = false;
        if (notDecoded is null)
        {
            try { ReadAfterFill(r, v7, out soft, out softHidden, out softGlow); }
            catch (LytFormatException) { /* an older or damaged variant: no soft shadow */ }
        }

        return new LytStyle
        {
            BodyMagic = magic, FontFile = font, CharSpacing = charSpacing, WordSpacing = wordSpacing, LineSpacing = lineSpacing,
            Align = align, LayerId = layerId, Color = color, AlternateColor = alternate, Flags = flags, Fill = fill, NotDecoded = notDecoded,
            SoftShadow = soft, SoftShadowHidden = softHidden, SoftShadowIsGlow = softGlow,
        };
    }

    /// <summary>
    /// The part after the fill block: three flag words (Flags2, Flags3, Flags4), then the blocks their bits announce in a fixed order. The Soft Shadow record is
    /// announced by bit 0x4000000 of the second word; the Glow and "hidden" marks are bits of the last word of the part, which is only reachable when no block
    /// before it is unknown.
    /// </summary>
    internal static void ReadAfterFill(ByteReader r, bool v7, out LytSoftShadow? soft, out bool hidden, out bool glow)
    {
        soft = null; hidden = false; glow = false;
        uint f69 = r.U32(), f6d = r.U32(), f71 = r.U32();
        if ((f69 & 0x40000000) != 0) return;                 // an unknown block
        if ((f6d & 0x1) != 0) r.Skip(8 + 8 + 100);
        if ((f6d & 0x800000) != 0) return;                   // an unknown block
        if ((f6d & 0x4000000) != 0) soft = ReadSoftShadow(r);
        if ((f6d & 0x8000000) != 0) return;                  // an unknown block
        if ((f71 & 0x200) != 0) r.Skip(260);
        if ((f71 & 0x800) != 0) r.Skip(4);
        if ((f71 & 0x200000) != 0) r.Skip(8);
        if ((f71 & 0x400000) != 0) r.Skip(4 + 4 + 4 + 8);
        if ((f71 & 0x2000000) != 0) r.Skip(4 * (57 + 16));
        if ((f71 & 0x80000000) != 0)
        {
            uint f75 = r.U32();
            if ((f75 & 1) != 0) r.Skip(1 + 4 + 300 + 12 + 16 + 4 + 4);
            if ((f75 & (0x40 | 0x80 | 0x20000)) != 0) return;   // unknown blocks
        }
        if (!v7) return;
        uint f79 = r.U32();
        hidden = (f79 & 0x400000) != 0;
        glow = (f79 & 0x2) != 0;
    }

    /// <summary>The Soft Shadow record (version 3, 4 or 5). Null when its numbers are not plausible (the block is then not what it was taken for).</summary>
    private static LytSoftShadow? ReadSoftShadow(ByteReader r)
    {
        int version = r.U8();
        float opacity = r.F32(), hs = r.F32(), vs = r.F32(), softness = r.F32();
        r.Skip(8);                                           // two words that hold leftover memory
        uint options = r.U32();
        float inset = r.F32();
        float slant = 0, height = 1;
        if (version > 3) { slant = r.F32(); height = r.F32(); }
        var color = default(LytColor);
        if (version > 4) color = ReadColor(r);
        bool plausible = version is >= 3 and <= 5 && (options & 1) != 0
            && float.IsFinite(opacity) && opacity is >= 0 and <= 1.0001f
            && float.IsFinite(softness) && softness is >= 0 and <= 10
            && float.IsFinite(hs) && float.IsFinite(vs) && Math.Abs(hs) < 1e5 && Math.Abs(vs) < 1e5
            && float.IsFinite(inset) && Math.Abs(inset) < 100
            && float.IsFinite(slant) && Math.Abs(slant) < 90 && float.IsFinite(height) && height is > 0 and < 100;
        return plausible
            ? new LytSoftShadow { Version = version, Opacity = opacity, Hs = hs, Vs = vs, Softness = softness, Inset = inset, Slant = slant, Height = height, Color = color }
            : null;
    }

    /// <summary>The fill/effect block: its own u16 version, then parameters that grow with the version.</summary>
    private static LytFill ReadFill(ByteReader r)
    {
        int w = r.U16();
        if (w != 0xFA6 && !(w >= 0x1770 && w <= 0x1786)) throw new LytFormatException($"unknown fill version {w}");
        uint type = r.U32();
        // The blend part of the block (gradient and transparency): kept for the importer; the rest is skipped as before.
        var blend = new LytBlend { GradType = type, TransType = r.U32() };
        r.Skip(4);
        if (w < 6000) r.Skip(16);
        else for (int i = 0; i < 4; i++) blend.Colors[i] = ReadColor(r);
        for (int i = 0; i < 8; i++) blend.Points[i] = r.F32();
        for (int i = 0; i < 8; i++) blend.TransPoints[i] = r.F32();
        r.Skip(1);
        if (w > 0x1771) { blend.TransMin = r.F32(); blend.FeatherAmount = r.F32(); if (w > 0x1777) blend.FeatherInset = r.F32(); }
        if (w > 0x1772)
        {
            blend.NodeCount = (int)r.U32();
            for (int i = 0; i < 16; i++)
            {
                float x = r.F32(), y = r.F32();
                r.Skip(8);
                var color = ReadColor(r);
                ushort flag = w > 0x1776 ? r.U16() : (ushort)0;
                blend.ExtraNodes.Add(new LytBlendNode(x, y, color, flag));
            }
            r.Skip(80);
        }
        if (w > 0x1773) r.Skip(8);
        if (w > 0x1774) blend.TransSwap = r.U32() != 0;
        if (w > 0x1775) blend.TransMax = r.F32();
        if (w > 0x1785) r.Skip(4);
        int embeddedFlagAt = -1;
        if (w > 0x1778)
        {
            r.Skip(16 + 260);
            if (w > 0x177A) r.Skip(260);
            if (w > 0x1783) { r.Skip(16); if (w > 0x1784) r.Skip(4); }
            if (w > 0x1779 && r.U16() != 0) embeddedFlagAt = r.Position;
        }

        if (embeddedFlagAt < 0) return ReadFillTail(r, w, type, blend);

        // The flag says "the picture's source file is embedded", but the writer only stores it (u32 length + bytes) if that file
        // still existed when the layout was saved; otherwise nothing follows. Try "nothing", and if no image can be found,
        // try "length + bytes".
        try
        {
            var first = ReadFillTail(r, w, type, blend);
            if (type != 4 || first.Image is not null) return first;
        }
        catch (LytFormatException) { }

        r.Position = embeddedFlagAt;
        uint length = r.U32();
        if (length > r.Remaining) throw new LytFormatException("the embedded source file is larger than the object");
        r.Skip((int)length);
        return ReadFillTail(r, w, type, blend);
    }

    /// <summary>The effect parameters and, for bitmap fills, the picture.</summary>
    private static LytFill ReadFillTail(ByteReader r, int w, uint type, LytBlend blend)
    {
        if (w > 0x177D)
        {
            r.Skip(4 * (w == 0x177E ? 8 : 7) + 57 + 8 + 57);
            if (w > 0x177E) r.Skip(4 * (w > 0x1780 ? 7 : 5) + 57 * (w > 0x1780 ? 3 : 2));
            if (w > 0x177F) r.Skip(20 + 57);
            if (w > 0x1781) r.Skip(16);
            if (w > 0x1782) r.Skip(4 + 57);
        }
        r.Skip(16);

        byte[]? image = null;
        string? mediaType = null, link = null;
        if (type == 4)
        {
            r.Skip(12);
            if (w > 0x177C) r.Skip(4);
            if (w > 0x177B) r.Skip(20);
            // Embedded: u32 length, u8 colour order (not in the oldest fill version), then a complete image file.
            // Linked: the same two fields followed by a 260-byte path.
            uint n = r.U32();
            if (w > 6000) r.Skip(1);
            if (n > 0 && n <= r.Remaining - 4)
            {
                image = r.Bytes((int)n);
                mediaType = MediaType(image);
            }
            else link = r.FixedString(260);
        }
        else if (type == 9)
        {
            throw new NotDecodedException("multi-bitmap fills are not imported yet");
        }
        else if (r.Remaining >= 260)
        {
            r.Skip(260);                                     // the legacy text buffer every other fill type carries (empty here)
        }

        // The last field of the block: Alternate Colors. Read leniently (a block that ends early just has none), so the fill is never lost for it.
        if (r.Remaining >= 4) blend.AlternateColors = r.U32() == 1;
        return new LytFill { Version = w, Type = type, Image = image, ImageMediaType = mediaType, LinkedImagePath = link, Blend = blend };
    }

    private static string? MediaType(byte[] d)
    {
        if (d.Length > 4 && d[0] == 0xFF && d[1] == 0xD8) return "image/jpeg";
        if (d.Length > 8 && d[0] == 0x89 && d[1] == (byte)'P' && d[2] == (byte)'N' && d[3] == (byte)'G') return "image/png";
        if (d.Length > 2 && d[0] == (byte)'B' && d[1] == (byte)'M') return "image/bmp";
        if (d.Length > 4 && d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F') return "image/gif";
        if (d.Length > 4 && ((d[0] == (byte)'I' && d[1] == (byte)'I' && d[2] == 42) || (d[0] == (byte)'M' && d[1] == (byte)'M' && d[3] == 42))) return "image/tiff";
        return null;
    }
}
