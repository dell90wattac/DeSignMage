using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;

namespace DeSignMage.Formats.Lyt;

public sealed record LytImportOptions
{
    /// <summary>Rebuild groups as <see cref="GroupObject"/>s. False gives one flat list in drawing order.</summary>
    public bool PreserveGroups { get; init; } = true;

    public bool ImportGuides { get; init; } = true;

    /// <summary>
    /// Bring text in as live <see cref="TextObject"/>s (TrueType text that is still text in the layout). False keeps every text as its stored outlines.
    /// </summary>
    public bool LiveText { get; init; } = true;
}

public sealed class LytImportResult(Document document, LytFile source, IReadOnlyList<string> warnings, int parts, int groups, int skipped)
{
    public Document Document { get; } = document;
    public LytFile Source { get; } = source;

    /// <summary>Things that were skipped or approximated; shown to the user, never fatal.</summary>
    public IReadOnlyList<string> Warnings { get; } = warnings;

    public int PartCount { get; } = parts;
    public int GroupCount { get; } = groups;
    public int SkippedCount { get; } = skipped;

    /// <summary>Text records that became live text objects.</summary>
    public int LiveTextCount { get; init; }

    /// <summary>Installed-font names that live text asked for but that are not installed (shown in the fallback font), with how many texts use each.</summary>
    public IReadOnlyDictionary<string, int> MissingFonts { get; init; } = new Dictionary<string, int>();

    /// <summary>Text records that stayed shapes, by reason.</summary>
    public IReadOnlyDictionary<LytTextKeptAs, int> TextKeptAsShapes { get; init; } = new Dictionary<LytTextKeptAs, int>();
}

/// <summary>Counts collected while text is converted.</summary>
internal sealed class TextImportStats
{
    public bool Enabled { get; init; } = true;
    public int Live;
    public Dictionary<string, int> MissingFonts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<LytTextKeptAs, int> Kept { get; } = [];
}

/// <summary>
/// Turns a parsed .lyt into a <see cref="Document"/>.
/// <list type="bullet">
/// <item>Units: layout pages are inches with y up from the bottom-left; documents here are points with y down from the top-left.</item>
/// <item>Each part keeps its own transform: <c>Scale(72s·xScale, 72s) · Rotate(-rotation) · Translate(org)</c> on its nodes with y negated.</item>
/// <item>Circular arcs stay arcs (<see cref="PathBuilder.ArcTo"/>); contours with the hole flag are wound opposite
/// to outer ones, so the non-zero fill rule gives result.</item>
/// <item>Text that is still text in the layout and uses a TrueType font becomes a live <see cref="TextObject"/> (<see cref="LytTextImporter"/>).
/// Text already converted to vectors, legacy <c>.FNT</c> fonts and anything live text cannot reproduce stay their stored
/// outlines (a <see cref="PathObject"/> named after the text).</item>
/// <item>A rectangle with an image fill becomes an <see cref="ImageObject"/>; other shapes with image fills get the object color.</item>
/// <item>Every group of the layout is kept, however its members are interleaved with other objects: each object has its own place in the
/// drawing order (the order of the file) and, separately, the group it belongs to (R22).</item>
/// </list>
/// </summary>
public static class LytImporter
{
    private const double MinSweep = 1e-9;

    /// <summary>Node units (inches at scale 1); an arc whose middle is closer than this to its chord is read as a line.</summary>
    private const double FlatArc = 1e-5;

    /// <summary>
    /// The coordinates are 32-bit floats: a point of magnitude M is only known to about M × 1.2e-7, and an arc that is built from a far centre
    /// (radius R, so M ≥ R) is known to a few times that. An arc that bulges by less than this many times M is a line to the eye and its circle
    /// is not reliable.
    /// </summary>
    private const double FlatArcNoise = 1e-6;

    /// <summary>Line width (points) for shapes that have no fill and no color: cut lines, outlines.</summary>
    private const double Hairline = 0.72;

    public static LytImportResult Open(string path, LytImportOptions? options = null) => Import(LytParser.Parse(path), options);

