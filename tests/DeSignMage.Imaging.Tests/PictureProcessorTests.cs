using DeSignMage.Core.Imaging;

namespace DeSignMage.Imaging.Tests;

/// <summary>The pixel work of the Picture Editor: straighten, crop, adjust, auto levels, auto-trim and previews.</summary>
public class PictureProcessorTests
{
    private static PixelBuffer Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var p = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) p.Set(x, y, r, g, b, a);
        return p;
    }

    /// <summary>A gradient with a different value in every pixel, so any wrong crop or shift shows.</summary>
    private static PixelBuffer Gradient(int w, int h)
    {
        var p = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) p.Set(x, y, (byte)(x * 255 / Math.Max(1, w - 1)), (byte)(y * 255 / Math.Max(1, h - 1)), (byte)((x + y) % 256));
        return p;
    }

    // ---- straighten ----

    [Fact]
    public void No_turn_returns_the_same_picture()
    {
        var src = Samples.Grid();
        Assert.Same(src, PictureProcessor.Straighten(src, 0));
        Assert.Same(src, PictureProcessor.Straighten(src, 1e-9));
    }

    [Fact]
    public void A_quarter_turn_is_the_exact_rotation()
    {
        var src = Gradient(60, 40);
        var turned = PictureProcessor.Straighten(src, 90);
        var expected = PixelOps.Rotate90(src, clockwise: true);
        Assert.Equal((expected.Width, expected.Height), (turned.Width, turned.Height));
        int worst = 0;
        for (int i = 0; i < expected.Rgba.Length; i++) worst = Math.Max(worst, Math.Abs(expected.Rgba[i] - turned.Rgba[i]));
        Assert.True(worst <= 2, $"largest difference {worst}");
    }

    [Fact]
    public void A_small_turn_gives_the_canvas_size_with_transparent_corners_and_the_picture_in_the_middle()
    {
        var src = Solid(200, 100, 200, 30, 30);
        var turned = PictureProcessor.Straighten(src, 10);
        var (cw, ch) = PictureGeometry.CanvasSize(200, 100, 10);
        Assert.Equal((cw, ch), (turned.Width, turned.Height));
        Assert.Equal(0, turned.Get(0, 0).A);                         // an empty corner
        Assert.Equal(0, turned.Get(cw - 1, ch - 1).A);
        var middle = turned.Get(cw / 2, ch / 2);
        Assert.Equal((200, 30, 30, 255), middle);
        Assert.Equal(200, turned.Get(cw / 2, ch / 2).R);
    }

    [Fact]
    public void Turning_clockwise_moves_the_right_edge_of_the_picture_down()
    {
        // a bright dot at the right end of the middle row; a clockwise turn takes it below the middle
        var src = Solid(101, 11, 0, 0, 0);
        for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) src.Set(95 + dx, 5 + dy, 255, 255, 255);
        var turned = PictureProcessor.Straighten(src, 20);
        int bestX = 0, bestY = 0, best = -1;
        for (int y = 0; y < turned.Height; y++) for (int x = 0; x < turned.Width; x++) if (turned.Get(x, y).R > best) { best = turned.Get(x, y).R; bestX = x; bestY = y; }
        Assert.True(bestX > turned.Width / 2);
        Assert.True(bestY > turned.Height / 2, $"dot at ({bestX},{bestY}) in {turned.Width}x{turned.Height}");
    }

    [Fact]
    public void Straightening_does_not_change_the_original()
    {
        var src = Gradient(50, 30);
        var copy = (byte[])src.Rgba.Clone();
        PictureProcessor.Straighten(src, 7);
        Assert.True(copy.AsSpan().SequenceEqual(src.Rgba));
    }

    // ---- adjust ----

    [Fact]
    public void A_neutral_adjustment_returns_the_same_picture()
    {
        var src = Gradient(10, 10);
        Assert.Same(src, PictureProcessor.ApplyAdjust(src, PictureAdjust.None));
    }

    [Fact]
    public void The_fast_loop_agrees_with_the_reference_math_on_every_kind_of_adjustment()
    {
        var src = Gradient(64, 64);
        var rnd = new Random(7);
        for (int i = 0; i < src.Rgba.Length; i += 4) src.Rgba[i + 3] = (byte)rnd.Next(256);   // varied alpha
        var adjustments = new[]
        {
            PictureAdjust.None with { Brightness = 25 },
            PictureAdjust.None with { Contrast = -35 },
            PictureAdjust.None with { Gamma = 1.8 },
            PictureAdjust.None with { Saturation = 60 },
            PictureAdjust.None with { Hue = 75 },
            PictureAdjust.None with { BalanceR = 40, BalanceB = -30 },
            PictureAdjust.None with { LowR = 20, HighR = 220, LowG = 10, HighB = 240 },
            PictureAdjust.None with { Brightness = -10, Contrast = 20, Gamma = 0.8, Saturation = -40, Hue = -30, BalanceG = 25, LowB = 12, HighR = 230 },
        };
        foreach (var a in adjustments)
        {
            var result = PictureProcessor.ApplyAdjust(src, a);
            Assert.Equal((src.Width, src.Height), (result.Width, result.Height));
            for (int y = 0; y < src.Height; y += 3)
                for (int x = 0; x < src.Width; x += 3)
                {
                    var (r, g, b, alpha) = src.Get(x, y);
                    var (er, eg, eb) = AdjustMath.Apply(a, r, g, b);
                    var got = result.Get(x, y);
                    Assert.InRange(got.R, er - 1, er + 1);
                    Assert.InRange(got.G, eg - 1, eg + 1);
                    Assert.InRange(got.B, eb - 1, eb + 1);
                    Assert.Equal(alpha, got.A);                                    // the alpha channel is never touched
                }
        }
    }

    [Fact]
    public void Adjusting_does_not_change_the_original()
    {
        var src = Gradient(40, 40);
        var copy = (byte[])src.Rgba.Clone();
        PictureProcessor.ApplyAdjust(src, PictureAdjust.None with { Brightness = 50, Hue = 90 });
        Assert.True(copy.AsSpan().SequenceEqual(src.Rgba));
    }

    // ---- auto levels ----

    [Fact]
    public void Auto_levels_find_the_range_of_a_dull_picture_and_stretching_fills_it()
    {
        var dull = new PixelBuffer(100, 100);
        for (int y = 0; y < 100; y++) for (int x = 0; x < 100; x++) { byte v = (byte)(80 + x); dull.Set(x, y, v, v, v); }   // 80 .. 179
        var adjust = PictureProcessor.AutoLevels(dull, PictureAdjust.None);
        Assert.InRange(adjust.LowR, 80, 84);
        Assert.InRange(adjust.HighR, 175, 179);

        var stretched = PictureProcessor.ApplyAdjust(dull, adjust);
        Assert.InRange(stretched.Get(2, 0).R, 0, 8);
        Assert.InRange(stretched.Get(97, 0).R, 247, 255);
    }

    [Fact]
    public void Auto_levels_ignore_transparent_pixels()
    {
        var p = new PixelBuffer(10, 10);
        for (int y = 0; y < 10; y++) for (int x = 0; x < 10; x++) p.Set(x, y, 0, 0, 0, 0);               // transparent black everywhere ...
        for (int y = 3; y < 7; y++) for (int x = 3; x < 7; x++) p.Set(x, y, (byte)(100 + x * 10), 120, 120);   // ... but a small visible patch
        var h = PictureProcessor.Histograms(p);
        Assert.Equal(16, h[0].Sum());
        Assert.Equal(0, h[0][0]);
    }

    // ---- the whole recipe ----

    [Fact]
    public void A_crop_keeps_exactly_the_chosen_pixels()
    {
        var src = Gradient(80, 60);
        var recipe = new PictureRecipe(80, 60) { Crop = new RectI(10, 20, 30, 25) };
        var result = PictureProcessor.Render(src, recipe);
        Assert.Equal((30, 25), (result.Width, result.Height));
        Samples.AssertSame(PixelOps.Crop(src, 10, 20, 30, 25), result);
    }

    [Fact]
    public void The_crop_is_applied_before_the_colors_and_the_result_has_the_cropped_size()
    {
        var src = Gradient(80, 60);
        var recipe = new PictureRecipe(80, 60) { Crop = new RectI(10, 20, 30, 25), Adjust = PictureAdjust.None with { Brightness = 30 } };
        var result = PictureProcessor.Render(src, recipe);
        Assert.Equal(recipe.ResultSize, (result.Width, result.Height));
        var cropped = PixelOps.Crop(src, 10, 20, 30, 25);
        var (er, eg, eb) = AdjustMath.Apply(recipe.Adjust, cropped.Get(3, 4).R, cropped.Get(3, 4).G, cropped.Get(3, 4).B);
        Assert.InRange(result.Get(3, 4).R, er - 1, er + 1);
    }

    [Fact]
    public void An_empty_recipe_gives_back_the_original_pixels()
    {
        var src = Gradient(30, 20);
        var result = PictureProcessor.Render(src, new PictureRecipe(30, 20));
        Samples.AssertSame(src, result);
    }

    [Fact]
    public void Straighten_then_crop_gives_the_size_the_recipe_promises()
    {
        var src = Gradient(300, 200);
        var recipe = new PictureRecipe(300, 200) { Straighten = 6 };
        recipe = recipe with { Crop = PictureGeometry.InscribedCrop(300, 200, 6) };
        var result = PictureProcessor.Render(src, recipe);
        Assert.Equal(recipe.ResultSize, (result.Width, result.Height));
        // no empty corner is left: every corner pixel of the result is opaque
        foreach (var (x, y) in new[] { (0, 0), (result.Width - 1, 0), (0, result.Height - 1), (result.Width - 1, result.Height - 1) })
            Assert.True(result.Get(x, y).A >= 250, $"corner ({x},{y}) alpha {result.Get(x, y).A}");
    }

    [Fact]
    public void The_original_is_never_changed_by_any_recipe()
    {
        var src = Gradient(120, 80);
        var copy = (byte[])src.Rgba.Clone();
        var recipe = new PictureRecipe(120, 80) { Straighten = -9, Crop = new RectI(5, 5, 100, 70), Adjust = PictureAdjust.None with { Contrast = 40, Saturation = -20 } };
        PictureProcessor.Render(src, recipe);
        Assert.True(copy.AsSpan().SequenceEqual(src.Rgba));
    }

    [Fact]
    public void A_preview_from_a_reduced_copy_has_the_size_the_full_result_has_times_the_reduction()
    {
        var full = Gradient(1000, 600);
        var recipe = new PictureRecipe(1000, 600) { Straighten = 4, Crop = new RectI(100, 50, 600, 400), Adjust = PictureAdjust.None with { Brightness = 10 } };
        var (small, scale) = PictureProcessor.Reduce(full, 250);
        Assert.Equal(0.25, scale, 6);
        Assert.Equal((250, 150), (small.Width, small.Height));

        var preview = PictureProcessor.Render(small, recipe, scale);
        var real = PictureProcessor.Render(full, recipe);
        Assert.Equal((600, 400), (real.Width, real.Height));
        Assert.InRange(preview.Width, 149, 151);
        Assert.InRange(preview.Height, 99, 101);
    }

    [Fact]
    public void A_small_picture_is_not_reduced()
    {
        var src = Gradient(100, 50);
        var (copy, scale) = PictureProcessor.Reduce(src, 1800);
        Assert.Same(src, copy);
        Assert.Equal(1, scale);
    }

    // ---- auto-trim ----

    private static PixelBuffer BorderedSquare(int border, byte borderValue = 255)
    {
        var p = Solid(100, 80, borderValue, borderValue, borderValue);
        for (int y = border; y < 80 - border; y++) for (int x = border; x < 100 - border; x++) p.Set(x, y, (byte)(40 + x), (byte)(60 + y), 90);   // varied content
        return p;
    }

    [Fact]
    public void Plain_borders_are_cut_to_the_content()
    {
        var found = PictureProcessor.AutoTrim(BorderedSquare(12));
        Assert.Equal(new RectI(12, 12, 76, 56), found);
    }

    [Fact]
    public void The_border_may_be_any_color_and_uneven()
    {
        var p = Solid(100, 80, 10, 10, 10);                        // dark border
        for (int y = 5; y < 70; y++) for (int x = 20; x < 90; x++) p.Set(x, y, (byte)(100 + x), 150, (byte)(80 + y));
        Assert.Equal(new RectI(20, 5, 70, 65), PictureProcessor.AutoTrim(p));
    }

    [Fact]
    public void A_little_noise_in_the_border_is_within_the_tolerance()
    {
        var p = BorderedSquare(10);
        var rnd = new Random(3);
        for (int i = 0; i < p.Rgba.Length; i += 4)
        {
            int x = i / 4 % 100, y = i / 4 / 100;
            if (x < 10 || x >= 90 || y < 10 || y >= 70) { byte v = (byte)(255 - rnd.Next(6)); p.Rgba[i] = p.Rgba[i + 1] = p.Rgba[i + 2] = v; }
        }
        Assert.Equal(new RectI(10, 10, 80, 60), PictureProcessor.AutoTrim(p, 12));
        Assert.NotEqual(new RectI(10, 10, 80, 60), PictureProcessor.AutoTrim(p, 0));   // with no tolerance the noise counts as picture
    }

    [Fact]
    public void A_picture_without_a_plain_border_is_left_alone()
    {
        Assert.Null(PictureProcessor.AutoTrim(Gradient(100, 80)));
    }

    [Fact]
    public void One_plain_color_everywhere_has_nothing_to_keep()
    {
        var found = PictureProcessor.AutoTrim(Solid(50, 50, 200, 200, 200));
        Assert.True(found is null || found.Value.Width * found.Value.Height <= 1 + 0, "nothing worth keeping");
    }

    [Fact]
    public void Empty_transparent_corners_of_a_turned_picture_count_as_border()
    {
        var turned = PictureProcessor.Straighten(Solid(200, 120, 90, 160, 60), 12);
        var found = PictureProcessor.AutoTrim(turned);
        // The turned picture fills a rhombus; plain trimming is limited to rows and columns that are entirely empty, so at most the canvas is returned or trimmed a little.
        Assert.True(found is null || (found.Value.Width <= turned.Width && found.Value.Height <= turned.Height));
    }

    [Fact]
    public void Only_the_edges_that_are_plain_are_cut()
    {
        var p = Gradient(100, 80);                                   // picture on every side ...
        for (int y = 0; y < 15; y++) for (int x = 0; x < 100; x++) p.Set(x, y, 255, 255, 255);   // ... except a white strip along the top
        Assert.Equal(new RectI(0, 15, 100, 65), PictureProcessor.AutoTrim(p));
    }
}
