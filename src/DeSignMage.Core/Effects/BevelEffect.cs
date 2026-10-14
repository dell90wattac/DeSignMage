using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Effects;

/// <summary>
/// What decides how an Easy Bevel looks (notes/effects/easy-bevel.md). Inset, Width and Softness are fractions of the object's height, so the same
/// numbers look the same on a big and a small object. Highlight and Shadow are how opaque the two crescents are (0..1).
/// </summary>
public sealed record BevelSettings
{
    /// <summary>How far the bevel's outline is pulled in from the object's edge (default 1 % of the height).</summary>
    public double Inset { get; init; } = 0.01;

    /// <summary>How wide the crescents are (default 2 % of the height).</summary>
    public double Width { get; init; } = 0.02;

    /// <summary>How soft the crescents' edges are: the feather of each crescent, as a fraction of its size (default 10 %; about 0..20 % is useful).</summary>
    public double Softness { get; init; } = 0.10;

    /// <summary>Make the shadow crescent too (default on). The Context Bar turns it off by setting Shadow to 0.</summary>
    public bool IncludeShadow { get; init; } = true;

    /// <summary>Neutral: the highlight is white and the shadow black, whatever colors are chosen.</summary>
    public bool Neutral { get; init; }

    /// <summary>Opacity of the highlight crescent (default 0.8).</summary>
    public double Highlight { get; init; } = 0.8;

    /// <summary>Opacity of the shadow crescent (default 0.8).</summary>
    public double Shadow { get; init; } = 0.8;

    /// <summary>
    /// Where the light comes from, in degrees counter-clockwise from the right (90 = from above, 135 = from the upper left, the default). It is the direction as seen
    /// when the object is not turned: a turned object keeps the same light on itself, so the rim stays on the same edges when it is rotated.
    /// </summary>
    public double LightAngle { get; init; } = DefaultLightAngle;

    public const double DefaultLightAngle = 135;

    public DocColor HighlightColor { get; init; } = DocColor.White;
    public DocColor ShadowColor { get; init; } = DocColor.Black;

    public BevelSettings Normalized() => this with
    {
        Inset = Math.Clamp(Inset, -0.1, 0.3),
        Width = Math.Clamp(Width, 0, 0.5),
        Softness = Math.Clamp(Softness, 0, 1),
        Highlight = Math.Clamp(Highlight, 0, 1),
        Shadow = Math.Clamp(Shadow, 0, 1),
        LightAngle = double.IsFinite(LightAngle) ? (LightAngle % 360 + 360) % 360 : DefaultLightAngle,
    };

    /// <summary>The color the highlight is drawn in (white when Neutral).</summary>
    public DocColor EffectiveHighlightColor => Neutral ? DocColor.White : HighlightColor;

    public DocColor EffectiveShadowColor => Neutral ? DocColor.Black : ShadowColor;
}

/// <summary>A named combination of width, softness, highlight and shadow.</summary>
public sealed record BevelPreset(string Name, double Width, double Softness, double Highlight, double Shadow)
{
    public BevelSettings ApplyTo(BevelSettings s) => s with { Width = Width, Softness = Softness, Highlight = Highlight, Shadow = Shadow, IncludeShadow = Shadow > 0 };

    public bool Matches(BevelSettings s) =>
        Math.Abs(s.Width - Width) < 1e-6 && Math.Abs(s.Softness - Softness) < 1e-6 && Math.Abs(s.Highlight - Highlight) < 1e-6 && Math.Abs(s.Shadow - Shadow) < 1e-6;
}

/// <summary>The built-in presets: three widths (slight, medium, heavy) in hard, normal and soft edges.</summary>
public static class BevelPresets
{
    public static readonly IReadOnlyList<BevelPreset> All =
    [
        new("Slight Hard", 0.005, 0.01, 0.80, 0.80), new("Slight Soft", 0.009, 0.06, 0.99, 0.99), new("Slight Normal", 0.005, 0.02, 0.99, 0.99),
        new("Medium Hard", 0.015, 0.01, 0.80, 0.80), new("Medium Soft", 0.015, 0.06, 0.99, 0.99), new("Medium Normal", 0.015, 0.03, 0.99, 0.80),
        new("Heavy Hard", 0.025, 0.01, 0.80, 0.80), new("Heavy Medium", 0.028, 0.03, 0.80, 0.80), new("Heavy Soft", 0.033, 0.06, 0.80, 0.80),
    ];