    public static LytImportResult Import(LytFile file, LytImportOptions? options = null)
    {
        options ??= new LytImportOptions();
        var warnings = new List<string>(file.Warnings);

        double w = file.PageWidth, h = file.PageHeight;
        if (!(w > 0 && h > 0 && w < 10_000 && h < 10_000))
        {
            warnings.Add($"The page size ({w} x {h} in) is not usable; using 24 x 12 in.");
            w = 24; h = 12;
        }
        var page = PageSetup.FromInches(w, h);
        var doc = new Document(page);

        // Layers: the header lists the top layer first; a document stores bottom first.
        var layerById = new Dictionary<uint, Layer>();
        var defaultLayer = doc.Layers[0];
        if (file.Layers.Count == 0)
        {
            layerById[0] = defaultLayer;
        }
        else
        {
            foreach (var l in Enumerable.Reverse(file.Layers))
            {
                var layer = new Layer(l.Name.Length > 0 ? l.Name : $"Layer {l.Id}") { Color = l.Color.IsValid ? l.Color.ToDocColor() : DocColor.Black };
                doc.InsertLayer(doc.Layers.Count, layer);
                layerById.TryAdd(l.Id, layer);
            }
            doc.RemoveLayer(defaultLayer);
        }
        var fallbackLayer = layerById.GetValueOrDefault(0u) ?? doc.Layers[^1];

        var groupRecords = new Dictionary<int, LytObject>();
        foreach (var g in file.Objects.Where(o => o.IsGroup))
            if (!groupRecords.TryAdd(g.GroupId, g)) warnings.Add($"Group id {g.GroupId} is used twice; the first record is used.");

        var created = new Dictionary<int, GroupObject>();
        var reportedGroups = new HashSet<int>();
        var partsPerLayer = new Dictionary<Layer, int>();
        var unknownLayers = new HashSet<uint>();
        int parts = 0, skipped = 0;
        var textStats = new TextImportStats { Enabled = options.LiveText };

        // The group a record belongs to (made when its first member needs it, inside the group around it), or null for none. Groups are only
        // membership: each object goes into the drawing order of its own layer in the order of the file, wherever its group's other members are.
        GroupObject? GroupFor(int groupId, Layer layer, HashSet<int>? chain = null)
        {
            if (!options.PreserveGroups || groupId == 0) return null;
            if (!groupRecords.TryGetValue(groupId, out var rec))
            {
                if (reportedGroups.Add(groupId)) warnings.Add($"Objects refer to group {groupId}, which does not exist; they were placed outside any group.");
                return null;
            }
            if (created.TryGetValue(groupId, out var existing)) return existing;
            chain ??= [];
            if (!chain.Add(groupId))
            {
                if (reportedGroups.Add(groupId)) warnings.Add($"Group {groupId} contains itself; its members were placed outside any group.");
                return null;
            }
            var parent = GroupFor(rec.ParentGroup, layer, chain);
            var group = new GroupObject { Name = rec.Name.Length > 0 ? rec.Name : null };
            if (parent is null) doc.Insert(layer, -1, group);
            else doc.Insert(parent, -1, group);
            created[groupId] = group;
            return group;
        }

        var records = file.Objects.Where(o => !o.IsGroup).ToList();
        var converted = records.Select(o => ConvertPart(o, page.Height, doc, warnings, textStats)).ToList();

        for (int ri = 0; ri < records.Count; ri++)
        {
            var o = records[ri];
            var obj = converted[ri];
            if (obj is null) { skipped++; continue; }

            uint layerId = o.Style?.LayerId ?? 0;
            if (!layerById.TryGetValue(layerId, out var layerOfPart))
            {
                if (unknownLayers.Add(layerId)) warnings.Add($"Objects use layer {layerId}, which is not defined; they were put on '{fallbackLayer.Name}'.");
                layerOfPart = fallbackLayer;
            }
            doc.Insert(layerOfPart, -1, obj);
            if (GroupFor(o.ParentGroup, layerOfPart) is { } group) doc.SetGroup(obj, group);
            partsPerLayer[layerOfPart] = partsPerLayer.GetValueOrDefault(layerOfPart) + 1;
            parts++;
        }

        // Work happens on the busiest layer; ties go to the upper one.
        if (partsPerLayer.Count > 0)
            doc.ActiveLayer = doc.Layers.Reverse().OrderByDescending(l => partsPerLayer.GetValueOrDefault(l)).First();

        if (options.ImportGuides)
        {
            foreach (var g in file.Guides)
            {
                if (!double.IsFinite(g.X) || !double.IsFinite(g.Y) || !double.IsFinite(g.AngleRadians)) continue;
                var origin = Units.FromUser(g.X, g.Y, page);
                new AddGuideCommand(new Guide(origin, g.AngleRadians * 180 / Math.PI)).Do(doc);
            }
        }

        foreach (var (font, count) in textStats.MissingFonts.OrderBy(kv => kv.Key))
            warnings.Add($"The font '{font}' is not installed; {Plural(count, "text")} shown in {TextEngine.Fonts.Resolve(font, 400, false).Family} instead (the name is kept, so it can be fixed by installing the font).");
        // Text that stays outlines (legacy fonts, text converted to vectors, unsupported fills) is not reported: it looks exactly as before and is counted in TextKeptAsShapes.

        return new LytImportResult(doc, file, warnings, parts, created.Count, skipped)
        {
            LiveTextCount = textStats.Live,
            MissingFonts = textStats.MissingFonts,
            TextKeptAsShapes = textStats.Kept,
        };
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun} is" : $"{n} {noun}s are";

