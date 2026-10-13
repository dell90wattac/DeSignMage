using DeSignMage.Core.Geometry;

namespace DeSignMage.Formats.Lyt;

/// <summary>
/// Turns a path into the 28-byte nodes of a layout record, the inverse of <see cref="LytImporter.BuildPath"/>:
/// lines, circular arcs (centre and signed sweep) and cubic Béziers (flag 0x400), one contour after the other, a closing node that ends
/// where the contour starts, and the outer / hole flag of every contour. The record's y axis points up and the document's down, so the
/// <c>map</c> that moves points into node space must flip the vertical axis (the signs of the arc sweeps follow from that).
/// </summary>
internal static class LytNodeBuilder
{
    /// <summary>Contour flags: start, line, arc, close, hole (counter-clockwise), outer (clockwise), first contour of a glyph.</summary>
    private const uint Start = LytNode.FlagMove, Line = 0x2, Arc = LytNode.FlagArc, Close = LytNode.FlagClose;
    private const uint Hole = LytNode.FlagHole, Outer = LytNode.FlagOuter, FirstOfGlyph = LytNode.FlagMarker | 0x200000;

    /// <summary>With more closed contours than this the stray-contour check (which compares every pair) is skipped.</summary>
    private const int NestingLimit = 1500;

    /// <summary>An arc wider than this is written as several, so each stays well inside what the layout programs draw.</summary>
    private const double MaxArcSweep = Math.PI;

    /// <summary>Source units (points); an arc whose middle is closer than this to its chord is written as a line.</summary>
    private const double FlatArc = 1e-3;

    /// <param name="geometry">The path in its source space (any units).</param>
    /// <param name="map">Source point to node space; it must flip the vertical axis.</param>
    /// <param name="closeOpen">Close open contours (a filled shape is closed anyway); false keeps them open.</param>
    /// <param name="markGlyph">Flag the first node of the first contour as the start of a glyph (text records).</param>
    public static List<OutNode> Build(PathGeometry geometry, Func<Vec2, Vec2> map, bool closeOpen, bool markGlyph = false)
    {
        var loops = PathEditing.ToLoops(geometry);
        if (closeOpen)
        {
            foreach (var l in loops.Where(l => !l.Closed))
            {
                if (Vec2.Distance(l.Segments[^1].End, l.Start) > 1e-9) l.Segments.Add(Segment.Line(l.Start));
                else l.Segments[^1] = l.Segments[^1] with { End = l.Start };
                l.Closed = true;
            }
        }
        Orient(loops, geometry.FillRule);

        var nodes = new List<OutNode>();
        bool first = true;
        foreach (var l in loops)
        {
            uint side = !l.Closed ? 0 : l.IsClockwise ? Outer : Hole;
            uint startFlags = Start | side | (first && markGlyph ? FirstOfGlyph : 0);
            first = false;
            var s = map(l.Start);
            nodes.Add(OutNode.Point(startFlags, (float)s.X, (float)s.Y));

            var from = l.Start;
            for (int i = 0; i < l.Segments.Count; i++)
            {
                var seg = l.Segments[i];
                uint close = l.Closed && i == l.Segments.Count - 1 ? Close : 0;
                switch (seg.Kind)
                {
                    case SegmentKind.Line:
                    {
                        var p = map(seg.End);
                        nodes.Add(OutNode.Point(Line | side | close, (float)p.X, (float)p.Y));
                        break;
                    }
                    case SegmentKind.Cubic:
                    {
                        var c1 = map(seg.C1); var c2 = map(seg.C2); var p = map(seg.End);
                        nodes.Add(OutNode.Cubic(side | close, (float)p.X, (float)p.Y, (float)c1.X, (float)c1.Y, (float)c2.X, (float)c2.Y));
                        break;
                    }
                    default:
                        AddArc(nodes, from, seg, side, close, map);
                        break;
                }
                from = seg.End;
            }
        }
        return nodes;
    }

    private static void AddArc(List<OutNode> nodes, Vec2 from, Segment seg, uint side, uint close, Func<Vec2, Vec2> map)
    {
        // An arc that bulges less than FlatArc from its chord is a straight line within what the 32-bit coordinates can tell apart; rebuilding
        // its circle from three points that close together would give a wild centre (and sometimes the long way round).
        double chord = Vec2.Distance(from, seg.End);
        double bulge = chord < 1e-12 ? double.PositiveInfinity : Math.Abs(Vec2.Cross(seg.End - from, seg.C1 - from)) / chord;
        if (bulge < FlatArc || !Arcs.TryCircle(from, seg.C1, seg.End, out var centre, out double radius) || radius < 1e-9)
        {
            var q = map(seg.End);
            nodes.Add(OutNode.Point(Line | side | close, (float)q.X, (float)q.Y));
            return;
        }
        var (a0, sweep) = Arcs.Angles(from, seg.C1, seg.End, centre);
        int pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / MaxArcSweep - 1e-9));
        var c = map(centre);
        double step = sweep / pieces;
        for (int k = 1; k <= pieces; k++)
        {
            bool last = k == pieces;
            var end = last ? seg.End : centre + new Vec2(Math.Cos(a0 + step * k), Math.Sin(a0 + step * k)) * radius;
            var p = map(end);
            // The source space is y down and the node space y up, so the same arc runs the other way round.
            nodes.Add(OutNode.Arc(Arc | side | (last ? close : 0), (float)p.X, (float)p.Y, (float)c.X, (float)c.Y, -step));
        }
    }

    // ---------------------------------------------------------------- contour direction

    /// <summary>
    /// Makes the direction of every contour agree with what it means, as the layout expects (outer contours clockwise, holes counter-clockwise):
    /// <list type="bullet">
    /// <item>Even-odd paths: a contour inside an odd number of others is a hole.</item>
    /// <item>Non-zero paths: the directions already say which contour is a hole. A counter-clockwise contour that lies inside nothing would
    /// fill as a hole-less area in our drawing but be taken for a hole by the layout, so it is turned around.</item>
    /// </list>
    /// </summary>
    private static void Orient(List<Loop> loops, FillRule rule)
    {
        var closed = loops.Where(l => l.Closed).ToList();
        if (closed.Count == 0) return;

        if (rule == FillRule.EvenOdd)
        {
            if (closed.Count > NestingLimit) return;   // too many contours to compare pairwise: their directions are kept as they are
            var kinds = PathEditing.Classify(closed);
            for (int i = 0; i < closed.Count; i++)
            {
                bool wantClockwise = kinds[i] != LoopKind.Interior;
                if (closed[i].IsClockwise != wantClockwise) PathEditing.Reverse(closed[i]);
            }
            return;
        }

        var ccw = closed.Where(l => !l.IsClockwise).ToList();
        if (ccw.Count == 0 || closed.Count > NestingLimit) return;
        var outers = closed.Where(l => l.IsClockwise).Select(l => (Geometry: l.ToGeometry(), Bounds: l.ToGeometry().GetBounds())).ToList();
        foreach (var l in ccw)
        {
            var sample = l.PointOn(0, 0.5);
            bool inside = outers.Any(o => o.Bounds.Contains(sample) && o.Geometry.Contains(sample, 0.01));
            if (!inside) PathEditing.Reverse(l);
        }
    }
}
