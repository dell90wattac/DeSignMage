using System.Diagnostics;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Rendering;
using SkiaSharp;
using Xunit.Abstractions;

namespace DeSignMage.Perf.Tests;

/// <summary>
/// Speed budgets for the soft paint (Soft Shadow, Glow, Feather; R8): the softness is made when it is drawn, so what matters is that it is made once and kept.
/// Same rules as <see cref="PerfBudgetTests"/>: the best of several runs, budgets many times the measured time (see <c>notes/performance-new-app.md</c> section 6d).
/// </summary>
[Collection("perf")]
public class SoftEffectPerfTests(ITestOutputHelper output)
{
    private static double Time(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static double Best(int runs, Func<double> run)
    {
        double best = double.MaxValue;
        for (int i = 0; i < runs; i++) best = Math.Min(best, run());
        return best;
    }

    private void Report(string what, double ms, double budget) => output.WriteLine($"{what}: {ms:0.00} ms (budget {budget:0} ms)");

    /// <summary>A page of <paramref name="n"/> shapes, each with a soft shadow object in a group of its own (what the effect makes), or a feather.</summary>
    private static (Document Doc, List<GroupObject> Groups) Page(int n, bool feather = false)
    {
        var doc = new Document(PageSetup.FromInches(48, 24));
        var rnd = new Random(2);
        var groups = new List<GroupObject>();
        var layer = doc.ActiveLayer;
        for (int i = 0; i < n; i++)
        {
            double w = Units.FromInches(0.4 + rnd.NextDouble() * 1.5), h = Units.FromInches(0.4 + rnd.NextDouble() * 1.5);
            var at = Affine2.Translation(rnd.NextDouble() * (doc.Page.Width - 200), rnd.NextDouble() * (doc.Page.Height - 200));
            DocObject src = rnd.Next(2) == 0 ? new RectangleObject(w, h) : new EllipseObject(w, h);
            src.Transform = at;
            src.Style = ObjectStyle.FillOnly(new DocColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)));
            if (feather)
            {
                src.Style = src.Style.WithFeather(new FeatherStyle(0.08, 0.38));
                doc.Insert(layer, -1, src);
                continue;
            }
            var shadowShape = src.GetLocalGeometry()!.Transform(at * Affine2.Translation(w * 0.1, h * 0.1));
            var shadow = new PathObject(shadowShape) { Name = "Soft Shadow", Style = new ObjectStyle(new SolidPaint(DocColor.Black), null, new BlurStyle(SoftKind.SoftShadow, 0.5, 0.5, h)) };
            doc.Insert(layer, -1, shadow);
            doc.Insert(layer, -1, src);
            var group = new GroupObject();
            doc.Insert(new Slot(layer, layer.Children.Count), group);
            doc.SetGroup(shadow, group);
            doc.SetGroup(src, group);
            groups.Add(group);
        }
        return (doc, groups);
    }

    [Fact]
    public void Making_the_masks_of_200_soft_shadows_the_first_time_stays_under_budget()
    {
        var (doc, _) = Page(200);
        double ms = Best(3, () =>
        {
            using var r = new SceneRenderer(doc) { ViewScale = 0.2 };
            return Time(() => r.GetDocumentPicture(null).Release());
        });
        Report("first record, 200 soft shadows", ms, 250);
        Assert.True(ms < 250, $"recording 200 soft shadows the first time took {ms:0} ms");
    }

    [Fact]
    public void Recording_again_after_an_edit_reuses_every_mask()
    {
        var (doc, groups) = Page(200);
        var s = new EditorSession(doc);
        using var r = new SceneRenderer(doc) { ViewScale = 0.2 };
        r.GetDocumentPicture(null).Release();
        Assert.Equal(200, r.SoftMasksMade);
        double ms = Best(8, () =>
        {
            s.History.Execute(TransformObjectsCommand.ApplyWorld("Move", [groups[0]], Affine2.Translation(1, 0)));
            return Time(() => r.GetDocumentPicture(null).Release());
        });
        Report("re-record after moving one soft shadowed object, 200 on the page", ms, 25);
        Assert.Equal(200, r.SoftMasksMade);                                      // nothing was blurred again: the speed is not luck
        Assert.True(ms < 25, $"re-recording after a move took {ms:0.0} ms");     // a cache that is lost makes this 200 blurs, many times over
    }

    [Fact]
    public void A_software_redraw_of_a_page_of_soft_shadows_stays_under_budget()
    {
        var (doc, _) = Page(200);
        using var r = new SceneRenderer(doc) { ViewScale = 0.2 };
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
        Report("software redraw, 200 soft shadows", ms, 150);
        Assert.True(ms < 150, $"a software redraw took {ms:0} ms");
    }

    [Fact]
    public void Changing_the_softness_in_the_tool_makes_one_mask_and_stays_interactive()
    {
        var session = new EditorSession(new Document(PageSetup.FromInches(48, 24)));
        var src = new RectangleObject(Units.FromInches(30), Units.FromInches(10)) { Transform = Affine2.Translation(100, 100), Style = ObjectStyle.FillOnly(DocColor.Black) };   // a big sign
        session.History.Execute(new AddObjectCommand(session.Document.ActiveLayer, src));
        session.Selection.Set(src);
        session.BeginSoftEffect(SoftKind.SoftShadow);
        var tool = session.GetTool<SoftEffectTool>();
        using var r = new SceneRenderer(session.Document) { ViewScale = 0.1 };
        r.GetDocumentPicture(null).Release();
        int made = r.SoftMasksMade;
        double softness = 0.5;
        double ms = Best(8, () =>
        {
            softness += 0.05;
            return Time(() =>
            {
                tool.Update(s => s with { Softness = softness });               // a field changed ...
                r.GetDocumentPicture(null).Release();                           // ... and the page is drawn again
            });
        });
        Report("one soft shadow field edit and redraw of a 30 x 10 inch sign", ms, 30);
        Assert.Equal(made + 8, r.SoftMasksMade);                                 // one new mask per edit, nothing more
        Assert.True(ms < 30, $"a field edit and redraw took {ms:0.0} ms");
    }

    [Fact]
    public void A_very_soft_shadow_of_a_huge_object_is_a_small_mask()
    {
        var doc = new Document(PageSetup.FromInches(200, 100));
        var big = new RectangleObject(Units.FromInches(190), Units.FromInches(90)) { Transform = Affine2.Translation(100, 100) };
        big.Style = new ObjectStyle(new SolidPaint(DocColor.Black), null, new BlurStyle(SoftKind.SoftShadow, 0.5, 5, big.Height));   // the largest softness the bar allows
        doc.Insert(doc.ActiveLayer, -1, big);
        using var r = new SceneRenderer(doc) { ViewScale = 1 };
        double ms = Best(3, () =>
        {
            using var fresh = new SceneRenderer(doc) { ViewScale = 1 };
            return Time(() => fresh.GetDocumentPicture(null).Release());
        });
        Report("first record of a 190 x 90 inch shape at softness 500 %", ms, 100);
        Assert.True(ms < 100, $"the mask of a huge blur took {ms:0} ms");   // the bitmap is as wide as the blur needs, not as the shape is
        _ = r;
    }

    [Fact]
    public void A_page_of_feathered_objects_makes_each_mask_once()
    {
        var (doc, _) = Page(200, feather: true);
        var s = new EditorSession(doc);
        var plain = new RectangleObject(50, 50) { Style = ObjectStyle.FillOnly(DocColor.Black) };   // an edit somewhere else on the page
        s.History.Execute(new AddObjectCommand(doc.ActiveLayer, plain));
        using var r = new SceneRenderer(doc) { ViewScale = 0.2 };
        double first = Time(() => r.GetDocumentPicture(null).Release());
        Assert.Equal(200, r.SoftMasksMade);
        double again = Best(6, () =>
        {
            s.History.Execute(TransformObjectsCommand.ApplyWorld("Move", [plain], Affine2.Translation(1, 0)));
            return Time(() => r.GetDocumentPicture(null).Release());
        });
        Report("first record, 200 feathered objects", first, 250);
        Report("re-record after an edit, 200 feathered objects", again, 25);
        Assert.Equal(200, r.SoftMasksMade);
        Assert.True(first < 250, $"recording 200 feathered objects took {first:0} ms");
        Assert.True(again < 25, $"re-recording after an edit took {again:0.0} ms");
    }
}
