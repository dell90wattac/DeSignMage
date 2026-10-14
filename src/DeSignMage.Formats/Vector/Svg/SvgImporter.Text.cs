using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;

namespace DeSignMage.Formats.Vector.Svg;

internal sealed partial class SvgImporter
{
    private sealed class Chunk(SvgStyle style, double x, double y)
    {
        public readonly StringBuilder Text = new();
        public SvgStyle Style = style;
        public double X = x, Y = y;
    }

    // ---------------------------------------------------------------- text

    private void Text(XElement e, Context ctx, SvgProps props)
    {
        double fs = ctx.Style.FontSize;
        double X(SvgProps p, string key, double cur) { var l = SvgUnits.Numbers(p.GetValueOrDefault(key)); return l.Count > 0 ? ScaleLen(p, key, fs, ctx) : cur; }
        double x = X(props, "x", 0), y = X(props, "y", 0);
        x += FirstNumber(props, "dx", fs, ctx.ViewWidth); y += FirstNumber(props, "dy", fs, ctx.ViewHeight);
        if (SvgUnits.Numbers(props.GetValueOrDefault("x")).Count > 1 || SvgUnits.Numbers(props.GetValueOrDefault("rotate")).Count > 0)
            _b.Count("{0} text(s) place single letters (x/y/rotate lists); the letters were set in a line instead.");

        var chunks = new List<Chunk>();
        Chunk? current = null;
        double penX = x, penY = y;

        void Flush()
        {
            if (current is null) return;
            string text = current.Text.ToString();
            if (text.Trim().Length > 0)
            {
                var t = MakeText(text, current, ctx);
                if (t is not null) penX = current.X + Advance(t, current);
                else penX = current.X;
            }
            penY = current.Y;
            current = null;
        }

        void Walk(XElement el, SvgStyle style, bool first)
        {
            foreach (var node in el.Nodes())
            {
                if (node is XText tx)
                {
                    string s = Regex.Replace(tx.Value, @"\s+", " ");
                    if (current is null) { s = s.TrimStart(); if (s.Length == 0) continue; current = new Chunk(style, penX, penY); }
                    else if (!ReferenceEquals(current.Style, style) && current.Style != style) { Flush(); s = s.TrimStart(); if (s.Length == 0) continue; current = new Chunk(style, penX, penY); }
                    current.Text.Append(s);
                }
                else if (node is XElement child)
                {
                    string cn = child.Name.LocalName;
                    if (cn is not ("tspan" or "a" or "textPath")) continue;
                    var cp = Props(child);
                    if (cp.GetValueOrDefault("display") == "none") continue;
                    var cs = style.Derive(cp, ctx.ViewWidth, ctx.ViewHeight);
                    if (cn == "textPath") { _b.Count("{0} text(s) on a path were set in a straight line."); }
                    bool positioned = cp.ContainsKey("x") || cp.ContainsKey("y");
                    if (positioned || cp.ContainsKey("dx") || cp.ContainsKey("dy"))
                    {
                        Flush();
                        if (cp.ContainsKey("x")) penX = X(cp, "x", penX);
                        if (cp.ContainsKey("y")) penY = X(cp, "y", penY);
                        penX += FirstNumber(cp, "dx", cs.FontSize, ctx.ViewWidth);
                        penY += FirstNumber(cp, "dy", cs.FontSize, ctx.ViewHeight);
                    }
                    Walk(child, cs, false);
                    if (cs != style) Flush();
                }
            }
        }

        Walk(e, ctx.Style, true);
        Flush();
    }

    private static double FirstNumber(SvgProps p, string key, double fs, double percentOf)
    {
        if (!p.TryGetValue(key, out var text)) return 0;
        var first = text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return SvgUnits.Length(first, fs, percentOf, 0);
    }

    private static double ScaleLen(SvgProps p, string key, double fs, Context ctx)
    {
        var first = p[key].Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return SvgUnits.Length(first, fs, key.StartsWith('x') ? ctx.ViewWidth : ctx.ViewHeight, 0);
    }

    private double Advance(TextObject t, Chunk c) => c.Style.Anchor == "start" ? t.Layout.Frame.Width : 0;

