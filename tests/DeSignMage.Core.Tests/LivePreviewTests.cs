using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Geometry;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>R31: live previews of Outline/Inline while dragging or typing: newest request wins, the quick offset agrees with the exact one.</summary>
public class LivePreviewTests
{
    // ---- LatestWinsRunner ----

    [Fact]
    public async Task Only_the_newest_waiting_request_is_computed_and_every_finished_one_is_delivered()
    {
        var runner = new LatestWinsRunner();
        var gate = new ManualResetEventSlim();
        var started = new ManualResetEventSlim();
        var delivered = new List<int>();
        var ran = new List<int>();
        runner.Submit(_ => { ran.Add(1); started.Set(); gate.Wait(); return 1; }, delivered.Add);
        Assert.True(started.Wait(5000));
        for (int i = 2; i <= 6; i++)
        {
            int n = i;
            runner.Submit(_ => { ran.Add(n); return n; }, delivered.Add);   // 2..5 are replaced while 1 still runs
        }
        Assert.True(runner.IsBusy);
        gate.Set();
        await runner.Idle;
        Assert.Equal([1, 6], ran);
        Assert.Equal([1, 6], delivered);
        Assert.Equal(4, runner.Skipped);
        Assert.Equal(2, runner.Computed);
        Assert.False(runner.IsBusy);
    }

    [Fact]
    public async Task Cancel_drops_what_is_waiting_and_what_is_running()
    {
        var runner = new LatestWinsRunner();
        var gate = new ManualResetEventSlim();
        var started = new ManualResetEventSlim();
        var delivered = new List<int>();
        CancellationToken seen = default;
        runner.Submit(ct => { seen = ct; started.Set(); gate.Wait(); return 1; }, delivered.Add);
        Assert.True(started.Wait(5000));
        runner.Submit(_ => 2, delivered.Add);
        runner.Cancel();
        Assert.True(seen.IsCancellationRequested);
        gate.Set();
        await runner.Idle;
        Assert.Empty(delivered);

        runner.Submit(_ => 3, delivered.Add);   // works again afterwards
        await runner.Idle;
        Assert.Equal([3], delivered);
    }

    [Fact]
    public async Task Results_are_handed_to_the_ui_thread_poster_and_failures_are_reported()
    {
        var posted = new List<string>();
        var runner = new LatestWinsRunner(a => { posted.Add("post"); a(); });
        string? failure = null;
        runner.Submit<int>(_ => throw new InvalidOperationException("boom"), _ => { }, e => failure = e.Message);
        await runner.Idle;
        Assert.Equal("boom", failure);
        Assert.Single(posted);
    }

    // ---- The quick offset ----

    private static PathGeometry Square(double x, double y, double size) =>
        new PathBuilder().MoveTo(x, y).LineTo(x + size, y).LineTo(x + size, y + size).LineTo(x, y + size).Close().Build();

    private static PathGeometry Ring(double x, double y, double size, double hole) =>
        EffectGeometry.Concat([Square(x, y, size), Square(x + hole, y + hole, size - 2 * hole)], FillRule.EvenOdd);

    private static void AssertClose(Rect2 a, Rect2 b, double tol)
    {
        Assert.InRange(a.Left, b.Left - tol, b.Left + tol);
        Assert.InRange(a.Top, b.Top - tol, b.Top + tol);
        Assert.InRange(a.Right, b.Right - tol, b.Right + tol);
        Assert.InRange(a.Bottom, b.Bottom - tol, b.Bottom + tol);
    }

    private static double Area(PathGeometry g) => Math.Abs(g.Flatten(0.1).Where(p => p.Points.Length >= 3).Sum(p => SignedArea(p.Points)));   // outlines and holes run opposite ways

    private static double SignedArea(Vec2[] p)
    {
        double a = 0;
        for (int i = 0; i < p.Length; i++) { var q = p[(i + 1) % p.Length]; a += p[i].X * q.Y - q.X * p[i].Y; }
        return a / 2;
    }

    [Theory]
    [InlineData(CornerStyle.Miter)]
    [InlineData(CornerStyle.Round)]
    [InlineData(CornerStyle.Bevel)]
    public void The_quick_offset_has_the_exact_offsets_bounds(CornerStyle corners)
    {
        var shape = EffectGeometry.Concat([Square(100, 100, 80), new PathBuilder().MoveTo(300, 300).LineTo(380, 300).LineTo(340, 380).Close().Build()], FillRule.NonZero);
        var source = GeometryEngines.OffsetPreview.Prepare(shape, 0.25, false)!;
        Assert.NotNull(source);
        var options = new OffsetOptions { Corners = corners, OutsideAngleThreshold = 30 };
        foreach (double d in new[] { 6.0, -6.0 })
        {
            var quick = source.Offset(d, 0, corners, 30);
            var exact = GeometryEngines.Offsets.Offset(shape, d, options);
            AssertClose(quick.GetBounds(), exact.GetBounds(), 0.6);
        }
    }

    [Fact]
    public void A_second_amount_gives_the_band_between_the_two_offsets()
    {
        var shape = Square(100, 100, 100);
        var source = GeometryEngines.OffsetPreview.Prepare(shape, 0.25, false)!;
        var band = source.Offset(5, 10, CornerStyle.Miter, 30);
        AssertClose(band.GetBounds(), new Rect2(85, 85, 215, 215), 0.6);   // the band from 5 out to 15 out
        Assert.Equal(130 * 130 - 110 * 110, Area(band), 0);                // a ring 10 wide (corners squared by the miter)
    }

