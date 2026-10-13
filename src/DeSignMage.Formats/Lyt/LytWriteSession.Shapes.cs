using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Lyt;

internal sealed partial class LytWriteSession
{
    private const uint GradLinear = 1, GradCircle = 2, GradEllipse = 3, GradHorizontal = 5, GradVertical = 6;
    private const uint TransLinear = 2, TransCircle = 3, TransEllipse = 4, TransHorizontal = 0xC, TransVertical = 0xD;
    private const int MaxNodes = GradientPaint.MaxStops;

    private int _strokesDropped, _strokeHairlines, _gradientStrokes, _gradientFlagsDropped, _alphaDropped, _fadeApproximated;

    /// <summary>The fill of a part: a plain color, or a color with a gradient / fade block.</summary>
    private readonly record struct ResolvedFill(DocColor? Color, FillSpec? Block);

    // ---------------------------------------------------------------- shapes

    /// <summary>
    /// Writes a path object: the fill as one record and, when the object has a stroke, the stroke as a ring shape above it. The geometry
    /// is baked: node coordinates are inches from the shape's own bottom-left corner, and the record's position says where that is.
    /// </summary>
    private void AddShape(PathGeometry local, Rect2 localBounds, Affine2 world, ObjectStyle style, string name, int group, uint layerId)
    {
        var geom = local.Transform(world);
        var bounds = geom.GetBounds();
        if (geom.IsEmpty || bounds.IsEmpty) { result.SkippedCount++; return; }
        name = LytEncoding.Sanitize(name);

        Vec2 Map(Vec2 p) => new((p.X - bounds.Left) / Units.PointsPerInch, (bounds.Bottom - p.Y) / Units.PointsPerInch);
        var fill = ResolveFill(style.Fill, localBounds, world, Map);

        bool filled = fill.Color is not null;
        bool strokeWritten = false;
        if (filled)
        {
            var part = ShapePart(geom, bounds, name, fill.Color!.Value, fill.Block, layerId);
            if (part is null) { result.SkippedCount++; return; }
            Add(part, group);
        }
        if (style.Stroke is { Width: > 0 } stroke)
        {
            var ring = BuildRing(geom, stroke, world);
            if (ring is not null && ShapePart(ring, ring.GetBounds(), filled ? name + " (stroke)" : name, StrokeColor(stroke), null, layerId) is { } ringPart)
            {
                Add(ringPart, group);
                result.StrokeShapeCount++;
                strokeWritten = true;
            }
            else if (!filled) _strokeHairlines++;
            else _strokesDropped++;
        }
        if (!filled && !strokeWritten)
        {
            // Nothing to fill: the outline stays as an unfilled hairline (a cut line), so the geometry is not lost.
            var line = ShapePart(geom, bounds, name, null, null, layerId);
            if (line is null) { result.SkippedCount++; return; }
            Add(line, group);
        }
    }

    private DocColor StrokeColor(StrokeStyle stroke)
    {
        if (stroke.Paint is SolidPaint sp) return sp.Color with { A = 255 };
        if (stroke.Paint is GradientPaint gp) { _gradientStrokes++; return gp.Stops[0].Color with { A = 255 }; }
        return DocColor.Black;
    }

    /// <summary>A shape record for <paramref name="geometry"/> (document points); a null color writes an unfilled outline.</summary>
    private PartRecord? ShapePart(PathGeometry geometry, Rect2 bounds, string name, DocColor? color, FillSpec? block, uint layerId)
    {
        double x0 = bounds.Left, y0 = bounds.Bottom;
        Vec2 Map(Vec2 p) => new((p.X - x0) / Units.PointsPerInch, (y0 - p.Y) / Units.PointsPerInch);
        var nodes = LytNodeBuilder.Build(geometry, Map, closeOpen: color is not null);
        if (nodes.Count == 0) return null;

        var (orgX, orgY) = Units.ToUser(new Vec2(x0, y0), Page);
        double w = bounds.Width / Units.PointsPerInch, h = bounds.Height / Units.PointsPerInch;
        var p = new PartRecord
        {
            Kind = PartKind.Shape, Name = name, OrgX = orgX, OrgY = orgY, SizeW = w, SizeH = h,
            ActualLeft = 0, ActualTop = h, ActualRight = w, ActualBottom = 0,
            Type = block is null || block.Type == 0 ? 1u : 0x81u, Type2 = 0x4000, LayerId = layerId,   // a fade is type 1 like a plain shape, a gradient 0x81
            Color = color is { } c ? LytColors.From(c) : default, Fill = block,
        };
        p.Nodes.AddRange(nodes);
        SetPageBounds(p, bounds);
        return p;
    }

