using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Lyt;

/// <summary>One run of <see cref="LytWriter"/>: walks the document, builds the records, assembles the file.</summary>
internal sealed partial class LytWriteSession(Document doc, LytWriteOptions options, LytWriteResult result)
{
    private const int PrefixSize = 74;
    private const short FormatVersion = 0x4A;
    private const uint HeaderSubVersion = 611;
    private const int MaxGroups = 65535;

    private readonly List<PartRecord> _parts = [];
    private readonly Dictionary<GroupObject, GroupRecord> _groups = [];
    private readonly List<GroupRecord> _groupList = [];
    private readonly Dictionary<Layer, uint> _layerIds = [];
    private readonly Dictionary<string, int> _fallbacks = [];
    private bool _groupLimitReported, _featherReported;

    private PageSetup Page => doc.Page;

    public byte[] Run()
    {
        int n = doc.Layers.Count;
        for (int i = 0; i < n; i++) _layerIds[doc.Layers[i]] = (uint)(n - 1 - i);   // the header lists the top layer first

        foreach (var layer in doc.Layers)
        {
            foreach (var obj in layer.Children)
            {
                if (!options.IncludeHidden && (!layer.Visible || !obj.IsShown)) { result.HiddenSkippedCount++; continue; }
                AddObject(obj, layer);
            }
        }

        foreach (var (reason, count) in _fallbacks.OrderBy(kv => kv.Key))
            result.Warnings.Add($"{(count == 1 ? "1 text was" : $"{count} texts were")} written as outlines: {reason}.");
        Summarize();
        result.PartCount = _parts.Count;
        result.GroupCount = _groupList.Count;
        return Assemble();
    }

    private void Fallback(string reason) => _fallbacks[reason] = _fallbacks.GetValueOrDefault(reason) + 1;

    // ---------------------------------------------------------------- objects

    private void AddObject(DocObject obj, Layer layer)
    {
        var style = obj.Style;
        var world = obj.GetWorldTransform();
        int group = GroupIdOf(obj.Parent as GroupObject);
        uint layerId = _layerIds[layer];
        string name = obj.Name ?? "";

        if (style.Blur is not null)
        {
            result.SoftEffectCount++;
            if (options.SoftEffects == LytSoftEffects.Skip) return;
        }
        if (style.Feather is not null && !_featherReported)
        {
            _featherReported = true;
            result.Warnings.Add("Feathered edges are not stored in a layout: feathered objects were written with hard edges.");
        }

        switch (obj)
        {
            case ImageObject image:
                AddPicture(image, world, group, layerId, name);
                break;
            case TextObject text:
                AddText(text, world, style, group, layerId);
                break;
            default:
                var geometry = obj.GetLocalGeometry();
                if (geometry is null || geometry.IsEmpty) { result.SkippedCount++; return; }
                AddShape(geometry, obj.GetLocalGeometry()!.GetBounds(), world, style, name.Length > 0 ? name : obj.KindName, group, layerId);
                break;
        }
    }

    /// <summary>Records a part: its group, and the extents of the groups around it.</summary>
    private void Add(PartRecord part, int group)
    {
        part.ParentGroup = group;
        _parts.Add(part);
        for (var g = group == 0 ? null : _groupList[group - 1]; g is not null; g = g.ParentId == 0 ? null : _groupList[g.ParentId - 1])
            g.Include(part.PageLeft, part.PageBottom, part.PageRight, part.PageTop);
        if (group != 0) _groupList[group - 1].Count++;
    }

    // ---------------------------------------------------------------- groups

    /// <summary>The id of the file group for a document group (made on first use, after its parent); 0 for none.</summary>
    private int GroupIdOf(GroupObject? g)
    {
        if (g is null) return 0;
        if (_groups.TryGetValue(g, out var existing)) return existing.Id;
        int parent = GroupIdOf(g.Parent as GroupObject);
        if (_groupList.Count >= MaxGroups)
        {
            if (!_groupLimitReported)
            {
                _groupLimitReported = true;
                result.Warnings.Add($"A layout holds at most {MaxGroups} groups; the rest of the objects were written ungrouped.");
            }
            return parent;
        }
        var rec = new GroupRecord { Id = _groupList.Count + 1, ParentId = parent, Name = LytEncoding.Sanitize(string.IsNullOrEmpty(g.Name) ? "Group" : g.Name) };
        _groups[g] = rec;
        _groupList.Add(rec);
        if (parent != 0) _groupList[parent - 1].Count++;
        return rec.Id;
    }

    // ---------------------------------------------------------------- placement helpers

    /// <summary>Page-space bounds in inches (origin bottom-left, y up) of a rectangle in document points.</summary>
    private void SetPageBounds(PartRecord p, Rect2 docBounds)
    {
        if (docBounds.IsEmpty) return;
        var (left, bottom) = Units.ToUser(new Vec2(docBounds.Left, docBounds.Bottom), Page);
        var (right, top) = Units.ToUser(new Vec2(docBounds.Right, docBounds.Top), Page);
        p.PageLeft = left; p.PageBottom = bottom; p.PageRight = right; p.PageTop = top;
    }

