using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>The four Arrange commands that change the drawing order.</summary>
public enum StackMove { ToFront, ToBack, Forward, Backward }

/// <summary>
/// How the drawing order changes when the selection is moved (Arrange ▸ To Top, One Up, One Down, To Bottom). One place for the rules (R22).
/// <list type="bullet">
/// <item><b>To Front / To Back</b>: every drawn object of the selection moves to the top / bottom, keeping their order among themselves.
/// (Unchanged; the reference program has no such command, only the one-step ones below.)</item>
/// <item><b>Forward / Backward</b> (the reference program's Page Up / Page Down: one step, tested by the user 2026-10-13). Every drawn object of
/// the selection that has a non-selected object next to it in that direction <b>hops past that neighbour</b>, all of them <b>at the same time</b>:
/// who hops is decided on the order before the step, nothing cascades. A selected object whose neighbour is also selected stays put (a group's
/// members never hop over their group mates, so a group does not travel as a block). The neighbour is an <i>item</i>: a loose object, or the whole
/// run of members of one group that the hopper meets next to each other, so a loose object hops over a group in one step, and where a group is
/// interleaved it hops over the run it meets. Checked against the four observed steps (yellow and green grouped, black loose between):
/// Y B G, group Down → Y G B; group Up → Y B G; black Down → B Y G; black Up → Y G B.</item>
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

        int n = order.Count;
        bool up = move == StackMove.Forward;
        // Where each hopper lands: after the last object of the item it hops over (up), before the first one (down).
        var landing = new List<DocObject>?[n];
        var hoppers = new HashSet<DocObject>();
        for (int i = 0; i < n; i++)
        {
            int next = up ? i + 1 : i - 1;
            if (!moving.Contains(order[i]) || next < 0 || next >= n || moving.Contains(order[next])) continue;
            var item = order[next].TopLevel;
            int edge = next;
            while (true)
            {
                int beyond = up ? edge + 1 : edge - 1;
                if (beyond < 0 || beyond >= n || moving.Contains(order[beyond]) || !ReferenceEquals(order[beyond].TopLevel, item)) break;
                edge = beyond;
            }
            (landing[edge] ??= []).Add(order[i]);
            hoppers.Add(order[i]);
        }

        var result = new List<DocObject>(n);
        for (int k = 0; k < n; k++)
        {
            if (!up && landing[k] is { } before) result.AddRange(before);
            if (!hoppers.Contains(order[k])) result.Add(order[k]);
            if (up && landing[k] is { } after) result.AddRange(after);
        }
        return result;
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
