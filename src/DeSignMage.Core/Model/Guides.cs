using System.Collections;
using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Model;

public enum GuideOrientation { Horizontal, Vertical, Angled }

/// <summary>
/// A ruler guide ("ruler mark", requirement R4): an infinite line through <see cref="Origin"/> at
/// <see cref="AngleDegrees"/>. The angle is in user space (y up, counter-clockwise): 0 = horizontal,
/// 90 = vertical, kept in [0, 180). Guides are part of the document but are never printed or exported.
/// Mutated only by guide commands, so undo/redo restores the same instance (and selection survives it).
/// </summary>
public sealed class Guide
{
    private const double AxisToleranceDegrees = 1e-6;

    public Guide(Vec2 origin, double angleDegrees = 0, bool locked = false)
    {
        Origin = origin;
        AngleDegrees = NormalizeAngle(angleDegrees);
        Locked = locked;
    }

    /// <summary>A point on the line, in document points (y down).</summary>
    public Vec2 Origin { get; internal set; }

    /// <summary>Direction of the line in degrees, user space (0 = horizontal, 90 = vertical), in [0, 180).</summary>
    public double AngleDegrees { get; internal set; }

    /// <summary>A locked guide can't be dragged (it can still be selected, unlocked and removed).</summary>
    public bool Locked { get; internal set; }

    /// <summary>A horizontal guide at document y (the x of its origin is irrelevant).</summary>
    public static Guide Horizontal(double docY, double originX = 0) => new(new Vec2(originX, docY), 0);

    /// <summary>A vertical guide at document x (the y of its origin is irrelevant).</summary>
    public static Guide Vertical(double docX, double originY = 0) => new(new Vec2(docX, originY), 90);

    public GuideOrientation Orientation => Orient(AngleDegrees);

    public static GuideOrientation Orient(double angleDegrees)
    {
        double a = NormalizeAngle(angleDegrees);
        if (a < AxisToleranceDegrees || a > 180 - AxisToleranceDegrees) return GuideOrientation.Horizontal;
        if (Math.Abs(a - 90) < AxisToleranceDegrees) return GuideOrientation.Vertical;
        return GuideOrientation.Angled;
    }

    /// <summary>Unit vector along the line, in document space (y down).</summary>
    public Vec2 Direction => DirectionOf(AngleDegrees);

    /// <summary>Unit vector perpendicular to the line (the only way a guide moves).</summary>
    public Vec2 Normal => NormalOf(AngleDegrees);

    public static Vec2 DirectionOf(double angleDegrees)
    {
        double r = angleDegrees * Math.PI / 180;
        return new Vec2(Math.Cos(r), -Math.Sin(r));
    }

    public static Vec2 NormalOf(double angleDegrees)
    {
        var d = DirectionOf(angleDegrees);
        return new Vec2(-d.Y, d.X);
    }

    /// <summary>Signed distance (points) from the line to <paramref name="p"/> along <see cref="Normal"/>.</summary>
    public double SignedDistance(Vec2 p) => Vec2.Dot(p - Origin, Normal);

    public double DistanceTo(Vec2 p) => Math.Abs(SignedDistance(p));

    /// <summary>The origin after moving the line perpendicular to itself by <paramref name="amount"/> points.</summary>
    public Vec2 OriginMovedBy(double amount) => Origin + Normal * amount;

    /// <summary>
    /// The origin that centers this guide on the page along its axis ("+" on the guide Context Bar): a vertical
    /// guide goes to the page's horizontal center, a horizontal one to its vertical center; an angled guide is
    /// shifted parallel to itself until it passes through the page center. The other coordinate is kept
    /// where it makes no difference.
    /// </summary>
    public Vec2 CenteredOn(PageSetup page)
    {
        var c = page.Bounds.Center;
        return Orientation switch
        {
            GuideOrientation.Horizontal => new Vec2(Origin.X, c.Y),
            GuideOrientation.Vertical => new Vec2(c.X, Origin.Y),
            _ => OriginMovedBy(SignedDistance(c)),
        };
    }

    public static double NormalizeAngle(double degrees)
    {
        double a = degrees % 180;
        if (a < 0) a += 180;
        // 180 - epsilon is horizontal; fold it onto 0 so equal guides compare equal.
        return 180 - a < AxisToleranceDegrees ? 0 : a;
    }

    /// <summary>The guide closest to <paramref name="doc"/> within <paramref name="tolerance"/> (points), or null.</summary>
    public static Guide? HitTest(IEnumerable<Guide> guides, Vec2 doc, double tolerance)
    {
        Guide? best = null;
        double bestDistance = tolerance;
        foreach (var g in guides)
        {
            double d = g.DistanceTo(doc);
            if (d > bestDistance) continue;
            best = g;
            bestDistance = d;
        }
        return best;
    }

    public override string ToString() =>
        FormattableString.Invariant($"Guide {Orientation} at {Origin}, {AngleDegrees:0.###}°{(Locked ? ", locked" : "")}");
}

/// <summary>The document's guides in creation order. Changed only by guide commands.</summary>
public sealed class GuideCollection : IReadOnlyList<Guide>
{
    private readonly List<Guide> _items = [];

    public int Count => _items.Count;
    public Guide this[int index] => _items[index];
    public bool Contains(Guide g) => _items.Contains(g);
    public int IndexOf(Guide g) => _items.IndexOf(g);

    internal void Insert(int index, Guide g)
    {
        if (_items.Contains(g)) throw new InvalidOperationException("Guide is already in the document.");
        if (index < 0 || index > _items.Count) index = _items.Count;
        _items.Insert(index, g);
    }

    internal int Remove(Guide g)
    {
        int index = _items.IndexOf(g);
        if (index < 0) throw new ArgumentException("Guide is not in the document.");
        _items.RemoveAt(index);
        return index;
    }

    internal void ReplaceAll(IEnumerable<Guide> guides)
    {
        var list = guides.ToList();
        _items.Clear();
        _items.AddRange(list);
    }

    public IEnumerator<Guide> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
