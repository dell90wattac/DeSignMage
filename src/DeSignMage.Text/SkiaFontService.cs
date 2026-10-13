using DeSignMage.Core.Geometry;
using DeSignMage.Core.Text;
using HarfBuzzSharp;
using SkiaSharp;

namespace DeSignMage.Text;

/// <summary>
/// Installed Windows fonts through Skia (<see cref="SKFontManager"/>), shaped with HarfBuzz, outlines from Skia's glyph
/// paths. Everything is cached: faces per matched typeface, glyph outlines per (face, glyph) in em units.
/// Ligatures are switched off so every letter has its own caret position and spacing dot; pair kerning stays on.
/// Not handled yet: right-to-left and complex-script reordering (letters would come out in logical order).
/// </summary>
public sealed class SkiaFontService : IFontService
{
    /// <summary>Glyph outlines are asked from Skia at this size and divided down, so they are em-normalized and size independent.</summary>
    private const float OutlineSize = 1000;

    private static readonly string[] FallbackFamilies = ["Arial", "Segoe UI", "Calibri", "Tahoma", "Verdana"];

    private sealed class Face(SKTypeface typeface, HarfBuzzSharp.Font hb, int unitsPerEm, SKFont skFont)
    {
        public SKTypeface Typeface { get; } = typeface;
        public HarfBuzzSharp.Font Hb { get; } = hb;
        public int UnitsPerEm { get; } = unitsPerEm;
        public SKFont SkFont { get; } = skFont;
        public Dictionary<ushort, PathGeometry> Glyphs { get; } = [];
        public double Ascent, Descent, LineGap;
    }

