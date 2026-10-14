using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;

namespace DeSignMage.Formats.Vector.Svg;

/// <summary>
/// Reads an SVG file: shapes and paths, groups (a group is a <see cref="GroupObject"/>; an Inkscape layer is a layer), transforms, fills and
/// strokes (colors, opacity, rules, caps and joins), linear and radial gradients (as <see cref="GradientPaint"/>), clip paths, text (live
/// <see cref="TextObject"/>), pictures (data URIs and files next to the SVG) and the CSS of a style sheet. Not read, and said so: dashes, filters,
/// masks, patterns, markers, text on a path, animation.
/// </summary>
internal sealed partial class SvgImporter
{
    private const double PointsPerUserUnit = 0.75;   // 1 CSS pixel = 1/96 in

    private readonly VectorBuilder _b;
    private readonly VectorImportOptions _options;
    private readonly string? _baseDirectory;
    private readonly Dictionary<string, XElement> _ids = new(StringComparer.Ordinal);
    private readonly SvgStyleSheet _css = new();
    private readonly HashSet<XElement> _active = [];   // elements being drawn (a <use> loop)

    private SvgImporter(VectorBuilder b, VectorImportOptions options, string? baseDirectory)
    {
        _b = b; _options = options; _baseDirectory = baseDirectory;
    }

    private sealed record Context(Affine2 World, SvgStyle Style, IReadOnlyList<PathGeometry> Clips, double ViewWidth, double ViewHeight);

