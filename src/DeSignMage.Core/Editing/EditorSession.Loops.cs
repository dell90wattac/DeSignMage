using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

public sealed partial class EditorSession
{
    /// <summary>True if any selected object is a path with two or more loops, or a combined object that has loops to split.</summary>
    public bool CanBreakIntoLoops => Selection.Items.Any(BreakIntoLoopsCommand.CanBreak);

    /// <summary>
    /// Arrange ▸ Break Into Loops: every selected multi-loop graphic becomes one object per loop (one undo step for the whole
    /// selection); afterwards nothing is selected, like Ungroup and Break Apart. Objects that have only one loop stay as they are.
    /// </summary>
    public void BreakIntoLoopsSelection()
    {
        var targets = Selection.Items.Where(BreakIntoLoopsCommand.CanBreak).ToList();
        if (targets.Count == 0) return;
        using (History.BeginTransaction("Break Into Loops"))
        {
            foreach (var t in targets)
                History.Execute(new BreakIntoLoopsCommand(t));
        }
        Selection.Clear();
    }

    // ---- Layout ▸ Shapes ▸ Convert to Rounded Rectangle ----

    /// <summary>True if the selection (groups looked through) holds a rectangle or a path that is a plain box.</summary>
    public bool CanConvertToRoundedRectangle => Stacking.LeavesBottomToTop(Document, Selection.Items).Any(o => RectangleShapes.TryGet(o, out _));

    /// <summary>Opens the Convert to Rounded Rectangle tool on the selected rectangles (radius on the Context Bar, live preview, Done applies).</summary>
    public void BeginConvertToRoundedRectangle()
    {
        if (CanConvertToRoundedRectangle) SetTool("roundCorners");
    }

    // ---- Arrange: loop cleanup (Close All Loops, Remove All Inner Loops, Break Into Outer Loops) ----

    /// <summary>The drawn objects the loop cleanup commands work on: the selection with groups looked through, bottom to top.</summary>
    private List<DocObject> LoopLeaves() => Stacking.LeavesBottomToTop(Document, Selection.Items);

    /// <summary>True if anything selected (also inside groups) has an open loop.</summary>
    public bool CanCloseAllLoops => LoopLeaves().Any(LoopCleanup.HasOpenLoops);

    /// <summary>
    /// Arrange ▸ Close All Loops: every open loop of the selection is joined end to start (a straight vector bridges a gap), so it can be
    /// filled. One undo step; the selection stays.
    /// </summary>
    public void CloseAllLoops()
    {
        var edits = new List<(DocObject Leaf, LoopMap Map, List<(int Piece, PathGeometry Geometry)> Changes)>();
        foreach (var leaf in LoopLeaves().Where(l => l is not TextObject))
            if (LoopMap.Of(leaf) is { } map && LoopCleanup.WithClosedLoops(map) is { Count: > 0 } changes) edits.Add((leaf, map, changes));
        RunLoopEdits("Close All Loops", edits);
    }

    /// <summary>True if anything selected (also inside groups) has a loop lying inside another loop (a hole).</summary>
    public bool CanRemoveInnerLoops => LoopLeaves().Any(LoopCleanup.HasInnerLoops);

    /// <summary>
    /// Arrange ▸ Remove All Inner Loops: the holes go (and anything inside them), only the loops that are not inside another loop stay.
    /// Live text becomes ordinary curves (as with any edit of its vectors). One undo step; the selection stays.
    /// </summary>
    public void RemoveAllInnerLoops()
    {
        var edits = new List<(DocObject Leaf, LoopMap Map, List<(int Piece, PathGeometry Geometry)> Changes)>();
        foreach (var leaf in LoopLeaves())
            if (LoopMap.Of(leaf) is { HasInnerLoops: true } map) edits.Add((leaf, map, LoopCleanup.WithoutInnerLoops(map)));
        RunLoopEdits("Remove All Inner Loops", edits);
    }

    private void RunLoopEdits(string description, List<(DocObject Leaf, LoopMap Map, List<(int Piece, PathGeometry Geometry)> Changes)> edits)
    {
        if (edits.Count == 0) return;
        var keep = Selection.Items.ToList();
        using (History.BeginTransaction(description))
        {
            foreach (var (leaf, map, changes) in edits)
            {
                var current = leaf;
                foreach (var (piece, geometry) in changes)
                {
                    var cmd = new SetVectorsCommand(description, current, map.Pieces[piece].PartIndex, geometry);
                    History.Execute(cmd);
                    if (cmd.Result != current)   // live text or a shape became a plain path
                    {
                        keep = keep.Select(o => ReferenceEquals(o, current) ? cmd.Result : o).ToList();
                        current = cmd.Result;
                    }
                }
            }
        }
        Selection.Set(keep.Where(Document.Contains));
    }

    /// <summary>True if anything selected (also inside groups) has two or more outer loops.</summary>
    public bool CanBreakIntoOuterLoops => LoopLeaves().Any(LoopCleanup.CanBreakIntoOuterLoops);

    /// <summary>
    /// Arrange ▸ Break Into Outer Loops: each object with several outer loops becomes one object per outer loop, and every piece keeps the
    /// holes inside its own loop (Break Into Loops splits every loop, holes included). One undo step; nothing is selected afterwards,
    /// like the other break commands.
    /// </summary>
    public void BreakIntoOuterLoopsSelection()
    {
        var targets = LoopLeaves().Where(LoopCleanup.CanBreakIntoOuterLoops).ToList();
        if (targets.Count == 0) return;
        using (History.BeginTransaction("Break Into Outer Loops"))
        {
            foreach (var t in targets) History.Execute(new BreakIntoOuterLoopsCommand(t));
        }
        Selection.Clear();
    }
}
