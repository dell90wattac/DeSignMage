namespace DeSignMage.Core.Model;

/// <summary>
/// Job (project) color palette (requirement R9): every distinct color in use anywhere in the document, on the page and on the
/// pasteboard, fills and strokes, inside groups, combined objects and live text. Read-only; colors that stop being used drop out.
/// </summary>
public static class JobColors
{
    /// <summary>
    /// The distinct colors in use, in order of first use (bottom to top, fill before stroke). Colors are compared as values, so the same
    /// RGB under two vinyl names, vendors or models is listed twice. Hidden layers and objects count: they are still in the job.
    /// </summary>
    public static IReadOnlyList<DocColor> Collect(Document doc)
    {
        var seen = new HashSet<DocColor>();
        var list = new List<DocColor>();
        void Add(Paint? paint)
        {
            if (paint is SolidPaint sp && seen.Add(sp.Color)) list.Add(sp.Color);
        }

        foreach (var o in doc.AllObjects())
        {
            // Groups paint nothing; a combined object's own style only mirrors its top part, so its parts are what counts.
            if (o is GroupObject) continue;
            var painted = o is CombinedObject c ? c.Parts : [o];
            foreach (var p in painted)
            {
                if (p is ImageObject) continue;
                Add(p.Style.Fill);
                Add(p.Style.Stroke?.Paint);
            }
        }
        return list;
    }
}

/// <summary>The job colors of one document, recomputed only when <see cref="Document.Revision"/> has moved on.</summary>
public sealed class JobPalette(Document document)
{
    private long _revision = -1;
    private IReadOnlyList<DocColor> _colors = [];

    public IReadOnlyList<DocColor> Colors
    {
        get
        {
            if (_revision != document.Revision)
            {
                _colors = JobColors.Collect(document);
                _revision = document.Revision;
            }
            return _colors;
        }
    }
}
