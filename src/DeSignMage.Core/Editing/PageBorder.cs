using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

/// <summary>The sides of the page that a drag has hold of: one side for an edge, two for a corner.</summary>
[Flags]
public enum PageEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

/// <summary>
/// Picking and dragging the page's border with the select tool (requirement R20): an edge changes one dimension, a corner two,
/// and the dragged rectangle becomes the new page.
/// </summary>
public static class PageBorder
{
    /// <summary>Smallest page a drag can make: a quarter inch each way.</summary>
    public const double MinSize = 18;

    /// <summary>Largest page a drag can make (about 1,389 inches).</summary>
    public const double MaxSize = 100_000;

    /// <summary>The side(s) of <paramref name="page"/> within <paramref name="radiusPx"/> screen DIPs of <paramref name="screen"/>, or None.</summary>
    public static PageEdges HitTest(Viewport view, Rect2 page, Vec2 screen, double radiusPx)
    {
        if (page.IsEmpty) return PageEdges.None;
        var a = view.ToScreen(page.TopLeft);
        var b = view.ToScreen(page.BottomRight);
        double left = Math.Min(a.X, b.X), right = Math.Max(a.X, b.X), top = Math.Min(a.Y, b.Y), bottom = Math.Max(a.Y, b.Y);

        bool withinX = screen.X >= left - radiusPx && screen.X <= right + radiusPx;
        bool withinY = screen.Y >= top - radiusPx && screen.Y <= bottom + radiusPx;
        if (!withinX || !withinY) return PageEdges.None;

        // On a page so small on screen that the two sides of one dimension overlap, take the nearer side.
        double dl = Math.Abs(screen.X - left), dr = Math.Abs(screen.X - right);
        double dt = Math.Abs(screen.Y - top), db = Math.Abs(screen.Y - bottom);
        var edges = PageEdges.None;
        if (Math.Min(dl, dr) <= radiusPx) edges |= dl <= dr ? PageEdges.Left : PageEdges.Right;
        if (Math.Min(dt, db) <= radiusPx) edges |= dt <= db ? PageEdges.Top : PageEdges.Bottom;
        return edges;
    }

    /// <summary>The resize cursor for the grabbed side(s).</summary>
    public static CursorKind Cursor(PageEdges edges)
    {
        bool horizontal = (edges & (PageEdges.Left | PageEdges.Right)) != 0;
        bool vertical = (edges & (PageEdges.Top | PageEdges.Bottom)) != 0;
        if (horizontal && vertical)
        {
            bool sameDiagonal = (edges.HasFlag(PageEdges.Left) && edges.HasFlag(PageEdges.Top)) ||
                                (edges.HasFlag(PageEdges.Right) && edges.HasFlag(PageEdges.Bottom));
            return sameDiagonal ? CursorKind.SizeNWSE : CursorKind.SizeNESW;
        }
        if (horizontal) return CursorKind.SizeWE;
        return vertical ? CursorKind.SizeNS : CursorKind.Arrow;
    }

    /// <summary>
    /// The page rectangle after dragging <paramref name="edges"/> of <paramref name="start"/> by <paramref name="delta"/> (document
    /// units). A dragged side stops at the minimum size from the opposite side, and at the maximum.
    /// </summary>
    public static Rect2 Drag(Rect2 start, PageEdges edges, Vec2 delta)
    {
        double left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
        if (edges.HasFlag(PageEdges.Left)) left = Math.Clamp(left + delta.X, right - MaxSize, right - MinSize);
        if (edges.HasFlag(PageEdges.Right)) right = Math.Clamp(right + delta.X, left + MinSize, left + MaxSize);
        if (edges.HasFlag(PageEdges.Top)) top = Math.Clamp(top + delta.Y, bottom - MaxSize, bottom - MinSize);
        if (edges.HasFlag(PageEdges.Bottom)) bottom = Math.Clamp(bottom + delta.Y, top + MinSize, top + MaxSize);
        return new Rect2(left, top, right, bottom);
    }

    /// <summary>The Info Line text while a page is being resized: its size in inches.</summary>
    public static string SizeText(Rect2 page) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Page: {Units.ToInches(page.Width):0.0000} x {Units.ToInches(page.Height):0.0000}");
}
