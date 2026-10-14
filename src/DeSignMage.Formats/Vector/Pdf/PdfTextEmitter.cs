using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.PdfFonts;

namespace DeSignMage.Formats.Vector.Pdf;

/// <summary>
/// Turns the letters PdfPig placed on a page into text objects: a run of letters in one font, size, color and direction becomes one
/// <see cref="TextObject"/> when the font is an installed font (every letter keeps its place through per-gap kerning), else one path object made of
/// the glyph outlines of the PDF's own (usually embedded) font.
/// </summary>
internal sealed class PdfTextEmitter(VectorBuilder builder, VectorImportOptions options, Affine2 pageBase)
{
    private readonly Dictionary<IFont, Dictionary<string, int>> _codes = [];

    private sealed class Placed
    {
        public required Letter Letter;
        public required string Text;
        public required Vec2 Start, End;
        public required double Size;
        public required double Angle;
        public required DocColor Color;
    }

    public void Emit(IReadOnlyList<Letter> letters)
    {
        var placed = new List<Placed>();
        foreach (var l in letters)
        {
            if (l.RenderingMode is TextRenderingMode.Neither or TextRenderingMode.NeitherClip) continue;
            if (string.IsNullOrEmpty(l.Value)) continue;
            var s = pageBase.Transform(new Vec2(l.StartBaseLine.X, l.StartBaseLine.Y));
            var e = pageBase.Transform(new Vec2(l.EndBaseLine.X, l.EndBaseLine.Y));
            double size = l.PointSize > 0 ? l.PointSize : l.FontSize;
            if (!(size > 0) || !double.IsFinite(s.X + s.Y + e.X + e.Y)) continue;
            double angle = Vec2.Distance(s, e) < 1e-9 ? placedAngle(l) : Math.Atan2(e.Y - s.Y, e.X - s.X);
            var color = ColorOf(l.RenderingMode is TextRenderingMode.Stroke or TextRenderingMode.StrokeClip ? l.StrokeColor ?? l.Color : l.FillColor ?? l.Color);
            placed.Add(new Placed { Letter = l, Text = l.Value, Start = s, End = e, Size = size, Angle = angle, Color = color });
        }

        foreach (var run in Runs(placed)) EmitRun(run);
    }

    // the baseline of a letter with no width follows the text orientation of the page
    private double placedAngle(Letter l)
    {
        var o = l.TextOrientation;
        double a = o switch { TextOrientation.Rotate90 => Math.PI / 2, TextOrientation.Rotate180 => Math.PI, TextOrientation.Rotate270 => -Math.PI / 2, _ => 0 };
        var d = pageBase.TransformVector(new Vec2(Math.Cos(a), -Math.Sin(a)));
        return Math.Atan2(d.Y, d.X);
    }

    // ---------------------------------------------------------------- runs

    private List<List<Placed>> Runs(List<Placed> letters)
    {
        var runs = new List<List<Placed>>();
        List<Placed>? run = null;
        Placed? prev = null;
        foreach (var p in letters)
        {
            bool joins = run is not null && prev is not null && Joins(prev, p);
            if (!joins) { run = []; runs.Add(run); }
            else if (GapBetween(prev!, p) is double gap && gap > 0.22 * p.Size && !IsSpace(prev!) && !IsSpace(p))
            {
                // a gap that is the width of a space but has no space letter in the file: put one in
                run!.Add(new Placed { Letter = prev!.Letter, Text = " ", Start = prev.End, End = p.Start, Size = p.Size, Angle = p.Angle, Color = p.Color });
            }
            run!.Add(p);
            prev = p;
        }
        return runs;
    }

    private static bool IsSpace(Placed p) => p.Text.All(char.IsWhiteSpace);

    private static double Along(Placed a, Vec2 point)
    {
        var d = new Vec2(Math.Cos(a.Angle), Math.Sin(a.Angle));
        return Vec2.Dot(point - a.End, d);
    }

    private static double GapBetween(Placed a, Placed b) => Along(a, b.Start);

    private static bool Joins(Placed a, Placed b)
    {
        if (a.Letter.FontName != b.Letter.FontName || a.Color != b.Color) return false;
        if (Math.Abs(a.Size - b.Size) > 0.01 * a.Size) return false;
        double da = Math.Abs(Math.IEEERemainder(a.Angle - b.Angle, 2 * Math.PI));
        if (da > 0.02) return false;
        var d = new Vec2(Math.Cos(a.Angle), Math.Sin(a.Angle));
        var n = new Vec2(-d.Y, d.X);
        double off = Math.Abs(Vec2.Dot(b.Start - a.End, n));
        double gap = Vec2.Dot(b.Start - a.End, d);
        return off < 0.3 * a.Size && gap > -0.6 * a.Size && gap < 4 * a.Size;
    }

    // ---------------------------------------------------------------- one run

