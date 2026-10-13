using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>
/// Drawing-order helpers shared by the commands. The drawing order is the order of <see cref="Layer.Children"/>: one flat list of drawn
/// objects per layer. A group has no place in it; where a group "is" for an operation that needs one (what to put a replacement over,
/// what comes first) is its topmost member, see <see cref="TopKey"/>.
/// </summary>
public static class Stacking
{
    /// <summary>Layer number and position of drawn objects, as numbers that compare in drawing order. Make one per operation: it must not outlive a change.</summary>
    public sealed class Order(Document doc)
    {
        private readonly Dictionary<Layer, int> _layers = doc.Layers.Select((l, i) => (l, i)).ToDictionary(p => p.l, p => p.i);

        /// <summary>The key of a drawn object (not a group); -1 when it is not in the document.</summary>
        public long KeyOf(DocObject leaf)
        {
            if (leaf.Layer is not { } layer || !_layers.TryGetValue(layer, out int li)) return -1;
            int index = layer.IndexOf(leaf);
            return index < 0 ? -1 : ((long)li << 32) | (uint)index;
        }

        public int LayerIndex(Layer layer) => _layers.GetValueOrDefault(layer, -1);

        /// <summary>The key of the topmost drawn object in <paramref name="o"/> (itself, or for a group its topmost member); -1 if there is none.</summary>
        public long TopKey(DocObject o)
        {
            if (o is not GroupObject g) return KeyOf(o);
            long best = -1;
            foreach (var leaf in g.Leaves()) best = Math.Max(best, KeyOf(leaf));
            return best;
        }
    }

    /// <summary>Objects sorted bottom to top: by the topmost drawn object in each (a group counts where its topmost member is).</summary>
    public static List<DocObject> BottomToTop(Document doc, IEnumerable<DocObject> objects)
    {
        var order = new Order(doc);
        return objects.Distinct().Select(o => (o, key: order.TopKey(o))).OrderBy(p => p.key).Select(p => p.o).ToList();
    }

    /// <summary>The drawn objects inside <paramref name="roots"/> (groups looked through, each object once), in drawing order, bottom to top.</summary>
    public static List<DocObject> LeavesBottomToTop(Document doc, IEnumerable<DocObject> roots)
    {
        var order = new Order(doc);
        var seen = new HashSet<DocObject>();
        var leaves = new List<(DocObject Leaf, long Key)>();
        foreach (var r in roots)
        {
            if (r is GroupObject g)
            {
                foreach (var l in g.Leaves())
                    if (seen.Add(l)) leaves.Add((l, order.KeyOf(l)));
            }
            else if (seen.Add(r)) leaves.Add((r, order.KeyOf(r)));
        }
        return leaves.OrderBy(p => p.Key).Select(p => p.Leaf).ToList();
    }

    /// <summary>
    /// Where a single object replacing <paramref name="bottomToTop"/> (top-level objects) goes: over the topmost drawn object among them, at
    /// the position that object will have once all of them are gone. Not inside any group.
    /// <para>
    /// (R22, open: a replacement for objects that were not next to each other in the drawing order covers whatever was between them. That is
    /// what Weld, Combine and the effect commands did before and still do; whether the reference behavior is the same with interleaved
    /// members is not known yet. This method is the one place to change it.)
    /// </para>
    /// </summary>
    internal static Slot ReplacementSlot(Document doc, IReadOnlyList<DocObject> bottomToTop)
    {
        var order = new Order(doc);
        DocObject? top = null;
        long topKey = -1;
        var leaves = new List<DocObject>();
        foreach (var m in bottomToTop)
        {
            if (m is GroupObject g) leaves.AddRange(g.Leaves());
            else leaves.Add(m);
        }
        foreach (var l in leaves)
        {
            long k = order.KeyOf(l);
            if (k > topKey) { topKey = k; top = l; }
        }
        if (top is null) return new Slot(doc.ActiveLayer, doc.ActiveLayer.Children.Count);
        var layer = top.Layer!;
        int topIndex = layer.IndexOf(top);
        int below = leaves.Count(l => ReferenceEquals(l.Layer, layer) && layer.IndexOf(l) < topIndex);
        return new Slot(layer, topIndex - below);
    }

    /// <summary>Removes objects top-down, returning what undo needs to put them back.</summary>
    internal static List<Removal> RemoveAll(Document doc, IReadOnlyList<DocObject> bottomToTop)
    {
        var removed = new List<Removal>(bottomToTop.Count);
        for (int i = bottomToTop.Count - 1; i >= 0; i--) removed.Add(doc.Remove(bottomToTop[i]));
        return removed;
    }

    internal static void Restore(Document doc, List<Removal> removed)
    {
        for (int i = removed.Count - 1; i >= 0; i--) doc.Restore(removed[i]);
    }

    /// <summary>
    /// Copies of top-level objects for the clipboard or a copy-drag. Every copied drawn object remembers where it stood in the drawing order
    /// of the document, so putting the copies down again (<see cref="AddObjectsCommand"/>) keeps what was between members of a group, and
    /// between the copied objects, exactly as it was.
    /// </summary>
    public static List<DocObject> CloneForCopy(Document doc, IEnumerable<DocObject> topLevel)
    {
        var order = new Order(doc);
        var copies = new List<DocObject>();
        foreach (var o in topLevel)
        {
            var copy = o.Clone();
            Mark(o, copy);
            copies.Add(copy);
        }
        return copies;

        void Mark(DocObject source, DocObject copy)
        {
            if (source is GroupObject sg && copy is GroupObject cg)
            {
                for (int i = 0; i < sg.Children.Count; i++) Mark(sg.Children[i], cg.Children[i]);
            }
            else copy.CopyRank = order.KeyOf(source) + 1;   // 0 means "unknown"
        }
    }

    /// <summary>
    /// The drawn objects inside new top-level objects, in the order they are to be drawn: by the place they were copied from when all of
    /// them have one (see <see cref="CloneForCopy"/>), else object after object.
    /// </summary>
    public static List<DocObject> DrawingOrderOf(IReadOnlyList<DocObject> roots)
    {
        var leaves = new List<DocObject>();
        foreach (var r in roots)
        {
            if (r is GroupObject g) leaves.AddRange(g.Leaves());
            else leaves.Add(r);
        }
        if (leaves.Count > 1 && leaves.All(l => l.CopyRank > 0))
            return leaves.Select((l, i) => (l, i)).OrderBy(p => p.l.CopyRank).ThenBy(p => p.i).Select(p => p.l).ToList();
        return leaves;
    }
}
