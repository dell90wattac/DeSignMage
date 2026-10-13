using System.Xml.Linq;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Vectorize;

namespace DeSignMage.Vectorize.Tests;

public class QuantizerTests
{
    [Fact]
    public void Flat_image_with_three_colors_gets_exactly_those_colors()
    {
        var img = TestImages.Canvas(60, 40, TestImages.White);
        TestImages.FillRect(img, 0, 0, 20, 40, TestImages.Red);
        TestImages.FillRect(img, 20, 0, 40, 40, TestImages.Blue);

        var q = Quantizer.Quantize(img, 16);

        Assert.Equal(3, q.Palette.Length);   // 16 allowed, but nothing more to separate
        foreach (var (r, g, b) in new[] { TestImages.Red, TestImages.Blue, TestImages.White })
            Assert.Contains(q.Palette, c => TestImages.Distance((c.R, c.G, c.B), (r, g, b)) < 4);
        // Same color, same index; different colors, different indices.
        Assert.Equal(q.Index[5], q.Index[19]);
        Assert.NotEqual(q.Index[5], q.Index[25]);
        Assert.NotEqual(q.Index[25], q.Index[45]);
    }

    [Fact]
    public void Palette_never_exceeds_the_requested_count()
    {
        var img = TestImages.Canvas(64, 64, TestImages.White);
        var rng = new Random(3);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++) img.SetPixel(x, y, (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));

        foreach (int k in new[] { 2, 5, 16, 40 })
            Assert.True(Quantizer.Quantize(img, k).Palette.Length <= k);
    }

    [Fact]
    public void Noisy_flat_colors_still_reduce_to_their_base_colors()
    {
        var img = TestImages.Canvas(64, 64, TestImages.Green);
        TestImages.FillRect(img, 0, 0, 32, 64, TestImages.Blue);
        var rng = new Random(11);
        for (int i = 0; i < img.Rgba.Length; i += 4)
            for (int c = 0; c < 3; c++) img.Rgba[i + c] = (byte)Math.Clamp(img.Rgba[i + c] + rng.Next(-6, 7), 0, 255);

        var q = Quantizer.Quantize(img, 8);
        Assert.InRange(q.Palette.Length, 2, 4);
        Assert.Contains(q.Palette, c => TestImages.Distance((c.R, c.G, c.B), TestImages.Blue) < 12);
        Assert.Contains(q.Palette, c => TestImages.Distance((c.R, c.G, c.B), TestImages.Green) < 12);
    }

    [Fact]
    public void Transparent_pixels_get_no_color()
    {
        var img = RasterImage.Solid(8, 8, 255, 0, 0, 0);
        img.SetPixel(3, 3, 0, 0, 255);
        var q = Quantizer.Quantize(img, 4);
        Assert.Equal(-1, q.Index[0]);
        Assert.True(q.Index[3 * 8 + 3] >= 0);
    }

    [Fact]
    public void Otsu_splits_a_two_level_histogram_between_the_levels()
    {
        var hist = new int[256];
        hist[40] = 500; hist[200] = 500;
        int t = Vectorizer.Otsu(hist, 1000);
        Assert.InRange(t, 41, 200);
    }
}

public class TraceTests
{
    [Theory]
    [InlineData(VectorizeLayering.Stacked)]
    [InlineData(VectorizeLayering.CutOut)]
    public void Square_and_disc_on_white_make_three_shapes(VectorizeLayering layering)
    {
        var result = Vectorizer.Trace(TestImages.SquareAndDisc(), TestImages.Small(new VectorizeOptions { Layering = layering }));

        Assert.Equal(3, result.Shapes.Count);
        Assert.All(result.Shapes, s => { Assert.NotNull(s.Fill); Assert.Null(s.Stroke); });
        Assert.Contains(result.Shapes, s => TestImages.Distance((s.Fill!.Value.R, s.Fill.Value.G, s.Fill.Value.B), TestImages.Red) < 10);
        Assert.Contains(result.Shapes, s => TestImages.Distance((s.Fill!.Value.R, s.Fill.Value.G, s.Fill.Value.B), TestImages.Blue) < 10);
        Assert.Contains(result.Shapes, s => TestImages.Distance((s.Fill!.Value.R, s.Fill.Value.G, s.Fill.Value.B), TestImages.White) < 10);
    }

