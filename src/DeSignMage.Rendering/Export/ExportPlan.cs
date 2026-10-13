using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Rendering.Export;

public enum ExportFormat { Svg, Pdf, Png }

/// <summary>What gets exported: the whole page, or only the selected objects (cropped to their outline).</summary>
public enum ExportScope { Page, Selection }

public sealed class ExportException(string message) : Exception(message);

public sealed record ExportOptions
{
    public ExportScope Scope { get; init; } = ExportScope.Page;

    /// <summary>The top-level objects to export when <see cref="Scope"/> is <see cref="ExportScope.Selection"/>.</summary>
    public IReadOnlyCollection<DocObject> Selection { get; init; } = [];

    /// <summary>
    /// Page scope only. Off (default): objects that lie entirely off the page (on the pasteboard) are left out and the export is
    /// the page. On: they are included and the export grows to cover them too.
    /// </summary>
    public bool IncludePasteboard { get; init; }

    /// <summary>PNG resolution. A page of W×H inches gives W·Dpi × H·Dpi pixels.</summary>
    public double Dpi { get; init; } = 300;

    /// <summary>PNG: leave the background transparent instead of white.</summary>
    public bool TransparentBackground { get; init; }

    /// <summary>SVG: draw live text as glyph outlines (exact look, the default). Off: write real &lt;text&gt; elements (editable, needs the font).</summary>
    public bool TextAsOutlines { get; init; } = true;

    /// <summary>Written to the PDF/SVG title.</summary>
    public string? Title { get; init; }
}

/// <summary>The drawn objects of one layer that an export draws, in drawing order (members of groups included; groups themselves are not in the list).</summary>
public sealed record ExportLayer(Layer Layer, IReadOnlyList<DocObject> Objects);

/// <summary>Which objects an export draws and the document-space rectangle it covers. Guides are never part of it.</summary>
public sealed record ExportPlan(Rect2 Area, IReadOnlyList<ExportLayer> Layers)
{
    public int ObjectCount => Layers.Sum(l => l.Objects.Count);

    public static ExportPlan Create(Document doc, ExportOptions options)
    {
        if (options.Scope == ExportScope.Selection)
        {
            var wanted = new HashSet<DocObject>(options.Selection.Select(o => o.TopLevel));
            if (wanted.Count == 0) throw new ExportException("Nothing is selected.");
            var layers = new List<ExportLayer>();
            var area = Rect2.Empty;
            foreach (var layer in doc.Layers)
            {
                var objs = layer.Children.Where(o => o.IsShown && wanted.Contains(o.TopLevel)).ToList();
                if (objs.Count == 0) continue;
                layers.Add(new ExportLayer(layer, objs));
                foreach (var o in objs) area = area.Union(VisualBounds(o));
            }
            if (area.IsEmpty) throw new ExportException("The selection has nothing to draw.");
            return new ExportPlan(area, layers);
        }

        var page = doc.Page.Bounds;
        var result = new List<ExportLayer>();
        var covered = page;
        foreach (var layer in doc.Layers)
        {
            if (!layer.Visible || !layer.Printable) continue;
            var objs = new List<DocObject>();
            foreach (var o in layer.Children)
            {
                if (!o.IsShown) continue;
                var vb = VisualBounds(o);
                bool onPage = vb.IsEmpty || vb.Intersects(page);
                if (!onPage && !options.IncludePasteboard) continue;
                objs.Add(o);
                if (options.IncludePasteboard && !vb.IsEmpty) covered = covered.Union(vb);
            }
            if (objs.Count > 0) result.Add(new ExportLayer(layer, objs));
        }
        return new ExportPlan(covered, result);
    }

    /// <summary>Bounds of what an object paints: its geometry plus half its stroke width (round/square caps and long miters are not counted).</summary>
    public static Rect2 VisualBounds(DocObject o)
    {
        switch (o)
        {
            case GroupObject g:
            {
                var r = Rect2.Empty;
                foreach (var c in g.Children) if (c.Visible) r = r.Union(VisualBounds(c));
                return r;
            }
            case CombinedObject c:
            {
                var world = c.GetWorldTransform();
                var r = Rect2.Empty;
                foreach (var p in c.Parts)
                    if (p.Visible && p.GetLocalGeometry() is { } g)
                        r = r.Union(g.Transform(p.Transform * world).GetBounds().Inflate(p.Style.StrokeOutset));
                return r;
            }
            default:
                return o.GetWorldBounds().Inflate(o.Style.StrokeOutset);
        }
    }
}
