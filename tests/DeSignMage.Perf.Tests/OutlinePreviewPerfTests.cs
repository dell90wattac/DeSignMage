using System.Diagnostics;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Lyt;
using DeSignMage.Geometry;
using DeSignMage.Rendering;
using DeSignMage.TestSupport;
using SkiaSharp;
using Xunit.Abstractions;

namespace DeSignMage.Perf.Tests;

/// <summary>
/// R31: dragging the Outline/Inline amount must follow the pointer. The UI thread only probes the distance and records the overlay (the offset itself
/// runs on a worker, newest request wins), and one preview must be quick enough to be worth drawing. Measured in Debug on the dev PC (Release is about
/// the same here: the time is in the geometry engine); budgets leave room for the old PC.
/// </summary>
[Collection("perf")]
public class OutlinePreviewPerfTests(ITestOutputHelper output)
{
    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static EditorSession NewSession(Document doc)
    {
        DeSignMage.Text.SkiaFontService.Install();
        var s = new EditorSession(doc)
        {
            BooleanService = GeometryEngines.Booleans,
            OffsetService = GeometryEngines.Offsets,
            OffsetPreviewService = GeometryEngines.OffsetPreview,
        };
        s.Viewport.SetViewSize(new Vec2(1600, 900));
        return s;
    }

