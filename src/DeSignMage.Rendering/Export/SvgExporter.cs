using System.Globalization;
using System.Text;
using System.Xml;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Rendering.Export;

/// <summary>
/// Writes the design as SVG with our own writer (not Skia's SVG canvas), so structure, names and colors stay exact:
/// <list type="bullet">
/// <item>One <c>&lt;g&gt;</c> per layer (<c>data-layer</c>), one per group; object names go to <c>data-name</c>, named (vinyl) colors to
/// <c>data-fill-name</c> / <c>data-stroke-name</c>.</item>
/// <item>User units are document points; the <c>viewBox</c> is the export area, <c>width</c>/<c>height</c> are in inches.</item>
/// <item>Every shape is written in page coordinates (its transform already applied), so stroke widths stay what they are in the
/// design. Circular arcs stay arcs (<c>A</c>) under rotation/uniform scale/mirror, otherwise they become Béziers.</item>
/// <item>A combined object keeps its look: fills clipped to the even-odd area of all its parts (overlaps knocked out), outlines on top.</item>
/// <item>Colors are written as <c>#RRGGBB</c> plus <c>fill-opacity</c>/<c>stroke-opacity</c> for alpha: exact.</item>
/// <item>Live text: glyph outlines by default (looks the same everywhere), or real <c>&lt;text&gt;</c> with one positioned
/// <c>&lt;tspan&gt;</c> per letter, so tracking and kerning survive, if <see cref="ExportOptions.TextAsOutlines"/> is off.</item>
/// <item>Bitmaps are embedded as data URIs. Guides are never written.</item>
/// </list>
/// </summary>
public static class SvgExporter
{
    private const string SvgNs = "http://www.w3.org/2000/svg";
    private const string XlinkNs = "http://www.w3.org/1999/xlink";

