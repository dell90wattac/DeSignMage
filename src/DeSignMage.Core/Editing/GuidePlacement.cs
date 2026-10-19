using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;

namespace DeSignMage.Core.Editing;

/// <summary>What Layout ▸ Guides can place a guide on.</summary>
public enum GuidePlace
{
    TopEdge, LeftEdge, RightEdge, BottomEdge, AllEdges,
    VerticalCenter, HorizontalCenter, BothCenters,
    TextCapHeight, TextBaseline,
}

/// <summary>
/// Where the Layout ▸ Guides commands put their guides (pure math, no document changes). The edge and center places use a rectangle
/// (the selection's bounds, or the page when nothing is selected); the text places use the first line of each text object, so a
/// rotated text gets a guide that runs along its own baseline.
/// </summary>
public static class GuidePlacement
{
    public static bool IsTextPlace(GuidePlace place) => place is GuidePlace.TextCapHeight or GuidePlace.TextBaseline;

    /// <summary>Guides on the edges or centers of <paramref name="box"/>. Each straight guide keeps its other coordinate at the box's center.</summary>
    public static IReadOnlyList<Guide> ForBox(GuidePlace place, Rect2 box)
    {
        if (box.IsEmpty) return [];
        var c = box.Center;
        Guide Top() => Guide.Horizontal(box.Top, c.X);
        Guide Bottom() => Guide.Horizontal(box.Bottom, c.X);
        Guide Left() => Guide.Vertical(box.Left, c.Y);
        Guide Right() => Guide.Vertical(box.Right, c.Y);
        return place switch
        {
            GuidePlace.TopEdge => [Top()],
            GuidePlace.BottomEdge => [Bottom()],
            GuidePlace.LeftEdge => [Left()],
            GuidePlace.RightEdge => [Right()],
            GuidePlace.AllEdges => [Top(), Left(), Right(), Bottom()],
            GuidePlace.VerticalCenter => [Guide.Vertical(c.X, c.Y)],
            GuidePlace.HorizontalCenter => [Guide.Horizontal(c.Y, c.X)],
            GuidePlace.BothCenters => [Guide.Vertical(c.X, c.Y), Guide.Horizontal(c.Y, c.X)],
            _ => [],
        };
    }

    /// <summary>
    /// A guide through the baseline or cap height (a capital H of the font actually used) of every line of each text, running in the
    /// text's own direction. Lines that would give the same guide give it once. Text on a path has one line, along the straight text.
    /// </summary>
    public static IReadOnlyList<Guide> ForText(GuidePlace place, IEnumerable<TextObject> texts)
    {
        var result = new List<Guide>();
        foreach (var t in texts)
        {
            double up = place == GuidePlace.TextCapHeight ? TextEngine.CapHeightEm(t.FontFamily, t.FontWeight, t.Italic) * t.FontSize : 0;
            var w = t.GetWorldTransform();
            var along = w.TransformVector(new Vec2(1, 0));
            if (along.LengthSquared < 1e-18) continue;
            double angle = Math.Atan2(-along.Y, along.X) * 180 / Math.PI;   // document y runs down, user angles run counter-clockwise
            var layout = t.Layout;
            var baselines = layout.IsOnPath || layout.Lines.Count == 0 ? [0.0] : layout.Lines.Select(l => l.Baseline).ToArray();
            foreach (double baseline in baselines)
            {
                var guide = new Guide(w.Transform(new Vec2(0, baseline - up)), angle);
                if (!result.Any(g => Same(g, guide))) result.Add(guide);
            }
        }
        return result;
    }

    /// <summary>The text objects among <paramref name="objects"/>, looking inside groups.</summary>
    public static IEnumerable<TextObject> TextsIn(IEnumerable<DocObject> objects)
    {
        foreach (var o in objects)
        {
            if (o is TextObject t) yield return t;
            else if (o is GroupObject g)
                foreach (var inner in TextsIn(g.Children)) yield return inner;
        }
    }

    /// <summary>The candidates that are not already among <paramref name="existing"/>: the same line placed twice adds nothing.</summary>
    public static IReadOnlyList<Guide> WithoutDuplicates(IEnumerable<Guide> existing, IEnumerable<Guide> candidates)
    {
        var have = existing.ToList();
        var fresh = new List<Guide>();
        foreach (var g in candidates)
        {
            if (have.Any(h => Same(h, g))) continue;
            have.Add(g);
            fresh.Add(g);
        }
        return fresh;
    }

    /// <summary>Two guides are the same line: the same direction, and each on the other.</summary>
    public static bool Same(Guide a, Guide b)
    {
        double da = Math.Abs(a.AngleDegrees - b.AngleDegrees);
        if (da > 1e-6 && Math.Abs(da - 180) > 1e-6) return false;
        return a.DistanceTo(b.Origin) < 1e-6;
    }
}