    // ---------------------------------------------------------------- fills

    private ResolvedFill ResolveFill(Paint? paint, Rect2 localBounds, Affine2 world, Func<Vec2, Vec2> map)
    {
        switch (paint)
        {
            case SolidPaint sp:
                if (sp.Color.A == 0) return default;
                if (sp.Color.A < 255) _alphaDropped++;
                return new ResolvedFill(sp.Color with { A = 255 }, null);
            case GradientPaint gp:
                if (!options.Gradients) return new ResolvedFill(gp.Stops[0].Color with { A = 255 }, null);
                return BuildGradient(gp, localBounds, world, map);
            default:
                return default;
        }
    }

    private ResolvedFill BuildGradient(GradientPaint gp, Rect2 localBounds, Affine2 world, Func<Vec2, Vec2> map)
    {
        var first = gp.Stops[0].Color;
        var solid = new ResolvedFill(first with { A = 255 }, null);
        if (localBounds.IsEmpty) return solid;

        var frame = gp.Frame(localBounds);
        Vec2 ToNode(Vec2 local) => map(world.Transform(local));
        var p0 = ToNode(frame.P0); var p1 = ToNode(frame.P1); var p2 = ToNode(frame.P2);
        if ((p1 - p0).LengthSquared < 1e-12 || !double.IsFinite(p0.X + p0.Y + p1.X + p1.Y + p2.X + p2.Y)) return solid;
        if (gp.AlternateColors || gp.Reflect) _gradientFlagsDropped++;

        var stops = gp.Stops;
        bool sameColor = stops.All(s => s.Color.R == first.R && s.Color.G == first.G && s.Color.B == first.B);
        bool anyAlpha = stops.Any(s => s.Color.A < 255);
        if (sameColor && anyAlpha) return BuildFade(gp, first, p0, p1, p2);

        if (anyAlpha) _alphaDropped++;
        var nodes = new List<(Vec2 Point, DocColor Color, bool Highlight)> { (p0, stops[0].Color, false), (p1, stops[^1].Color, false) };
        if (gp.Kind == GradientKind.Ellipse) nodes.Add((p2, stops[^1].Color, false));
        var axis = p1 - p0;
        for (int i = 1; i < stops.Count - 1 && nodes.Count < MaxNodes; i++)
            nodes.Add((p0 + axis * stops[i].Offset, stops[i].Color, stops[i].Highlight));
        if (stops.Count - 2 + (gp.Kind == GradientKind.Ellipse ? 3 : 2) > MaxNodes)
            result.Warnings.Add($"A gradient has more than {MaxNodes} colors; the rest were left out.");

        var spec = new FillSpec
        {
            Type = gp.Kind switch
            {
                GradientKind.Circle => GradCircle, GradientKind.Ellipse => GradEllipse, GradientKind.Horizontal => GradHorizontal,
                GradientKind.Vertical => GradVertical, _ => GradLinear,
            },
            NodeCount = nodes.Count,
        };
        for (int i = 0; i < nodes.Count; i++)
        {
            var (pt, color, highlight) = nodes[i];
            var lc = LytColors.From(color);
            if (i < 4) { spec.Colors[i] = lc; spec.Points[2 * i] = (float)pt.X; spec.Points[2 * i + 1] = (float)pt.Y; }
            else spec.Extras.Add(new LytBlendNode((float)pt.X, (float)pt.Y, lc, highlight ? (ushort)1 : (ushort)0));
        }
        result.GradientCount++;
        return new ResolvedFill(first with { A = 255 }, spec);
    }

