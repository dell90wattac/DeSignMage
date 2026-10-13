using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

public sealed partial class EditorSession
{
    private Rect2? _pagePreview;

    /// <summary>How close (screen DIPs) the pointer must be to the page's border to grab it with the select tool.</summary>
    public double PageBorderRadiusPx { get; set; } = 5;

    /// <summary>
    /// The page rectangle while its border is being dragged (null otherwise). The canvas draws the page there, the Info Line shows its
    /// size, and nothing in the document changes until the drag ends (<see cref="ResizePage"/>).
    /// </summary>
    public Rect2? PagePreview
    {
        get => _pagePreview;
        internal set
        {
            if (_pagePreview == value) return;
            _pagePreview = value;
            RequestRedraw();
        }
    }

    /// <summary>The page as shown right now: the drag preview if there is one, else the document's page.</summary>
    public Rect2 PageBounds => _pagePreview ?? Document.Page.Bounds;

    /// <summary>
    /// Makes <paramref name="bounds"/> the page, as one undo step. Objects and guides keep their place in the document, so they do not
    /// move on screen. Does nothing when the page would not change.
    /// </summary>
    public bool ResizePage(Rect2 bounds)
    {
        if (bounds.IsEmpty) return false;
        var page = PageSetup.FromBounds(bounds);
        if (page == Document.Page) return false;
        History.Execute(new ResizePageCommand(page));
        return true;
    }

    /// <summary>The Ctrl/Shift/Alt state changed with no pointer event (a key went down or up): lets a drag or hover cursor follow it.</summary>
    public void ModifiersChanged(ModifierKeys modifiers) => _activeTool.ModifiersChanged(modifiers);
}
