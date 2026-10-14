using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

/// <summary>Layout ▸ Guides, Layout Properties and Size to Fit.</summary>
public sealed partial class EditorSession
{
    // ---- Layout > Guides ----

    /// <summary>The rectangle the edge and center guides follow: the selection's bounds, or the page when nothing is selected.</summary>
    public Rect2 GuideTargetBox => Selection.IsEmpty ? Document.Page.Bounds : Selection.GetBounds();

    /// <summary>Whether <see cref="AddGuides"/> has something to place: the text places need a text in the selection (groups are searched too).</summary>
    public bool CanAddGuides(GuidePlace place) =>
        GuidePlacement.IsTextPlace(place) ? GuidePlacement.TextsIn(Selection.Items).Any() : !GuideTargetBox.IsEmpty;

    /// <summary>
    /// Adds the guides for <paramref name="place"/> as one undo step and returns how many were added. A guide that is already there
    /// is not added again. The new guides are not selected (the selection stays as it was); hidden guides are shown.
    /// </summary>
    public int AddGuides(GuidePlace place)
    {
        if (!CanAddGuides(place)) return 0;
        var candidates = GuidePlacement.IsTextPlace(place)
            ? GuidePlacement.ForText(place, GuidePlacement.TextsIn(Selection.Items))
            : GuidePlacement.ForBox(place, GuideTargetBox);
        var fresh = GuidePlacement.WithoutDuplicates(Document.Guides, candidates);
        if (fresh.Count == 0) return 0;
        ShowGuides = true;
        History.Execute(new AddGuidesCommand(fresh, fresh.Count == 1 ? "Add Guide" : "Add Guides"));
        return fresh.Count;
    }

    // ---- Layout > Layout Properties, Size to Fit ----

    /// <summary>
    /// Gives the page a new size (points), as one undo step. The bottom-left corner stays where it is, so the rulers' zero and every
    /// object's position in inches stay the same; a taller page grows upward. Objects and guides never move. The size is held
    /// between a quarter inch and about 1,389 inches (the limits of dragging the border). Returns false when nothing changes.
    /// </summary>
    public bool SetPageSize(double width, double height)
    {
        if (double.IsNaN(width) || double.IsNaN(height)) return false;
        width = Math.Clamp(width, PageBorder.MinSize, PageBorder.MaxSize);
        height = Math.Clamp(height, PageBorder.MinSize, PageBorder.MaxSize);
        var old = Document.Page;
        var page = new PageSetup(width, height, old.X, old.Y + old.Height - height);
        if (page == old) return false;
        History.Execute(new ResizePageCommand(page, "Layout Properties"));
        return true;
    }

    /// <summary>The area Size to Fit would make the page: everything the design draws, strokes and soft edges included, never smaller than the minimum page.</summary>
    public Rect2 FitArea
    {
        get
        {
            var area = VisualBounds.OfDocument(Document);
            if (area.IsEmpty) return area;
            double growX = Math.Max(0, PageBorder.MinSize - area.Width) / 2, growY = Math.Max(0, PageBorder.MinSize - area.Height) / 2;
            return new Rect2(area.Left - growX, area.Top - growY, area.Right + growX, area.Bottom + growY);
        }
    }

    public bool CanSizeToFit => !FitArea.IsEmpty;

    /// <summary>
    /// Layout ▸ Size to Fit: makes the page the bounds of everything that is drawn, so it shrinks or grows to the artwork (also a way to find
    /// objects that wandered off the page). One undo step; objects do not move on screen. Returns false for an empty design or when the
    /// page already fits.
    /// </summary>
    public bool SizeToFit()
    {
        var area = FitArea;
        if (area.IsEmpty) return false;
        var page = PageSetup.FromBounds(area);
        if (page == Document.Page) return false;
        History.Execute(new ResizePageCommand(page, "Size to Fit"));
        return true;
    }
}