    [Theory]
    [InlineData(VectorizeLayering.Stacked)]
    [InlineData(VectorizeLayering.CutOut)]
    public void Traced_picture_reproduces_the_source_and_leaves_no_gaps(VectorizeLayering layering)
    {
        var src = TestImages.SquareAndDisc();
        var result = Vectorizer.Trace(src, TestImages.Small(new VectorizeOptions { Layering = layering }));

        var (match, uncovered) = TestImages.Compare(src, result);
        Assert.True(match > 0.99, $"only {match:P1} of the flat areas have the right color");
        Assert.Equal(0, uncovered);
    }

    [Fact]
    public void Stacked_parts_extend_under_their_neighbors_so_the_background_is_a_plain_rectangle()
    {
        var result = Vectorizer.Trace(TestImages.SquareAndDisc(), TestImages.Small());
        var bottom = result.Shapes[0];

        // The background reaches under the square and the disc: one outline, no holes.
        Assert.Equal(1, TestImages.Subpaths(bottom.Path));
        var b = bottom.Path.GetBounds();
        Assert.InRange(b.Width, 95.5, 96.5);
        Assert.InRange(b.Height, 95.5, 96.5);
    }

    [Fact]
    public void Cut_out_parts_do_not_overlap_and_the_background_has_holes()
    {
        var result = Vectorizer.Trace(TestImages.SquareAndDisc(), TestImages.Small(new VectorizeOptions { Layering = VectorizeLayering.CutOut }));
        var background = result.Shapes.Single(s => s.Fill!.Value.R > 240);

        Assert.Equal(3, TestImages.Subpaths(background.Path));   // outer edge + square + disc
        // Pixel centers deep inside the red square belong to exactly one shape.
        var p = new Vec2(28.5, 28.5);
        Assert.Single(result.Shapes, s => s.Path.Contains(p, 0.05));
    }

    [Fact]
    public void A_ring_has_a_hole_and_what_is_inside_it_is_its_own_shape()
    {
        var img = TestImages.Canvas(96, 96, TestImages.White);
        TestImages.FillCircle(img, 48, 48, 36, TestImages.Black);
        TestImages.FillCircle(img, 48, 48, 20, TestImages.White);
        TestImages.FillCircle(img, 48, 48, 8, TestImages.Red);

        var result = Vectorizer.Trace(img, TestImages.Small(new VectorizeOptions { Layering = VectorizeLayering.CutOut }));

        Assert.Equal(4, result.Shapes.Count);   // background, ring, inner white disc, red dot
        var ring = result.Shapes.Single(s => s.Fill!.Value.R < 30 && s.Fill.Value.G < 30);
        Assert.Equal(2, TestImages.Subpaths(ring.Path));
        Assert.False(ring.Path.Contains(new Vec2(48.5, 48.5), 0.05));      // hole
        Assert.True(ring.Path.Contains(new Vec2(48.5, 48.5 - 28), 0.05));  // band
    }

    [Theory]
    [InlineData(VectorizeLayering.Stacked)]
    [InlineData(VectorizeLayering.CutOut)]
    public void Every_subpath_is_closed(VectorizeLayering layering)
    {
        var img = TestImages.SquareAndDisc();
        TestImages.FillCircle(img, 66, 62, 9, TestImages.Green);
        var result = Vectorizer.Trace(img, TestImages.Small(new VectorizeOptions { Layering = layering }));

        foreach (var s in result.Shapes) AssertClosed(s.Path);
    }

