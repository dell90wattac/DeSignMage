using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

/// <summary>Arrange ▸ Align: which edge or center of the reference the objects line up on.</summary>
public enum AlignMode { Left, Center, Right, Top, Middle, Bottom }

/// <summary>Arrange ▸ Align ▸ Center of Layout: which direction(s) the selection is centered on the page.</summary>
public enum PageCentering { Horizontal, Vertical, Both }

public sealed partial class EditorSession
{
    // ---- Align (Arrange ▸ Align) ----

    /// <summary>
    /// What Align lines up. With two or more objects selected, each selected top-level object (a group counts as one) lines up with the
    /// bounds of the whole selection. With one group selected, its members line up with each other inside the group's bounds, which is
    /// what a rubber band group needs (the rubber band makes a group). Locked objects are never moved; null means there is nothing to align.
    /// </summary>
    private (IReadOnlyList<DocObject> Units, Rect2 Reference)? AlignTarget()
    {
        var items = Selection.Items;
        if (items.Count == 0 || SelectionLocked) return null;
        if (items.Count >= 2) return (items, Selection.GetBounds());
        if (items[0] is GroupObject g)
        {
            var members = g.Children.Where(m => m.Visible && !m.Locked).ToList();
            if (members.Count < 2) return null;
            var reference = Rect2.Empty;
            foreach (var m in members) reference = reference.Union(m.GetWorldBounds());
            return (members, reference);
        }
        return null;
    }

    private static TextAlignment? TextAlignmentOf(AlignMode mode) => mode switch
    {
        AlignMode.Left => TextAlignment.Left,
        AlignMode.Center => TextAlignment.Center,
        AlignMode.Right => TextAlignment.Right,
        _ => null,
    };

    /// <summary>A single live text takes Align Left / Center / Right as its own alignment (and stays where it is), as there is nothing to line up with.</summary>
    private TextObject? SingleTextFor(AlignMode mode) =>
        TextAlignmentOf(mode) is { } wanted && Selection.Count == 1 && !SelectionLocked && Selection.Items[0] is TextObject t && t.Alignment != wanted ? t : null;

    /// <summary>True when Align would do something: two or more objects (or one group of two or more members) to line up, or one text whose alignment differs.</summary>
    public bool CanAlign(AlignMode mode) => AlignTarget() is not null || SingleTextFor(mode) is not null;

    /// <summary>
    /// Lines the objects up on the left / center / right / top / middle / bottom of their common bounds, as one undo step. A single live
    /// text only changes its alignment. Returns false when nothing was changed.
    /// </summary>
    public bool Align(AlignMode mode)
    {
        if (AlignTarget() is { } target)
        {
            var (units, reference) = target;
            var moves = new List<(DocObject, Affine2)>();
            foreach (var u in units)
            {
                var b = u.GetWorldBounds();
                if (b.IsEmpty) continue;
                var d = mode switch
                {
                    AlignMode.Left => new Vec2(reference.Left - b.Left, 0),
                    AlignMode.Center => new Vec2(reference.Center.X - b.Center.X, 0),
                    AlignMode.Right => new Vec2(reference.Right - b.Right, 0),
                    AlignMode.Top => new Vec2(0, reference.Top - b.Top),
                    AlignMode.Middle => new Vec2(0, reference.Center.Y - b.Center.Y),
                    _ => new Vec2(0, reference.Bottom - b.Bottom),
                };
                if (Math.Abs(d.X) < 1e-9 && Math.Abs(d.Y) < 1e-9) continue;
                moves.Add((u, TransformObjectsCommand.ComposeWorld(u, Affine2.Translation(d))));
            }
            if (moves.Count == 0) return false;
            History.Execute(new TransformObjectsCommand($"Align {mode}", moves));
            return true;
        }

        if (SingleTextFor(mode) is { } text)
        {
            var before = text.GetWorldBounds();
            using var tx = History.BeginTransaction($"Align {mode}");
            History.Execute(new SetPropertyCommand<TextObject, TextAlignment>("Alignment", [text], o => o.Alignment, (o, v) => o.Alignment = v, TextAlignmentOf(mode)!.Value));
            var after = text.GetWorldBounds();
            if (!before.IsEmpty && !after.IsEmpty)   // the new alignment moves the text around its anchor; put it back where it was
            {
                var d = before.Center - after.Center;
                if (Math.Abs(d.X) > 1e-9 || Math.Abs(d.Y) > 1e-9)
                    History.Execute(TransformObjectsCommand.ApplyWorld("Align", [text], Affine2.Translation(d)));
            }
            return true;
        }
        return false;
    }

