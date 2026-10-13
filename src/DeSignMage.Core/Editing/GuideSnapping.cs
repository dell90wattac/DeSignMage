using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

/// <summary>The offset that makes something sit on a guide, and the guides it snapped to (for feedback).</summary>
public readonly record struct SnapResult(Vec2 Delta, Guide? GuideX, Guide? GuideY, Guide? GuideAngled)
{
    public static readonly SnapResult None = new(Vec2.Zero, null, null, null);
    public bool Snapped => GuideX is not null || GuideY is not null || GuideAngled is not null;
}

/// <summary>
/// Snapping to ruler guides (requirement R4). Pure math, all in document points. Vertical guides snap the
/// x of an object's left edge, center or right edge; horizontal guides snap the y of its top, center or bottom;
/// an angled guide snaps the nearest corner or center, but only when no horizontal/vertical guide did.
/// </summary>
public static class GuideSnapping
{
    /// <summary>
    /// Offset to add to <paramref name="bounds"/> so an edge or center lies on a guide within
    /// <paramref name="tolerance"/> points. <paramref name="snapX"/>/<paramref name="snapY"/> restrict the axes
    /// (a Shift-constrained move only snaps along its free axis).
    /// </summary>
    public static SnapResult SnapBounds(IEnumerable<Guide> guides, Rect2 bounds, double tolerance, bool snapX = true, bool snapY = true)
    {
        if (bounds.IsEmpty) return SnapResult.None;

        double bestX = double.PositiveInfinity, bestY = double.PositiveInfinity;
        double dx = 0, dy = 0;
        Guide? gx = null, gy = null;

        double[] xs = [bounds.Left, bounds.Center.X, bounds.Right];
        double[] ys = [bounds.Top, bounds.Center.Y, bounds.Bottom];

        Guide? angled = null;
        double bestAngled = double.PositiveInfinity;
        Vec2 angledDelta = Vec2.Zero;
        Vec2[] probes = [bounds.TopLeft, new(bounds.Right, bounds.Top), bounds.BottomRight, new(bounds.Left, bounds.Bottom), bounds.Center];

        foreach (var g in guides)
        {
            switch (g.Orientation)
            {
                case GuideOrientation.Vertical when snapX:
                    foreach (double x in xs)
                    {
                        double d = g.Origin.X - x;
                        if (Math.Abs(d) <= tolerance && Math.Abs(d) < bestX) { bestX = Math.Abs(d); dx = d; gx = g; }
                    }
                    break;
                case GuideOrientation.Horizontal when snapY:
                    foreach (double y in ys)
                    {
                        double d = g.Origin.Y - y;
                        if (Math.Abs(d) <= tolerance && Math.Abs(d) < bestY) { bestY = Math.Abs(d); dy = d; gy = g; }
                    }
                    break;
                case GuideOrientation.Angled when snapX && snapY:
                    foreach (var p in probes)
                    {
                        double s = g.SignedDistance(p);
                        if (Math.Abs(s) <= tolerance && Math.Abs(s) < bestAngled)
                        {
                            bestAngled = Math.Abs(s);
                            angledDelta = g.Normal * -s;
                            angled = g;
                        }
                    }
                    break;
            }
        }

        if (gx is not null || gy is not null) return new SnapResult(new Vec2(dx, dy), gx, gy, null);
        return angled is null ? SnapResult.None : new SnapResult(angledDelta, null, null, angled);
    }

    /// <summary>Snaps a single point (drawing a shape, dragging a handle) to the nearest vertical and horizontal guide.</summary>
    public static SnapResult SnapPoint(IEnumerable<Guide> guides, Vec2 point, double tolerance, bool snapX = true, bool snapY = true) =>
        SnapBounds(guides, new Rect2(point.X, point.Y, point.X, point.Y), tolerance, snapX, snapY);
}