    private void EmitRun(List<Placed> run)
    {
        while (run.Count > 0 && IsSpace(run[^1])) run.RemoveAt(run.Count - 1);
        while (run.Count > 0 && IsSpace(run[0])) run.RemoveAt(0);
        if (run.Count == 0) return;
        var first = run[0];
        var details = first.Letter.FontDetails;
        var (family, weight, italic) = FontNames.FromPdf(first.Letter.FontName ?? details.Name, details.IsBold, details.IsItalic);
        string text = string.Concat(run.Select(r => r.Text));
        bool installed = TextEngine.Fonts.IsInstalled(family);

        if (options.LiveText && installed && !text.Any(char.IsControl))
        {
            var t = MakeText(run, text, family, weight, italic);
            if (t is not null) { builder.Add(t); return; }
        }
        var outlines = Outlines(run);
        if (outlines is not null)
        {
            string label = text.Length > 40 ? text[..40] : text;
            builder.Add(new PathObject(outlines) { Name = label, Style = ObjectStyle.FillOnly(first.Color) });
            return;
        }
        // no outlines to be had: live text in whatever the font maps to
        var fallback = MakeText(run, text, family, weight, italic);
        if (fallback is not null)
        {
            builder.Add(fallback);
            builder.Count($"{{0}} text(s) use a font that could not be read ({family}); they are shown in a replacement font.");
        }
    }

    private TextObject? MakeText(List<Placed> run, string text, string family, int weight, bool italic)
    {
        var first = run[0];
        var t = new TextObject(text, family, first.Size)
        {
            FontWeight = weight, Italic = italic,
            Style = ObjectStyle.FillOnly(first.Color),
        };
        var d = new Vec2(Math.Cos(first.Angle), Math.Sin(first.Angle));
        t.Transform = Affine2.Rotation(first.Angle) * Affine2.Translation(first.Start);

        // every letter where the file put it: the gaps beyond the font's own advances become tracking and kerning
        var layout = t.Layout;
        if (layout.Letters.Count == run.Count && run.Count > 1)
        {
            var pens = run.Select(r => Vec2.Dot(r.Start - first.Start, d)).ToList();
            var gaps = t.KerningForLetterPositions(pens).ToList();
            while (gaps.Count < pens.Count - 1) gaps.Add(0);
            double tracking = gaps.Count == 0 ? 0 : gaps.OrderBy(g => g).ElementAt(gaps.Count / 2);
            t.Tracking = tracking;
            t.Kerning = gaps.Select(g => Math.Abs(g - tracking) < 1e-6 ? 0 : g - tracking).ToArray();
        }
        else if (run.Count > 1) builder.Count("{0} text(s) have letters that do not match the font's letters; their spacing is the font's own.");
        return t;
    }

    // ---------------------------------------------------------------- outlines

    private PathGeometry? Outlines(List<Placed> run)
    {
        var verbs = new List<PathVerb>();
        var points = new List<Vec2>();
        bool any = false;
        foreach (var p in run)
        {
            if (IsSpace(p)) continue;
            var font = p.Letter.GetFont();
            if (font is null) return null;
            int code = CodeOf(font, p.Text);
            if (code < 0 || !font.TryGetNormalisedPath(code, out var paths) || paths is null) return null;
            var d = new Vec2(Math.Cos(p.Angle), Math.Sin(p.Angle));
            var u = new Vec2(d.Y, -d.X);
            var m = new Affine2(d.X * p.Size, d.Y * p.Size, u.X * p.Size, u.Y * p.Size, p.Start.X, p.Start.Y);
            var b = new PathBuilder();
            AppendSubpaths(b, paths);
            var g = b.Build().Transform(m);
            verbs.AddRange(g.Verbs); points.AddRange(g.Points);
            any = true;
        }
        return any ? new PathGeometry(verbs.ToArray(), points.ToArray(), FillRule.NonZero) : null;
    }

    private static void AppendSubpaths(PathBuilder b, IReadOnlyList<PdfSubpath> subpaths)
    {
        foreach (var sp in subpaths)
        {
            foreach (var cmd in sp.Commands)
            {
                switch (cmd)
                {
                    case PdfSubpath.Move m: b.MoveTo(m.Location.X, m.Location.Y); break;
                    case PdfSubpath.Line l: b.LineTo(l.To.X, l.To.Y); break;
                    case PdfSubpath.CubicBezierCurve c: b.CubicTo(c.FirstControlPoint.X, c.FirstControlPoint.Y, c.SecondControlPoint.X, c.SecondControlPoint.Y, c.EndPoint.X, c.EndPoint.Y); break;
                    case PdfSubpath.QuadraticBezierCurve q: b.QuadTo(new Vec2(q.ControlPoint.X, q.ControlPoint.Y), new Vec2(q.EndPoint.X, q.EndPoint.Y)); break;
                    case PdfSubpath.Close: b.Close(); break;
                }
            }
        }
    }

    /// <summary>The character code that a font shows as <paramref name="value"/> (found by asking the font about every code once).</summary>
    private int CodeOf(IFont font, string value)
    {
        if (!_codes.TryGetValue(font, out var map))
        {
            map = [];
            for (int c = 0; c < 65536; c++)
                if (font.TryGetUnicode(c, out var u) && !string.IsNullOrEmpty(u)) map.TryAdd(u, c);
            _codes[font] = map;
        }
        return map.TryGetValue(value, out int code) ? code : -1;
    }

    // ---------------------------------------------------------------- colors

    internal static DocColor ColorOf(IColor? c)
    {
        switch (c)
        {
            case null: return DocColor.Black;
            case CMYKColor k: return DocColor.FromCmyk(k.C * 100, k.M * 100, k.Y * 100, k.K * 100);
            default:
                var (r, g, b) = c.ToRGBValues();
                return new DocColor(B(r), B(g), B(b));
        }
    }

    private static byte B(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
}
