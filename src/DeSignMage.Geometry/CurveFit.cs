using DeSignMage.Core.Geometry;
using DeSignMage.Geometry.Internal;

namespace DeSignMage.Geometry;

/// <summary>Options for <see cref="CurveFit"/>. Lengths in points.</summary>
public sealed record CurveFitOptions
{
    public static readonly CurveFitOptions Default = new();

    /// <summary>Largest distance of the result from the input points. Default 0.01 pt.</summary>
    public double Tolerance { get; init; } = 0.01;

    /// <summary>A point where the polyline turns by more than this (degrees) stays a sharp corner. Default 30.</summary>
    public double CornerAngle { get; init; } = 30;

    /// <summary>Allow cubics where arcs would need many pieces (false = lines and arcs only). Default false.</summary>
    public bool PreferCubics { get; init; }
}

/// <summary>
/// Curve refit: polylines (flattened or traced outlines, offset results) back to a light, editable path of lines and
/// circular arcs (and cubics if asked) within a tolerance, keeping sharp corners. Arcs never sweep more than 180°.
/// </summary>
public static class CurveFit
{
    /// <summary>Fits one polyline. A closed polyline gives a closed loop.</summary>
    public static PathGeometry Fit(IReadOnlyList<Vec2> points, bool closed, CurveFitOptions? options = null)
    {
        options ??= CurveFitOptions.Default;
        var loop = FitLoop(points, closed, options);
        return loop is null ? PathGeometry.EmptyPath : OutPaths.Build([loop]);
    }

    /// <summary>Flattens a path (to a tenth of the tolerance) and fits every subpath again.</summary>
    public static PathGeometry Refit(PathGeometry path, CurveFitOptions? options = null)
    {
        options ??= CurveFitOptions.Default;
        var loops = new List<OutLoop>();
        foreach (var poly in path.Flatten(options.Tolerance / 10))
            if (FitLoop(poly.Points, poly.Closed, options) is { } loop) loops.Add(loop);
        var built = OutPaths.Build(loops);
        return new PathGeometry([.. built.Verbs], [.. built.Points], path.FillRule);
    }

    private static OutLoop? FitLoop(IReadOnlyList<Vec2> points, bool closed, CurveFitOptions options)
    {
        if (points.Count < 2) return null;
        var bounds = Rect2.Empty;
        foreach (var p in points) bounds = bounds.Union(p);
        double extent = Math.Max(bounds.Width, bounds.Height);
        double eps = Math.Max(extent * 1e-12, 1e-300);
        double tol = options.Tolerance > 0 ? options.Tolerance : 0.01;
        return Fitter.Fit(points, closed, null, null, tol, options.CornerAngle * Math.PI / 180, options.PreferCubics, eps);
    }
}
