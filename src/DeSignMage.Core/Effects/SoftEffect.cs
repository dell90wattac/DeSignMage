using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Effects;

/// <summary>
/// Everything that decides how a Soft Shadow or a Glow looks, as one value (notes/effects/soft-shadow.md, glow.md). Lengths in points, the shift in
/// document directions (x right, y DOWN; the Context Bar shows Vs the other way round: up is positive). <see cref="Softness"/>, <see cref="Inset"/>
/// and <see cref="Thickness"/> are fractions of the source's height, as in the legacy layouts, so the same numbers look the same on a big and a small object.
/// </summary>
public sealed record SoftSettings
{
    public SoftKind Kind { get; init; } = SoftKind.SoftShadow;

    /// <summary>0..1.</summary>
    public double Opacity { get; init; } = 0.5;

    /// <summary>Blur: sigma is 0.068 × Softness × the source's height. 0..5 (more than 1 is a very wide halo).</summary>
    public double Softness { get; init; } = 0.5;

    public double ShiftX { get; init; }
    public double ShiftY { get; init; }

    /// <summary>Soft Shadow: how far the shadow's outline is pulled in (positive) or pushed out (negative), as 0.04 × Inset × height per side.</summary>
    public double Inset { get; init; }

    /// <summary>Cast shadow: how far the top is pushed sideways, in degrees from vertical.</summary>
    public double Slant { get; init; }

    /// <summary>Cast shadow: the shadow's height compared to the source's (1 = the same; the shadow is squashed or stretched about the bottom).</summary>
    public double HeightScale { get; init; } = 1;

    /// <summary>Glow: how far the glow's shape grows, as 0.04 × Thickness × height per side. Stored in the file as a negative inset.</summary>
    public double Thickness { get; init; }

    public DocColor Color { get; init; } = DocColor.Black;

    public double Vs => -ShiftY;

    public bool IsGlow => Kind == SoftKind.Glow;

    /// <summary>The inset the shape is made with: a glow grows by its thickness, a shadow by (the negative of) its inset.</summary>
    public double EffectiveInset => IsGlow ? -Thickness : Inset;

    public SoftSettings Normalized() => this with
    {
        Opacity = Math.Clamp(Opacity, 0, 1),
        Softness = Math.Clamp(Softness, 0, 5),
        Thickness = IsGlow ? Math.Max(Thickness, 0) : 0,
        Inset = IsGlow ? 0 : Inset,
        Slant = IsGlow ? 0 : Math.Clamp(Slant, -89, 89),
        HeightScale = IsGlow ? 1 : Math.Clamp(HeightScale, 0.01, 10),
        ShiftX = IsGlow ? 0 : ShiftX,
        ShiftY = IsGlow ? 0 : ShiftY,
    };
}

/// <summary>
/// Effects ▸ Soft Shadow and Effects ▸ Glow (notes/effects/soft-shadow.md, glow.md; user decisions R7, R8, R13, R22): a separate vector object made from the
/// source's outline, <b>grouped with its source</b> (not linked: editing the source later leaves it as it is) and placed just below the source in the drawing
/// order. The object keeps the outline as vectors; the blur is a <see cref="BlurStyle"/> on its style that the renderer draws at any zoom or export resolution.
/// A group source gets the shadow as one more member, its outline is the union of all of its members; any other source and the shadow become a new group.
/// Everything is one <see cref="SoftEffectCommand"/>, so a slider drag or typing in the Context Bar is one undo step.
/// </summary>
public static class SoftEffect
{
    /// <summary>The shift a new Soft Shadow starts with, as a fraction of the source's height (10 %: the legacy program's "1 unit" on a text 10 units high).</summary>
    public const double DefaultShiftFraction = 0.10;

    public static string NameOf(SoftKind kind) => kind == SoftKind.Glow ? "Glow" : "Soft Shadow";

    // ---- What a source is ----

    /// <summary>The objects that make up the shape of <paramref name="source"/>: its drawn leaves, without effect objects that were made earlier.</summary>
    public static IEnumerable<DocObject> SourceLeaves(DocObject source)
    {
        var leaves = source is GroupObject g ? g.Leaves() : [source];
        return leaves.Where(l => l.Style.Blur is null);
    }

