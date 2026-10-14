using DeSignMage.Core.Commands;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

/// <summary>Arrange ▸ Group ▸ Group All and Group by Color. Both only tie objects together: grouping never changes the stacking order (R22).</summary>
public sealed partial class EditorSession
{
    /// <summary>
    /// The top-level objects on the page that can be touched: a group counts once, objects on hidden or locked layers and hidden or locked
    /// objects are left out. This is what Select All selects and what Group All and Group by Color work on (the layout, not just one layer).
    /// </summary>
    public IReadOnlyList<DocObject> TopLevelObjects() => Document.Layers
        .Where(l => l.Visible && !l.Locked)
        .SelectMany(l => l.Children)
        .Select(o => o.TopLevel)
        .Distinct()
        .Where(o => o.Visible && !o.Locked)
        .ToList();

    // ---- Group All ----

    public bool CanGroupAll => TopLevelObjects().Count >= 2;

    /// <summary>
    /// Group All: every top-level object of the layout becomes one group (an existing group becomes a member, so nothing is ungrouped). One undo
    /// step; nothing moves in the drawing order. The new group is selected.
    /// </summary>
    public GroupObject? GroupAll()
    {
        var all = TopLevelObjects();
        if (all.Count < 2) return null;
        using var tx = History.BeginTransaction("Group All");
        return GroupObjects(all);
    }

    // ---- Group by Color ----

    /// <summary>
    /// The fill color an object counts as for Group by Color: its solid fill, compared as a whole value, so the same RGB under two vinyl names
    /// (or vendors, or color models) is two colors, as in the job colors. Groups, pictures and objects without a solid fill have none.
    /// </summary>
    public static DocColor? GroupColorOf(DocObject o) =>
        o is GroupObject || o.Style.Fill is not SolidPaint sp ? null : sp.Color;

    private List<(DocColor Color, List<DocObject> Objects)> ColorBuckets()
    {
        var wanted = Selection.IsEmpty
            ? null
            : Selection.Items.Select(GroupColorOf).OfType<DocColor>().ToHashSet();
        var buckets = new List<(DocColor, List<DocObject>)>();
        var index = new Dictionary<DocColor, List<DocObject>>();
        foreach (var o in TopLevelObjects())
        {
            if (GroupColorOf(o) is not { } color || (wanted is not null && !wanted.Contains(color))) continue;
            if (!index.TryGetValue(color, out var list))
            {
                index[color] = list = [];
                buckets.Add((color, list));
            }
            list.Add(o);
        }
        return buckets.Where(b => b.Item2.Count >= 2).ToList();
    }

    public bool CanGroupByColor => ColorBuckets().Count > 0;

    /// <summary>
    /// Group by Color: for each fill color among the selected objects (every color when nothing is selected), all the top-level objects of the
    /// layout with that color become one group. Colors compare as whole values (name, vendor and model count). Existing groups, pictures and
    /// objects without a fill are left alone; a color with only one object makes no group. One undo step; the new groups are selected.
    /// Returns how many groups were made.
    /// </summary>
    public int GroupByColor()
    {
        var buckets = ColorBuckets();
        if (buckets.Count == 0) return 0;
        var made = new List<DocObject>();
        using (History.BeginTransaction("Group by Color"))
        {
            foreach (var (_, objects) in buckets)
            {
                var cmd = new GroupObjectsCommand(Document, objects);
                History.Execute(cmd);
                made.Add(cmd.Group);
            }
        }
        Selection.Set(made);
        return made.Count;
    }
}