    // ---------------------------------------------------------------- parts

    /// <summary>Maps a part's node space (inches/scale, y up) to document points (y down).</summary>
    public static Affine2 PartTransform(LytObject o, double pageHeightPoints)
    {
        double s = o.Scale * Units.PointsPerInch;
        return Affine2.Scale(s * o.XScale, s)
             * Slant(o)
             * Affine2.Rotation(-o.Rotation)
             * Affine2.Translation(o.OrgX * Units.PointsPerInch, pageHeightPoints - o.OrgY * Units.PointsPerInch);
    }

    /// <summary>
    /// The slant of a part (the slant handle, or an italic text): the angle the italic field holds, turning the part's vertical lines by that
    /// angle from the baseline of its own coordinates (x moves by y·tan). Text stores its rotation there too when it is only rotated, so for text
    /// the slant is what is left after the rotation. Shapes that are only rotated store 0.
    /// </summary>
    internal static Affine2 Slant(LytObject o)
    {
        double angle = o.IsText ? o.Italic - o.Rotation : o.Italic;
        if (!double.IsFinite(angle) || Math.Abs(angle) < 1e-4 || Math.Abs(angle) >= Math.PI / 2 - 0.05) return Affine2.Identity;
        // Node y is negated on import (y up in the file, down here), hence the sign.
        return new Affine2(1, 0, -Math.Tan(angle), 1, 0, 0);
    }