    /// <summary>True for objects a soft effect can be made from: a shape with an area, or a picture (its rectangle).</summary>
    public static bool HasArea(DocObject o) =>
        o is ImageObject || (EffectGeometry.IsShape(o) && EffectGeometry.World(o) is { } w && EffectGeometry.HasClosedArea(w));

    public static bool CanRun(EditorSession s) =>
        !s.Selection.IsEmpty && s.Selection.Items.Any(i => i.Style.Blur is null && SourceLeaves(i).Any(HasArea));

    /// <summary>The selected top-level objects that can be a source, bottom to top.</summary>
    public static List<DocObject> Sources(EditorSession s) =>
        Stacking.BottomToTop(s.Document, s.Selection.Items).Where(i => i.Style.Blur is null && SourceLeaves(i).Any(HasArea)).ToList();

    /// <summary>The effect object of this kind that belongs to <paramref name="source"/>: a member of the source group with a <see cref="BlurStyle"/> of that kind. Null when none.</summary>
    public static PathObject? Find(DocObject source, SoftKind kind) =>
        source is GroupObject g ? g.Children.OfType<PathObject>().FirstOrDefault(p => p.Style.Blur?.Kind == kind) : null;

    // ---- The outline of the source ----

    /// <summary>The source's outline in document coordinates: the union of the areas of its leaves (a picture counts as its rectangle). Null when it has none.</summary>
    public static PathGeometry? Silhouette(IBooleanService? booleans, DocObject source)
    {
        var parts = new List<PathGeometry>();
        foreach (var leaf in SourceLeaves(source).OrderBy(l => l.Layer?.IndexOf(l) ?? -1))
        {
            if (leaf is CombinedObject)
                parts.AddRange(EffectGeometry.Operands([leaf]).Where(x => EffectGeometry.HasClosedArea(x.World)).Select(x => x.World));
            else if (leaf is ImageObject img && img.GetLocalGeometry() is { IsEmpty: false } rect)
                parts.Add(rect.Transform(img.GetWorldTransform()));
            else if (EffectGeometry.World(leaf) is { } w && EffectGeometry.HasClosedArea(w))
                parts.Add(w);
        }
        if (parts.Count == 0) return null;
        if (parts.Count == 1) return parts[0];
        return booleans is null ? EffectGeometry.Concat(parts, FillRule.NonZero) : booleans.Union(parts);
    }

    // ---- The shape and style of the effect object ----

    /// <summary>
    /// The shadow's outline in document coordinates: the source's outline, slanted and squashed about its bottom edge (a cast shadow), grown or
    /// pulled in by the inset, then shifted. The blur is not part of it: that is the object's <see cref="BlurStyle"/>.
    /// </summary>
    public static PathGeometry Shape(IOffsetService? offsets, PathGeometry silhouette, SoftSettings s)
    {
        var bounds = silhouette.GetBounds();
        double h = bounds.Height;
        var shape = silhouette;
        if (!s.IsGlow && (s.Slant != 0 || s.HeightScale != 1) && h > 1e-9)
        {
            double slant = Math.Tan(s.Slant * Math.PI / 180), sy = s.HeightScale, bottom = bounds.Bottom;
            shape = shape.Transform(new Affine2(1, 0, -slant, sy, slant * bottom, bottom * (1 - sy)));
        }
        double grow = -0.04 * s.EffectiveInset * h;   // positive grows, negative pulls in
        if (Math.Abs(grow) > 1e-9 && offsets is not null)
            shape = offsets.Offset(shape, grow, new OffsetOptions { Corners = CornerStyle.Round });
        if (!s.IsGlow && (s.ShiftX != 0 || s.ShiftY != 0))
            shape = shape.Transform(Affine2.Translation(s.ShiftX, s.ShiftY));
        return shape;
    }

    /// <summary>Height of the source in the units of the effect object (the blur is a fraction of this; the object scales with its group, so does the blur).</summary>
    public static double Reference(PathGeometry silhouette, DocObject source)
    {
        double h = silhouette.GetBounds().Height;
        var parent = source is GroupObject g ? g.GetWorldTransform() : EffectGeometry.ParentWorld(source);
        double scale = Math.Sqrt(Math.Abs(parent.Determinant));
        return scale > 1e-12 ? h / scale : h;
    }

    public static ObjectStyle StyleOf(SoftSettings s, double reference) => new(
        new SolidPaint(s.Color), null,
        new BlurStyle(s.Kind, s.Opacity, s.Softness, reference, s.ShiftX, -s.ShiftY, s.Slant, s.HeightScale, s.EffectiveInset));