    /// <summary>A color that fades to nothing: one color under a transparency ramp (the legacy "Transparency" fade).</summary>
    private ResolvedFill BuildFade(GradientPaint gp, DocColor color, Vec2 p0, Vec2 p1, Vec2 p2)
    {
        var solid = new ResolvedFill(color with { A = 255 }, null);
        double a0 = gp.Stops[0].Color.A / 255.0, a1 = gp.Stops[^1].Color.A / 255.0;
        if (gp.Stops.Count > 2) _fadeApproximated++;
        if (Math.Abs(a0 - 1) < 1e-3 && Math.Abs(a1 - 1) < 1e-3) return solid;

        bool radial = gp.Kind is GradientKind.Circle or GradientKind.Ellipse;
        if (!SolveFade(radial, a0, a1, out double min, out double max, out bool swap)) return solid;

        var spec = new FillSpec
        {
            Type = 0, TransMin = (float)min, TransMax = (float)max, TransSwap = swap,
            TransType = gp.Kind switch
            {
                GradientKind.Circle => TransCircle, GradientKind.Ellipse => TransEllipse, GradientKind.Horizontal => TransHorizontal,
                GradientKind.Vertical => TransVertical, _ => TransLinear,
            },
        };
        spec.Colors[0] = LytColors.From(color with { A = 255 });
        spec.TransPoints[0] = (float)p0.X; spec.TransPoints[1] = (float)p0.Y;
        spec.TransPoints[2] = (float)p1.X; spec.TransPoints[3] = (float)p1.Y;
        spec.TransPoints[4] = (float)p2.X; spec.TransPoints[5] = (float)p2.Y;
        result.FadeCount++;
        return new ResolvedFill(color with { A = 255 }, spec);
    }

    /// <summary>
    /// The legacy transparency fields for an opacity <paramref name="a0"/> at the start (or the center) and <paramref name="a1"/> at the end (or the
    /// radius), the inverse of <see cref="LytGradients"/>: opacity runs from <c>1 − Max</c> to <c>1 − Min·Max</c>, the other way round when swapped,
    /// and in circles and ellipses the center is the high end.
    /// </summary>
    internal static bool SolveFade(bool radial, double a0, double a1, out double min, out double max, out bool swap)
    {
        min = 0; max = 1; swap = false;
        static bool Valid(double mn, double mx) => double.IsFinite(mn) && double.IsFinite(mx) && mx > 1e-6 && mx <= 1 + 1e-9 && mn >= -1e-9 && mn <= 1 + 1e-9;

        // Plain: linear start = low, end = high; radial start = high, end = low (high = 1 - min*max, low = 1 - max).
        double mx1 = radial ? 1 - a1 : 1 - a0;
        double mn1 = (radial ? 1 - a0 : 1 - a1) / mx1;
        if (Valid(mn1, mx1)) { min = Math.Clamp(mn1, 0, 1); max = Math.Min(mx1, 1); swap = false; return true; }

        // Swapped: linear start = high, end = low; radial start = 1 - high, end = 1 - low.
        double mx2 = radial ? a1 : 1 - a1;
        double mn2 = radial ? a0 / a1 : (1 - a0) / mx2;
        if (Valid(mn2, mx2)) { min = Math.Clamp(mn2, 0, 1); max = Math.Min(mx2, 1); swap = true; return true; }
        return false;
    }

    // ---------------------------------------------------------------- strokes