    [Fact]
    public void Speckles_are_merged_into_their_surroundings()
    {
        var img = TestImages.Canvas(64, 64, TestImages.White);
        TestImages.FillRect(img, 20, 20, 44, 44, TestImages.Blue);
        TestImages.FillRect(img, 5, 5, 8, 8, TestImages.Red);      // 9 px
        TestImages.FillRect(img, 50, 10, 53, 13, TestImages.Black);   // 9 px

        var noisy = Vectorizer.Trace(img, TestImages.Small(new VectorizeOptions { SpeckleSize = 0 }));
        var clean = Vectorizer.Trace(img, TestImages.Small(new VectorizeOptions { SpeckleSize = 12 }));

        Assert.Equal(4, noisy.Shapes.Count);
        Assert.Equal(2, clean.Shapes.Count);
    }

    [Fact]
    public void Diagonal_neighbors_stay_separate_shapes_and_loops_do_not_cross()
    {
        // 2x2 checkerboard of 16 px cells: the two black cells touch only at a corner.
        var img = TestImages.Canvas(32, 32, TestImages.White);
        TestImages.FillRect(img, 0, 0, 16, 16, TestImages.Black);
        TestImages.FillRect(img, 16, 16, 32, 32, TestImages.Black);

        var result = Vectorizer.Trace(img, TestImages.Small(new VectorizeOptions { Layering = VectorizeLayering.CutOut, SpeckleSize = 0, Colors = 2 }));

        Assert.Equal(4, result.Shapes.Count);   // two black squares, two white squares
        Assert.All(result.Shapes, s => { Assert.Equal(1, TestImages.Subpaths(s.Path)); AssertClosed(s.Path); });
        Assert.All(result.Shapes, s => Assert.InRange(s.Path.GetBounds().Width, 15.5, 16.5));
    }

    [Fact]
    public void Straight_edges_and_sharp_corners_stay_straight_and_sharp()
    {
        var img = TestImages.Canvas(120, 80, TestImages.White);
        TestImages.FillRect(img, 20, 15, 100, 65, TestImages.Blue);

        var result = Vectorizer.Trace(img, new VectorizeOptions { Layering = VectorizeLayering.CutOut });   // default: enlarged 1000 px

        var blue = result.Shapes.Single(s => s.Fill!.Value.B > 150 && s.Fill.Value.R < 80);
        Assert.Equal(1, TestImages.Subpaths(blue.Path));
        Assert.All(blue.Path.Verbs.Where(v => v != PathVerb.MoveTo && v != PathVerb.Close), v => Assert.Equal(PathVerb.LineTo, v));
        var b = blue.Path.GetBounds();
        Assert.InRange(b.Left, 19.6, 20.4); Assert.InRange(b.Top, 14.6, 15.4);
        Assert.InRange(b.Right, 99.6, 100.4); Assert.InRange(b.Bottom, 64.6, 65.4);
    }

    [Fact]
    public void A_circle_is_a_few_smooth_curves()
    {
        var img = TestImages.Canvas(120, 120, TestImages.White);
        TestImages.FillCircle(img, 60, 60, 40, TestImages.Red);

        var result = Vectorizer.Trace(img, new VectorizeOptions { Layering = VectorizeLayering.CutOut });

        var disc = result.Shapes.Single(s => s.Fill!.Value.R < 240 && s.Fill.Value.G < 100);
        Assert.InRange(disc.Path.Verbs.Count(v => v == PathVerb.CubicTo), 2, 24);
        // All outline points stay within half a pixel of the true circle.
        double worst = 0;
        foreach (var poly in disc.Path.Flatten(0.1))
            foreach (var p in poly.Points) worst = Math.Max(worst, Math.Abs(Vec2.Distance(p, new Vec2(60, 60)) - 40));
        Assert.True(worst < 0.8, $"circle error {worst:0.00} px");
    }