    private readonly object _gate = new();
    private string[]? _families;
    private HashSet<string>? _familySet;
    private readonly Dictionary<string, IReadOnlyList<FontStyleInfo>> _styles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Family, int Weight, bool Italic), ResolvedFont> _resolved = [];
    private readonly Dictionary<(string Family, int Weight, int Slant, int Width), Face> _faces = [];
    private static readonly Feature[] Features =
    [
        new(Tag.Parse("liga"), 0), new(Tag.Parse("clig"), 0), new(Tag.Parse("dlig"), 0),
    ];

    /// <summary>Makes this the font source for all live text (idempotent).</summary>
    public static void Install()
    {
        if (TextEngine.Fonts is not SkiaFontService) TextEngine.Fonts = new SkiaFontService();
    }

    public IReadOnlyList<string> Families
    {
        get
        {
            lock (_gate)
            {
                EnsureFamilies();
                return _families!;
            }
        }
    }

    private void EnsureFamilies()
    {
        if (_families is not null) return;
        _families = SKFontManager.Default.FontFamilies.Where(f => !string.IsNullOrWhiteSpace(f) && !f.StartsWith('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToArray();
        _familySet = new HashSet<string>(_families, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsInstalled(string family)
    {
        lock (_gate)
        {
            EnsureFamilies();
            return !string.IsNullOrWhiteSpace(family) && (_familySet!.Contains(family) || SplitLegacyName(family) is not null);
        }
    }

    // Older programs (and anything using GDI font names) call "Segoe UI" at weight 600 "Segoe UI Semibold"; DirectWrite only lists
    // the base family. Names that are a known family plus style words resolve to that family with the style applied.
    private static readonly (string Word, int Weight)[] WeightWords =
    [
        ("Hairline", 100), ("Thin", 100), ("ExtraLight", 200), ("UltraLight", 200), ("Semilight", 350), ("Light", 300), ("Regular", 400),
        ("Normal", 400), ("Book", 400), ("Medium", 500), ("Semibold", 600), ("DemiBold", 600), ("Demi", 600), ("ExtraBold", 800),
        ("UltraBold", 800), ("Bold", 700), ("Black", 900), ("Heavy", 900),
    ];

    private (string Family, int? Weight, bool? Italic)? SplitLegacyName(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        int? weight = null;
        bool? italic = null;
        while (words.Count > 1)
        {
            string last = words[^1];
            if (last.Equals("Italic", StringComparison.OrdinalIgnoreCase) || last.Equals("Oblique", StringComparison.OrdinalIgnoreCase)) italic ??= true;
            else if (WeightWords.FirstOrDefault(w => w.Word.Equals(last, StringComparison.OrdinalIgnoreCase)) is { Word: not null } hit) weight ??= hit.Weight;
            else break;
            words.RemoveAt(words.Count - 1);
            var family = string.Join(' ', words);
            if (_familySet!.Contains(family)) return (family, weight, italic);
        }
        return null;
    }

    public IReadOnlyList<FontStyleInfo> StylesOf(string family)
    {
        lock (_gate)
        {
            EnsureFamilies();
            if (!_familySet!.Contains(family))
            {
                if (SplitLegacyName(family) is not { } legacy) return [];
                return StylesOf(legacy.Family);
            }
            if (_styles.TryGetValue(family, out var cached)) return cached;

            var list = new List<FontStyleInfo>();
            using var set = SKFontManager.Default.GetFontStyles(family);
            for (int i = 0; i < set.Count; i++)
            {
                var s = set[i];
                bool italic = s.Slant != SKFontStyleSlant.Upright;
                if (list.Any(x => x.Weight == s.Weight && x.Italic == italic)) continue;
                list.Add(new FontStyleInfo(StyleName(s.Weight, italic), s.Weight, italic));
            }
            if (list.Count == 0) list.Add(new FontStyleInfo("Regular", 400, false));
            var sorted = list.OrderBy(x => x.Weight).ThenBy(x => x.Italic).ToArray();
            _styles[family] = sorted;
            return sorted;
        }
    }

    /// <summary>The name the style picker shows for a weight/slant pair.</summary>
    public static string StyleName(int weight, bool italic)
    {
        string w = weight switch
        {
            <= 150 => "Thin",
            <= 250 => "Extra Light",
            <= 350 => "Light",
            <= 450 => "",
            <= 550 => "Medium",
            <= 650 => "Semibold",
            <= 750 => "Bold",
            <= 850 => "Extra Bold",
            _ => "Black",
        };
        if (w.Length == 0) return italic ? "Italic" : "Regular";
        return italic ? w + " Italic" : w;
    }

    public ResolvedFont Resolve(string family, int weight, bool italic)
    {
        lock (_gate)
        {
            var key = (family.ToLowerInvariant(), weight, italic);
            if (_resolved.TryGetValue(key, out var done)) return done;

            EnsureFamilies();
            string used = family;
            bool missing = string.IsNullOrWhiteSpace(family);
            if (!missing && !_familySet!.Contains(family))
            {
                if (SplitLegacyName(family) is { } legacy)
                {
                    used = legacy.Family;
                    if (legacy.Weight is { } lw && weight == 400) weight = lw;   // an explicit weight choice on the object wins
                    if (legacy.Italic == true) italic = true;
                }
                else missing = true;
            }
            if (missing) used = FallbackFamily();
            var style = new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            var typeface = SKFontManager.Default.MatchFamily(used, style) ?? SKTypeface.FromFamilyName(used, style) ?? SKTypeface.Default;

            var face = FaceFor(typeface);
            var result = new ResolvedFont(typeface.FamilyName, missing, face.Ascent, face.Descent, face.LineGap, face);
            _resolved[key] = result;
            return result;
        }
    }

    private string FallbackFamily() =>
        FallbackFamilies.FirstOrDefault(f => _familySet!.Contains(f)) ?? _families!.FirstOrDefault() ?? SKTypeface.Default.FamilyName;

    private Face FaceFor(SKTypeface typeface)
    {
        var key = (typeface.FamilyName, typeface.FontWeight, (int)typeface.FontSlant, typeface.FontWidth);
        if (_faces.TryGetValue(key, out var face)) return face;

        using var stream = typeface.OpenStream(out int ttcIndex);
        int length = (int)stream.Length;
        var data = new byte[length];
        stream.Read(data, length);
        // The blob must own memory that outlives the face: faces live as long as the service, so this is never freed.
        var memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(length);
        System.Runtime.InteropServices.Marshal.Copy(data, 0, memory, length);
        var blob = new Blob(memory, length, MemoryMode.ReadOnly, () => System.Runtime.InteropServices.Marshal.FreeHGlobal(memory));
        var hbFace = new HarfBuzzSharp.Face(blob, ttcIndex);
        int upem = Math.Max(hbFace.UnitsPerEm, 1);
        var hb = new HarfBuzzSharp.Font(hbFace);
        hb.SetScale(upem, upem);
        hb.SetFunctionsOpenType();

        var sk = new SKFont(typeface, OutlineSize) { Hinting = SKFontHinting.None, Subpixel = true, Edging = SKFontEdging.Antialias };
        var m = sk.Metrics;
        face = new Face(typeface, hb, upem, sk)
        {
            Ascent = -m.Ascent / OutlineSize,
            Descent = m.Descent / OutlineSize,
            LineGap = Math.Max(m.Leading, 0) / OutlineSize,
        };
        _faces[key] = face;
        return face;
    }

    public IReadOnlyList<ShapedElement> Shape(ResolvedFont font, IReadOnlyList<string> elements, double size)
    {
        lock (_gate)
        {
            var face = (Face)font.Handle!;
            var result = new ShapedElement[elements.Count];
            if (elements.Count == 0) return result;

            // UTF-16 start of every element, to map HarfBuzz clusters back to letters.
            var starts = new int[elements.Count];
            int offset = 0;
            for (int i = 0; i < elements.Count; i++) { starts[i] = offset; offset += elements[i].Length; }

            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf16(string.Concat(elements));
            buffer.GuessSegmentProperties();
            buffer.Direction = Direction.LeftToRight;
            face.Hb.Shape(buffer, Features);

            var infos = buffer.GlyphInfos;
            var positions = buffer.GlyphPositions;
            double scale = size / face.UnitsPerEm;

            var advance = new double[elements.Count];
            var origin = new double[elements.Count];
            var seen = new bool[elements.Count];
            var parts = new List<PathGeometry>?[elements.Count];
            double pen = 0;
            for (int g = 0; g < infos.Length; g++)
            {
                int el = ElementOf(starts, (int)infos[g].Cluster);
                if (!seen[el]) { seen[el] = true; origin[el] = pen; }
                var outline = GlyphOutline(face, (ushort)infos[g].Codepoint);
                if (!outline.IsEmpty)
                {
                    // Placed relative to the letter's own origin; vertical offsets come up positive from HarfBuzz, y is down here.
                    double dx = pen - origin[el] + positions[g].XOffset * scale;
                    double dy = -positions[g].YOffset * scale;
                    (parts[el] ??= []).Add(outline.Transform(new Affine2(size, 0, 0, size, dx, dy)));
                }
                double adv = positions[g].XAdvance * scale;
                advance[el] += adv;
                pen += adv;
            }

            for (int i = 0; i < result.Length; i++) result[i] = new ShapedElement(advance[i], Join(parts[i]));
            return result;
        }
    }

    private static int ElementOf(int[] starts, int cluster)
    {
        int i = Array.BinarySearch(starts, cluster);
        return i >= 0 ? i : Math.Max(~i - 1, 0);
    }

    private static PathGeometry Join(List<PathGeometry>? parts)
    {
        if (parts is null || parts.Count == 0) return PathGeometry.EmptyPath;
        if (parts.Count == 1) return parts[0];
        var verbs = new List<PathVerb>();
        var points = new List<Vec2>();
        foreach (var p in parts) { verbs.AddRange(p.Verbs); points.AddRange(p.Points); }
        return new PathGeometry(verbs.ToArray(), points.ToArray());
    }

    /// <summary>The outline of one glyph in em units (1 = the font size), baseline at y = 0, y down.</summary>
    private static PathGeometry GlyphOutline(Face face, ushort glyph)
    {
        if (face.Glyphs.TryGetValue(glyph, out var cached)) return cached;
        using var path = face.SkFont.GetGlyphPath(glyph);
        var geometry = path is null || path.IsEmpty ? PathGeometry.EmptyPath : Convert(path);
        face.Glyphs[glyph] = geometry;
        return geometry;
    }

    private static PathGeometry Convert(SKPath path)
    {
        const double k = 1.0 / OutlineSize;
        static Vec2 P(SKPoint p) => new(p.X * k, p.Y * k);

        var b = new PathBuilder();
        using var it = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move: b.MoveTo(P(pts[0])); break;
                case SKPathVerb.Line: b.LineTo(P(pts[1])); break;
                case SKPathVerb.Quad: b.QuadTo(P(pts[1]), P(pts[2])); break;
                case SKPathVerb.Cubic: b.CubicTo(P(pts[1]), P(pts[2]), P(pts[3])); break;
                case SKPathVerb.Conic:
                {
                    var quads = SKPath.ConvertConicToQuads(pts[0], pts[1], pts[2], it.ConicWeight(), 2);
                    for (int i = 1; i + 1 < quads.Length; i += 2) b.QuadTo(P(quads[i]), P(quads[i + 1]));
                    break;
                }
                case SKPathVerb.Close: b.Close(); break;
            }
        }
        return b.Build();
    }
}