    /// <summary>Center of Layout: there is something selected and nothing locked.</summary>
    public bool CanCenterOnPage => !Selection.IsEmpty && !SelectionLocked;

    /// <summary>
    /// Arrange ▸ Align ▸ Horizontal / Vertical / (both) Center of Layout: moves the selection, as one unit, so the center of its bounds is
    /// at the page's center in that direction. One undo step; false when it is already there.
    /// </summary>
    public bool CenterOnPage(PageCentering centering)
    {
        if (!CanCenterOnPage) return false;
        var b = Selection.GetBounds();
        if (b.IsEmpty) return false;
        var page = Document.Page.Bounds.Center;
        double dx = centering is PageCentering.Vertical ? 0 : page.X - b.Center.X;
        double dy = centering is PageCentering.Horizontal ? 0 : page.Y - b.Center.Y;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return false;
        string name = centering switch { PageCentering.Horizontal => "Center Horizontally", PageCentering.Vertical => "Center Vertically", _ => "Center on Layout" };
        History.Execute(TransformObjectsCommand.ApplyWorld(name, Selection.Items, Affine2.Translation(dx, dy)));
        return true;
    }

    // ---- Mirror (Arrange ▸ Horizontal / Vertical Mirror) ----

    public bool CanMirror => !Selection.IsEmpty && !SelectionLocked && !Selection.GetBounds().IsEmpty;

    /// <summary>
    /// Flips the selection in place about the center of its bounds, as one unit and one undo step: horizontal turns left into right (a
    /// mirror image across a vertical line), vertical turns top into bottom. Live text stays live text (mirrored).
    /// </summary>
    public bool Mirror(bool horizontal)
    {
        if (!CanMirror) return false;
        var c = Selection.GetBounds().Center;
        var flip = horizontal ? Affine2.Scale(-1, 1, c) : Affine2.Scale(1, -1, c);
        History.Execute(TransformObjectsCommand.ApplyWorld(horizontal ? "Mirror Horizontal" : "Mirror Vertical", Selection.Items, flip));
        return true;
    }

    // ---- Lock (Arrange ▸ Locked, Unlock All) ----

    /// <summary>
    /// True when something selected is locked. A locked object can't be picked on the canvas, but it can be selected in the Layers Panel
    /// (to unlock it again); while it is selected it can't be moved, transformed, deleted or regrouped.
    /// </summary>
    public bool SelectionLocked => Selection.Items.Any(o => o.Locked);

    /// <summary>True when everything selected is locked (the Locked menu item is then checked and unlocks).</summary>
    public bool SelectionAllLocked => !Selection.IsEmpty && Selection.Items.All(o => o.Locked);

    public bool CanLock => !Selection.IsEmpty;

    /// <summary>Arrange ▸ Locked: locks the selected objects, or, when they are all locked already, unlocks them. One undo step.</summary>
    public bool ToggleLockSelection()
    {
        if (!CanLock) return false;
        bool lockThem = !SelectionAllLocked;
        var cmd = new SetLockedCommand(Selection.Items, lockThem);
        if (cmd.IsNoOp) return false;
        History.Execute(cmd);
        return true;
    }

    /// <summary>True when any object of the document is locked.</summary>
    public bool HasLockedObjects => Document.AllObjects().Any(o => o.Locked);

    /// <summary>Arrange ▸ Unlock All: unlocks every locked object of the document, as one undo step.</summary>
    public bool UnlockAll()
    {
        var locked = Document.AllObjects().Where(o => o.Locked).ToList();
        if (locked.Count == 0) return false;
        History.Execute(new SetLockedCommand(locked, false, "Unlock All"));
        return true;
    }
}
