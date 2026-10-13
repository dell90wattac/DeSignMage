using System.Diagnostics;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Lyt;
using DeSignMage.Rendering;
using DeSignMage.TestSupport;
using SkiaSharp;
using Xunit.Abstractions;

namespace DeSignMage.Perf.Tests;

/// <summary>
/// Speed budgets for the things that must stay instant on an older PC (8th-gen Intel, integrated graphics, roughly 2.5x slower than the
/// dev PC). Each check takes the BEST of several runs, so a busy machine can only make a run slower than it should be, never fail a
/// healthy build by itself; and the budgets are many times the measured time even in a Debug build. They fail on a real regression, e.g.
/// text laid out again on every edit (the edit of the big sample goes from 0.3 ms to about 10 ms) or a per-object cache lost (10k shapes).
/// The numbers are written to the test output (<c>dotnet test --logger "console;verbosity=detailed"</c>).
/// </summary>
[Collection("perf")]   // one at a time: parallel tests would disturb each other's timings
public class PerfBudgetTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    /// <summary>A big real layout (283 objects, 37 texts) found in local-testdata/layouts; those tests are skipped where it is not available.</summary>
    internal static readonly string? Sample = FindSample();

    public PerfBudgetTests(ITestOutputHelper output)
    {
        _output = output;
        DeSignMage.Text.SkiaFontService.Install();   // real glyph outlines, as in the program
    }

    public void Dispose() { }

    private static string? FindSample() => LocalData.Has("layouts/sample.lyt") ? LocalData.Sample : null;

    private static double Time(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>The fastest of <paramref name="runs"/> runs.</summary>
    private static double Best(int runs, Func<double> run)
    {
        double best = double.MaxValue;
        for (int i = 0; i < runs; i++) best = Math.Min(best, run());
        return best;
    }

    private void Report(string what, double ms, double budget) =>
        _output.WriteLine($"{what}: {ms:0.00} ms (budget {budget:0} ms)");

    private static Document OpenSample() => LytImporter.Open(Sample!).Document;

    private static Document Shapes(int n)
    {
        var doc = new Document(PageSetup.FromInches(48, 24));
        var s = new EditorSession(doc);
        var rnd = new Random(1);
        using (s.History.BeginTransaction("shapes"))
            for (int i = 0; i < n; i++)
            {
                double w = Units.FromInches(0.2 + rnd.NextDouble() * 1.5), h = Units.FromInches(0.2 + rnd.NextDouble() * 1.5);
                DocObject o = rnd.Next(2) == 0 ? new RectangleObject(w, h) : new EllipseObject(w, h);
                o.Transform = Affine2.Translation(rnd.NextDouble() * doc.Page.Width, rnd.NextDouble() * doc.Page.Height);
                o.Style = new ObjectStyle(new SolidPaint(new DocColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256))), StrokeStyle.Solid(DocColor.Black, 0.5));
                s.History.Execute(new AddObjectCommand(doc.ActiveLayer, o));
            }
        return doc;
    }

    private static void Move(EditorSession s, DocObject o) =>
        s.History.Execute(TransformObjectsCommand.ApplyWorld("Move", [o], Affine2.Translation(1, 0)));

    /// <summary>Re-records after one edit to <paramref name="o"/>: the best of several moves.</summary>
    private static double ReRecordAfterEdit(EditorSession s, SceneRenderer r, DocObject o, int runs = 8) =>
        Best(runs, () =>
        {
            Move(s, o);
            return Time(() => r.GetDocumentPicture(null).Release());
        });

    // ---- Opening ----

    [FactIfSample]
    public void Opening_the_big_sample_stays_under_budget()
    {
        OpenSample();   // warm up: JIT and fonts, like the second time anyone opens a file
        // Opening reads the file; the first picture then lays out the 37 texts and builds every path, so both count.
        double ms = Best(3, () =>
        {
            var watch = Stopwatch.StartNew();
            var doc = OpenSample();
            using var r = new SceneRenderer(doc) { ViewScale = 1 };
            r.GetDocumentPicture(null).Release();
            return watch.Elapsed.TotalMilliseconds;
        });
        Report("open big sample and draw it", ms, 1000);
        Assert.True(ms < 1000, $"opening the big sample and drawing it took {ms:0} ms");   // about 70 ms measured warm; 300-500 ms the very first time in a new process
    }

    // ---- Re-recording the picture after an edit ----

    [FactIfSample]
    public void An_edit_in_the_big_sample_does_not_redo_the_text_or_the_bounds_of_everything_else()
    {
        var doc = OpenSample();
        var s = new EditorSession(doc);
        using var r = new SceneRenderer(doc) { ViewScale = 1 };
        double first = Time(() => r.GetDocumentPicture(null).Release());   // lays out all the text, builds every path

        var plain = doc.Layers.SelectMany(l => l.Children).First(o => o is not TextObject);
        var text = doc.AllObjects().OfType<TextObject>().First();
        double afterShapeEdit = ReRecordAfterEdit(s, r, plain);
        double afterTextMove = ReRecordAfterEdit(s, r, text);
        Report("big sample first record", first, 0);
        Report("big sample re-record after moving a shape", afterShapeEdit, 6);
        Report("big sample re-record after moving a text", afterTextMove, 6);

        // 0.3 ms measured: 20x headroom, yet below the 7-13 ms it cost when every edit measured all the bounds and laid every text out again.
        Assert.True(afterShapeEdit < 6, $"re-recording after an edit took {afterShapeEdit:0.0} ms");
        Assert.True(afterTextMove < 6, $"re-recording after moving a text took {afterTextMove:0.0} ms");
        // Whatever the machine's speed, an edit must cost a small fraction of building the picture from scratch.
        Assert.True(afterShapeEdit < first * 0.15, $"an edit costs {afterShapeEdit:0.0} ms against {first:0.0} ms for the first record");
    }

    [FactIfSample]
    public void Changing_a_texts_font_size_relays_out_only_that_text()
    {
        var doc = OpenSample();
        var s = new EditorSession(doc);
        using var r = new SceneRenderer(doc) { ViewScale = 1 };
        r.GetDocumentPicture(null).Release();
        var texts = doc.AllObjects().OfType<TextObject>().ToList();
        var layouts = texts.Select(t => t.Layout).ToList();

        s.History.Execute(new ChangeTextFormatCommand([texts[0]], new TextFormatChange(Size: texts[0].FontSize * 1.1)));
        r.GetDocumentPicture(null).Release();

        Assert.NotSame(layouts[0], texts[0].Layout);
        for (int i = 1; i < texts.Count; i++) Assert.Same(layouts[i], texts[i].Layout);
    }

    [FactIfSample]
    public void An_edit_in_the_big_sample_in_outline_view_is_cheap_too()
    {
        var doc = OpenSample();
        var s = new EditorSession(doc);
        using var r = new SceneRenderer(doc) { ViewScale = 1, Wireframe = true };
        double first = Time(() => r.GetDocumentPicture(null).Release());   // classifies every letter and loop
        var plain = doc.Layers.SelectMany(l => l.Children).First(o => o is not TextObject);
        double edit = ReRecordAfterEdit(s, r, plain);
        Report("big sample outline view first record", first, 0);
        Report("big sample outline view re-record after an edit", edit, 12);
        Assert.True(edit < 12, $"re-recording the outline view after an edit took {edit:0.0} ms");   // 0.4 ms measured; 37 ms before the loops were cached
        Assert.True(edit < first * 0.15, $"an edit costs {edit:0.0} ms against {first:0.0} ms for the first record");
    }

    [Fact]
    public void Recording_10000_shapes_and_re_recording_after_an_edit_stay_under_budget()
    {
        var doc = Shapes(10_000);
        var s = new EditorSession(doc);
        using var r = new SceneRenderer(doc) { ViewScale = 1 };
        double first = Time(() => r.GetDocumentPicture(null).Release());
        var one = doc.Layers[0].Children[0];
        double edit = ReRecordAfterEdit(s, r, one, runs: 5);
        Report("10,000 shapes first record", first, 2500);
        Report("10,000 shapes re-record after an edit", edit, 150);
        Assert.True(first < 2500, $"the first record took {first:0} ms");
        Assert.True(edit < 150, $"re-recording after an edit took {edit:0} ms");
    }

    // ---- Groups (R22: a group is membership only, the drawing order is one flat list) ----

    [Fact]
    public void Grouping_moving_and_deleting_a_group_of_10000_shapes_stay_under_budget()
    {
        var doc = Shapes(10_000);
        var s = new EditorSession(doc);
        using var r = new SceneRenderer(doc) { ViewScale = 1 };
        r.GetDocumentPicture(null).Release();

        double group = Time(() => s.GroupObjects(doc.Layers[0].Children.ToList()));          // 10,000 members: a rubber band over everything
        var g = Assert.IsType<GroupObject>(s.Selection.Items[0]);
        double move = Best(5, () =>
        {
            s.TransformSelection("Move", Affine2.Translation(1, 0));                            // one transform on the group, whatever its size
            return Time(() => r.GetDocumentPicture(null).Release());
        });
        double delete = Time(() => s.DeleteSelection());
        double undo = Time(() => s.Undo());
        double ungroup = Time(() => { s.Selection.Set(g); s.UngroupSelection(); });
        Report("group 10,000 shapes", group, 400);
        Report("re-record after moving a group of 10,000", move, 150);
        Report("delete a group of 10,000", delete, 800);
        Report("undo the delete", undo, 800);
        Report("ungroup 10,000", ungroup, 400);
        Assert.True(group < 400, $"grouping took {group:0} ms");
        Assert.True(move < 150, $"re-recording after moving the group took {move:0} ms");
        Assert.True(delete < 800 && undo < 800, $"delete {delete:0} ms, undo {undo:0} ms");
        Assert.True(ungroup < 400, $"ungrouping took {ungroup:0} ms");
        Assert.Equal(10_000, doc.Layers[0].Children.Count);
    }

    // ---- Hit testing ----

    [Fact]
    public void Hit_testing_a_10000_shape_document_stays_under_budget()
    {
        var doc = Shapes(10_000);
        var page = doc.Page;
        var rnd = new Random(4);
        var points = Enumerable.Range(0, 60).Select(_ => new Vec2(rnd.NextDouble() * page.Width, rnd.NextDouble() * page.Height)).ToArray();
        foreach (var p in points.Take(10)) HitTesting.HitTest(doc, p, 16);   // warm up
        double perHit = Best(3, () => Time(() => { foreach (var p in points) HitTesting.HitTest(doc, p, 16); }) / points.Length);
        Report("hit test, 10,000 shapes", perHit, 15);
        Assert.True(perHit < 15, $"one hit test took {perHit:0.0} ms");   // it runs on every mouse move (the hover cursor)
    }

    [FactIfSample]
    public void Hit_testing_the_big_sample_is_nearly_instant()
    {
        var doc = OpenSample();
        var page = doc.Page;
        var rnd = new Random(3);
        var points = Enumerable.Range(0, 200).Select(_ => new Vec2(rnd.NextDouble() * page.Width, rnd.NextDouble() * page.Height)).ToArray();
        foreach (var p in points.Take(20)) HitTesting.HitTest(doc, p, 16);
        double perHit = Best(3, () => Time(() => { foreach (var p in points) HitTesting.HitTest(doc, p, 16); }) / points.Length);
        Report("hit test, big sample", perHit, 3);
        Assert.True(perHit < 3, $"one hit test took {perHit:0.00} ms");
    }

    // ---- Redrawing without the GPU ----

    [Fact]
    public void A_software_redraw_of_10000_shapes_stays_under_budget()
    {
        // The worst case (no GPU): replaying the recorded picture into a 1080p bitmap. Normally the GPU does this.
        var doc = Shapes(10_000);
        using var r = new SceneRenderer(doc) { ViewScale = 1 };
        var pic = r.GetDocumentPicture(null);
        var bounds = doc.GetContentBounds().Union(doc.Page.Bounds);
        float scale = (float)Math.Min(1920 / bounds.Width, 1080 / bounds.Height);
        using var surface = SKSurface.Create(new SKImageInfo(1920, 1080));
        double ms = Best(4, () => Time(() =>
        {
            var c = surface.Canvas;
            c.Clear(SKColors.White);
            c.Save(); c.Scale(scale); c.Translate((float)-bounds.Left, (float)-bounds.Top);
            c.DrawPicture(pic.Picture);
            c.Restore();
            c.Flush();
        }));
        pic.Release();
        Report("software redraw, 10,000 shapes", ms, 400);
        Assert.True(ms < 400, $"a software redraw took {ms:0} ms");
    }
}

/// <summary>A test that needs the big sample layout; skipped (not failed) where the file is not available.</summary>
public sealed class FactIfSampleAttribute : FactAttribute
{
    public FactIfSampleAttribute()
    {
        if (PerfBudgetTests.Sample is null) Skip = "the big sample layout is not available";
    }
}