    /// <summary>What an existing effect object was made with. The inverse of <see cref="StyleOf"/>.</summary>
    public static SoftSettings Read(PathObject effect)
    {
        var b = effect.Style.Blur!;
        return new SoftSettings
        {
            Kind = b.Kind,
            Opacity = b.Opacity,
            Softness = b.Softness,
            ShiftX = b.Hs,
            ShiftY = -b.Vs,
            Inset = b.Kind == SoftKind.Glow ? 0 : b.Inset,
            Thickness = b.Kind == SoftKind.Glow ? -b.Inset : 0,
            Slant = b.Slant,
            HeightScale = b.HeightScale,
            Color = effect.Style.Fill is SolidPaint sp ? sp.Color : DocColor.Black,
        };
    }

    /// <summary>The settings a first start uses: the legacy program's defaults (50 % opacity, 50 % softness), the shadow shifted down and to the right.</summary>
    public static SoftSettings Defaults(SoftKind kind, double sourceHeight, DocColor? sourceColor = null)
    {
        if (kind == SoftKind.Glow)
        {
            // white on a dark source, black on a light one: the halo has to show against the page and against the source (open question: the legacy program picks white on a dark page)
            var c = sourceColor ?? DocColor.Black;
            double luma = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
            return new SoftSettings { Kind = kind, Opacity = 0.5, Softness = 0.5, Thickness = 0.5, Color = luma < 0.5 ? DocColor.White : DocColor.Black };
        }
        double shift = DefaultShiftFraction * sourceHeight;
        return new SoftSettings { Kind = kind, Opacity = 0.5, Softness = 0.5, ShiftX = shift, ShiftY = shift, Color = DocColor.Black };
    }

    /// <summary>The color of the source's paint, for choosing a glow color (null for a source without a plain fill).</summary>
    public static DocColor? ColorOf(DocObject source) =>
        SourceLeaves(source).Select(l => l.Style.Fill as SolidPaint).FirstOrDefault(p => p is not null)?.Color;

    // ---- Building the command ----

    /// <summary>
    /// The new state for one source: the effect object's outline in the space of the group it lives in (the source group's own space, or the document's)
    /// and its style. Null when the source has no outline.
    /// </summary>
    public static (PathGeometry Local, ObjectStyle Style)? Make(IBooleanService? booleans, IOffsetService? offsets, DocObject source, SoftSettings settings, PathGeometry? silhouette = null)
    {
        silhouette ??= Silhouette(booleans, source);
        if (silhouette is null) return null;
        var shape = Shape(offsets, silhouette, settings);
        if (shape.IsEmpty) return null;
        var into = source is GroupObject g ? g.GetWorldTransform() : Affine2.Identity;
        var local = into.IsIdentity || !into.IsInvertible ? shape : shape.Transform(into.Invert());
        return (local, StyleOf(settings, Reference(silhouette, source)));
    }

    /// <summary>Builds the command for these sources (not executed). An existing effect of the kind is modified, otherwise a new one is made.</summary>
    public static SoftEffectCommand? Plan(EditorSession s, IReadOnlyList<DocObject> sources, SoftSettings settings, IReadOnlyDictionary<DocObject, PathGeometry>? silhouettes = null)
    {
        var targets = new List<SoftEffectCommand.Target>();
        foreach (var source in sources)
        {
            PathGeometry? sil = null;
            silhouettes?.TryGetValue(source, out sil);
            sil ??= Silhouette(s.BooleanService, source);
            if (sil is null) continue;
            var made = Make(s.BooleanService, s.OffsetService, source, settings, sil);
            if (made is null) continue;
            var existing = Find(source, settings.Kind);
            string name = NameOf(settings.Kind);
            var effect = existing ?? new PathObject(made.Value.Local) { Name = name, Style = made.Value.Style, Visible = source.Visible, Locked = source.Locked };
            targets.Add(new SoftEffectCommand.Target(source, effect, existing is null, made.Value.Local, made.Value.Style));
        }
        if (targets.Count == 0) return null;
        bool anyNew = targets.Any(t => t.IsNew);
        return new SoftEffectCommand(targets, (anyNew ? "Add " : "Modify ") + NameOf(settings.Kind));
    }
}
