namespace DeSignMage.Core.Model;

/// <summary>
/// Document space is in points (1/72 inch), x to the right, y DOWN. The page's top-left corner is the origin until the page is
/// resized from its top or left edge (then it is <see cref="PageSetup.X"/>, <see cref="PageSetup.Y"/>; objects never move when the page does).
/// The user-facing display shows inches with y UP from the page's bottom-left; <see cref="ToUser"/> / <see cref="FromUser"/>
/// do that conversion for rulers, the info line and numeric fields.
/// </summary>
public static class Units
{
    public const double PointsPerInch = 72.0;
    public const double PointsPerMm = 72.0 / 25.4;

    public static double ToInches(double points) => points / PointsPerInch;
    public static double FromInches(double inches) => inches * PointsPerInch;

    /// <summary>Document point → user coordinates (inches, origin bottom-left of page, y up).</summary>
    public static (double X, double Y) ToUser(Geometry.Vec2 p, PageSetup page) =>
        (ToInches(p.X - page.X), ToInches(page.Y + page.Height - p.Y));

    public static Geometry.Vec2 FromUser(double xInches, double yInches, PageSetup page) =>
        new(page.X + FromInches(xInches), page.Y + page.Height - FromInches(yInches));
}

/// <summary>
/// Page (artboard) in points: its size and where its top-left corner is in document space (<see cref="X"/>, <see cref="Y"/>, the
/// origin until the page is resized from its top or left edge). Objects may live outside it on the pasteboard.
/// </summary>
public sealed record PageSetup(double Width, double Height, double X = 0, double Y = 0)
{
    public static PageSetup FromInches(double w, double h) => new(Units.FromInches(w), Units.FromInches(h));
    public Geometry.Rect2 Bounds => new(X, Y, X + Width, Y + Height);

    /// <summary>The page that covers <paramref name="bounds"/>.</summary>
    public static PageSetup FromBounds(Geometry.Rect2 bounds) => new(bounds.Width, bounds.Height, bounds.Left, bounds.Top);
}