    [Fact]
    public void Contour_only_ignores_the_holes_and_otherwise_they_shrink()
    {
        var ring = Ring(100, 100, 100, 30);   // hole 40 wide
        var withHoles = GeometryEngines.OffsetPreview.Prepare(ring, 0.25, false)!.Offset(10, 0, CornerStyle.Miter, 30);
        Assert.Equal(120 * 120 - 20 * 20, Area(withHoles), 0);   // the hole closes in to 20 wide
        var contour = GeometryEngines.OffsetPreview.Prepare(ring, 0.25, true)!.Offset(10, 0, CornerStyle.Miter, 30);
        Assert.Equal(120 * 120, Area(contour), 0);
    }

    [Fact]
    public void Shapes_with_open_paths_are_not_prepared()
    {
        var line = new PathBuilder().MoveTo(0, 0).LineTo(100, 50).Build();
        Assert.Null(GeometryEngines.OffsetPreview.Prepare(line, 0.25, false));
    }

    [Fact]
    public void A_shape_with_closed_and_open_paths_previews_the_area_quickly_and_the_line_exactly()
    {
        var mixed = EffectGeometry.Concat([Square(100, 100, 50), new PathBuilder().MoveTo(400, 100).LineTo(500, 100).Build()], FillRule.NonZero);
        var doc = NewDoc();
        var s = new EditorSession(doc) { BooleanService = GeometryEngines.Booleans, OffsetService = GeometryEngines.Offsets, OffsetPreviewService = GeometryEngines.OffsetPreview };
        var path = new PathObject(mixed) { Style = ObjectStyle.FillOnly(DocColor.Black) };
        s.History.Execute(new AddObjectCommand(doc.ActiveLayer, path));
        s.Selection.Set(path);
        var snap = OutlineEffect.Snapshot(s)!;
        var prepared = OutlineEffect.PrepareQuick(GeometryEngines.OffsetPreview, snap, 0.25, false);
        var settings = new OutlineSettings { First = 8 };
        var quick = OutlineEffect.ComputeQuick(prepared, GeometryEngines.Offsets, GeometryEngines.Booleans, OutlineKind.Outline, settings);
        var exact = OutlineEffect.Compute(GeometryEngines.Offsets, GeometryEngines.Booleans, snap, OutlineKind.Outline, settings).Select(x => x.Result).ToList();
        Assert.Single(quick);
        AssertClose(quick[0].GetBounds(), exact[0].GetBounds(), 0.6);   // the square grown AND the parallel copy of the line
    }

    // ---- The tool ----

    private static (EditorSession S, OutlineTool Tool) ToolOn(params DocObject[] objects)
    {
        var s = new EditorSession(NewDoc()) { BooleanService = GeometryEngines.Booleans, OffsetService = GeometryEngines.Offsets, OffsetPreviewService = GeometryEngines.OffsetPreview };
        s.Viewport.SetViewSize(new Vec2(1000, 800));
        s.Viewport.Set(1, Vec2.Zero);
        foreach (var o in objects) s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, o));
        s.Selection.Set(objects);
        Assert.True(s.BeginOutline(OutlineKind.Outline));
        return (s, s.GetTool<OutlineTool>());
    }

    [Fact]
    public async Task Typing_amounts_previews_the_newest_without_an_undo_step()
    {
        var (s, tool) = ToolOn(Rect(200, 200, 100, 100));
        int steps = s.History.UndoList.Count;
        for (int i = 1; i <= 20; i++) tool.Update(x => x with { First = i });
        await tool.PreviewSettled;
        var bounds = tool.PreviewPaths.Aggregate(Rect2.Empty, (r, g) => r.Union(g.GetBounds()));
        AssertClose(bounds, new Rect2(180, 180, 320, 320), 0.6);   // the last amount, 20
        Assert.Equal(steps, s.History.UndoList.Count);
        Assert.True(tool.PreviewCounts.Computed < 20);
    }

    [Fact]
    public async Task The_preview_after_a_drag_matches_what_Done_makes()
    {
        var (s, tool) = ToolOn(new EllipseObject(120, 120) { Transform = Affine2.Translation(200, 200), Style = ObjectStyle.FillOnly(DocColor.Black) });
        tool.Update(x => x with { First = 12 });
        await tool.PreviewSettled;
        var preview = tool.PreviewPaths.Aggregate(Rect2.Empty, (r, g) => r.Union(g.GetBounds()));
        Assert.True(await tool.Apply());
        var made = s.Selection.Items[0].GetWorldBounds();
        AssertClose(preview, made, 0.6);
    }

    [Fact]
    public async Task Changing_the_selection_re_prepares_the_preview()
    {
        var (s, tool) = ToolOn(Rect(100, 100, 50, 50));
        tool.Update(x => x with { First = 5 });
        await tool.PreviewSettled;
        var b = new EllipseObject(40, 40) { Transform = Affine2.Translation(500, 500), Style = ObjectStyle.FillOnly(DocColor.Black) };
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, b));
        s.Selection.Set(b);
        await tool.PreviewSettled;
        var bounds = tool.PreviewPaths.Aggregate(Rect2.Empty, (r, g) => r.Union(g.GetBounds()));
        AssertClose(bounds, new Rect2(495, 495, 545, 545), 0.6);
    }
}
