using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;

namespace DeSignMage.Imaging.Tests;

/// <summary>The Transparency tab's pixel work: knocking out colors, the plain-background fill from the edge, the cut outlines through turns and crops, and the soft border.</summary>
public class TransparencyTests
{
    private const int Red = 0xFF0000, White = 0xFFFFFF;

    /// <summary>White everywhere, a red square (x0..x1, y0..y1 exclusive) in it.</summary>
    private static PixelBuffer WhiteWithRed(int w, int h, int x0, int y0, int x1, int y1)
    {
        var p = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool red = x >= x0 && x < x1 && y >= y0 && y < y1;
                p.Set(x, y, 255, red ? (byte)0 : (byte)255, red ? (byte)0 : (byte)255);
            }
        return p;
    }

    private static byte A(PixelBuffer p, int x, int y) => p.Get(x, y).A;

    private static Func<Vec2, Vec2> Same => v => v;

    private static CutShape Poly(bool keep, params (double X, double Y)[] points) => new(new ValueList<Vec2>(points.Select(p => new Vec2(p.X, p.Y))), keep);

    private static PictureTransparency Cut(params CutShape[] shapes) => new() { Cuts = new ValueList<CutShape>(shapes) };

    private static PictureTransparency Knock(int color, int tolerance = 12, bool edgeOnly = false) =>
        new() { Colors = new ValueList<int>([color]), Tolerance = tolerance, EdgeOnly = edgeOnly };

    // ---- colors ----

    [Fact]
    public void Knocking_out_white_makes_the_white_background_clear_and_keeps_the_rest()
    {
        var p = WhiteWithRed(20, 20, 5, 5, 15, 15);
        var r = TransparencyOps.Apply(p, Knock(White), Same);
        Assert.Equal(0, A(r, 1, 1));
        Assert.Equal(0, A(r, 19, 19));
        Assert.Equal(255, A(r, 10, 10));
        Assert.Equal((255, 0, 0), (r.Get(10, 10).R, r.Get(10, 10).G, r.Get(10, 10).B));   // the colors are untouched
    }

    [Fact]
    public void An_unchanged_recipe_gives_back_the_same_picture()
    {
        var p = WhiteWithRed(8, 8, 2, 2, 6, 6);
        Assert.Same(p, TransparencyOps.Apply(p, PictureTransparency.None, Same));
        Assert.Same(p, TransparencyOps.Apply(p, PictureTransparency.None with { Softness = 9 }, Same));
    }

    [Fact]
    public void The_tolerance_decides_how_near_a_color_goes()
    {
        var p = new PixelBuffer(4, 1);
        p.Set(0, 0, 255, 255, 255);
        p.Set(1, 0, 245, 245, 245);     // about 4 % away from white
        p.Set(2, 0, 200, 200, 200);     // about 22 %
        p.Set(3, 0, 0, 0, 0);
        var tight = TransparencyOps.Apply(p, Knock(White, 0), Same);
        Assert.Equal(0, A(tight, 0, 0));                        // exactly white goes
        Assert.InRange((int)A(tight, 1, 0), 100, 230);          // a near white is only partly removed, on the ramp
        Assert.Equal(255, A(tight, 2, 0));
        var loose = TransparencyOps.Apply(p, Knock(White, 10), Same);
        Assert.Equal(0, A(loose, 1, 0));
        Assert.Equal(255, A(loose, 2, 0));
        var wide = TransparencyOps.Apply(p, Knock(White, 30), Same);
        Assert.Equal(0, A(wide, 2, 0));
        Assert.Equal(255, A(wide, 3, 0));
        var all = TransparencyOps.Apply(p, Knock(White, 100), Same);
        Assert.Equal(0, A(all, 3, 0));
    }

    [Fact]
    public void A_color_a_little_beyond_the_tolerance_is_only_partly_removed_so_the_edge_is_smooth()
    {
        var p = new PixelBuffer(1, 1);
        double limit = 0.10 * 441.67, ramp = TransparencyOps.RampPercent / 100 * 441.67;
        byte v = (byte)(255 - (limit + ramp / 2) / Math.Sqrt(3));   // gray this far from white
        p.Set(0, 0, v, v, v);
        var a = A(TransparencyOps.Apply(p, Knock(White, 10), Same), 0, 0);
        Assert.InRange((int)a, 80, 180);
    }

    [Fact]
    public void Several_colors_are_all_removed()
    {
        var p = new PixelBuffer(3, 1);
        p.Set(0, 0, 255, 255, 255); p.Set(1, 0, 255, 0, 0); p.Set(2, 0, 0, 0, 255);
        var t = new PictureTransparency { Colors = new ValueList<int>([White, Red]), Tolerance = 5 };
        var r = TransparencyOps.Apply(p, t, Same);
        Assert.Equal((0, 0, 255), (A(r, 0, 0), A(r, 1, 0), A(r, 2, 0)));
    }

    [Fact]
    public void The_edge_only_mode_keeps_white_that_is_enclosed_by_the_picture()
    {
        // a red ring on white: the white outside goes, the white in the middle stays
        var p = WhiteWithRed(21, 21, 4, 4, 17, 17);
        for (int y = 8; y < 13; y++) for (int x = 8; x < 13; x++) p.Set(x, y, 255, 255, 255);
        var all = TransparencyOps.Apply(p, Knock(White), Same);
        var edge = TransparencyOps.Apply(p, Knock(White, edgeOnly: true), Same);
        Assert.Equal(0, A(all, 10, 10));       // everywhere: the middle goes too
        Assert.Equal(255, A(edge, 10, 10));    // from the edge only: the middle stays
        Assert.Equal(0, A(edge, 0, 0));
        Assert.Equal(255, A(edge, 5, 5));
    }

    [Fact]
    public void The_edge_only_fill_passes_through_corners_that_are_already_clear()
    {
        var p = WhiteWithRed(10, 10, 3, 3, 7, 7);
        for (int x = 0; x < 3; x++) p.Set(x, 0, 0, 0, 0, 0);   // clear pixels at the corner
        var r = TransparencyOps.Apply(p, Knock(White, edgeOnly: true), Same);
        Assert.Equal(0, A(r, 2, 0));
        Assert.Equal(0, A(r, 0, 5));
    }

    [Fact]
    public void A_pixel_that_was_already_see_through_stays_at_most_that_visible()
    {
        var p = new PixelBuffer(2, 1);
        p.Set(0, 0, 255, 0, 0, 100);
        p.Set(1, 0, 255, 0, 0, 255);
        var r = TransparencyOps.Apply(p, PictureTransparency.None with { Opacity = 50 }, Same);
        Assert.Equal(50, A(r, 0, 0));
        Assert.Equal(128, A(r, 1, 0));
    }

    [Fact]
    public void The_background_color_is_the_color_most_of_the_edge_shares()
    {
        var p = WhiteWithRed(30, 30, 10, 10, 20, 20);
        Assert.Equal(White, TransparencyOps.BackgroundColor(p));
        var photo = new PixelBuffer(32, 32);
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) photo.Set(x, y, (byte)(x * 8), (byte)(y * 8), (byte)((x + y) * 4));
        Assert.Null(TransparencyOps.BackgroundColor(photo));        // no plain edge
        var clear = new PixelBuffer(4, 4);
        Assert.Null(TransparencyOps.BackgroundColor(clear));
    }

    // ---- cut outlines ----

    [Fact]
    public void Keeping_the_inside_of_an_outline_clears_everything_else()
    {
        var p = WhiteWithRed(20, 20, 0, 0, 20, 20);
        var r = TransparencyOps.Apply(p, Cut(Poly(true, (5, 5), (15, 5), (15, 15), (5, 15))), Same);
        Assert.Equal(255, A(r, 10, 10));
        Assert.Equal(0, A(r, 2, 2));
        Assert.Equal(0, A(r, 18, 10));
    }

    [Fact]
    public void Removing_the_inside_of_an_outline_cuts_a_hole()
    {
        var p = WhiteWithRed(20, 20, 0, 0, 20, 20);
        var r = TransparencyOps.Apply(p, Cut(Poly(false, (5, 5), (15, 5), (15, 15), (5, 15))), Same);
        Assert.Equal(0, A(r, 10, 10));
        Assert.Equal(255, A(r, 2, 2));
    }

    [Fact]
    public void Several_keep_outlines_add_up_and_remove_outlines_are_cut_out_of_them()
    {
        var p = WhiteWithRed(30, 10, 0, 0, 30, 10);
        var t = Cut(Poly(true, (1, 1), (9, 1), (9, 9), (1, 9)), Poly(true, (21, 1), (29, 1), (29, 9), (21, 9)), Poly(false, (4, 4), (6, 4), (6, 6), (4, 6)));
        var r = TransparencyOps.Apply(p, t, Same);
        Assert.Equal(255, A(r, 2, 2));
        Assert.Equal(255, A(r, 25, 5));
        Assert.Equal(0, A(r, 15, 5));          // between the two kept ones
        Assert.Equal(0, A(r, 5, 5));           // the hole
    }

    [Fact]
    public void The_edge_of_an_outline_is_smooth_not_jagged()
    {
        var p = WhiteWithRed(40, 40, 0, 0, 40, 40);
        var r = TransparencyOps.Apply(p, Cut(Poly(true, (5, 5), (35, 12), (20, 35))), Same);
        int partial = 0;
        for (int y = 0; y < 40; y++) for (int x = 0; x < 40; x++) if (A(r, x, y) is > 0 and < 255) partial++;
        Assert.True(partial > 20, "the slanted edges are anti-aliased");
    }

    [Fact]
    public void A_cut_follows_the_picture_when_it_is_turned_and_cropped()
    {
        // The cut is drawn on the original: keep a 10 x 10 square whose top-left is at (20, 4). Quarter turn clockwise, then crop.
        var source = WhiteWithRed(60, 30, 0, 0, 60, 30);
        for (int y = 4; y < 14; y++) for (int x = 20; x < 30; x++) source.Set(x, y, 0, 0, 255);   // mark where the square is
        var recipe = new PictureRecipe(60, 30)
        {
            Quarter = 1,
            Crop = new RectI(10, 10, 20, 40),
            Transparency = Cut(Poly(true, (20, 4), (30, 4), (30, 14), (20, 14))),
        };
        var result = PictureProcessor.Render(source, recipe);
        Assert.Equal((20, 40), (result.Width, result.Height));
        int visible = 0, blueVisible = 0;
        for (int y = 0; y < result.Height; y++)
            for (int x = 0; x < result.Width; x++)
            {
                var (r, g, b, a) = result.Get(x, y);
                if (a == 255) { visible++; if (b == 255 && r == 0) blueVisible++; }
            }
        Assert.Equal(100, visible);          // exactly the square
        Assert.Equal(100, blueVisible);      // and it is the marked one
    }

    [Fact]
    public void A_cut_follows_a_free_rotation()
    {
        var source = WhiteWithRed(80, 80, 0, 0, 80, 80);
        for (int y = 30; y < 50; y++) for (int x = 30; x < 50; x++) source.Set(x, y, 0, 0, 255);
        var recipe = new PictureRecipe(80, 80) { Straighten = 25, Transparency = Cut(Poly(true, (30, 30), (50, 30), (50, 50), (30, 50))) };
        var result = PictureProcessor.Render(source, recipe);
        int blue = 0, nonBlueVisible = 0;
        for (int y = 0; y < result.Height; y++)
            for (int x = 0; x < result.Width; x++)
            {
                var (r, g, b, a) = result.Get(x, y);
                if (a > 200 && b == 255 && r < 40) blue++;
                else if (a > 200) nonBlueVisible++;
            }
        Assert.InRange(blue, 300, 410);       // about 400 pixels of the square
        Assert.True(nonBlueVisible < 60, $"almost nothing but the square is kept ({nonBlueVisible})");
    }

    [Fact]
    public void The_preview_of_a_reduced_copy_matches_the_full_size_result()
    {
        var source = WhiteWithRed(400, 300, 100, 80, 300, 220);
        var recipe = new PictureRecipe(400, 300)
        {
            Crop = new RectI(50, 40, 300, 220),
            Transparency = new PictureTransparency
            {
                Colors = new ValueList<int>([White]),
                Cuts = new ValueList<CutShape>([Poly(true, (80, 60), (330, 60), (330, 250), (80, 250))]),
                Softness = 6,
            },
        };
        var full = PictureProcessor.Render(source, recipe);
        var (small, scale) = PictureProcessor.Reduce(source, 200);
        var preview = PictureProcessor.Render(small, recipe, scale);
        Assert.Equal(0.5, scale, 3);
        int differ = 0, total = 0;
        for (int y = 0; y < preview.Height; y++)
            for (int x = 0; x < preview.Width; x++)
            {
                total++;
                if (Math.Abs(A(preview, x, y) - A(full, Math.Min(full.Width - 1, x * 2), Math.Min(full.Height - 1, y * 2))) > 60) differ++;
            }
        Assert.True(differ < total * 0.03, $"{differ} of {total} pixels differ a lot");
    }

    // ---- the border ----

    [Fact]
    public void Shrink_eats_the_picture_away_from_the_cut()
    {
        var p = WhiteWithRed(30, 30, 10, 10, 20, 20);
        var plain = TransparencyOps.Apply(p, Knock(White), Same);
        var shrunk = TransparencyOps.Apply(p, Knock(White) with { Shrink = 2 }, Same);
        Assert.Equal(255, A(plain, 10, 10));
        Assert.Equal(0, A(shrunk, 10, 10));
        Assert.Equal(0, A(shrunk, 11, 11));
        Assert.Equal(255, A(shrunk, 14, 14));
    }

    [Fact]
    public void Shrink_does_not_eat_at_the_edge_of_the_picture_itself()
    {
        var p = WhiteWithRed(20, 20, 0, 0, 20, 20);
        var r = TransparencyOps.Apply(p, Cut(Poly(false, (8, 8), (12, 8), (12, 12), (8, 12))) with { Shrink = 3 }, Same);
        Assert.Equal(255, A(r, 0, 0));
        Assert.Equal(255, A(r, 19, 19));
    }

    [Fact]
    public void Softness_makes_a_gradual_border()
    {
        var p = WhiteWithRed(60, 60, 20, 20, 40, 40);
        var hard = TransparencyOps.Apply(p, Knock(White), Same);
        var soft = TransparencyOps.Apply(p, Knock(White) with { Softness = 8 }, Same);
        int Partial(PixelBuffer b) { int n = 0; for (int y = 0; y < 60; y++) for (int x = 0; x < 60; x++) if (A(b, x, y) is > 10 and < 245) n++; return n; }
        Assert.True(Partial(soft) > Partial(hard) + 100);
        Assert.True(A(soft, 30, 30) >= 235);      // the middle of the red square is still nearly solid
        Assert.Equal(0, A(soft, 2, 2));
    }

    [Fact]
    public void Opacity_makes_the_whole_picture_see_through()
    {
        var p = WhiteWithRed(6, 6, 0, 0, 6, 6);
        var r = TransparencyOps.Apply(p, PictureTransparency.None with { Opacity = 40 }, Same);
        Assert.Equal(102, A(r, 3, 3));
    }

    [Fact]
    public void The_steps_after_transparency_keep_the_clear_parts_clear()
    {
        var source = WhiteWithRed(20, 20, 5, 5, 15, 15);
        var recipe = new PictureRecipe(20, 20)
        {
            Transparency = Knock(White),
            Adjust = PictureAdjust.None with { Brightness = 30, Saturation = 20 },
            Effects = PictureEffects.None with { Grayscale = true },
        };
        var r = PictureProcessor.Render(source, recipe);
        Assert.Equal(0, A(r, 1, 1));
        Assert.Equal(255, A(r, 10, 10));
    }

    [Fact]
    public void The_cache_gives_the_same_result_and_reuses_the_cut_when_only_a_late_step_changes()
    {
        var source = WhiteWithRed(100, 80, 20, 20, 80, 60);
        var cache = new PictureProcessor.RenderCache();
        var recipe = new PictureRecipe(100, 80) { Transparency = Knock(White) };
        var first = PictureProcessor.RenderParts(source, recipe, 1, default, cache).Result;
        var second = PictureProcessor.RenderParts(source, recipe with { Adjust = PictureAdjust.None with { Brightness = 10 } }, 1, default, cache).Result;
        var third = PictureProcessor.RenderParts(source, recipe with { Transparency = Knock(White, 40) }, 1, default, cache).Result;
        Assert.Equal(first.Rgba.Where((_, i) => i % 4 == 3), second.Rgba.Where((_, i) => i % 4 == 3));
        Assert.Equal(0, A(third, 1, 1));
        var plain = PictureProcessor.RenderParts(source, recipe, 1).Result;
        Assert.True(first.Rgba.AsSpan().SequenceEqual(plain.Rgba));
    }

    // ---- picking ----

    [Fact]
    public void The_pick_mask_marks_the_visible_cells_and_is_null_for_a_picture_that_is_solid()
    {
        var solid = WhiteWithRed(50, 50, 0, 0, 50, 50);
        Assert.Null(AlphaMasks.FromPixels(solid));

        var cutOut = TransparencyOps.Apply(solid, Cut(Poly(true, (0, 0), (25, 0), (25, 50), (0, 50))), Same);
        var mask = AlphaMasks.FromPixels(cutOut);
        Assert.NotNull(mask);
        Assert.True(mask!.IsVisibleNear(0.25, 0.5, 0, 0));
        Assert.False(mask.IsVisibleNear(0.75, 0.5, 0, 0));
        Assert.True(mask.IsVisibleNear(0.75, 0.5, 0.3, 0.1));     // a click tolerance reaches over the border

        var nothing = new PixelBuffer(10, 10);
        Assert.Null(AlphaMasks.FromPixels(nothing));
    }

    [Fact]
    public void A_jpeg_never_gets_a_pick_mask()
    {
        var p = WhiteWithRed(20, 20, 5, 5, 10, 10);
        using var bitmap = new SkiaSharp.SKBitmap(20, 20);
        var jpeg = SkiaSharp.SKImage.FromBitmap(bitmap).Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80).ToArray();
        Assert.Null(AlphaMasks.Load(jpeg));
        var png = ImageCodec.EncodePng(TransparencyOps.Apply(p, Knock(White), Same));
        Assert.NotNull(AlphaMasks.Load(png));
    }
}