    public static BevelPreset? Find(BevelSettings s) => All.FirstOrDefault(p => p.Matches(s));
}

/// <summary>One object to bevel: the drawn object, its outline in document coordinates, the height its percentages are of, and the way it is turned.</summary>
public sealed record BevelSource(DocObject Leaf, PathGeometry Silhouette, double Height, double Angle);

/// <summary>The two crescents computed for one object, in document coordinates. A null outline = no crescent (the object is empty, or the shadow is off).</summary>
public sealed record BevelPieces(PathGeometry? Highlight, ObjectStyle HighlightStyle, PathGeometry? Shadow, ObjectStyle ShadowStyle);

/// <summary>
/// Effects ▸ Easy Bevel (notes/effects/easy-bevel.md; user decisions R7, R8, R13, R22): every selected drawn object gets two plain vector objects, "Bevel
/// Highlight" and "Bevel Shadow", drawn <b>just above it</b> and grouped with it (not linked). Each is the object's outline (pulled in by the inset) minus a copy
/// of itself moved by the width: toward the lower right for the highlight, so a crescent is left along the upper left edges; toward the upper left for the shadow.
/// They are drawn in the chosen color at the chosen opacity with a feather, so whatever lies under them (a flat color, a gradient, a picture) shows through a
/// lighter or darker rim. Nothing is rasterized: the soft edge is drawn by the renderer. Each object is beveled by itself (the members of a group one by one),
/// with percentages of its own height. The light turns with the object.
/// </summary>
public static class BevelEffect
{
    public const string HighlightName = "Bevel Highlight", ShadowName = "Bevel Shadow";

    /// <summary>True for the objects this effect made.</summary>
    public static bool IsCrescent(DocObject o) => o is PathObject { Name: HighlightName or ShadowName };

    // ---- What can be beveled ----

    /// <summary>The objects of the selection that can be beveled: shapes with an area, groups looked through, without crescents and soft effect objects.</summary>
    public static List<DocObject> Leaves(EditorSession s)
    {
        var list = new List<DocObject>();
        foreach (var leaf in EffectGeometry.Leaves(Stacking.BottomToTop(s.Document, s.Selection.Items)))
            if (!IsCrescent(leaf) && !ShadowEffect.IsShadow(leaf) && leaf.Style.Blur is null && EffectGeometry.IsShape(leaf) && HasArea(leaf)) list.Add(leaf);
        return list;
    }

    private static bool HasArea(DocObject leaf) =>
        leaf is CombinedObject ? EffectGeometry.Operands([leaf]).Any(x => EffectGeometry.HasClosedArea(x.World))
                               : EffectGeometry.World(leaf) is { } w && EffectGeometry.HasClosedArea(w);

    public static bool CanRun(EditorSession s) =>
        s.BooleanService is not null && s.OffsetService is not null && !s.Selection.IsEmpty && Leaves(s).Count > 0;

    /// <summary>The crescents that sit right above <paramref name="leaf"/> in the drawing order: its highlight, then its shadow.</summary>
    public static List<PathObject> Existing(DocObject leaf)
    {
        var found = new List<PathObject>();
        if (leaf.Layer is not { } layer) return found;
        int i = layer.IndexOf(leaf);
        if (i < 0) return found;
        for (int k = i + 1; k < layer.Children.Count && found.Count < 2 && layer.Children[k] is PathObject p && IsCrescent(p); k++) found.Add(p);
        return found;
    }

    public static bool HasBevel(EditorSession s) => Leaves(s).Any(l => Existing(l).Count > 0);

    // ---- Sources and geometry ----

    /// <summary>The objects to bevel with their outlines (a combined object's outline is its parts united). Taken on the UI thread.</summary>
    public static List<BevelSource> Sources(EditorSession s)
    {
        var sources = new List<BevelSource>();
        foreach (var leaf in Leaves(s))
        {
            var parts = EffectGeometry.Operands([leaf]).Where(x => EffectGeometry.HasClosedArea(x.World)).Select(x => x.World).ToList();
            if (parts.Count == 0) continue;
            var silhouette = parts.Count == 1 ? parts[0] : s.BooleanService is { } b ? b.Union(parts) : EffectGeometry.Concat(parts, FillRule.NonZero);
            var world = leaf.GetWorldTransform();
            sources.Add(new BevelSource(leaf, silhouette, HeightOf(leaf, silhouette, world), Math.Atan2(world.M12, world.M11)));
        }
        return sources;
    }

