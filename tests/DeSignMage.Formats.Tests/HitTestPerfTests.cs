using System.Diagnostics;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Lyt;
using Xunit.Abstractions;
using DeSignMage.TestSupport;

namespace DeSignMage.Formats.Tests;

/// <summary>Click-to-select (R10) must stay instant on the big sample layouts: it also runs on every mouse move for the hover cursor.</summary>
public class HitTestPerfTests(ITestOutputHelper output)
{
    [LocalTheory("layouts/art-a.lyt")]
    [InlineData("art-a.lyt")]
    public void Hit_testing_a_280_object_layout_takes_about_a_millisecond_or_less(string name)
    {
        var doc = LytImporter.Open(TestFiles.Layout(name)).Document;
        var page = doc.Page;
        var rnd = new Random(7);
        var points = Enumerable.Range(0, 400).Select(_ => new Vec2(rnd.NextDouble() * page.Width, rnd.NextDouble() * page.Height)).ToArray();
        double tol = 8 / 0.5;   // 8 screen pixels at a 50% zoom

        foreach (var p in points.Take(20)) HitTesting.HitTest(doc, p, tol);   // warm up (layouts, JIT)
        var watch = Stopwatch.StartNew();
        int hits = 0;
        foreach (var p in points) if (HitTesting.HitTest(doc, p, tol) is not null) hits++;
        watch.Stop();
        double ms = watch.Elapsed.TotalMilliseconds / points.Length;
        output.WriteLine($"{doc.ObjectCount} objects: {ms:0.000} ms per hit test, {hits}/{points.Length} points hit something");
        Assert.True(hits > 0);
        Assert.True(ms < 3, $"{ms:0.000} ms per hit test");
    }
}