    /// <summary>Several lines of big lettering in one group: a few thousand curve verbs, as in a real sign.</summary>
    private static EditorSession SyntheticLettering()
    {
        var s = NewSession(new Document(PageSetup.FromInches(24, 12)));
        var group = new List<DocObject>();
        string[] lines = ["THE QUICK BROWN FOX", "JUMPS OVER THE LAZY DOG", "Sphinx of black quartz, judge my vow", "0123456789 &@%$ SIGNS 2026"];
        for (int i = 0; i < lines.Length; i++)
        {
            var t = new TextObject(lines[i], i % 2 == 0 ? "Arial" : "Times New Roman", 120) { Transform = Affine2.Translation(100, 200 + i * 160), Style = ObjectStyle.FillOnly(DocColor.Black) };
            s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, t));
            group.Add(t);
        }
        s.Selection.Set(group);
        s.ZoomToSelection();
        return s;
    }

    private static EditorSession Local(string file, int rank)
    {
        DeSignMage.Text.SkiaFontService.Install();
        var doc = LytImporter.Open(LocalData.Layout(file)).Document;
        var s = NewSession(doc);
        var o = doc.AllObjects().Where(x => x is not GroupObject && x.GetLocalGeometry() is { } g && g.Verbs.Count > 100)
            .OrderByDescending(x => x.GetLocalGeometry()!.Verbs.Count).ElementAt(rank);
        s.Selection.Set(o.TopLevel);
        s.ZoomToSelection();
        return s;
    }

    private (double Prepare, double Quick, double Exact) Offsets(EditorSession s, string name, bool measureExact)
    {
        var snap = OutlineEffect.Snapshot(s)!;
        double h = snap.Height;
        var settings = new OutlineSettings { First = h * 0.03 };
        double tol = OutlineEffect.PreviewTolerance(s.Viewport.Zoom);
        long t = Stopwatch.GetTimestamp();
        var prepared = OutlineEffect.PrepareQuick(GeometryEngines.OffsetPreview, snap, tol, false);
        double prep = Ms(Stopwatch.GetTimestamp() - t);
        var times = new List<double>();
        for (int i = 0; i < 12; i++)
        {
            var st = settings with { First = h * (0.01 + 0.004 * i) };   // the amount changes with every pointer move
            t = Stopwatch.GetTimestamp();
            var r = OutlineEffect.ComputeQuick(prepared, GeometryEngines.Offsets, GeometryEngines.Booleans, OutlineKind.Outline, st);
            times.Add(Ms(Stopwatch.GetTimestamp() - t));
            Assert.NotEmpty(r);
        }
        times.Sort();
        double exact = 0;
        if (measureExact)
        {
            t = Stopwatch.GetTimestamp();
            OutlineEffect.Compute(GeometryEngines.Offsets, GeometryEngines.Booleans, snap, OutlineKind.Outline, settings);
            exact = Ms(Stopwatch.GetTimestamp() - t);
        }
        output.WriteLine($"{name}: {snap.Geometries.Sum(g => g.Verbs.Count)} verbs, tol {tol:0.00}: prepare {prep:0.0} ms, quick update median {times[times.Count / 2]:0.0} ms (worst {times[^1]:0.0}), exact {exact:0} ms");
        return (prep, times[times.Count / 2], exact);
    }

    [Fact]
    public void A_quick_preview_of_big_lettering_is_much_faster_than_the_exact_outline()
    {
        var s = SyntheticLettering();
        var (prepare, quick, exact) = Offsets(s, "synthetic lettering", measureExact: true);
        Assert.True(quick < 16, $"one preview update took {quick:0.0} ms");
        Assert.True(prepare < 200, $"preparing the shapes took {prepare:0} ms");
        Assert.True(exact > quick * 8, $"exact {exact:0} ms against quick {quick:0.0} ms: the quick pass is not doing its job");
    }

    [LocalTheory("layouts/art-d.lyt")]
    [InlineData("art-d.lyt", 0, 25)]   // a logo of 11,000 verbs: 7-18 ms, the exact outline takes 6-10 s
    [InlineData("art-d.lyt", 2, 16)]
    [InlineData("art-a.lyt", 0, 100)]   // 221 letters in one logo, grown by 1-5 % of its height: 60-70 ms, the exact outline takes 2-3 s
    [InlineData("sample-c.lyt", 1, 16)]
    [InlineData("sample-c.lyt", 2, 16)]
    public void Quick_previews_of_real_artwork_stay_within_budget(string file, int rank, double budget)
    {
        var (prepare, quick, _) = Offsets(Local(file, rank), $"{file} #{rank}", measureExact: false);
        Assert.True(quick < budget, $"one preview update took {quick:0.0} ms");
        Assert.True(prepare < 250, $"preparing the shapes took {prepare:0} ms");
    }

    private (double WorstUi, double WorstDraw, double Settle, int Moves, int Computed, int Skipped) DragOutward(EditorSession s, string name)
    {
        var tool = s.GetTool<OutlineTool>();
        Assert.True(s.BeginOutline(OutlineKind.Outline));
        var frames = new CanvasFrameBuilder(s) { Theme = CanvasTheme.Light };
        using var surface = SKSurface.Create(new SKImageInfo(1600, 900));
        var b = s.Selection.GetBounds();
        var start = new Vec2(b.Right + 1, (b.Top + b.Bottom) / 2);
        double step = 1.5 / s.Viewport.Zoom;   // one and a half screen pixels per move
        PointerInput At(Vec2 d) => new(s.Viewport.ToScreen(d), d, PointerButton.Left, ModifierKeys.None);

        s.PointerDown(At(start));
        var costs = new List<double>();
        var draws = new List<double>();
        const int moves = 40;
        Vec2 pos = start;
        for (int k = 1; k <= moves; k++)
        {
            pos = start + new Vec2(step * k, 0);
            long t0 = Stopwatch.GetTimestamp();
            s.PointerMove(At(pos));
            using var f = frames.Build(1600, 900);
            double ui = Ms(Stopwatch.GetTimestamp() - t0);
            long t1 = Stopwatch.GetTimestamp();
            f.Draw(surface.Canvas);
            double draw = Ms(Stopwatch.GetTimestamp() - t1);
            costs.Add(ui);
            draws.Add(draw);
        }
        long tLast = Stopwatch.GetTimestamp();
        s.PointerUp(At(pos));
        tool.PreviewSettled.Wait(TimeSpan.FromSeconds(60));
        double settle = Ms(Stopwatch.GetTimestamp() - tLast);
        Assert.NotEmpty(tool.PreviewPaths);
        Assert.True(tool.Settings.First > step * moves * 0.9, $"the amount is {tool.Settings.First:0.0}");   // the amount followed the pointer
        var counts = tool.PreviewCounts;
        double worstUi = costs.Skip(1).Max(), worstDraw = draws.Skip(1).Max();   // the first move prepares the probe's data
        output.WriteLine($"{name}: drag of {moves} moves: UI thread worst {worstUi:0.0} ms (median {costs.OrderBy(x => x).ElementAt(moves / 2):0.0}), drawing worst {worstDraw:0.0} ms, preview ready {settle:0.0} ms after the last move; {counts.Computed} previews computed, {counts.Skipped} requests dropped");
        s.SetTool("select");
        return (worstUi, worstDraw, settle, moves, counts.Computed, counts.Skipped);
    }

    [Fact]
    public void Dragging_an_outline_over_big_lettering_follows_the_pointer()
    {
        var s = SyntheticLettering();
        var (ui, draw, settle, moves, computed, _) = DragOutward(s, "synthetic lettering");
        Assert.True(ui < 6, $"a drag move cost the UI thread {ui:0.0} ms");
        Assert.True(draw < 16, $"drawing a drag frame took {draw:0.0} ms");
        Assert.True(settle < 120, $"the last preview took {settle:0} ms to arrive after the last move");
        Assert.True(computed <= moves, $"{computed} previews for {moves} moves");
    }

    [LocalTheory("layouts/art-d.lyt")]
    [InlineData("art-d.lyt", 0)]
    [InlineData("art-a.lyt", 0)]
    [InlineData("sample-c.lyt", 1)]
    public void Dragging_an_outline_over_real_artwork_follows_the_pointer(string file, int rank)
    {
        var s = Local(file, rank);
        var (ui, draw, settle, _, _, _) = DragOutward(s, $"{file} #{rank}");
        Assert.True(ui < 8, $"a drag move cost the UI thread {ui:0.0} ms");
        Assert.True(draw < 20, $"drawing a drag frame took {draw:0.0} ms");
        Assert.True(settle < 300, $"the last preview took {settle:0} ms to arrive after the last move");
    }

    [Fact]
    public async Task Typed_amounts_are_computed_newest_first_and_never_pile_up()
    {
        var s = SyntheticLettering();
        var tool = s.GetTool<OutlineTool>();
        Assert.True(s.BeginOutline(OutlineKind.Outline));
        long t0 = Stopwatch.GetTimestamp();
        for (int i = 1; i <= 50; i++) tool.Update(x => x with { First = i });   // 50 changes without waiting
        double ask = Ms(Stopwatch.GetTimestamp() - t0);
        await tool.PreviewSettled;
        double all = Ms(Stopwatch.GetTimestamp() - t0);
        var (computed, skipped) = tool.PreviewCounts;
        output.WriteLine($"50 amounts asked in {ask:0.0} ms, all settled after {all:0} ms; {computed} computed, {skipped} dropped");
        Assert.True(ask < 50, $"asking for 50 previews took {ask:0} ms of the caller's time");
        Assert.True(skipped > 0, "requests were not replaced by newer ones");
        Assert.True(computed < 50);
        Assert.NotEmpty(tool.PreviewPaths);
        s.SetTool("select");
    }
}