    public static VectorImportResult Read(byte[] bytes, VectorImportOptions options, string? baseDirectory)
    {
        XDocument xml;
        try
        {
            if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                gz.CopyTo(ms);
                bytes = ms.ToArray();
            }
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersFromEntities = 1_000_000 };
            using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
            xml = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException e)
        {
            throw new VectorImportException($"The SVG file is not valid XML: {e.Message}", e);
        }
        var root = xml.Root;
        if (root is null || root.Name.LocalName != "svg") throw new VectorImportException("This is not an SVG file (no <svg> element).");

        // the page: width and height (default: the view box), the view box maps user units onto it
        var vb = SvgUnits.Numbers((string?)root.Attribute("viewBox"));
        bool hasViewBox = vb.Count == 4 && vb[2] > 0 && vb[3] > 0;
        double w = SvgUnits.Length((string?)root.Attribute("width"), 16, hasViewBox ? vb[2] : 300, double.NaN) * PointsPerUserUnit;
        double h = SvgUnits.Length((string?)root.Attribute("height"), 16, hasViewBox ? vb[3] : 150, double.NaN) * PointsPerUserUnit;
        if ((string?)root.Attribute("width") is { } ws && ws.TrimEnd().EndsWith('%')) w = double.NaN;
        if ((string?)root.Attribute("height") is { } hs && hs.TrimEnd().EndsWith('%')) h = double.NaN;
        if (double.IsNaN(w) && hasViewBox) w = vb[2] * PointsPerUserUnit;
        if (double.IsNaN(h) && hasViewBox) h = vb[3] * PointsPerUserUnit;
        if (double.IsNaN(w) && !double.IsNaN(h) && hasViewBox) w = h * vb[2] / vb[3];
        if (double.IsNaN(h) && !double.IsNaN(w) && hasViewBox) h = w * vb[3] / vb[2];
        if (double.IsNaN(w) || w <= 0) w = 300 * PointsPerUserUnit;
        if (double.IsNaN(h) || h <= 0) h = 150 * PointsPerUserUnit;

        var page = new PageSetup(w, h);
        var builder = new VectorBuilder(page, options);
        var importer = new SvgImporter(builder, options, baseDirectory);

        Affine2 world;
        double viewW, viewH;
        if (hasViewBox)
        {
            // preserveAspectRatio: xMidYMid meet by default
            string par = ((string?)root.Attribute("preserveAspectRatio") ?? "xMidYMid meet").Trim();
            bool none = par.StartsWith("none", StringComparison.Ordinal);
            double sx = w / vb[2], sy = h / vb[3];
            if (!none) { double s = par.EndsWith("slice", StringComparison.Ordinal) ? Math.Max(sx, sy) : Math.Min(sx, sy); sx = sy = s; }
            double ox = none ? 0 : AlignOffset(par, 'x', w - vb[2] * sx), oy = none ? 0 : AlignOffset(par, 'y', h - vb[3] * sy);
            world = Affine2.Translation(-vb[0], -vb[1]) * Affine2.Scale(sx, sy) * Affine2.Translation(ox, oy);
            viewW = vb[2]; viewH = vb[3];
        }
        else
        {
            world = Affine2.Scale(PointsPerUserUnit, PointsPerUserUnit);
            viewW = w / PointsPerUserUnit; viewH = h / PointsPerUserUnit;
        }

        foreach (var e in root.DescendantsAndSelf())
        {
            if ((string?)e.Attribute("id") is { Length: > 0 } id) _ = importer._ids.TryAdd(id, e);
            if (e.Name.LocalName == "style" && (((string?)e.Attribute("type")) is null or "" or "text/css")) importer._css.Add(e.Value);
        }

        var props = importer.Props(root);
        var style = SvgStyle.Default.Derive(props, viewW, viewH);
        importer.Children(root, new Context(world, style, [], viewW, viewH), top: true);

        if (importer._css.Skipped > 0)
            builder.Warn($"{importer._css.Skipped} style sheet rule(s) use selectors that are not read (only tag, .class and #id selectors are).");
        var doc = builder.Build();
        return new VectorImportResult(doc, VectorFormat.Svg, 1, 0, builder.Warnings);
    }

    private static double AlignOffset(string par, char axis, double free)
    {
        string a = par.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "xMidYMid";
        string part = axis == 'x' ? (a.Length >= 4 ? a[..4] : "xMid") : (a.Length >= 8 ? a.Substring(4, 4) : "YMid");
        return part.EndsWith("Min", StringComparison.Ordinal) ? 0 : part.EndsWith("Max", StringComparison.Ordinal) ? free : free / 2;
    }

    // ---------------------------------------------------------------- properties

    private SvgProps Props(XElement e)
    {
        var props = new SvgProps();
        foreach (var a in e.Attributes())
        {
            if (a.IsNamespaceDeclaration || a.Name.Namespace != XNamespace.None) continue;
            props[a.Name.LocalName] = a.Value;
        }
        _css.Apply(e, props);
        if ((string?)e.Attribute("style") is { } inline)
            foreach (var (k, v) in SvgStyleSheet.ParseDeclarations(inline)) props[k] = v;
        return props;
    }

    private static string? Local(XElement e, string name) =>
        (string?)e.Attribute(name) ?? e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    private static string? Label(XElement e) =>
        (string?)e.Attribute("data-layer") ?? (string?)e.Attribute("data-name") ?? e.Attributes().FirstOrDefault(a => a.Name.LocalName == "label" && a.Name.NamespaceName.Contains("inkscape"))?.Value ?? (string?)e.Attribute("id");

    /// <summary>An editor layer (Inkscape) or a layer our own exporter wrote (<c>data-layer</c>).</summary>
    private static bool IsLayer(XElement e) =>
        e.Attribute("data-layer") is not null ||
        e.Attributes().Any(a => a.Name.LocalName == "groupmode" && a.Name.NamespaceName.Contains("inkscape") && a.Value == "layer");

    private XElement? Resolve(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        string r = reference.Trim();
        if (r.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) r = r[4..].TrimEnd(')').Trim().Trim('\'', '"');
        if (!r.StartsWith('#')) return null;
        return _ids.GetValueOrDefault(r[1..]);
    }

    // ---------------------------------------------------------------- tree

    private void Children(XElement parent, Context ctx, bool top = false)
    {
        foreach (var e in parent.Elements()) Element(e, ctx, top);
    }

    private void Element(XElement e, Context parent, bool top)
    {
        string name = e.Name.LocalName;
        if (e.Name.Namespace != XNamespace.None && !e.Name.NamespaceName.Contains("svg", StringComparison.OrdinalIgnoreCase)) return;
        if (name is "defs" or "title" or "desc" or "metadata" or "style" or "script" or "linearGradient" or "radialGradient" or "clipPath" or "symbol"
            or "marker" or "pattern" or "mask" or "filter" or "namedview" or "font" or "view" or "foreignObject" or "animate" or "animateTransform" or "set")
        {
            if (name is "pattern" or "mask" or "marker" or "filter" or "foreignObject") { }
            return;
        }

        var props = Props(e);
        if (props.GetValueOrDefault("display") == "none") return;
        var style = parent.Style.Derive(props, parent.ViewWidth, parent.ViewHeight);
        var own = SvgUnits.Transform(props.GetValueOrDefault("transform"));
        if (own is null) { _b.Warn("An element has a transform that could not be read; it was drawn without it."); own = Affine2.Identity; }
        var world = own.Value * parent.World;
        var ctx = parent with { World = world, Style = style };

        if (props.GetValueOrDefault("filter") is { } filter && filter != "none") _b.Warn("Filters (blur, shadows, ...) are not read; the objects are drawn without them.");
        if (props.GetValueOrDefault("mask") is { } mask && mask != "none") _b.Warn("Masks are not read; the objects are drawn unmasked.");
        var clips = ctx.Clips;
        if (props.GetValueOrDefault("clip-path") is { } cp && cp != "none")
        {
            var clip = ClipGeometry(cp, ctx);
            if (clip is not null) { clips = [.. clips, clip]; ctx = ctx with { Clips = clips }; }
        }

        switch (name)
        {
            case "g" or "a" or "switch":
            {
                bool layer = top && IsLayer(e);
                if (layer)
                {
                    _b.SetLayer(Label(e) ?? "Layer");
                    Children(e, ctx, top: true);
                    _b.SetLayer(null);
                }
                else
                {
                    _b.BeginGroup(Label(e));
                    if (name == "switch") { if (e.Elements().FirstOrDefault() is { } first) Element(first, ctx, false); }
                    else Children(e, ctx, top: top && IsLayer(e));
                    _b.EndGroup();
                }
                break;
            }
            case "svg":
            {
                var vb = SvgUnits.Numbers((string?)e.Attribute("viewBox"));
                double x = SvgUnits.Length((string?)e.Attribute("x"), style.FontSize, ctx.ViewWidth, 0), y = SvgUnits.Length((string?)e.Attribute("y"), style.FontSize, ctx.ViewHeight, 0);
                double w = SvgUnits.Length((string?)e.Attribute("width"), style.FontSize, ctx.ViewWidth, ctx.ViewWidth), h = SvgUnits.Length((string?)e.Attribute("height"), style.FontSize, ctx.ViewHeight, ctx.ViewHeight);
                var m = Affine2.Translation(x, y);
                if (vb.Count == 4 && vb[2] > 0 && vb[3] > 0) m = Affine2.Translation(-vb[0], -vb[1]) * Affine2.Scale(Math.Min(w / vb[2], h / vb[3]), Math.Min(w / vb[2], h / vb[3])) * m;
                _b.BeginGroup(Label(e));
                Children(e, ctx with { World = m * world }, false);
                _b.EndGroup();
                break;
            }
            case "use":
                Use(e, ctx, props);
                break;
            case "text":
                Text(e, ctx, props);
                break;
            case "image":
                Image(e, ctx, props);
                break;
            case "path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon":
            {
                var geometry = Shape(e, name, ctx, props);
                if (geometry is not null) Draw(geometry, ctx, Label(e));
                break;
            }
            default:
                break;
        }
    }

    private void Use(XElement e, Context ctx, SvgProps props)
    {
        var target = Resolve(Local(e, "href"));
        if (target is null) return;
        if (!_active.Add(target)) { _b.Warn("A <use> element refers to itself; it was skipped."); return; }
        try
        {
            double x = SvgUnits.Length(props.GetValueOrDefault("x"), ctx.Style.FontSize, ctx.ViewWidth, 0), y = SvgUnits.Length(props.GetValueOrDefault("y"), ctx.Style.FontSize, ctx.ViewHeight, 0);
            var inner = ctx with { World = Affine2.Translation(x, y) * ctx.World };
            _b.BeginGroup(Label(e));
            if (target.Name.LocalName is "symbol" or "svg")
            {
                var sp = Props(target);
                var vb = SvgUnits.Numbers((string?)target.Attribute("viewBox"));
                double w = SvgUnits.Length(props.GetValueOrDefault("width") ?? sp.GetValueOrDefault("width"), ctx.Style.FontSize, ctx.ViewWidth, vb.Count == 4 ? vb[2] : ctx.ViewWidth);
                double h = SvgUnits.Length(props.GetValueOrDefault("height") ?? sp.GetValueOrDefault("height"), ctx.Style.FontSize, ctx.ViewHeight, vb.Count == 4 ? vb[3] : ctx.ViewHeight);
                if (vb.Count == 4 && vb[2] > 0 && vb[3] > 0)
                    inner = inner with { World = Affine2.Translation(-vb[0], -vb[1]) * Affine2.Scale(Math.Min(w / vb[2], h / vb[3]), Math.Min(w / vb[2], h / vb[3])) * inner.World };
                Children(target, inner with { Style = ctx.Style.Derive(sp, ctx.ViewWidth, ctx.ViewHeight) });
            }
            else Element(target, inner, false);
            _b.EndGroup();
        }
        finally { _active.Remove(target); }
    }

    // ---------------------------------------------------------------- shapes

    private PathGeometry? Shape(XElement e, string name, Context ctx, SvgProps props)
    {
        double fs = ctx.Style.FontSize;
        double L(string key, double percentOf, double fallback = 0) => SvgUnits.Length(props.GetValueOrDefault(key), fs, percentOf, fallback);
        var rule = ctx.Style.EvenOdd ? FillRule.EvenOdd : FillRule.NonZero;
        switch (name)
        {
            case "path":
            {
                string? d = props.GetValueOrDefault("d");
                if (string.IsNullOrWhiteSpace(d)) return null;
                var g = SvgPathData.Parse(d, rule, out bool complete);
                if (!complete) _b.Warn("A path has a syntax error; it was drawn up to that point.");
                return g.IsEmpty ? null : g;
            }
            case "rect":
            {
                double x = L("x", ctx.ViewWidth), y = L("y", ctx.ViewHeight), w = L("width", ctx.ViewWidth), h = L("height", ctx.ViewHeight);
                if (!(w > 0 && h > 0)) return null;
                bool hasRx = SvgUnits.TryLength(props.GetValueOrDefault("rx"), fs, ctx.ViewWidth, out double rx);
                bool hasRy = SvgUnits.TryLength(props.GetValueOrDefault("ry"), fs, ctx.ViewHeight, out double ry);
                if (!hasRx && hasRy) rx = ry;
                if (!hasRy && hasRx) ry = rx;
                rx = Math.Clamp(rx, 0, w / 2); ry = Math.Clamp(ry, 0, h / 2);
                var r = new Rect2(x, y, x + w, y + h);
                if (rx <= 0 || ry <= 0) return WithRule(PathGeometry.Rectangle(r), rule);
                if (Math.Abs(rx - ry) < 1e-9) return WithRule(PathGeometry.Rectangle(r, rx), rule);
                return WithRule(RoundedRect(r, rx, ry), rule);
            }
            case "circle":
            {
                double cx = L("cx", ctx.ViewWidth), cy = L("cy", ctx.ViewHeight), r = L("r", Math.Sqrt((ctx.ViewWidth * ctx.ViewWidth + ctx.ViewHeight * ctx.ViewHeight) / 2));
                return r > 0 ? WithRule(PathGeometry.Ellipse(new Rect2(cx - r, cy - r, cx + r, cy + r)), rule) : null;
            }
            case "ellipse":
            {
                double cx = L("cx", ctx.ViewWidth), cy = L("cy", ctx.ViewHeight);
                bool hasRx = SvgUnits.TryLength(props.GetValueOrDefault("rx"), fs, ctx.ViewWidth, out double rx);
                bool hasRy = SvgUnits.TryLength(props.GetValueOrDefault("ry"), fs, ctx.ViewHeight, out double ry);
                if (!hasRx && hasRy) rx = ry;
                if (!hasRy && hasRx) ry = rx;
                return rx > 0 && ry > 0 ? WithRule(PathGeometry.Ellipse(new Rect2(cx - rx, cy - ry, cx + rx, cy + ry)), rule) : null;
            }
            case "line":
            {
                var b = new PathBuilder();
                b.MoveTo(L("x1", ctx.ViewWidth), L("y1", ctx.ViewHeight)).LineTo(L("x2", ctx.ViewWidth), L("y2", ctx.ViewHeight));
                return b.Build();
            }
            default: // polyline, polygon
            {
                var n = SvgUnits.Numbers(props.GetValueOrDefault("points"));
                if (n.Count < 4) return null;
                var b = new PathBuilder { FillRule = rule };
                b.MoveTo(n[0], n[1]);
                for (int i = 2; i + 1 < n.Count; i += 2) b.LineTo(n[i], n[i + 1]);
                if (name == "polygon") b.Close();
                return b.Build();
            }
        }
    }

    private static PathGeometry WithRule(PathGeometry g, FillRule rule) =>
        g.FillRule == rule ? g : new PathGeometry(g.Verbs.ToArray(), g.Points.ToArray(), rule);

    private static PathGeometry RoundedRect(Rect2 r, double rx, double ry)
    {
        const double k = PathGeometry.Kappa;
        double kx = rx * (1 - k), ky = ry * (1 - k);
        var b = new PathBuilder();
        b.MoveTo(r.Left + rx, r.Top).LineTo(r.Right - rx, r.Top)
            .CubicTo(r.Right - kx, r.Top, r.Right, r.Top + ky, r.Right, r.Top + ry).LineTo(r.Right, r.Bottom - ry)
            .CubicTo(r.Right, r.Bottom - ky, r.Right - kx, r.Bottom, r.Right - rx, r.Bottom).LineTo(r.Left + rx, r.Bottom)
            .CubicTo(r.Left + kx, r.Bottom, r.Left, r.Bottom - ky, r.Left, r.Bottom - ry).LineTo(r.Left, r.Top + ry)
            .CubicTo(r.Left, r.Top + ky, r.Left + kx, r.Top, r.Left + rx, r.Top).Close();
        return b.Build();
    }

    // ---------------------------------------------------------------- drawing

    private void Draw(PathGeometry local, Context ctx, string? name)
    {
        var style = ctx.Style;
        if (!style.Visible) return;
        bool wantFill = style.Fill is not null && style.Fill != "none";
        var flat = local.GetBounds();
        if ((!flat.IsEmpty && (flat.Width < 1e-9 || flat.Height < 1e-9)) || local.Points.Count <= 2) wantFill = false;   // a line has no area to fill
        bool wantStroke = style.Stroke is not null && style.Stroke != "none" && style.StrokeWidth > 0;
        if (!wantFill && !wantStroke) return;

        var world = local.Transform(ctx.World);
        if (world.IsEmpty) return;
        var bounds = world.GetBounds();
        if (bounds.IsEmpty) return;

        PathGeometry? clipped = _b.ApplyClips(world, ctx.Clips);
        if (clipped is null) return;
        var objectBounds = clipped.GetBounds();

        Paint? fill = wantFill ? ResolvePaint(style.Fill!, style, style.FillOpacity, local.GetBounds(), objectBounds, ctx, style.FillName, style.FillVendor) : null;
        StrokeStyle? stroke = null;
        if (wantStroke)
        {
            var sp = ResolvePaint(style.Stroke!, style, style.StrokeOpacity, local.GetBounds(), objectBounds, ctx, style.StrokeName, style.StrokeVendor);
            if (sp is not null)
            {
                stroke = new StrokeStyle(sp, style.StrokeWidth * ctx.World.AverageScale, style.Join, style.Cap, style.MiterLimit);
                if (style.Dash is not null) _b.Count("{0} stroke(s) are dashed in the file; dashes are not supported, so they are drawn solid.");
            }
        }
        if (fill is null && stroke is null) return;
        _b.Add(new PathObject(clipped) { Name = name, Style = new ObjectStyle(fill, stroke) });
    }

    private Paint? ResolvePaint(string spec, SvgStyle style, double opacity, Rect2 localBounds, Rect2 objectBounds, Context ctx, string? name, string? vendor)
    {
        spec = spec.Trim();
        double alpha = Math.Clamp(opacity * style.Opacity, 0, 1);
        if (spec.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            int close = spec.IndexOf(')');
            string fallback = close >= 0 ? spec[(close + 1)..].Trim() : "";
            var target = Resolve(spec);
            if (target is not null && target.Name.LocalName is "linearGradient" or "radialGradient")
            {
                var g = Gradient(target, localBounds, objectBounds, ctx, alpha);
                if (g is not null) return g;
            }
            else if (target is not null)
            {
                _b.Count("{0} fill/stroke(s) use a pattern, which is not supported; a flat gray is used instead.");
                return new SolidPaint(new DocColor(128, 128, 128, (byte)Math.Round(alpha * 255)));
            }
            if (fallback.Length == 0 || fallback == "none") return null;
            spec = fallback;
        }
        if (spec.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) return Solid(style.Color with { Name = name, Vendor = vendor }, alpha);
        if (SvgColors.TryParse(spec, out var c)) return Solid(c with { Name = name, Vendor = vendor }, alpha);
        _b.Count("{0} paint(s) could not be read and were left out.");
        return null;
    }

    private static SolidPaint? Solid(DocColor c, double alpha)
    {
        int a = (int)Math.Round(c.A * alpha);
        return a <= 0 ? null : new SolidPaint(c with { A = (byte)a });
    }

    // ---------------------------------------------------------------- gradients

    private string? GradAttr(XElement g, string name)
    {
        for (var e = g; e is not null; e = Resolve(Local(e, "href")))
        {
            if ((string?)e.Attribute(name) is { } v) return v;
            if (e == Resolve(Local(e, "href"))) break;
        }
        return null;
    }

    private IEnumerable<XElement> GradStops(XElement g)
    {
        var seen = new HashSet<XElement>();
        for (var e = g; e is not null && seen.Add(e); e = Resolve(Local(e, "href")))
        {
            var stops = e.Elements().Where(x => x.Name.LocalName == "stop").ToList();
            if (stops.Count > 0) return stops;
        }
        return [];
    }

    private Paint? Gradient(XElement g, Rect2 bbox, Rect2 objectBounds, Context ctx, double alpha)
    {
        var stops = new List<GradientNode>();
        double last = 0;
        foreach (var s in GradStops(g))
        {
            var sp = Props(s);
            double offset = double.TryParse(sp.GetValueOrDefault("offset")?.TrimEnd('%') ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture, out double o)
                ? (sp.GetValueOrDefault("offset")?.EndsWith('%') == true ? o / 100 : o) : 0;
            offset = Math.Clamp(Math.Max(offset, last), 0, 1);
            last = offset;
            string colorText = sp.GetValueOrDefault("stop-color") ?? "black";
            var color = colorText.Equals("currentColor", StringComparison.OrdinalIgnoreCase) ? ctx.Style.Color
                : SvgColors.TryParse(colorText, out var c) ? c : DocColor.Black;
            double so = double.TryParse(sp.GetValueOrDefault("stop-opacity") ?? "1", NumberStyles.Float, CultureInfo.InvariantCulture, out double sov) ? sov : 1;
            stops.Add(new GradientNode(offset, color with { A = (byte)Math.Round(color.A * Math.Clamp(so, 0, 1) * alpha) }));
        }
        if (stops.Count == 0) return null;
        if (stops.Count == 1) return new SolidPaint(stops[0].Color);
        if (stops.Count > GradientPaint.MaxStops)
        {
            _b.Warn($"A gradient has more than {GradientPaint.MaxStops} colors; the extra ones were left out.");
            stops = stops.Take(GradientPaint.MaxStops - 1).Append(stops[^1]).ToList();
        }
        string? spread = GradAttr(g, "spreadMethod");
        if (spread is "reflect" or "repeat") _b.Warn("Gradients that reflect or repeat are drawn with padding instead.");

        bool bboxUnits = (GradAttr(g, "gradientUnits") ?? "objectBoundingBox") != "userSpaceOnUse";
        var gt = SvgUnits.Transform(GradAttr(g, "gradientTransform")) ?? Affine2.Identity;
        if (bboxUnits && (bbox.Width <= 1e-9 || bbox.Height <= 1e-9)) return new SolidPaint(stops[0].Color);

        // gradient space → element local → document
        var toLocal = bboxUnits ? Affine2.Scale(bbox.Width, bbox.Height) * Affine2.Translation(bbox.Left, bbox.Top) : Affine2.Identity;
        var toDoc = gt * toLocal * ctx.World;
        double Len(string name, double dflt)
        {
            string? v = GradAttr(g, name);
            if (v is null) return dflt;
            if (bboxUnits) return v.EndsWith('%') ? double.Parse(v.TrimEnd('%'), CultureInfo.InvariantCulture) / 100 : SvgUnits.Length(v, ctx.Style.FontSize, 1, dflt);
            return SvgUnits.Length(v, ctx.Style.FontSize, name is "x1" or "x2" or "cx" or "fx" ? ctx.ViewWidth : name is "y1" or "y2" or "cy" or "fy" ? ctx.ViewHeight : Math.Sqrt((ctx.ViewWidth * ctx.ViewWidth + ctx.ViewHeight * ctx.ViewHeight) / 2), dflt);
        }

        Vec2 P(double x, double y) => toDoc.Transform(new Vec2(x, y));
        // control points are relative to the bounds of the object as it is written (its geometry in document points)
        GradientPaint Build(GradientKind kind, params Vec2[] points) => new(kind, points.Select(p => GradientPaint.ToRelative(p, objectBounds)).ToList(), stops);

        if (g.Name.LocalName == "linearGradient")
        {
            var p0 = P(Len("x1", 0), Len("y1", 0));
            var p1 = P(Len("x2", bboxUnits ? 1 : ctx.ViewWidth), Len("y2", 0));
            if (Vec2.Distance(p0, p1) < 1e-9) return new SolidPaint(stops[^1].Color);
            return Build(GradientKind.Linear, p0, p1);
        }
        double cx = Len("cx", bboxUnits ? 0.5 : ctx.ViewWidth / 2), cy = Len("cy", bboxUnits ? 0.5 : ctx.ViewHeight / 2);
        double r = Len("r", bboxUnits ? 0.5 : Math.Sqrt((ctx.ViewWidth * ctx.ViewWidth + ctx.ViewHeight * ctx.ViewHeight) / 2) / 2);
        if (r <= 0) return new SolidPaint(stops[^1].Color);
        if (GradAttr(g, "fx") is { } fx && GradAttr(g, "fy") is { } fy && (fx != GradAttr(g, "cx") || fy != GradAttr(g, "cy")))
            _b.Count("{0} radial gradient(s) have a focal point off center; they are drawn centered.");
        var c0 = P(cx, cy); var e1 = P(cx + r, cy); var e2 = P(cx, cy + r);
        var a1 = e1 - c0; var a2 = e2 - c0;
        bool circular = Math.Abs(a1.Length - a2.Length) < 1e-6 * Math.Max(a1.Length, 1) && Math.Abs(Vec2.Dot(a1, a2)) < 1e-6 * Math.Max(a1.LengthSquared, 1);
        return circular ? Build(GradientKind.Circle, c0, e1) : Build(GradientKind.Ellipse, c0, e1, e2);
    }

    // ---------------------------------------------------------------- clip paths

    private PathGeometry? ClipGeometry(string reference, Context ctx)
    {
        var clipEl = Resolve(reference);
        if (clipEl is null || clipEl.Name.LocalName != "clipPath") return null;
        bool bboxUnits = (string?)clipEl.Attribute("clipPathUnits") == "objectBoundingBox";
        if (bboxUnits) { _b.Count("{0} clip path(s) use object bounding box units, which are not read; they were ignored."); return null; }
        var verbs = new List<PathVerb>();
        var points = new List<Vec2>();
        void Collect(XElement parent, Affine2 world)
        {
            foreach (var e in parent.Elements())
            {
                string n = e.Name.LocalName;
                var p = Props(e);
                if (p.GetValueOrDefault("display") == "none") continue;
                var t = SvgUnits.Transform(p.GetValueOrDefault("transform")) ?? Affine2.Identity;
                var w = t * world;
                if (n == "use")
                {
                    var target = Resolve(Local(e, "href"));
                    if (target is not null && _active.Add(target))
                    {
                        double x = SvgUnits.Length(p.GetValueOrDefault("x"), 16, ctx.ViewWidth, 0), y = SvgUnits.Length(p.GetValueOrDefault("y"), 16, ctx.ViewHeight, 0);
                        var fake = new XElement("g", target);
                        Collect(fake, Affine2.Translation(x, y) * w);
                        _active.Remove(target);
                    }
                    continue;
                }
                if (n is "path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon")
                {
                    var tmp = ctx with { Style = ctx.Style.Derive(p, ctx.ViewWidth, ctx.ViewHeight) };
                    var geom = Shape(e, n, tmp, p);
                    if (geom is null) continue;
                    var g = geom.Transform(w);
                    verbs.AddRange(g.Verbs); points.AddRange(g.Points);
                }
            }
        }
        Collect(clipEl, ctx.World);
        if (verbs.Count == 0) return null;
        return new PathGeometry(verbs.ToArray(), points.ToArray(), FillRule.NonZero);
    }
}