    /// <summary>The height a bevel's percentages are of: the outline's height; for live text the letter height of its font, so the same percentage gives the same rim on every string.</summary>
    public static double HeightOf(DocObject leaf, PathGeometry silhouette, Affine2 world) =>
        leaf is TextObject t ? t.FontSize * 0.72 * Math.Sqrt(Math.Abs(world.Determinant)) : silhouette.GetBounds().Height;

    /// <summary>Makes the two crescents of one object. Safe on any thread.</summary>
    public static BevelPieces Compute(IBooleanService booleans, IOffsetService offsets, BevelSource src, BevelSettings settings, CancellationToken ct = default)
    {
        var s = settings.Normalized();
        double h = src.Height, d = s.Width * h, inset = s.Inset * h;
        var shape = src.Silhouette;
        if (Math.Abs(inset) > 1e-9) shape = offsets.Offset(shape, -inset);   // inward for a positive inset
        ct.ThrowIfCancellationRequested();
        var none = new BevelPieces(null, ObjectStyle.None, null, ObjectStyle.None);
        if (shape.IsEmpty || d < 1e-9) return none;

        // The light direction: the highlight's cutter moves away from the light (toward the lower right for the default 135 degrees; document y points
        // down), turned with the object. The longer axis of the move is always d, so an edge that faces the light head on gets a rim d wide at the
        // default and at any multiple of 90 degrees, and (d, d) at the default as it always was.
        var away = AwayFromLight(s.LightAngle);
        var shift = Affine2.Rotation(src.Angle).TransformVector(away * (d / Math.Max(Math.Abs(away.X), Math.Abs(away.Y))));
        var options = new BooleanOptions { MinLoopArea = 1e-4 };
        var highlight = booleans.Difference(shape, shape.Transform(Affine2.Translation(shift)), options);
        ct.ThrowIfCancellationRequested();
        PathGeometry? shadow = null;
        if (s.IncludeShadow && s.Shadow > 0)
            shadow = booleans.Difference(shape, shape.Transform(Affine2.Translation(-shift)), options);

        return new BevelPieces(
            highlight.IsEmpty ? null : highlight, CrescentStyle(s.EffectiveHighlightColor, s.Highlight, s.Softness),
            shadow is { IsEmpty: false } ? shadow : null, CrescentStyle(s.EffectiveShadowColor, s.Shadow, s.Softness));
    }

    /// <summary>The unit direction, in document coordinates (y down) before the object's own turn, that points away from a light at <paramref name="lightAngle"/> degrees (counter-clockwise from the right).</summary>
    public static Vec2 AwayFromLight(double lightAngle)
    {
        double a = lightAngle * Math.PI / 180;
        return new Vec2(-Math.Cos(a), Math.Sin(a));
    }

    /// <summary>A crescent's paint: the color at the given opacity, with a feather of the softness (none at 0).</summary>
    public static ObjectStyle CrescentStyle(DocColor color, double opacity, double softness) =>
        new(new SolidPaint(color with { A = (byte)Math.Round(255 * Math.Clamp(opacity, 0, 1) * color.A / 255.0) }), null, null,
            softness > 0 ? new FeatherStyle(softness, 0) : null);

    public static List<BevelPieces> ComputeAll(IBooleanService booleans, IOffsetService offsets, IReadOnlyList<BevelSource> sources, BevelSettings settings, CancellationToken ct = default)
    {
        var all = new List<BevelPieces>(sources.Count);
        foreach (var src in sources)
        {
            ct.ThrowIfCancellationRequested();
            all.Add(Compute(booleans, offsets, src, settings, ct));
        }
        return all;
    }

    /// <summary>Builds the command for these sources and pieces (not executed). Existing crescents of the sources are replaced by it.</summary>
    public static BevelCommand? Plan(IReadOnlyList<BevelSource> sources, IReadOnlyList<BevelPieces> pieces)
    {
        var targets = new List<BevelCommand.Target>();
        for (int i = 0; i < sources.Count && i < pieces.Count; i++)
            targets.Add(new BevelCommand.Target(sources[i].Leaf, Existing(sources[i].Leaf), pieces[i]));
        return targets.Count == 0 ? null : new BevelCommand(targets, "Easy Bevel");
    }
}
