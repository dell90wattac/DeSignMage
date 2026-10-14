using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Text;

/// <summary>
/// Puts a straight <see cref="TextLayout"/> on a path (<see cref="TextOnPath"/>). The path is flattened to a fine polyline and measured by arc
/// length; each letter's anchor (the middle of its advance box, on the baseline) goes to its distance along the path and the letter is
/// rotated by the path's direction there. The result has the same letters, with outlines already placed, and a placement matrix per letter
/// (so carets, dots and hit tests can map between the straight text and what is drawn).
/// </summary>
public static class TextPathPlacer
{
    /// <summary>Flattening tolerance in points: far below what is visible, so the polyline is smooth and the bend sums are accurate.</summary>
    private const double FlattenTolerance = 0.01;

    /// <summary>A turn this sharp or sharper counts as this much (tan of 76°): a corner would otherwise push letters to infinity.</summary>
    private const double TurnLimit = 4;

    /// <summary>
    /// How strongly a bend moves the letters after it: <c>shift = BendFactor · pathLength · tan(turn) · compensation</c>. The spec reads 0.2 from the
    /// legacy code, but with that the stored layout in the sample file (compensation 0.76) comes out visibly crowded, while 0.12 reproduces it (a fit of the
    /// letter positions against the file puts the factor at 0.124) and also reconciles the two conflicting defaults: the legacy help says "6 %" and the code's
    /// default is 0.5, and 0.12 × 0.5 = 6 %. See notes/effects/text-on-path.md section 8.
    /// </summary>
    public const double BendFactor = 0.12;

    public static TextLayout Place(TextLayout straight, TextOnPath settings)
    {
        var walker = PathWalker.Create(settings.Path, settings.Flip, settings.EffectiveCompensation);
        if (walker is null) return straight;   // a path with no length: nothing to follow, the text stays straight

        var letters = straight.Letters;
        var anchors = new List<int>();
        for (int i = 0; i < letters.Count; i++) if (!letters[i].IsBreak) anchors.Add(i);
        if (anchors.Count == 0) return straight;

        double Center(int i) => letters[i].X + letters[i].Advance / 2;
        double first = Center(anchors[0]), last = Center(anchors[^1]);
        double fitScale = last - first > 1e-6 ? walker.Length / (last - first) : 1;   // the legacy "spread over the whole path"
        double lift = settings.Raise + (settings.Side == TextPathSide.Below ? -straight.Ascent : 0);
        double alignBase = settings.Spacing == TextPathSpacing.Fit ? 0 : walker.AlignmentBase(AlignmentOf(straight));

        var placed = new TextLetter[letters.Count];
        var placements = new Affine2[letters.Count];
        var outlines = new List<PathGeometry>();
        var boxes = Rect2.Empty;
        for (int i = 0; i < letters.Count; i++)
        {
            var l = letters[i];
            if (l.IsBreak) { placed[i] = l; placements[i] = Affine2.Identity; continue; }
            double cx = Center(i);
            double d = settings.StartOffset + (settings.Spacing == TextPathSpacing.Fit ? (cx - first) * fitScale : alignBase + cx);
            var (point, angle) = walker.At(d);
            var m = Affine2.Translation(-cx, -lift) * Affine2.Rotation(angle) * Affine2.Translation(point);
            placements[i] = m;
            var outline = l.Outline.IsEmpty ? PathGeometry.EmptyPath : l.Outline.Transform(m);
            if (!outline.IsEmpty) outlines.Add(outline);
            placed[i] = new TextLetter
            {
                Index = l.Index, Start = l.Start, Length = l.Length, Line = l.Line, IsBreak = false,
                X = l.X, Baseline = l.Baseline, Advance = l.Advance, Outline = outline,
            };
            foreach (var corner in Box(l, straight)) boxes = boxes.Union(m.Transform(corner));
        }

        return new TextLayout
        {
            Letters = placed, Lines = straight.Lines, Outline = Join(outlines), Frame = boxes.IsEmpty ? straight.Frame : boxes,
            ResolvedFamily = straight.ResolvedFamily, FontMissing = straight.FontMissing,
            Ascent = straight.Ascent, Descent = straight.Descent, LineHeight = straight.LineHeight,
            Placements = placements, StraightFrame = straight.Frame, Alignment = straight.Alignment,
        };
    }