    [Fact]
    public void Black_and_white_mode_makes_only_the_dark_shapes()
    {
        var img = TestImages.Canvas(96, 96, TestImages.White);
        TestImages.FillRect(img, 10, 10, 40, 40, TestImages.Black);
        TestImages.FillCircle(img, 66, 62, 20, TestImages.Black);
        TestImages.FillCircle(img, 66, 62, 8, TestImages.White);

        var result = Vectorizer.Trace(img, TestImages.Small(new VectorizeOptions { Mode = VectorizeMode.BlackAndWhite }));

        Assert.Equal(2, result.Shapes.Count);   // square, and the disc with its hole
        Assert.All(result.Shapes, s => Assert.Equal(DocColor.Black, s.Fill));
        Assert.Contains(result.Shapes, s => TestImages.Subpaths(s.Path) == 2);
    }

    [Fact]
    public void Black_and_white_threshold_decides_what_is_dark()
    {
        var img = TestImages.Canvas(64, 64, TestImages.White);
        TestImages.FillRect(img, 8, 8, 56, 56, (140, 140, 140));

        var low = Vectorizer.Trace(img, TestImages.Small(new VectorizeOptions { Mode = VectorizeMode.BlackAndWhite, Threshold = 100 }));
        var high = Vectorizer.Trace(img, TestImages.Small(new VectorizeOptions { Mode = VectorizeMode.BlackAndWhite, Threshold = 200 }));

        Assert.Empty(low.Shapes);
        Assert.Single(high.Shapes);
    }

    [Fact]
    public void Outline_mode_has_strokes_and_no_fills()
    {
        var result = Vectorizer.Trace(TestImages.SquareAndDisc(), TestImages.Small(new VectorizeOptions { Mode = VectorizeMode.Outline }));

        Assert.NotEmpty(result.Shapes);
        Assert.All(result.Shapes, s => { Assert.Null(s.Fill); Assert.NotNull(s.Stroke); });
    }

    [Fact]
    public void Omit_background_drops_the_region_behind_everything()
    {
        var src = TestImages.SquareAndDisc();
        var with = Vectorizer.Trace(src, TestImages.Small());
        var without = Vectorizer.Trace(src, TestImages.Small(new VectorizeOptions { OmitBackground = true }));

        Assert.Equal(with.Shapes.Count - 1, without.Shapes.Count);
        Assert.DoesNotContain(without.Shapes, s => s.Fill!.Value.R > 240 && s.Fill.Value.G > 240);
    }

    [Fact]
    public void Transparent_areas_are_left_empty()
    {
        var img = RasterImage.Solid(64, 64, 0, 0, 0, 0);
        TestImages.FillRect(img, 16, 16, 48, 48, TestImages.Red);
        for (int y = 16; y < 48; y++) for (int x = 16; x < 48; x++) img.SetPixel(x, y, 220, 30, 30, 255);

        var result = Vectorizer.Trace(img, TestImages.Small());

        var shape = Assert.Single(result.Shapes);
        Assert.False(shape.Path.Contains(new Vec2(4.5, 4.5), 0.05));
        Assert.True(shape.Path.Contains(new Vec2(32.5, 32.5), 0.05));
    }

    [Fact]
    public void More_colors_means_more_detail()
    {
        var img = TestImages.Canvas(128, 128, TestImages.White);
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++) img.SetPixel(x, y, (byte)(x * 2), (byte)(y * 2), (byte)((x + y)));

        var few = Vectorizer.Trace(img, TestImages.Small(new VectorizeOptions { Colors = 4 }));
        var many = Vectorizer.Trace(img, TestImages.Small(new VectorizeOptions { Colors = 24, SpeckleSize = 4 }));