    /// <summary>The area a stroke covers, as a shape of its own (document points), or null when no geometry services were given.</summary>
    private PathGeometry? BuildRing(PathGeometry geom, StrokeStyle stroke, Affine2 world)
    {
        var offset = options.OffsetService; var boolean = options.BooleanService;
        if (offset is null || boolean is null) return null;
        double half = stroke.Width * world.AverageScale / 2;
        if (!double.IsFinite(half) || half <= 1e-6) return null;

        var loops = PathEditing.ToLoops(geom);
        var closedLoops = loops.Where(l => l.Closed).ToList();
        var openLoops = loops.Where(l => !l.Closed).ToList();
        var opts = new OffsetOptions
        {
            Corners = stroke.Join switch { LineJoin.Round => CornerStyle.Round, LineJoin.Bevel => CornerStyle.Bevel, _ => CornerStyle.Miter },
            Caps = stroke.Cap switch { LineCap.Butt => CapStyle.Butt, LineCap.Square => CapStyle.Square, _ => CapStyle.Round },
            OpenPaths = OpenPathMode.AroundStroke,
        };

        var pieces = new List<PathGeometry>();
        if (closedLoops.Count > 0)
        {
            var closed = PathEditing.FromLoops(closedLoops, geom.FillRule);
            var outer = offset.Offset(closed, half, opts);
            var inner = offset.Offset(closed, -half, opts);
            var ring = inner.IsEmpty ? outer : boolean.Difference(outer, inner);
            if (!ring.IsEmpty) pieces.Add(ring);
        }
        if (openLoops.Count > 0)
        {
            var open = offset.Offset(PathEditing.FromLoops(openLoops, FillRule.NonZero), half, opts);
            if (!open.IsEmpty) pieces.Add(open);
        }
        return pieces.Count switch { 0 => null, 1 => pieces[0], _ => boolean.Union(pieces) };
    }

    /// <summary>Adds the warnings for what was dropped or approximated; called once at the end.</summary>
    private void Summarize()
    {
        if (_strokesDropped > 0)
            result.Warnings.Add($"{(_strokesDropped == 1 ? "1 stroke was" : $"{_strokesDropped} strokes were")} left out: the legacy objects have no stroke and no geometry services were given to turn it into a shape.");
        if (_strokeHairlines > 0)
            result.Warnings.Add($"{(_strokeHairlines == 1 ? "1 object that is only a stroke was" : $"{_strokeHairlines} objects that are only a stroke were")} written as an unfilled outline (no geometry services were given).");
        if (_gradientStrokes > 0)
            result.Warnings.Add($"{(_gradientStrokes == 1 ? "1 stroke has" : $"{_gradientStrokes} strokes have")} a gradient; the first color was used.");
        if (_gradientFlagsDropped > 0)
            result.Warnings.Add("Alternate-color and reflected gradients are written as ordinary ones.");
        if (_alphaDropped > 0)
            result.Warnings.Add($"{(_alphaDropped == 1 ? "1 fill has" : $"{_alphaDropped} fills have")} transparency that a layout cannot store on this kind of fill; it was written opaque.");
        if (_fadeApproximated > 0)
            result.Warnings.Add($"{(_fadeApproximated == 1 ? "1 fade has" : $"{_fadeApproximated} fades have")} more than two opacity steps; the first and last were used.");
        if (_picturesStraightened > 0)
            result.Warnings.Add($"{(_picturesStraightened == 1 ? "1 picture is" : $"{_picturesStraightened} pictures are")} skewed or mirrored; a layout holds it upright in the box it covers.");
        if (result.SoftEffectCount > 0)
            result.Warnings.Add(options.SoftEffects == LytSoftEffects.Skip
                ? $"{result.SoftEffectCount} soft shadow or glow object(s) were left out: a layout has no blurred objects."
                : $"{result.SoftEffectCount} soft shadow or glow object(s) were written as plain objects: a layout has no blurred objects.");
        if (result.HiddenSkippedCount > 0)
            result.Warnings.Add($"{result.HiddenSkippedCount} hidden object(s) were not written.");
        if (result.SkippedCount > 0)
            result.Warnings.Add($"{result.SkippedCount} object(s) could not be written (no outline, or a picture that could not be stored).");
    }
}