    private TextObject? MakeText(string text, Chunk chunk, Context ctx)
    {
        var st = chunk.Style;
        if (!st.Visible) return null;
        var (family, installed) = FontNames.FromCss(st.FontFamily);
        double fs = st.FontSize;
        if (!(fs > 0)) return null;

        var fillSpec = st.Fill;
        DocColor? color = null;
        if (fillSpec is not null && fillSpec != "none")
        {
            double alpha = Math.Clamp(st.FillOpacity * st.Opacity, 0, 1);
            if (fillSpec.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
            {
                var target = Resolve(fillSpec);
                var first = target is null ? null : GradStops(target).FirstOrDefault();
                if (first is not null && SvgColors.TryParse(Props(first).GetValueOrDefault("stop-color") ?? "black", out var gc)) color = gc;
                _b.Count("{0} text(s) have a gradient or pattern fill; the first color is used.");
            }
            else if (fillSpec.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) color = st.Color;
            else if (SvgColors.TryParse(fillSpec, out var c)) color = c;
            if (color is { } cc) color = cc with { A = (byte)Math.Round(cc.A * alpha) };
        }
        StrokeStyle? stroke = null;
        if (st.Stroke is { } ss && ss != "none" && st.StrokeWidth > 0 && SvgColors.TryParse(ss, out var sc))
            stroke = new StrokeStyle(new SolidPaint(sc with { A = (byte)Math.Round(sc.A * st.StrokeOpacity * st.Opacity) }), st.StrokeWidth * ctx.World.AverageScale, st.Join, st.Cap, st.MiterLimit);
        if ((color is null || color.Value.A == 0) && stroke is null) return null;

        var t = new TextObject(text, family, fs)
        {
            FontWeight = st.FontWeight, Italic = st.Italic,
            Alignment = st.Anchor switch { "middle" => TextAlignment.Center, "end" => TextAlignment.Right, _ => TextAlignment.Left },
            Tracking = st.LetterSpacing / fs,
            Style = new ObjectStyle(color is { A: > 0 } col ? new SolidPaint(col) : null, stroke),
        };
        var m = Affine2.Translation(chunk.X, chunk.Y) * ctx.World;
        // upright and uniformly scaled: the scale goes into the font size, the rest stays as the transform
        double s = Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
        if (s > 1e-9 && Arcs.IsSimilarity(m) && m.Determinant > 0)
        {
            t.FontSize = fs * s;
            t.Tracking = st.LetterSpacing / fs;
            m = Affine2.Scale(1 / s, 1 / s) * m;
            if (stroke is not null) t.Style = t.Style with { Stroke = stroke };
        }
        t.Transform = m;
        if (!installed) _b.Count("{0} text(s) use a font that is not installed here; they keep its name and show in a replacement font.");
        EmitText(t);
        return t;
    }

    private void EmitText(TextObject t)
    {
        if (_options.LiveText) { _b.Add(t); return; }
        _b.Add(VectorText.ToOutlines(t));
    }

    // ---------------------------------------------------------------- pictures

    private void Image(XElement e, Context ctx, SvgProps props)
    {
        string? href = Local(e, "href");
        if (string.IsNullOrWhiteSpace(href)) return;
        var data = LoadImage(href.Trim());
        if (data is null) { _b.Count("{0} picture(s) could not be loaded (a missing file or an unsupported link)."); return; }
        if (!ImageInfo.TryRead(data, out string media, out int pw, out int ph))
        {
            _b.Count("{0} picture(s) are in a format that is not supported (such as SVG inside SVG).");
            return;
        }
        double fs = ctx.Style.FontSize;
        double x = SvgUnits.Length(props.GetValueOrDefault("x"), fs, ctx.ViewWidth, 0), y = SvgUnits.Length(props.GetValueOrDefault("y"), fs, ctx.ViewHeight, 0);
        double w = SvgUnits.Length(props.GetValueOrDefault("width"), fs, ctx.ViewWidth, pw > 0 ? pw : 0), h = SvgUnits.Length(props.GetValueOrDefault("height"), fs, ctx.ViewHeight, ph > 0 ? ph : 0);
        if (!(w > 0 && h > 0)) { if (pw > 0 && ph > 0) { w = w > 0 ? w : h * pw / ph; h = h > 0 ? h : w * ph / pw; } if (!(w > 0 && h > 0)) return; }

        // preserveAspectRatio: the picture keeps its proportions inside the box (xMidYMid meet unless told otherwise)
        string par = (props.GetValueOrDefault("preserveAspectRatio") ?? "xMidYMid meet").Trim();
        double iw = w, ih = h, ox = 0, oy = 0;
        if (!par.StartsWith("none", StringComparison.Ordinal) && pw > 0 && ph > 0)
        {
            double sx = w / pw, sy = h / ph;
            double k = par.EndsWith("slice", StringComparison.Ordinal) ? Math.Max(sx, sy) : Math.Min(sx, sy);
            iw = pw * k; ih = ph * k;
            ox = AlignOffset(par, 'x', w - iw); oy = AlignOffset(par, 'y', h - ih);
            if (par.EndsWith("slice", StringComparison.Ordinal) && (iw > w + 1e-9 || ih > h + 1e-9)) _b.Count("{0} picture(s) are cropped by their box in the file; they are shown whole.");
        }
        string assetId = _b.Assets.Add(data, media);
        var img = new ImageObject(assetId, iw, ih, Math.Max(pw, 0), Math.Max(ph, 0)) { Name = Label(e), Transform = Affine2.Translation(x + ox, y + oy) * ctx.World };
        if (ctx.Style.Opacity < 1) _b.Count("{0} picture(s) are partly transparent in the file; they are drawn opaque.");
        _b.Add(img);
    }

    private byte[]? LoadImage(string href)
    {
        try
        {
            if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = href.IndexOf(',');
                if (comma < 0) return null;
                string meta = href[5..comma];
                string payload = href[(comma + 1)..];
                return meta.Contains(";base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(Regex.Replace(payload, @"\s+", ""))
                    : Encoding.Latin1.GetBytes(Uri.UnescapeDataString(payload));
            }
            if (_baseDirectory is null || href.Contains("://", StringComparison.Ordinal)) return null;
            string path = Path.GetFullPath(Path.Combine(_baseDirectory, Uri.UnescapeDataString(href).Replace('/', Path.DirectorySeparatorChar)));
            var info = new FileInfo(path);
            return info.Exists && info.Length < 100_000_000 ? File.ReadAllBytes(path) : null;
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