    /// <summary>The four corners of a letter's advance box (the line's ascent above its baseline to the descent below), in the straight text.</summary>
    internal static Vec2[] Box(TextLetter l, TextLayout straight) =>
    [
        new(l.X, l.Baseline - straight.Ascent), new(l.X + l.Advance, l.Baseline - straight.Ascent),
        new(l.X + l.Advance, l.Baseline + straight.Descent), new(l.X, l.Baseline + straight.Descent),
    ];

    private static Model.TextAlignment AlignmentOf(TextLayout straight) => straight.Alignment;

    private static PathGeometry Join(List<PathGeometry> parts)
    {
        if (parts.Count == 0) return PathGeometry.EmptyPath;
        if (parts.Count == 1) return parts[0];
        var verbs = new List<PathVerb>();
        var points = new List<Vec2>();
        foreach (var p in parts) { verbs.AddRange(p.Verbs); points.AddRange(p.Points); }
        return new PathGeometry(verbs.ToArray(), points.ToArray());
    }

    /// <summary>A path as a polyline with distances, and the bend compensation at every corner of it.</summary>
    internal sealed class PathWalker
    {
        private readonly Vec2[] _pts;
        private readonly double[] _start;    // distance at the start of segment j
        private readonly Vec2[] _dir;        // unit direction of segment j
        private readonly double[] _comp;     // compensation in force on segment j (sum of the bend terms up to j)

        public double Length { get; }

        private PathWalker(Vec2[] pts, double[] start, Vec2[] dir, double[] comp, double length)
        {
            _pts = pts; _start = start; _dir = dir; _comp = comp; Length = length;
        }

        public static PathWalker? Create(PathGeometry path, bool flip, double compensation)
        {
            var poly = path.Flatten(FlattenTolerance).Where(p => p.Points.Length >= 2).OrderByDescending(Measure).FirstOrDefault();
            if (poly.Points is null) return null;
            var pts = new List<Vec2>();
            foreach (var p in poly.Points)
                if (pts.Count == 0 || Vec2.Distance(pts[^1], p) > 1e-9) pts.Add(p);
            if (poly.Closed && pts.Count > 1 && Vec2.Distance(pts[^1], pts[0]) > 1e-9) pts.Add(pts[0]);
            if (pts.Count < 2) return null;
            if (flip) pts.Reverse();

            int n = pts.Count - 1;
            var start = new double[n];
            var dir = new Vec2[n];
            double total = 0;
            for (int j = 0; j < n; j++)
            {
                var d = pts[j + 1] - pts[j];
                double len = d.Length;
                start[j] = total;
                dir[j] = d / len;
                total += len;
            }

            // Bend compensation, summed bend by bend: positive where the path turns toward the letters' tops (up is to the left of the heading).
            var comp = new double[n];
            double sum = 0;
            for (int j = 1; j < n; j++)
            {
                double sin = dir[j - 1].X * dir[j].Y - dir[j - 1].Y * dir[j].X;     // > 0 turns clockwise on screen (away from up)
                double cos = Vec2.Dot(dir[j - 1], dir[j]);
                double t = Math.Clamp(sin / Math.Max(Math.Abs(cos), 1e-9), -TurnLimit, TurnLimit);
                sum += BendFactor * total * -t * compensation;
                comp[j] = sum;
            }
            return new PathWalker(pts.ToArray(), start, dir, comp, total);
        }

        private static double Measure(Polyline p)
        {
            double len = 0;
            for (int i = 1; i < p.Points.Length; i++) len += Vec2.Distance(p.Points[i - 1], p.Points[i]);
            return len;
        }

        /// <summary>Where a text with this alignment starts (Left at the path start, Center at its middle, Right at its end), for natural spacing.</summary>
        public double AlignmentBase(Model.TextAlignment alignment) => alignment switch
        {
            Model.TextAlignment.Center => Length / 2,
            Model.TextAlignment.Right => Length,
            _ => 0,
        };

        /// <summary>The point at distance <paramref name="d"/> from the start and the heading there (radians, y down). Before the start and past the end the path is extended straight.</summary>
        public (Vec2 Point, double Angle) At(double d)
        {
            int n = _dir.Length;
            for (int j = 0; j < n; j++)
            {
                double t = d + _comp[j];
                double end = j + 1 < n ? _start[j + 1] : double.PositiveInfinity;
                if (t > end) continue;
                if (j > 0) t = Math.Max(t, _start[j]);
                return (_pts[j] + _dir[j] * (t - _start[j]), Math.Atan2(_dir[j].Y, _dir[j].X));
            }
            return (_pts[0], 0);   // unreachable: the last segment takes everything
        }
    }
}