    private static DocObject? ConvertPart(LytObject o, double pageHeightPoints, Document doc, List<string> warnings, TextImportStats text)
    {
        string label = o.Name.Length > 0 ? $"'{o.Name}'" : "an unnamed object";
        foreach (var note in o.Notes) warnings.Add($"{label}: {note}");

        if (o.Nodes.Length == 0)
        {
            if (!o.Notes.Any()) warnings.Add($"{label} has no outline and was skipped.");
            return null;
        }
        if (o.Nodes.Any(n => !float.IsFinite(n.X) || !float.IsFinite(n.Y)))
        {
            warnings.Add($"{label} has invalid coordinates and was skipped.");
            return null;
        }

        var xf = PartTransform(o, pageHeightPoints);
        if (!double.IsFinite(xf.M11 + xf.M12 + xf.M21 + xf.M22 + xf.OffsetX + xf.OffsetY) || !xf.IsInvertible)
        {
            warnings.Add($"{label} has a degenerate size or position and was skipped.");
            return null;
        }

        var style = o.Style;
        // The fill is the main color record, or the second one in two early files. Bitmaps and cut lines have neither:
        // they get no fill and a hairline outline so they stay visible.
        // The second record is a fill only in the two early files; elsewhere it holds the cut line's gray (a mark, not a color to print).
        LytColor? chosen = style is null ? null : style.Color.IsValid ? style.Color : style.AlternateColor.Model is LytColor.ModelRgb or LytColor.ModelCmyk ? style.AlternateColor : null;
        if (style is null) warnings.Add($"{label} has no readable style; it is filled black.");
        ObjectStyle objectStyle = chosen is { } c ? ObjectStyle.FillOnly(c.ToDocColor())
            : style is null ? ObjectStyle.FillOnly(DocColor.Black)
            : new ObjectStyle(null, StrokeStyle.Solid(DocColor.Black, Hairline));

        string? name = o.Name.Length > 0 ? o.Name : null;

        // A gradient, or a color fading to nothing: the fill becomes a gradient paint. Such text stays outlines so the fade fits its shape exactly.
        PathGeometry? outline = null;
        GradientPaint? gradient = chosen is { } blendColor && style?.Fill?.Blend is { } blend
            ? LytGradients.TryCreate(blend, blendColor.ToDocColor(), outline = BuildPath(o.Nodes))
            : null;
        if (gradient is not null) objectStyle = new ObjectStyle(gradient, null);

        if (o.IsText && text.Enabled && chosen is not null && gradient is null)
        {
            var kept = LytTextImporter.TryCreate(o, pageHeightPoints, objectStyle, out var live);
            if (live is not null)
            {
                text.Live++;
                if (live.IsFontMissing) text.MissingFonts[live.FontFamily] = text.MissingFonts.GetValueOrDefault(live.FontFamily) + 1;
                return live;
            }
            text.Kept[kept] = text.Kept.GetValueOrDefault(kept) + 1;
        }

        if (style?.Fill is { Type: 4, Image: { } image } && TryGetRectangle(o.Nodes, out var rect))
        {
            string assetId = doc.Assets.Add(image, style.Fill.ImageMediaType ?? "application/octet-stream");
            // The pixel size is what the Info Line's "Bitmaps" total counts (width x height x 4 bytes), not the size of the JPEG file.
            int pixelWidth = 0, pixelHeight = 0;
            if (DeSignMage.Formats.Vector.ImageInfo.TryRead(image, out _, out int w, out int h)) { pixelWidth = w; pixelHeight = h; }
            return new ImageObject(assetId, rect.Width, rect.Height, pixelWidth, pixelHeight)
            {
                Name = name,
                Transform = Affine2.Translation(rect.Left, rect.Top) * xf,
            };
        }
        if (style?.Fill is { Type: 4 or 9 })
            warnings.Add($"{label} has an image fill that is not a plain rectangle; it is filled with its color instead.");

        return new PathObject(outline ?? BuildPath(o.Nodes)) { Name = name, Transform = xf, Style = objectStyle };
    }

    /// <summary>
    /// Path in node space with y negated (so that the transform has a positive determinant).
    /// Per-character marker nodes are skipped. Contours stay wound them: outer clockwise, holes counter-clockwise
    /// (in y-up view), so the non-zero rule cuts the holes out.
    /// </summary>
    public static PathGeometry BuildPath(IReadOnlyList<LytNode> nodes)
    {
        var b = new PathBuilder { FillRule = FillRule.NonZero };
        Vec2 current = default, start = default;
        bool open = false;

        foreach (var n in nodes)
        {
            if (n.Kind == LytNodeKind.Char) continue;
            var p = new Vec2(n.X, -n.Y);
            if (n.Kind == LytNodeKind.Move || !open)
            {
                b.MoveTo(p);
                start = p;
                open = true;
            }
            else if (n.Kind == LytNodeKind.Arc)
            {
                AddArc(b, current, n, p);
            }
            else if (n.Kind == LytNodeKind.Bezier)
            {
                b.CubicTo(new Vec2(n.C1X, -n.C1Y), new Vec2(n.C2X, -n.C2Y), p);
            }
            else if (!(n.Closes && Vec2.Distance(p, start) < 1e-9))   // a closing line back to the start is what Close does
            {
                b.LineTo(p);
            }
            current = p;
            if (n.Closes)
            {
                b.Close();
                open = false;
            }
        }
        return b.Build();
    }