        Assert.True(few.Palette.Count <= 4);
        Assert.True(many.Palette.Count > few.Palette.Count);
        Assert.True(many.Shapes.Count > few.Shapes.Count);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 40)]
    [InlineData(40, 1)]
    [InlineData(3, 3)]
    public void Tiny_and_single_color_images_trace_to_one_covering_shape(int w, int h)
    {
        var img = RasterImage.Solid(w, h, 40, 90, 160);

        foreach (var layering in new[] { VectorizeLayering.Stacked, VectorizeLayering.CutOut })
        {
            var result = Vectorizer.Trace(img, TestImages.Small(new VectorizeOptions { Layering = layering }));
            var shape = Assert.Single(result.Shapes);
            AssertClosed(shape.Path);
            var b = shape.Path.GetBounds();
            Assert.InRange(b.Width, w - 0.01, w + 0.01);
            Assert.InRange(b.Height, h - 0.01, h + 0.01);
        }
    }

    [Fact]
    public void A_fully_transparent_image_traces_to_nothing()
    {
        var result = Vectorizer.Trace(RasterImage.Solid(20, 20, 0, 0, 0, 0), TestImages.Small());
        Assert.Empty(result.Shapes);
    }

    [Fact]
    public void Thin_dark_lines_are_kept_by_black_and_white_mode()
    {
        var img = TestImages.Canvas(100, 60, TestImages.White);
        TestImages.FillRect(img, 10, 29, 90, 31, TestImages.Black);   // 2 px rule

        var result = Vectorizer.Trace(img, new VectorizeOptions { Mode = VectorizeMode.BlackAndWhite });

        var line = Assert.Single(result.Shapes);
        var b = line.Path.GetBounds();
        Assert.InRange(b.Width, 79.5, 80.5);
        Assert.InRange(b.Height, 1.5, 2.5);
    }

    [Fact]
    public void Tracing_is_repeatable()
    {
        var a = SvgWriter.ToSvg(Vectorizer.Trace(TestImages.SquareAndDisc(), TestImages.Small()));
        var b = SvgWriter.ToSvg(Vectorizer.Trace(TestImages.SquareAndDisc(), TestImages.Small()));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Larger_than_working_size_is_traced_smaller_and_scaled_back()
    {
        var img = TestImages.Canvas(400, 200, TestImages.White);
        TestImages.FillRect(img, 100, 50, 300, 150, TestImages.Blue);

        var result = Vectorizer.Trace(img, new VectorizeOptions { MaxWorkingSize = 100, MinWorkingSize = 0 });

        Assert.Equal(400, result.Width);
        var blue = result.Shapes.Single(s => s.Fill!.Value.B > 150 && s.Fill.Value.R < 80).Path.GetBounds();
        Assert.InRange(blue.Left, 96, 104); Assert.InRange(blue.Right, 296, 304);
    }

    [Fact]
    public void Cancelling_stops_the_trace()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Vectorizer.Trace(TestImages.SquareAndDisc(), TestImages.Small(), cts.Token));
    }

    internal static void AssertClosed(PathGeometry g)
    {
        Assert.False(g.IsEmpty);
        Assert.Equal(PathVerb.MoveTo, g.Verbs[0]);
        Assert.Equal(PathVerb.Close, g.Verbs[^1]);
        int pi = 0;
        Vec2 start = default, cur = default;
        bool open = false;
        foreach (var v in g.Verbs)
        {
            switch (v)
            {
                case PathVerb.MoveTo:
                    Assert.False(open, "a subpath was started before the previous one was closed");
                    start = cur = g.Points[pi++]; open = true; break;
                case PathVerb.LineTo: cur = g.Points[pi++]; break;
                case PathVerb.CubicTo: cur = g.Points[pi + 2]; pi += 3; break;
                case PathVerb.Close:
                    Assert.True(open);
                    Assert.True(Vec2.Distance(cur, start) < 1e-6, $"subpath ends at {cur} instead of {start}");
                    open = false; break;
            }
        }
        Assert.False(open);
        Assert.Equal(FillRule.EvenOdd, g.FillRule);
    }
}

