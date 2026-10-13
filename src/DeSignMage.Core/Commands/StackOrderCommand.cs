using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>The four Arrange commands that change the drawing order.</summary>
public enum StackMove { ToFront, ToBack, Forward, Backward }

/// <summary>
/// How the drawing order changes when the selection is moved up or down (Arrange ▸ To Front, To Back, Forward, Backward). One place, so what
/// the reference behavior turns out to be can be put in here (R22).
/// <para>
/// <b>Open (R22), waiting for the user's tests of the reference behavior:</b> what each command does to a group whose members are not next to each
/// other in the drawing order. Today, which is what these commands always did for a group whose members were next to each other:
/// </para>
/// <list type="bullet">
/// <item><b>To Front / To Back</b>: every member of the selected objects moves to the top / bottom, keeping their order among themselves.</item>
/// <item><b>Forward / Backward</b>: every drawn object of the selection hops over the next <i>item</i> above / below it. An item is a run of drawn
/// objects next to each other that belong to the same top-level object, so a whole group that lies together counts as one item, and a plain
/// object is one. A group whose members are apart is several items, each hopped over on its own.</item>
/// </list>
/// </summary>
public static class StackOrder
{
    /// <summary>The new drawing order of one layer when <paramref name="moving"/> (drawn objects) are moved.</summary>
    public static List<DocObject> Apply(IReadOnlyList<DocObject> order, HashSet<DocObject> moving, StackMove move)
    {
        switch (move)
        {
            case StackMove.ToFront:
                return [.. order.Where(o => !moving.Contains(o)), .. order.Where(moving.Contains)];
            case StackMove.ToBack:
                return [.. order.Where(moving.Contains), .. order.Where(o => !moving.Contains(o))];
        }

        var list = order.ToList();
        int n = list.Count;
        if (move == StackMove.Forward)
        {
            for (int i = n - 2; i >= 0; i--)
            {
                if (!moving.Contains(list[i]) || moving.Contains(list[i + 1])) continue;
                var item = list[i + 1].TopLevel;
                int j = i + 1;
                while (j + 1 < n && !moving.Contains(list[j + 1]) && ReferenceEquals(list[j + 1].TopLevel, item)) j++;
                var o = list[i];
                list.RemoveAt(i);
                list.Insert(j, o);   // the item now fills i..j-1
            }
        }
        else
        {
            for (int i = 1; i < n; i++)
            {
                if (!moving.Contains(list[i]) || moving.Contains(list[i - 1])) continue;
                var item = list[i - 1].TopLevel;
                int j = i - 1;
                while (j - 1 >= 0 && !moving.Contains(list[j - 1]) && ReferenceEquals(list[j - 1].TopLevel, item)) j--;
                var o = list[i];
                list.RemoveAt(i);
                list.Insert(j, o);
            }
        }
        return list;
    }
}

/// <summary>
/// Change Order: moves the selected top-level objects (all drawn objects in them) in the drawing order of their layers (see <see cref="StackOrder"/>).
/// One undo step. Grouping is not touched: it is membership, not position.
/// </summary>
public sealed class StackOrderCommand : IDocumentCommand, IUndoMemory
{
    private readonly List<(Layer Layer, DocObject[] Old, DocObject[] New)> _changes = [];

    public StackOrderCommand(Document doc, IEnumerable<DocObject> selection, StackMove move)
    {
        var leaves = Stacking.LeavesBottomToTop(doc, selection);
        foreach (var byLayer in leaves.Where(l => l.Layer is not null).GroupBy(l => l.Layer!))
        {
            var layer = byLayer.Key;
            var newOrder = StackOrder.Apply(layer.Children, byLayer.ToHashSet(), move);
            if (!newOrder.SequenceEqual(layer.Children)) _changes.Add((layer, layer.Children.ToArray(), newOrder.ToArray()));
        }
    }

    /// <summary>False when the objects are already as far as they can go.</summary>
    public bool ChangesAnything => _changes.Count > 0;

    public string Description => "Change Order";
    public long ApproximateBytes => _changes.Sum(c => 16L * (c.Old.Length + c.New.Length)) + UndoMemory.SmallStepBytes;
    public IEnumerable<string> HeldAssetIds => [];

    public void Do(Document doc)
    {
        foreach (var (layer, _, order) in _changes) doc.SetDrawingOrder(layer, order);
    }

    public void Undo(Document doc)
    {
        foreach (var (layer, old, _) in _changes) doc.SetDrawingOrder(layer, old);
    }
}
