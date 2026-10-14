using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Rendering.Export;

/// <summary>The file formats of File > Export. <see cref="Lyt"/> (the legacy layout) is written by the application, which has the writer; <see cref="DocumentExporter"/> does not render it.</summary>
public enum ExportFormat { Svg, Pdf, Png, Jpeg, Eps, Ai, Lyt }

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

    /// <summary>
    /// Picture size in pixels (PNG, JPEG). When both are null, <see cref="Dpi"/> decides. With one of them, the other follows the aspect ratio of
    /// the exported area (the page, or the bounds of the selection with its effects); with both, they are used as given.
    /// </summary>
    public int? PixelWidth { get; init; }

    public int? PixelHeight { get; init; }

    /// <summary>PNG resolution when no pixel size is given. A page of W×H inches gives W·Dpi × H·Dpi pixels.</summary>
    public double Dpi { get; init; } = 300;

    /// <summary>
    /// JPEG: the solid color behind the art (default white). A JPEG cannot be transparent, so the corners a selection leaves uncovered and every soft edge
    /// (anti-aliasing, soft shadow, glow, feather, bevel rims) blend into this color. Formats that keep transparency ignore it. Only the color matters (not its alpha).
    /// </summary>
    public DocColor Background { get; init; } = DocColor.White;

    /// <summary>JPEG quality, 1 (smallest file) to 100 (best picture).</summary>
    public int JpegQuality { get; init; } = 90;

    /// <summary>PNG compression effort, 0 (fastest, biggest file) to 9 (slowest, smallest). The picture is the same at every level.</summary>
    public int PngCompression { get; init; } = 6;

    /// <summary>EPS: dots per inch of the pictures that stand in for what PostScript cannot draw (soft effects, transparency) and the cap for embedded photos.</summary>
    public double EpsRasterDpi { get; init; } = 300;


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
    public static Rect2 VisualBounds(DocObject o) => DeSignMage.Core.Model.VisualBounds.Of(o);   // the rule lives in Core so Size to Fit uses the same one
}