public class OutputTests
{
    [Fact]
    public void Svg_is_well_formed_with_one_path_per_shape()
    {
        var result = Vectorizer.Trace(TestImages.SquareAndDisc(), TestImages.Small());
        var doc = XDocument.Parse(SvgWriter.ToSvg(result));

        XNamespace ns = "http://www.w3.org/2000/svg";
        Assert.Equal(result.Shapes.Count, doc.Descendants(ns + "path").Count());
        Assert.Equal("0 0 96 96", doc.Root!.Attribute("viewBox")!.Value);
        Assert.All(doc.Descendants(ns + "path"), p => Assert.EndsWith("Z", p.Attribute("d")!.Value));
    }

    [Fact]
    public void Chain_curves_reverse_exactly()
    {
        var segs = new[]
        {
            new Seg(true, default, default, new Vec2(10, 0)),
            new Seg(false, new Vec2(14, 0), new Vec2(20, 4), new Vec2(20, 10)),
            new Seg(true, default, default, new Vec2(20, 30)),
        };
        var c = new ChainCurve(new Vec2(0, 0), segs);

        var back = c.Reversed();
        Assert.Equal(new Vec2(20, 30), back.Start);
        Assert.Equal(new Vec2(0, 0), back.End);
        Assert.Equal(new Vec2(20, 10), back.Segs[0].End);
        Assert.Equal(new Vec2(20, 4), back.Segs[1].C1);
        Assert.Equal(new Vec2(14, 0), back.Segs[1].C2);
        Assert.Equal(c.Segs, back.Reversed().Segs);
        Assert.Equal(c.Start, back.Reversed().Start);
    }

    [Fact]
    public void Resize_keeps_flat_colors_and_alpha()
    {
        var img = RasterImage.Solid(40, 20, 10, 200, 30);
        var small = img.Resize(10, 5);
        Assert.Equal((10, 200, 30, 255), small.GetPixel(4, 2));
        var big = img.Resize(100, 50);
        Assert.Equal((10, 200, 30, 255), big.GetPixel(77, 31));
    }
}

public class InsertCommandTests
{
    [Fact]
    public void Insert_is_one_undoable_group_of_path_objects()
    {
        var doc = new Document(PageSetup.FromInches(8, 6));
        var history = new CommandHistory(doc);
        var result = Vectorizer.Trace(TestImages.SquareAndDisc(), TestImages.Small());

        var cmd = new InsertVectorGroupCommand(doc.ActiveLayer, result, Affine2.Scale(2, 2));
        history.Execute(cmd);

        var group = Assert.IsType<GroupObject>(Assert.Single(doc.ActiveLayer.Children.Select(o => o.TopLevel).Distinct()));
        Assert.Equal(3, group.Children.Count);
        Assert.All(group.Children, c => Assert.IsType<PathObject>(c));
        Assert.Equal(group.Children, doc.ActiveLayer.Children);   // the three parts, in the drawing order
        Assert.Equal(4, doc.ObjectCount);   // group + parts
        // 96 px at 2x = 192 pt wide
        Assert.InRange(group.GetWorldBounds().Width, 191.5, 192.5);

        history.Undo();
        Assert.Empty(doc.ActiveLayer.Children);
        Assert.Equal(0, doc.ObjectCount);

        history.Redo();
        Assert.Equal(3, Assert.IsType<GroupObject>(Assert.Single(doc.ActiveLayer.Children.Select(o => o.TopLevel).Distinct())).Children.Count);
        Assert.Equal(4, doc.ObjectCount);
    }

    [Fact]
    public void Outline_results_become_unfilled_stroked_objects()
    {
        var doc = new Document(PageSetup.FromInches(8, 6));
        var history = new CommandHistory(doc);
        var result = Vectorizer.Trace(TestImages.SquareAndDisc(), TestImages.Small(new VectorizeOptions { Mode = VectorizeMode.Outline }));

        history.Execute(new InsertVectorGroupCommand(doc.ActiveLayer, result, Affine2.Identity, 0.75));

        var group = (GroupObject)doc.ActiveLayer.Children[0].TopLevel;
        Assert.All(group.Children, c => { Assert.Null(c.Style.Fill); Assert.Equal(0.75, c.Style.Stroke!.Width); });
    }
}


