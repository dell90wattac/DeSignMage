namespace DeSignMage.Core.Model;

/// <summary>Which objects a fill/stroke change really lands on, and which style the color UI shows for a selection.</summary>
public static class StyleTargets
{
    /// <summary>
    /// The objects whose style a change to <paramref name="selection"/> modifies: every painted object inside groups, and
    /// the parts of combined objects (followed by the combined object itself, which mirrors its top part). Groups have no
    /// paint of their own and are not included.
    /// </summary>
    public static IEnumerable<DocObject> Expand(IEnumerable<DocObject> selection)
    {
        var seen = new HashSet<DocObject>();
        foreach (var o in selection)
            foreach (var t in ExpandOne(o))
                if (seen.Add(t)) yield return t;
    }

    private static IEnumerable<DocObject> ExpandOne(DocObject o)
    {
        switch (o)
        {
            case GroupObject g:
                foreach (var child in g.Children)
                {
                    // a Soft Shadow or Glow object in the group keeps its own color: recoloring the group recolors what it draws, not its halo
                    if (child.Style.Blur is not null) continue;
                    foreach (var t in ExpandOne(child)) yield return t;
                }
                break;
            case CombinedObject c:
                foreach (var part in c.Parts) yield return part;
                yield return c;
                break;
            default:
                yield return o;
                break;
        }
    }

    /// <summary>
    /// The style shown for one object: its own, or for a group the style of its top-most member (so a group of gold shapes
    /// shows gold). An empty group shows no paint.
    /// </summary>
    public static ObjectStyle Representative(DocObject o)
    {
        while (o is GroupObject g)
        {
            if (g.Children.Count == 0) return ObjectStyle.None;
            o = g.Children[^1];
        }
        return o.Style;
    }
}