    private static void AddArc(PathBuilder b, Vec2 from, LytNode n, Vec2 to)
    {
        var center = new Vec2(n.Cx, -n.Cy);
        double radius = Vec2.Distance(from, center);
        double sweep = -n.Sweep;                                  // y is negated, so the direction flips
        if (!double.IsFinite(sweep) || radius < 1e-9 || Math.Abs(sweep) < MinSweep)
        {
            b.LineTo(to);
            return;
        }
        double a0 = Math.Atan2(from.Y - center.Y, from.X - center.X);
        if (Math.Abs(sweep) < 2 * Math.PI - 1e-3)
        {
            // The end point is exact, the stored sweep is not always: it can be a degree short, half or double of the angle the end point makes
            // about the centre, and a dummy 4 to 5 degrees on a repeated point. So the arc ends where the end point is, on the turn nearest to the
            // stored sweep (which still tells a long arc from a short one).
            double d = Math.Atan2(to.Y - center.Y, to.X - center.X) - a0;
            while (d > Math.PI) d -= 2 * Math.PI;
            while (d <= -Math.PI) d += 2 * Math.PI;
            double best = d, bestError = Math.Abs(d - sweep);
            foreach (double turn in new[] { d + 2 * Math.PI, d - 2 * Math.PI })
                if (Math.Abs(turn - sweep) < bestError) { best = turn; bestError = Math.Abs(turn - sweep); }
            sweep = best;
            if (Math.Abs(sweep) < MinSweep)
            {
                b.LineTo(to);
                return;
            }
        }
        Vec2 At(double t) => center + new Vec2(Math.Cos(a0 + sweep * t), Math.Sin(a0 + sweep * t)) * radius;

        // An arc that bulges by about as little as the coordinates can tell apart (see FlatArcNoise) is a line to the eye, and the three-point
        // circle through such points is not reliable: it can come out as a huge circle, or the long way round.
        if (Math.Abs(sweep) < Math.PI)
        {
            double sagitta = radius * (1 - Math.Cos(Math.Abs(sweep) / 2));
            double magnitude = Math.Max(1, Math.Max(Math.Max(Math.Abs(from.X), Math.Abs(from.Y)), Math.Max(Math.Max(Math.Abs(to.X), Math.Abs(to.Y)), Math.Max(Math.Abs(center.X), Math.Abs(center.Y)))));
            if (sagitta < FlatArc || sagitta < FlatArcNoise * magnitude)
            {
                b.LineTo(to);
                return;
            }
        }

        if (Math.Abs(sweep) >= 2 * Math.PI - 1e-6)
        {
            // A three-point arc cannot describe a full circle: use two halves.
            b.ArcTo(At(0.25), At(0.5));
            b.ArcTo(At(0.75), to);
        }
        else
        {
            b.ArcTo(At(0.5), to);
        }
    }

    /// <summary>True if the nodes outline an axis-aligned rectangle (4 corners, straight sides). Rect is in y-negated space.</summary>
    internal static bool TryGetRectangle(IReadOnlyList<LytNode> nodes, out Rect2 rect)
    {
        rect = default;
        var pts = new List<(float X, float Y)>();
        foreach (var n in nodes)
        {
            if (n.Kind == LytNodeKind.Char) continue;
            if (n.Kind is LytNodeKind.Arc or LytNodeKind.Bezier) return false;
            if (n.Kind == LytNodeKind.Move && pts.Count > 0) return false;
            pts.Add((n.X, n.Y));
        }
        if (pts.Count == 5 && pts[0] == pts[4]) pts.RemoveAt(4);
        if (pts.Count != 4) return false;

        float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        if (maxX - minX <= 0 || maxY - minY <= 0) return false;
        var corners = new HashSet<(bool, bool)>();
        foreach (var (x, y) in pts)
        {
            if (x != minX && x != maxX) return false;
            if (y != minY && y != maxY) return false;
            corners.Add((x == maxX, y == maxY));
        }
        if (corners.Count != 4) return false;
        rect = new Rect2(minX, -maxY, maxX, -minY);
        return true;
    }
}
