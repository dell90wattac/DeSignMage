using System.Globalization;
using System.Text;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Vectorize;

/// <summary>Writes a <see cref="VectorizeResult"/> as a plain SVG file (one path per part, bottom to top).</summary>
public static class SvgWriter
{
    public static string ToSvg(VectorizeResult r, DocColor? background = null)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.Append(inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{r.Width}\" height=\"{r.Height}\" viewBox=\"0 0 {r.Width} {r.Height}\">\n");
        if (background is { } bg) sb.Append(inv, $"<rect width=\"{r.Width}\" height=\"{r.Height}\" fill=\"{bg.ToHex()}\"/>\n");
        foreach (var s in r.Shapes)
        {
            sb.Append("<path d=\"").Append(PathData(s.Path)).Append('"');
            sb.Append(s.Fill is { } f ? $" fill=\"{f.ToHex()}\"" : " fill=\"none\"");
            if (s.Fill is not null) sb.Append(" fill-rule=\"evenodd\"");
            if (s.Stroke is { } st) sb.Append(inv, $" stroke=\"{st.ToHex()}\" stroke-width=\"{r.StrokeWidth}\" stroke-linejoin=\"round\"");
            sb.Append("/>\n");
        }
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    public static string PathData(PathGeometry p)
    {
        p = p.WithArcsAsCubics();   // user geometry may contain arcs; the tracer itself only makes lines and cubics
        var sb = new StringBuilder();
        var pts = p.Points;
        int pi = 0;
        static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
        foreach (var verb in p.Verbs)
        {
            switch (verb)
            {
                case PathVerb.MoveTo: sb.Append('M').Append(N(pts[pi].X)).Append(' ').Append(N(pts[pi].Y)); pi++; break;
                case PathVerb.LineTo: sb.Append('L').Append(N(pts[pi].X)).Append(' ').Append(N(pts[pi].Y)); pi++; break;
                case PathVerb.CubicTo:
                    sb.Append('C')
                      .Append(N(pts[pi].X)).Append(' ').Append(N(pts[pi].Y)).Append(' ')
                      .Append(N(pts[pi + 1].X)).Append(' ').Append(N(pts[pi + 1].Y)).Append(' ')
                      .Append(N(pts[pi + 2].X)).Append(' ').Append(N(pts[pi + 2].Y));
                    pi += 3;
                    break;
                case PathVerb.Close: sb.Append('Z'); break;
            }
        }
        return sb.ToString();
    }
}
