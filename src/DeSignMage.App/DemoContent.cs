using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.App;

/// <summary>Sample artwork and stress content for development (--demo, --stress, Tools > Debug).</summary>
internal static class DemoContent
{
    private static readonly DocColor Gold = DocColor.FromHex("#F2B705");

    public static Document NewDocument() => new(PageSetup.FromInches(24, 12));

    /// <summary>Adds a small sign layout through the command system (so it is undoable like anything else).</summary>
    public static void AddSample(EditorSession s)
    {
        using var tx = s.History.BeginTransaction("Sample Artwork");
        var layer = s.Document.ActiveLayer;
        void Add(DocObject o) => s.History.Execute(new AddObjectCommand(layer, o));

        Add(Shape(new RectangleObject(In(22), In(8), In(0.4)), In(1), In(2), ObjectStyle.FillOnly(DocColor.Black)));

        // Paw-like cluster of ellipses
        var gold = ObjectStyle.FillOnly(Gold);
        Add(Shape(new EllipseObject(In(3.2), In(2.8)), In(2.0), In(5.6), gold));
        foreach (var (x, y, w, h) in new[] { (1.7, 4.1, 1.1, 1.4), (2.9, 3.4, 1.1, 1.5), (4.1, 3.6, 1.1, 1.4), (5.0, 4.6, 1.0, 1.3) })
            Add(Shape(new EllipseObject(In(w), In(h)), In(x), In(y), gold));

        var title = new TextObject("DeSign Mage", "Segoe UI Semibold", In(2.2))
        {
            Transform = Affine2.Translation(In(7), In(6.8)),
            Style = ObjectStyle.FillOnly(DocColor.White),
        };
        Add(title);
        Add(new TextObject("vector sign design", "Segoe UI", In(0.9))
        {
            Transform = Affine2.Translation(In(7.2), In(8.6)),
            Style = ObjectStyle.FillOnly(Gold),
        });

        // A group out on the pasteboard, where users often keep variants.
        var group = new GroupObject { Transform = Affine2.Translation(In(25), In(3)) };
        s.History.Execute(new AddObjectCommand(layer, group));
        var stroke = new ObjectStyle(new SolidPaint(DocColor.FromHex("#1E88E5")), StrokeStyle.Solid(DocColor.Black, 2));
        s.History.Execute(new AddObjectCommand(group, Shape(new RectangleObject(In(3), In(2)), 0, 0, stroke)));
        s.History.Execute(new AddObjectCommand(group, Shape(new EllipseObject(In(2), In(2)), In(2), In(1.2), ObjectStyle.FillOnly(DocColor.FromHex("#E53935")))));

        s.Selection.Set(title);
    }

    /// <summary>A few ruler guides (--guides): two vertical, two horizontal, the last one selected.</summary>
    public static void AddGuides(EditorSession s)
    {
        var page = s.Document.Page;
        using var tx = s.History.BeginTransaction("Sample Guides");
        s.History.Execute(new AddGuideCommand(Guide.Vertical(page.Width * 0.25)));
        s.History.Execute(new AddGuideCommand(Guide.Vertical(page.Width / 2)));
        s.History.Execute(new AddGuideCommand(Guide.Horizontal(In(2))));
        var selected = Guide.Horizontal(page.Height * 0.6);
        s.History.Execute(new AddGuideCommand(selected));
        s.SelectedGuide = selected;
    }

    /// <summary>Adds <paramref name="count"/> random shapes on and around the page as one undo step.</summary>
    public static void AddStress(EditorSession s, int count, int seed = 1)
    {
        var rnd = new Random(seed);
        var page = s.Document.Page;
        using var tx = s.History.BeginTransaction($"Add {count} Test Shapes");
        var layer = s.Document.ActiveLayer;
        for (int i = 0; i < count; i++)
        {
            double w = In(0.2 + rnd.NextDouble() * 1.5), h = In(0.2 + rnd.NextDouble() * 1.5);
            DocObject o = rnd.Next(2) == 0 ? new RectangleObject(w, h, rnd.Next(3) == 0 ? In(0.1) : 0) : new EllipseObject(w, h);
            var color = new DocColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
            var style = new ObjectStyle(new SolidPaint(color), StrokeStyle.Solid(DocColor.Black, 0.5));
            s.History.Execute(new AddObjectCommand(layer, Shape(o, rnd.NextDouble() * page.Width, rnd.NextDouble() * page.Height, style)));
        }
    }

    private static DocObject Shape(DocObject o, double x, double y, ObjectStyle style)
    {
        o.Transform = Affine2.Translation(x, y);
        o.Style = style;
        return o;
    }

    private static double In(double inches) => Units.FromInches(inches);
}