    /// <summary>
    /// Splits a transform into <c>Scale(a, b) · Rotation</c> (a stretch along the text or picture's own axes and a turn, no skew, no mirror),
    /// the only form a record's scale, stretch and rotation fields can hold. <paramref name="rotation"/> is the record's counter-clockwise angle.
    /// </summary>
    internal static bool Decompose(Affine2 m, out double a, out double b, out double rotation)
    {
        a = Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
        b = Math.Sqrt(m.M21 * m.M21 + m.M22 * m.M22);
        rotation = 0;
        if (a < 1e-9 || b < 1e-9 || !double.IsFinite(a + b) || m.Determinant <= 0) return false;
        double sr = -m.M12 / a, cr = m.M11 / a;
        if (Math.Abs(m.M21 / b - sr) > 1e-6 || Math.Abs(m.M22 / b - cr) > 1e-6) return false;
        rotation = Math.Atan2(sr, cr);
        return true;
    }

    // ---------------------------------------------------------------- file

    private byte[] Assemble()
    {
        var records = new List<byte[]>();
        uint stamp = 0x2C3A0000u + (uint)(StableHash(options.Title) & 0xFFFF);
        for (int i = 0; i < _parts.Count; i++) records.Add(LytRecordWriter.WritePart(_parts[i], stamp, (uint)(i + 1)));
        foreach (var g in _groupList) records.Add(LytRecordWriter.WriteGroup(g));

        var body = new ByteWriter();
        WriteHeaderBody(body);
        var bodyBytes = body.ToArray();

        int first = PrefixSize + bodyBytes.Length + 4;
        var offsets = new int[records.Count];
        int at = first;
        for (int i = 0; i < records.Count; i++) { offsets[i] = at; at += 4 + records[i].Length; }

        var file = new ByteWriter();
        byte[]? thumbnail = ValidThumbnail();
        if (thumbnail is not null) file.Bytes(thumbnail);

        file.I16(FormatVersion);
        file.U32((uint)records.Count);
        file.U32(records.Count == 0 ? 0 : (uint)first);
        file.U32(records.Count == 0 ? 0 : (uint)offsets[^1]);
        file.U32(0);
        file.Fixed(LytEncoding.Sanitize(options.Title), 40);
        file.F32(Page.Width / Units.PointsPerInch);
        file.F32(Page.Height / Units.PointsPerInch);
        file.F32(2); file.F32(2);
        file.Bytes(bodyBytes);
        file.U32(0);
        for (int i = 0; i < records.Count; i++)
        {
            file.U32(i + 1 < records.Count ? (uint)offsets[i + 1] : 0);
            file.Bytes(records[i]);
        }
        if (thumbnail is not null) file.U32((uint)thumbnail.Length);
        return file.ToArray();
    }

    private byte[]? ValidThumbnail()
    {
        var t = options.ThumbnailJpeg;
        if (t is null) return null;
        // The reader recognises a JFIF file (it starts FF D8 FF E0) and ends it at the EOI marker.
        if (t.Length < 8 || t[0] != 0xFF || t[1] != 0xD8 || t[2] != 0xFF || t[3] != 0xE0 || t[^2] != 0xFF || t[^1] != 0xD9)
        {
            result.Warnings.Add("The preview picture is not a complete JFIF JPEG file; the layout was written without a preview.");
            return null;
        }
        return t;
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = 17;
            foreach (char c in s) h = h * 31 + c;
            return h;
        }
    }

    private void WriteHeaderBody(ByteWriter w)
    {
        w.U32(HeaderSubVersion);
        w.U16(499);                                          // document flags
        w.U32(4108);
        for (int i = 0; i < 4; i++) w.String16("");          // info strings
        w.String16("");                                      // background image
        w.F32(100); w.U32(10); w.U32(24);                    // view settings
        w.String16("");                                      // color profile
        w.U32(4);
        w.U32(0);
        w.F32(2); w.F32(2); w.U16(0);
        w.U16(1);
        w.Zeros(16);                                         // one block of 16 bytes

        w.U16((ushort)doc.Guides.Count);
        foreach (var g in doc.Guides)
        {
            var (x, y) = Units.ToUser(g.Origin, Page);
            w.U32(g.AngleDegrees < 1e-6 || g.AngleDegrees > 180 - 1e-6 ? 1u : 2u);
            w.F32(x); w.F32(y);
            w.F64(g.AngleDegrees * Math.PI / 180);
        }

        w.NoColor();                                         // current color
        var colors = new List<LytColor>();
        foreach (var p in _parts)
            if (p.Color.IsValid && !colors.Contains(p.Color) && colors.Count < 256) colors.Add(p.Color);
        w.I32(colors.Count);
        foreach (var c in colors) w.Color(c);

        w.I32(doc.Layers.Count);
        for (int i = doc.Layers.Count - 1; i >= 0; i--)
        {
            var layer = doc.Layers[i];
            w.Color(LytColors.From(layer.Color));
            w.Fixed(LytEncoding.Sanitize(layer.Name), 101);
            w.U32(_layerIds[layer]);
            w.U32(751);
            w.U32(0);
        }

        w.U32(0);                                            // rectangles
        w.U32(75);
        w.U32(0);
        w.U32(0); w.F32(Page.Height / Units.PointsPerInch); w.F32(Page.Width / Units.PointsPerInch); w.U32(0);
    }
}
