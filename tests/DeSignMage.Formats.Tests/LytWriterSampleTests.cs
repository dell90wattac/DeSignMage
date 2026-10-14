using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Lyt;
using DeSignMage.TestSupport;
using Xunit.Abstractions;

namespace DeSignMage.Formats.Tests;

/// <summary>
/// The sample layouts are imported, written back out and imported again: both documents have to hold the same objects in the same order,
/// with the same shapes, colors, pictures, text and groups. (The layouts are not in the repository; the tests skip without them.)
/// </summary>
public class LytWriterSampleTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Cases() => TestFiles.LayoutCases();

    [LocalTheory("layouts/manifest.json")]
    [MemberData(nameof(Cases))]
    public void A_sample_layout_written_back_out_imports_as_the_same_document(string relativePath, int _)
    {
        var first = LytImporter.Open(TestFiles.Layout(relativePath));
        var original = first.Document;

        var bytes = LytWriter.ToBytes(original, new LytWriteOptions { Title = "Round trip" }, out var written);
        output.WriteLine($"{relativePath}: {original.ObjectCount} objects -> {written.PartCount} parts, {written.GroupCount} groups, {bytes.Length / 1024} KB; " +
                         $"{written.LiveTextCount} live texts, {written.TextAsOutlinesCount} as outlines, {written.PictureCount} pictures, {written.GradientCount + written.FadeCount} gradients/fades");
        foreach (var w in written.Warnings) output.WriteLine("  warning: " + w);
        Assert.Equal(0, written.SkippedCount);

        // the bytes are a layout the parser reads without complaint, with the counts that were written
        var parsed = LytParser.Parse(bytes);
        Assert.Empty(parsed.Warnings);
        Assert.Equal(written.PartCount + written.GroupCount, parsed.Objects.Count);
        Assert.All(parsed.Objects.Where(o => !o.IsGroup), o => Assert.Empty(o.Notes));

        var second = LytImporter.Import(parsed);
        var after = second.Document;
        Assert.Equal(original.Layers.Count, after.Layers.Count);
        Assert.Equal(original.Layers.Select(l => l.Name), after.Layers.Select(l => l.Name));
        Assert.Equal(original.Page.Width, after.Page.Width, 3);
        Assert.Equal(original.Page.Height, after.Page.Height, 3);

        var a = WriterTestKit.Drawn(original);
        var b = WriterTestKit.Drawn(after);
        Assert.Equal(a.Count, b.Count);
        Assert.Equal(original.Layers.Select(l => l.Children.Count), after.Layers.Select(l => l.Children.Count));
        Assert.Equal(first.GroupCount, second.GroupCount);

        var groupsA = GroupNumbers(a);
        var groupsB = GroupNumbers(b);
        var problems = new List<string>();
        for (int i = 0; i < a.Count; i++)
        {
            string? problem = Compare(a[i], b[i], original, after);
            if (problem is not null && problems.Count < 15) problems.Add($"#{i} {a[i].KindName} '{a[i].Name}': {problem}");
            if (!groupsA[i].SequenceEqual(groupsB[i]) && problems.Count < 15) problems.Add($"#{i} '{a[i].Name}': groups {string.Join(">", groupsA[i])} vs {string.Join(">", groupsB[i])}");
        }
        Assert.True(problems.Count == 0, $"{relativePath}: {string.Join("; ", problems)}");
    }

    /// <summary>For every drawn object the numbers of the groups around it (outermost first), numbering groups by first appearance.</summary>
    private static List<int[]> GroupNumbers(List<DocObject> drawn)
    {
        var numbers = new Dictionary<GroupObject, int>();
        var result = new List<int[]>();
        foreach (var o in drawn)
        {
            var chain = new List<int>();
            for (var g = o.Parent as GroupObject; g is not null; g = g.Parent as GroupObject)
            {
                if (!numbers.TryGetValue(g, out int n)) numbers[g] = n = numbers.Count + 1;
                chain.Insert(0, n);
            }
            result.Add([.. chain]);
        }
        return result;
    }

    private static string? Compare(DocObject a, DocObject b, Document da, Document db)
    {
        // A text on a path is written as the outlines of its letters (the legacy path effect is not written yet), so it comes back as a shape in the same place.
        bool flowedText = a is TextObject { IsOnPath: true } && b is PathObject;
        if (a.GetType() != b.GetType() && !flowedText) return $"became {b.GetType().Name}";
        var ba = a.GetWorldBounds(); var bb = b.GetWorldBounds();
        double tol = 0.02 + 1e-4 * Math.Max(Math.Max(ba.Width, ba.Height), 1);
        if (Math.Abs(ba.Left - bb.Left) > tol || Math.Abs(ba.Top - bb.Top) > tol || Math.Abs(ba.Right - bb.Right) > tol || Math.Abs(ba.Bottom - bb.Bottom) > tol)
            return $"bounds {ba} vs {bb}";

        if (a is TextObject ta && b is TextObject tb)
        {
            if (ta.Text != tb.Text || ta.FontFamily != tb.FontFamily) return $"text '{ta.Text}'/{ta.FontFamily} vs '{tb.Text}'/{tb.FontFamily}";
        }
        if (a is ImageObject ia)
        {
            var ib = (ImageObject)b;
            if (!da.Assets.Get(ia.AssetId)!.Data.AsSpan().SequenceEqual(db.Assets.Get(ib.AssetId)!.Data)) return "picture bytes differ";
            return null;
        }
        return ComparePaint(a.Style.Fill, b.Style.Fill) ?? CompareStroke(a.Style.Stroke, b.Style.Stroke);
    }

    private static string? ComparePaint(Paint? x, Paint? y)
    {
        switch (x, y)
        {
            case (null, null): return null;
            case (SolidPaint sx, SolidPaint sy):
                if ((sx.Color.R, sx.Color.G, sx.Color.B) != (sy.Color.R, sy.Color.G, sy.Color.B)) return $"color {sx.Color} vs {sy.Color}";
                string? nx = Truncate(sx.Color.Name), ny = sy.Color.Name;
                return nx == ny ? null : $"color name '{nx}' vs '{ny}'";
            case (GradientPaint gx, GradientPaint gy):
                if (gx.Kind != gy.Kind || gx.Stops.Count != gy.Stops.Count) return $"gradient {gx.Kind}/{gx.Stops.Count} vs {gy.Kind}/{gy.Stops.Count}";
                for (int i = 0; i < gx.Stops.Count; i++)
                {
                    var cx = gx.Stops[i].Color; var cy = gy.Stops[i].Color;
                    if (Math.Abs(cx.R - cy.R) > 1 || Math.Abs(cx.G - cy.G) > 1 || Math.Abs(cx.B - cy.B) > 1 || Math.Abs(cx.A - cy.A) > 2 || Math.Abs(gx.Stops[i].Offset - gy.Stops[i].Offset) > 1e-3)
                        return $"gradient stop {i}: {cx}@{gx.Stops[i].Offset:0.###} vs {cy}@{gy.Stops[i].Offset:0.###}";
                }
                for (int i = 0; i < gx.Points.Count; i++)
                    if (Vec2.Distance(gx.Points[i], gy.Points[i]) > 1e-3) return $"gradient point {i}: {gx.Points[i]} vs {gy.Points[i]}";
                return null;
            default:
                return $"fill {x?.GetType().Name ?? "none"} vs {y?.GetType().Name ?? "none"}";
        }
    }

    private static string? CompareStroke(StrokeStyle? x, StrokeStyle? y)
    {
        if (x is null && y is null) return null;
        if (x is null || y is null) return "stroke appeared or vanished";
        return Math.Abs(x.Width - y.Width) < 1e-6 ? null : $"stroke {x.Width} vs {y.Width}";
    }

    /// <summary>A color name is cut to what the 28-byte record holds.</summary>
    private static string? Truncate(string? name) => name is { Length: > 27 } ? name[..27] : name;
}