    public static string Render(Document doc, ExportOptions options)
    {
        var plan = ExportPlan.Create(doc, options);
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = false, Encoding = new UTF8Encoding(false) };
        using (var xml = XmlWriter.Create(new StringWriterUtf8(sb), settings))
            new Writer(doc, plan, options, xml).Write();
        return sb.ToString();
    }

    public static byte[] RenderBytes(Document doc, ExportOptions options) => new UTF8Encoding(false).GetBytes(Render(doc, options));

    private sealed class StringWriterUtf8(StringBuilder sb) : StringWriter(sb, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }

    private sealed class Writer(Document doc, ExportPlan plan, ExportOptions options, XmlWriter xml)
    {
        private int _clipCounter;

        public void Write()
        {
            var a = plan.Area;
            xml.WriteStartElement("svg", SvgNs);
            xml.WriteAttributeString("xmlns", "xlink", null, XlinkNs);
            xml.WriteAttributeString("version", "1.1");
            xml.WriteAttributeString("width", Num(a.Width / Units.PointsPerInch) + "in");
            xml.WriteAttributeString("height", Num(a.Height / Units.PointsPerInch) + "in");
            xml.WriteAttributeString("viewBox", $"{Num(a.Left)} {Num(a.Top)} {Num(a.Width)} {Num(a.Height)}");
            xml.WriteElementString("title", options.Title ?? "DeSign Mage export");

            int index = 0;
            foreach (var layer in plan.Layers)
            {
                index++;
                xml.WriteStartElement("g");
                xml.WriteAttributeString("id", "layer-" + index);
                xml.WriteAttributeString("data-layer", layer.Layer.Name);

                // Objects come in drawing order. A group is written as a <g> around its members; when other objects are drawn between its
                // members (R22) the group's <g> is closed and opened again around the next run, so the picture is exact and each run is named.
                var open = new List<GroupObject>();
                foreach (var o in layer.Objects)
                {
                    var chain = new List<GroupObject>();
                    for (var g = o.Parent as GroupObject; g is not null; g = g.Parent as GroupObject) chain.Insert(0, g);
                    int common = 0;
                    while (common < open.Count && common < chain.Count && ReferenceEquals(open[common], chain[common])) common++;
                    for (int i = open.Count - 1; i >= common; i--)
                    {
                        xml.WriteEndElement();
                        open.RemoveAt(i);
                    }
                    for (int i = common; i < chain.Count; i++)
                    {
                        xml.WriteStartElement("g");
                        Name(chain[i]);
                        open.Add(chain[i]);
                    }
                    WriteObject(o);
                }
                foreach (var _ in open) xml.WriteEndElement();
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }

        private void WriteObject(DocObject o)
        {
            if (!o.Visible) return;
            // Feather (R8): the object is written as usual inside a group that carries a mask (the blurred, eroded silhouette)
            if (o is not GroupObject && o.Style.Feather is { Amount: > 0 } feather && BeginFeather(o, feather))
            {
                WriteUnfeathered(o);
                xml.WriteEndElement();
                return;
            }
            WriteUnfeathered(o);
        }

        private void WriteUnfeathered(DocObject o)
        {
            switch (o)
            {
                case GroupObject g:
                    xml.WriteStartElement("g");
                    Name(o);
                    foreach (var c in g.Children) WriteObject(c);
                    xml.WriteEndElement();
                    break;

                case CombinedObject combined:
                    WriteCombined(combined);
                    break;

                case ImageObject img:
                    WriteImage(img, img.GetWorldTransform());
                    break;

                case TextObject text when !options.TextAsOutlines:
                    WriteLiveText(text, text.GetWorldTransform());
                    break;

                default:
                    WriteShape(o, o.GetWorldTransform(), fill: true, stroke: true);
                    break;
            }
        }

        private void WriteCombined(CombinedObject combined)
        {
            var world = combined.GetWorldTransform();
            string clipId = "clip-" + (++_clipCounter);
            xml.WriteStartElement("g");
            Name(combined);

            xml.WriteStartElement("defs");
            xml.WriteStartElement("clipPath");
            xml.WriteAttributeString("id", clipId);
            xml.WriteStartElement("path");
            xml.WriteAttributeString("d", PathData(combined.GetLocalGeometry().Transform(world)));
            xml.WriteAttributeString("clip-rule", "evenodd");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();

            // Fills first, clipped to the knocked-out area; then outlines, unclipped (same order as the screen).
            xml.WriteStartElement("g");
            xml.WriteAttributeString("clip-path", $"url(#{clipId})");
            foreach (var part in combined.Parts) WritePart(part, world, fill: true, stroke: false);
            xml.WriteEndElement();
            xml.WriteStartElement("g");
            foreach (var part in combined.Parts) WritePart(part, world, fill: false, stroke: true);
            xml.WriteEndElement();

            xml.WriteEndElement();
        }

        private void WritePart(DocObject part, Affine2 parentWorld, bool fill, bool stroke)
        {
            if (!part.Visible) return;
            var world = part.Transform * parentWorld;
            switch (part)
            {
                case ImageObject img:
                    if (fill) WriteImage(img, world);
                    break;
                case TextObject text when !options.TextAsOutlines:
                    if (fill) WriteLiveText(text, world);   // live text keeps one element; its outline (if any) goes with it
                    break;
                default:
                    WriteShape(part, world, fill, stroke);
                    break;
            }
        }

        private void WriteShape(DocObject o, Affine2 world, bool fill, bool stroke)
        {
            var style = o.Style;
            if (style.Blur is { } blur && style.Fill is SolidPaint blurFill)
            {
                if (fill) WriteSoftShape(o, world, blur, blurFill.Color);
                return;
            }
            bool drawFill = fill && style.Fill is not null;
            bool drawStroke = stroke && style.Stroke is { Width: > 0 };
            if (!drawFill && !drawStroke) return;
            if (o.GetLocalGeometry() is not { IsEmpty: false } geom) return;
            var world2 = geom.Transform(world);

            // A gradient is defined in the object's own space and carried to the page by the same matrix as the shape (exact under any transform).
            string? gradientId = drawFill && style.Fill is GradientPaint gp ? WriteGradient(gp, geom.GetBounds(), world) : null;

            xml.WriteStartElement("path");
            Name(o);
            xml.WriteAttributeString("d", PathData(world2));
            if (drawFill)
            {
                if (style.Fill is SolidPaint sf) PaintAttrs("fill", sf.Color);
                else if (gradientId is not null) xml.WriteAttributeString("fill", $"url(#{gradientId})");
                else if (style.Fill is GradientPaint flat) PaintAttrs("fill", flat.Stops[^1].Color);   // a gradient with no extent
                if (world2.FillRule == FillRule.EvenOdd) xml.WriteAttributeString("fill-rule", "evenodd");
            }
            else xml.WriteAttributeString("fill", "none");
            if (drawStroke) StrokeAttrs(style.Stroke!);
            xml.WriteEndElement();
        }

        private void WriteLiveText(TextObject t, Affine2 world)
        {
            var style = t.Style;
            var layout = t.Layout;
            // The text element's own transform is its world matrix, so its gradient needs none (it is in the text's local space).
            string? gradientId = style.Fill is GradientPaint gp ? WriteGradient(gp, layout.Outline.GetBounds(), null) : null;
            xml.WriteStartElement("text");
            Name(t);
            xml.WriteAttributeString("transform", Matrix(world));
            var family = t.FontFamily.Replace("\"", "'");
            xml.WriteAttributeString("font-family", t.IsFontMissing ? $"\"{family}\", \"{layout.ResolvedFamily}\", sans-serif" : $"\"{family}\", sans-serif");
            xml.WriteAttributeString("font-size", Num(t.FontSize));
            if (t.FontWeight != 400) xml.WriteAttributeString("font-weight", t.FontWeight.ToString(CultureInfo.InvariantCulture));
            if (t.Italic) xml.WriteAttributeString("font-style", "italic");
            xml.WriteAttributeString("xml", "space", null, "preserve");
            if (style.Fill is SolidPaint sf) PaintAttrs("fill", sf.Color);
            else if (gradientId is not null) xml.WriteAttributeString("fill", $"url(#{gradientId})");
            else xml.WriteAttributeString("fill", "none");
            if (style.Stroke is { Width: > 0 } stroke) StrokeAttrs(stroke);

            // One positioned tspan per letter keeps tracking and per-gap kerning. Written raw so no indentation whitespace lands inside the text.
            var spans = new StringBuilder();
            foreach (var letter in layout.Letters)
            {
                if (letter.IsBreak) continue;
                var s = t.Text.Substring(letter.Start, letter.Length);
                if (string.IsNullOrWhiteSpace(s)) continue;
                spans.Append("<tspan x=\"").Append(Num(letter.X)).Append("\" y=\"").Append(Num(letter.Baseline)).Append("\">")
                     .Append(System.Security.SecurityElement.Escape(s)).Append("</tspan>");
            }
            if (spans.Length > 0) xml.WriteRaw(spans.ToString());
            xml.WriteEndElement();
        }

        private void WriteImage(ImageObject img, Affine2 world)
        {
            if (doc.Assets.Get(img.AssetId) is not { } asset) return;
            xml.WriteStartElement("image");
            Name(img);
            xml.WriteAttributeString("x", "0");
            xml.WriteAttributeString("y", "0");
            xml.WriteAttributeString("width", Num(img.Width));
            xml.WriteAttributeString("height", Num(img.Height));
            xml.WriteAttributeString("preserveAspectRatio", "none");
            if (!world.IsIdentity) xml.WriteAttributeString("transform", Matrix(world));
            xml.WriteAttributeString("xlink", "href", XlinkNs, $"data:{asset.MediaType};base64,{Convert.ToBase64String(asset.Data)}");
            xml.WriteEndElement();
        }

        // ---- soft paint (Soft Shadow, Glow, Feather) ----

        private int _softCounter;

        /// <summary>A Soft Shadow or Glow object: its outline with a Gaussian blur filter (feGaussianBlur), in its color times the opacity. The page is not rasterized; the viewer blurs.</summary>
        private void WriteSoftShape(DocObject o, Affine2 world, BlurStyle blur, DocColor color)
        {
            if (o.GetLocalGeometry() is not { IsEmpty: false } geom) return;
            var shape = geom.Transform(world);
            double sigma = blur.Sigma * Math.Sqrt(Math.Abs(world.Determinant));
            var faded = color with { A = (byte)Math.Round(Math.Clamp(blur.Opacity, 0, 1) * color.A) };
            string? filterId = null;
            if (sigma >= 0.02)
            {
                var b = shape.GetBounds();
                double margin = 3 * sigma + 1;
                filterId = "soft-" + (++_softCounter);
                xml.WriteStartElement("defs");
                xml.WriteStartElement("filter");
                xml.WriteAttributeString("id", filterId);
                xml.WriteAttributeString("filterUnits", "userSpaceOnUse");
                xml.WriteAttributeString("x", Num(b.Left - margin));
                xml.WriteAttributeString("y", Num(b.Top - margin));
                xml.WriteAttributeString("width", Num(b.Width + 2 * margin));
                xml.WriteAttributeString("height", Num(b.Height + 2 * margin));
                xml.WriteStartElement("feGaussianBlur");
                xml.WriteAttributeString("stdDeviation", Num(sigma));
                xml.WriteEndElement();
                xml.WriteEndElement();
                xml.WriteEndElement();
            }
            xml.WriteStartElement("path");
            Name(o);
            xml.WriteAttributeString("d", PathData(shape));
            PaintAttrs("fill", faded);
            if (shape.FillRule == FillRule.EvenOdd) xml.WriteAttributeString("fill-rule", "evenodd");
            if (filterId is not null) xml.WriteAttributeString("filter", $"url(#{filterId})");
            xml.WriteEndElement();
        }

        /// <summary>Writes the mask of a feather and opens the group that uses it (the caller closes it). False when the object has no outline to feather.</summary>
        private bool BeginFeather(DocObject o, FeatherStyle feather)
        {
            if (o.GetLocalGeometry() is not { IsEmpty: false } geom) return false;
            var world = o.GetWorldTransform();
            var shape = geom.Transform(world);
            var b = shape.GetBounds();
            if (b.IsEmpty) return false;
            var (sigma, erode) = SoftLayers.FeatherSizes(o, b, feather);
            if (sigma < 1e-6 && erode < 1e-6) return false;
            double margin = 3 * sigma + erode + 1;
            string id = "feather-" + (++_softCounter);
            string x = Num(b.Left - margin), y = Num(b.Top - margin), w = Num(b.Width + 2 * margin), h = Num(b.Height + 2 * margin);
            xml.WriteStartElement("defs");
            if (sigma >= 0.02)
            {
                xml.WriteStartElement("filter");
                xml.WriteAttributeString("id", id + "-blur");
                xml.WriteAttributeString("filterUnits", "userSpaceOnUse");
                xml.WriteAttributeString("x", x); xml.WriteAttributeString("y", y); xml.WriteAttributeString("width", w); xml.WriteAttributeString("height", h);
                xml.WriteStartElement("feGaussianBlur");
                xml.WriteAttributeString("stdDeviation", Num(sigma));
                xml.WriteEndElement();
                xml.WriteEndElement();
            }
            xml.WriteStartElement("mask");
            xml.WriteAttributeString("id", id);
            xml.WriteAttributeString("maskUnits", "userSpaceOnUse");
            xml.WriteAttributeString("x", x); xml.WriteAttributeString("y", y); xml.WriteAttributeString("width", w); xml.WriteAttributeString("height", h);
            xml.WriteStartElement("g");
            if (sigma >= 0.02) xml.WriteAttributeString("filter", $"url(#{id}-blur)");
            xml.WriteStartElement("path");
            xml.WriteAttributeString("d", PathData(shape));
            xml.WriteAttributeString("fill", "#ffffff");
            if (shape.FillRule == FillRule.EvenOdd) xml.WriteAttributeString("fill-rule", "evenodd");
            xml.WriteEndElement();
            if (erode > 1e-9)
            {
                // the inset: a stroke centered on the outline, in black, takes the edge away before the blur
                xml.WriteStartElement("path");
                xml.WriteAttributeString("d", PathData(shape));
                xml.WriteAttributeString("fill", "none");
                xml.WriteAttributeString("stroke", "#000000");
                xml.WriteAttributeString("stroke-width", Num(erode));
                xml.WriteAttributeString("stroke-linejoin", "round");
                xml.WriteEndElement();
            }
            xml.WriteEndElement();   // g
            xml.WriteEndElement();   // mask
            xml.WriteEndElement();   // defs
            xml.WriteStartElement("g");
            xml.WriteAttributeString("mask", $"url(#{id})");
            return true;
        }

        // ---- gradients ----

        private int _gradientCounter;

        /// <summary>
        /// Writes a <c>linearGradient</c> / <c>radialGradient</c> in <c>defs</c> and returns its id. Coordinates are the object's local ones;
        /// <paramref name="toPage"/> (null when the element already carries the object's transform) is the matrix that places them on the
        /// page. The stops are the dense HSV ramp, so every viewer shows the same colors. A gradient with no extent becomes its last color.
        /// </summary>
        private string? WriteGradient(GradientPaint g, Rect2 bounds, Affine2? toPage)
        {
            if (bounds.IsEmpty) return null;
            var frame = g.Frame(bounds);
            if (frame.IsDegenerate) return null;
            string id = "gradient-" + (++_gradientCounter);

            xml.WriteStartElement("defs");
            if (g.IsLinear)
            {
                xml.WriteStartElement("linearGradient");
                xml.WriteAttributeString("id", id);
                xml.WriteAttributeString("gradientUnits", "userSpaceOnUse");
                xml.WriteAttributeString("x1", Num(frame.P0.X)); xml.WriteAttributeString("y1", Num(frame.P0.Y));
                xml.WriteAttributeString("x2", Num(frame.P1.X)); xml.WriteAttributeString("y2", Num(frame.P1.Y));
                if (toPage is { } m) xml.WriteAttributeString("gradientTransform", Matrix(m));
            }
            else
            {
                // A unit circle at the origin, squeezed into the circle/ellipse by the gradient transform.
                xml.WriteStartElement("radialGradient");
                xml.WriteAttributeString("id", id);
                xml.WriteAttributeString("gradientUnits", "userSpaceOnUse");
                xml.WriteAttributeString("cx", "0"); xml.WriteAttributeString("cy", "0"); xml.WriteAttributeString("r", "1");
                var placed = toPage is { } p ? frame.UnitToLocal * p : frame.UnitToLocal;
                xml.WriteAttributeString("gradientTransform", Matrix(placed));
            }
            foreach (var e in GradientRamp.Build(g))
            {
                xml.WriteStartElement("stop");
                xml.WriteAttributeString("offset", Num(e.T));
                xml.WriteAttributeString("stop-color", $"#{e.R:X2}{e.G:X2}{e.B:X2}");
                if (e.A != 255) xml.WriteAttributeString("stop-opacity", (e.A / 255.0).ToString("0.######", CultureInfo.InvariantCulture));
                xml.WriteEndElement();
            }
            xml.WriteEndElement();   // the gradient
            xml.WriteEndElement();   // defs
            return id;
        }

        // ---- attributes ----

        private void Name(DocObject o)
        {
            if (!string.IsNullOrEmpty(o.Name)) xml.WriteAttributeString("data-name", o.Name);
        }

        private void PaintAttrs(string attr, DocColor c)
        {
            xml.WriteAttributeString(attr, $"#{c.R:X2}{c.G:X2}{c.B:X2}");
            if (c.A != 255) xml.WriteAttributeString(attr + "-opacity", (c.A / 255.0).ToString("0.######", CultureInfo.InvariantCulture));
            if (c.Name is { Length: > 0 } name) xml.WriteAttributeString($"data-{attr}-name", name);
            if (c.Vendor is { Length: > 0 } vendor) xml.WriteAttributeString($"data-{attr}-vendor", vendor);
        }

        private void StrokeAttrs(StrokeStyle s)
        {
            if (s.Paint is SolidPaint sp) PaintAttrs("stroke", sp.Color);
            xml.WriteAttributeString("stroke-width", Num(s.Width));
            if (s.Join != LineJoin.Miter) xml.WriteAttributeString("stroke-linejoin", s.Join == LineJoin.Round ? "round" : "bevel");
            if (s.Cap != LineCap.Butt) xml.WriteAttributeString("stroke-linecap", s.Cap == LineCap.Round ? "round" : "square");
            if (s.Join == LineJoin.Miter && s.MiterLimit != 4) xml.WriteAttributeString("stroke-miterlimit", Num(s.MiterLimit));
        }
    }

    // ---------------------------------------------------------------- path data

    public static string Matrix(Affine2 m) =>
        $"matrix({Num(m.M11)} {Num(m.M12)} {Num(m.M21)} {Num(m.M22)} {Num(m.OffsetX)} {Num(m.OffsetY)})";

    /// <summary>SVG path data for a path already in page coordinates. Arcs become <c>A</c> commands.</summary>
    public static string PathData(PathGeometry g)
    {
        var sb = new StringBuilder();
        var pts = g.Points;
        int pi = 0;
        Vec2 cur = default, start = default;
        foreach (var v in g.Verbs)
        {
            switch (v)
            {
                case PathVerb.MoveTo:
                    cur = start = pts[pi++];
                    sb.Append('M').Append(Num(cur.X)).Append(' ').Append(Num(cur.Y));
                    break;
                case PathVerb.LineTo:
                    cur = pts[pi++];
                    sb.Append('L').Append(Num(cur.X)).Append(' ').Append(Num(cur.Y));
                    break;
                case PathVerb.CubicTo:
                    sb.Append('C')
                      .Append(Num(pts[pi].X)).Append(' ').Append(Num(pts[pi].Y)).Append(' ')
                      .Append(Num(pts[pi + 1].X)).Append(' ').Append(Num(pts[pi + 1].Y)).Append(' ')
                      .Append(Num(pts[pi + 2].X)).Append(' ').Append(Num(pts[pi + 2].Y));
                    cur = pts[pi + 2];
                    pi += 3;
                    break;
                case PathVerb.ArcTo:
                    AppendArc(sb, cur, pts[pi], pts[pi + 1]);
                    cur = pts[pi + 1];
                    pi += 2;
                    break;
                case PathVerb.Close:
                    sb.Append('Z');
                    cur = start;
                    break;
            }
        }
        return sb.ToString();
    }

    private static void AppendArc(StringBuilder sb, Vec2 s, Vec2 through, Vec2 e)
    {
        if (!Arcs.TryCircle(s, through, e, out var center, out double r))
        {
            sb.Append('L').Append(Num(e.X)).Append(' ').Append(Num(e.Y));
            return;
        }
        var (a0, sweep) = Arcs.Angles(s, through, e, center);
        // One SVG arc can't be a full or half circle unambiguously, so long arcs are written as two halves.
        int pieces = Math.Abs(sweep) > Math.PI - 1e-6 ? 2 : 1;
        for (int i = 1; i <= pieces; i++)
        {
            var end = i == pieces ? e : center + new Vec2(Math.Cos(a0 + sweep * i / pieces), Math.Sin(a0 + sweep * i / pieces)) * r;
            sb.Append('A').Append(Num(r)).Append(' ').Append(Num(r)).Append(" 0 0 ")
              .Append(sweep > 0 ? '1' : '0').Append(' ').Append(Num(end.X)).Append(' ').Append(Num(end.Y));
        }
    }

    /// <summary>A number with at most 5 decimals (a hundred-thousandth of a point), no exponent, no trailing zeros.</summary>
    public static string Num(double v)
    {
        var s = v.ToString("0.#####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}


