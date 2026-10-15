using DeSignMage.Core.Imaging;

namespace DeSignMage.Imaging.Tests;

/// <summary>The pixel work of the later recipe steps: orientation (flips and quarter turns), the free angle, and the effects.</summary>
public class PictureStepsProcessorTests
{
    private static PixelBuffer Gradient(int w, int h)
    {
        var p = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) p.Set(x, y, (byte)(x * 255 / Math.Max(1, w - 1)), (byte)(y * 255 / Math.Max(1, h - 1)), (byte)((x * 7 + y * 3) % 256));
        return p;
    }

    private static PixelBuffer Solid(int w, int h, byte r, byte g, byte b)
    {
        var p = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) p.Set(x, y, r, g, b);
        return p;
    }

    private static PixelBuffer Edge(int w, int h)
    {
        // left half dark, right half light
        var p = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { byte v = x < w / 2 ? (byte)40 : (byte)200; p.Set(x, y, v, v, v); }
        return p;
    }

    private static void AssertSame(PixelBuffer expected, PixelBuffer actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        Assert.True(expected.Rgba.AsSpan().SequenceEqual(actual.Rgba), "the pixels differ");
    }

    private static double MeanDifference(PixelBuffer a, PixelBuffer b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        long sum = 0, count = 0;
        for (int i = 0; i < a.Rgba.Length; i += 4)
        {
            if (a.Rgba[i + 3] < 255 || b.Rgba[i + 3] < 255) continue;   // empty corners differ at their edges: not what is compared
            for (int c = 0; c < 3; c++) sum += Math.Abs(a.Rgba[i + c] - b.Rgba[i + c]);
            count += 3;
        }
        return count == 0 ? 0 : (double)sum / count;
    }

    // ---- orientation ----

    [Fact]
    public void Orientation_is_the_flip_first_and_then_the_clockwise_quarter_turns()
    {
        var src = Gradient(30, 20);
        foreach (bool mirror in new[] { false, true })
            for (int q = 0; q < 4; q++)
            {
                var expected = mirror ? PixelOps.Flip(src, true) : src;
                for (int i = 0; i < q; i++) expected = PixelOps.Rotate90(expected, true);
                AssertSame(expected, PictureProcessor.Orient(src, mirror, q));
            }
        Assert.Same(src, PictureProcessor.Orient(src, false, 0));
    }

    [Fact]
    public void The_rotate_and_flip_buttons_of_the_recipe_do_what_the_pixel_operations_do_on_the_result()
    {
        var src = Gradient(60, 40);
        var recipe = new PictureRecipe(60, 40) { Crop = new RectI(10, 5, 30, 20) };
        var plain = PictureProcessor.Render(src, recipe);

        AssertSame(PixelOps.Rotate90(plain, true), PictureProcessor.Render(src, recipe.RotatedRight()));
        AssertSame(PixelOps.Rotate90(plain, false), PictureProcessor.Render(src, recipe.RotatedLeft()));
        AssertSame(PixelOps.Flip(plain, true), PictureProcessor.Render(src, recipe.FlippedHorizontally()));
        AssertSame(PixelOps.Flip(plain, false), PictureProcessor.Render(src, recipe.FlippedVertically()));
    }

    [Fact]
    public void Several_buttons_in_a_row_still_equal_the_same_pixel_operations_in_a_row()
    {
        var src = Gradient(50, 30);
        var recipe = new PictureRecipe(50, 30) { Crop = new RectI(4, 6, 20, 10) };
        var steps = recipe.RotatedRight().FlippedHorizontally().RotatedLeft().FlippedVertically().RotatedRight();
        var expected = PictureProcessor.Render(src, recipe);
        expected = PixelOps.Rotate90(expected, true);
        expected = PixelOps.Flip(expected, true);
        expected = PixelOps.Rotate90(expected, false);
        expected = PixelOps.Flip(expected, false);
        expected = PixelOps.Rotate90(expected, true);
        AssertSame(expected, PictureProcessor.Render(src, steps));
    }

    [Fact]
    public void With_a_free_angle_the_buttons_still_agree_with_the_pixel_operations_up_to_smoothing()
    {
        var src = Gradient(80, 50);
        var recipe = new PictureRecipe(80, 50) { Straighten = 13 };
        var plain = PictureProcessor.Render(src, recipe);
        Assert.True(MeanDifference(PixelOps.Rotate90(plain, true), PictureProcessor.Render(src, recipe.RotatedRight())) < 2);
        Assert.True(MeanDifference(PixelOps.Flip(plain, true), PictureProcessor.Render(src, recipe.FlippedHorizontally())) < 3);
        Assert.True(MeanDifference(PixelOps.Flip(plain, false), PictureProcessor.Render(src, recipe.FlippedVertically())) < 3);
    }

    [Fact]
    public void A_half_turn_by_the_free_angle_adds_no_pixels_and_is_the_picture_upside_down()
    {
        var src = Gradient(60, 40);
        var turned = PictureProcessor.Render(src, new PictureRecipe(60, 40) { Straighten = 180 });
        Assert.Equal((60, 40), (turned.Width, turned.Height));
        Assert.True(MeanDifference(PixelOps.Flip(PixelOps.Flip(src, true), false), turned) < 1);
    }

    [Fact]
    public void The_rendered_size_is_always_the_size_the_recipe_promises()
    {
        var src = Gradient(70, 50);
        foreach (var recipe in new[]
        {
            new PictureRecipe(70, 50).RotatedRight(),
            new PictureRecipe(70, 50) { Straighten = 20 }.RotatedLeft(),
            new PictureRecipe(70, 50) { Straighten = -33, Crop = new RectI(5, 5, 40, 30) }.FlippedHorizontally(),
            new PictureRecipe(70, 50) { Quarter = 1, Straighten = 110 },
        })
        {
            var result = PictureProcessor.Render(src, recipe);
            Assert.Equal(recipe.ResultSize, (result.Width, result.Height));
        }
    }

    // ---- effects: the looks ----

    [Fact]
    public void No_effects_returns_the_same_picture()
    {
        var src = Gradient(20, 20);
        Assert.Same(src, PictureProcessor.ApplyEffects(src, PictureEffects.None));
    }

    [Fact]
    public void Invert_is_the_pixel_inversion_and_keeps_alpha()
    {
        var src = Gradient(20, 12);
        src.Set(3, 3, 10, 20, 30, 77);
        var result = PictureProcessor.ApplyEffects(src, PictureEffects.None with { Invert = true });
        AssertSame(PixelOps.Invert(src), result);
        Assert.Equal((245, 235, 225, 77), result.Get(3, 3));
    }

    [Fact]
    public void Grayscale_gives_equal_channels_and_sepia_a_brown_tone()
    {
        var src = Gradient(32, 32);
        var gray = PictureProcessor.ApplyEffects(src, PictureEffects.None with { Grayscale = true });
        for (int i = 0; i < gray.Rgba.Length; i += 4) Assert.True(gray.Rgba[i] == gray.Rgba[i + 1] && gray.Rgba[i + 1] == gray.Rgba[i + 2]);
        var sepia = PictureProcessor.ApplyEffects(Solid(4, 4, 120, 120, 120), PictureEffects.None with { Sepia = 100 });
        var (r, g, b, _) = sepia.Get(1, 1);
        Assert.True(r > g && g > b);
    }

    [Fact]
    public void Black_and_white_leaves_only_two_values_and_posterize_only_the_levels_asked_for()
    {
        var src = Gradient(64, 64);
        var bw = PictureProcessor.ApplyEffects(src, PictureEffects.None with { Threshold = 120 });
        for (int i = 0; i < bw.Rgba.Length; i += 4) Assert.True(bw.Rgba[i] is 0 or 255 && bw.Rgba[i] == bw.Rgba[i + 1] && bw.Rgba[i] == bw.Rgba[i + 2]);

        var poster = PictureProcessor.ApplyEffects(src, PictureEffects.None with { Posterize = 3 });
        var seen = new HashSet<byte>();
        for (int i = 0; i < poster.Rgba.Length; i += 4) { seen.Add(poster.Rgba[i]); seen.Add(poster.Rgba[i + 1]); seen.Add(poster.Rgba[i + 2]); }
        Assert.Subset(new HashSet<byte> { 0, 128, 255 }, seen);
        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void The_fast_loop_agrees_with_the_reference_look_for_every_combination()
    {
        var src = Gradient(40, 30);
        var cases = new[]
        {
            PictureEffects.None with { Grayscale = true, Sepia = 60 },
            PictureEffects.None with { Sepia = 40, Invert = true },
            PictureEffects.None with { Invert = true, Posterize = 5 },
            PictureEffects.None with { Grayscale = true, Threshold = 100, Invert = true },
            PictureEffects.None with { Posterize = 2 },
        };
        foreach (var fx in cases)
        {
            var result = PictureProcessor.ApplyEffects(src, fx);
            for (int y = 0; y < src.Height; y += 3)
                for (int x = 0; x < src.Width; x += 3)
                {
                    var (r, g, b, _) = src.Get(x, y);
                    var expected = EffectMath.Look(fx, r, g, b);
                    var (rr, gg, bb, _) = result.Get(x, y);
                    Assert.Equal((expected.R, expected.G, expected.B), (rr, gg, bb));
                }
        }
    }

    // ---- effects: the filters ----

    [Fact]
    public void Despeckle_removes_a_lone_speck_and_leaves_a_clean_edge_alone()
    {
        var flat = Solid(15, 15, 100, 100, 100);
        flat.Set(7, 7, 255, 0, 0);
        var cleaned = PictureProcessor.Despeckle(flat, 100);
        Assert.Equal((100, 100, 100, 255), cleaned.Get(7, 7));

        var edge = Edge(20, 10);
        AssertSame(edge, PictureProcessor.Despeckle(edge, 100));                   // a median keeps a straight edge exactly
    }

    [Fact]
    public void Despeckle_strength_blends_between_the_original_and_the_cleaned_picture()
    {
        var flat = Solid(11, 11, 100, 100, 100);
        flat.Set(5, 5, 200, 100, 100);
        var half = PictureProcessor.Despeckle(flat, 50);
        Assert.Equal(150, half.Get(5, 5).R);                                       // half way between 200 and the median 100
        Assert.Same(flat, PictureProcessor.Despeckle(flat, 0));
    }

    [Fact]
    public void Median_of_nine_is_the_middle_value_of_any_nine()
    {
        var rng = new Random(7);
        for (int n = 0; n < 500; n++)
        {
            var v = Enumerable.Range(0, 9).Select(_ => rng.Next(256)).ToArray();
            Assert.Equal(v.Order().ElementAt(4), PictureProcessor.Median9(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8]));
        }
    }

    [Fact]
    public void Blur_softens_an_edge_and_keeps_a_flat_picture_flat()
    {
        var edge = Edge(60, 20);
        var blurred = PictureProcessor.Render(edge, new PictureRecipe(60, 20) { Effects = PictureEffects.None with { Blur = 6 } });
        int before = edge.Get(30, 10).R - edge.Get(29, 10).R, after = blurred.Get(30, 10).R - blurred.Get(29, 10).R;
        Assert.True(after < before / 2, $"the step across the edge went from {before} to {after}");
        Assert.True(blurred.Get(29, 10).R > 40 && blurred.Get(30, 10).R < 200);

        var flat = Solid(30, 30, 90, 120, 150);
        var result = PictureProcessor.Render(flat, new PictureRecipe(30, 30) { Effects = PictureEffects.None with { Blur = 8 } });
        Assert.InRange(result.Get(0, 0).R, 89, 91);                                // the edges of the picture repeat: no dark rim
        Assert.InRange(result.Get(15, 15).B, 149, 151);
    }

    [Fact]
    public void Sharpen_makes_an_edge_crisper_and_leaves_a_flat_picture_alone()
    {
        var edge = Edge(60, 20);
        var sharp = PictureProcessor.Render(edge, new PictureRecipe(60, 20) { Effects = PictureEffects.None with { Blur = 2 } });
        var sharper = PictureProcessor.Render(edge, new PictureRecipe(60, 20) { Effects = PictureEffects.None with { Blur = 2, Sharpen = 100 } });
        // after a blur the edge has a ramp; sharpening steepens it
        int ramp = sharp.Get(31, 10).R - sharp.Get(28, 10).R, steeper = sharper.Get(31, 10).R - sharper.Get(28, 10).R;
        Assert.True(steeper > ramp, $"{ramp} -> {steeper}");

        var flat = Solid(30, 30, 90, 120, 150);
        var result = PictureProcessor.Render(flat, new PictureRecipe(30, 30) { Effects = PictureEffects.None with { Sharpen = 100 } });
        Assert.Equal((90, 120, 150, 255), result.Get(10, 10));
    }

    [Fact]
    public void Filters_keep_the_alpha_channel()
    {
        var src = Gradient(20, 20);
        src.Set(4, 4, 50, 60, 70, 33);
        foreach (var fx in new[] { PictureEffects.None with { NoiseReduction = 80 }, PictureEffects.None with { Sharpen = 50 } })
            Assert.Equal(33, PictureProcessor.ApplyEffects(src, fx).Get(4, 4).A);
    }

    // ---- the order of the steps ----

    [Fact]
    public void Effects_work_on_the_adjusted_colors_and_after_the_crop()
    {
        var src = Gradient(40, 20);
        var adjust = PictureAdjust.None with { Brightness = 20 };
        var recipe = new PictureRecipe(40, 20) { Crop = new RectI(5, 3, 20, 10), Adjust = adjust, Effects = PictureEffects.None with { Invert = true } };
        var result = PictureProcessor.Render(src, recipe);
        Assert.Equal((20, 10), (result.Width, result.Height));
        var (r, g, b, _) = src.Get(5 + 4, 3 + 2);
        var adjusted = AdjustMath.Apply(adjust, r, g, b);
        Assert.Equal((255 - adjusted.R, 255 - adjusted.G, 255 - adjusted.B), (result.Get(4, 2).R, result.Get(4, 2).G, result.Get(4, 2).B));
    }

    [Fact]
    public void The_free_angle_comes_after_the_quarter_turns_and_before_the_crop()
    {
        var src = Gradient(60, 40);
        var recipe = new PictureRecipe(60, 40) { Quarter = 1, Straighten = 10 };
        var (w, h) = PictureGeometry.CanvasSize(40, 60, 10);                            // the turned picture is 40 x 60 before the free angle
        Assert.Equal((w, h), recipe.CanvasSize);
        var result = PictureProcessor.Render(src, recipe);
        Assert.Equal((w, h), (result.Width, result.Height));
    }

    // ---- previews ----

    [Fact]
    public void A_slider_of_a_late_step_reuses_the_turned_canvas_the_crop_and_the_colors_it_did_not_change()
    {
        var src = Gradient(120, 80);
        var cache = new PictureProcessor.RenderCache();
        var recipe = new PictureRecipe(120, 80) { Straighten = 5, Crop = new RectI(10, 10, 80, 50), Adjust = PictureAdjust.None with { Contrast = 20 } };
        var first = PictureProcessor.RenderParts(src, recipe, 1, default, cache);

        var withEffect = recipe with { Effects = PictureEffects.None with { Grayscale = true } };
        var second = PictureProcessor.RenderParts(src, withEffect, 1, default, cache);
        Assert.Same(first.Canvas, second.Canvas);                                       // the same turned picture, not made again
        AssertSame(PictureProcessor.Render(src, withEffect), second.Result);            // and the same answer as without a cache

        var changedColors = withEffect with { Adjust = withEffect.Adjust with { Brightness = 15 } };
        var third = PictureProcessor.RenderParts(src, changedColors, 1, default, cache);
        Assert.Same(first.Canvas, third.Canvas);
        AssertSame(PictureProcessor.Render(src, changedColors), third.Result);

        var turned = changedColors with { Straighten = 9 };
        var fourth = PictureProcessor.RenderParts(src, turned, 1, default, cache);
        Assert.NotSame(first.Canvas, fourth.Canvas);                                    // a new angle needs a new canvas
        AssertSame(PictureProcessor.Render(src, turned), fourth.Result);
    }

    [Fact]
    public void A_cache_never_gives_a_stale_filter_result()
    {
        var src = Gradient(60, 40);
        var cache = new PictureProcessor.RenderCache();
        var recipe = new PictureRecipe(60, 40) { Effects = PictureEffects.None with { Blur = 3 } };
        PictureProcessor.RenderParts(src, recipe, 1, default, cache);
        var other = recipe with { Adjust = PictureAdjust.None with { Brightness = 30 } };              // same filter, other input
        AssertSame(PictureProcessor.Render(src, other), PictureProcessor.RenderParts(src, other, 1, default, cache).Result);
        var sharper = other with { Effects = other.Effects with { Sharpen = 40 } };
        AssertSame(PictureProcessor.Render(src, sharper), PictureProcessor.RenderParts(src, sharper, 1, default, cache).Result);
    }

    [Fact]
    public void Filter_sizes_shrink_with_the_preview_so_a_preview_looks_like_the_result()
    {
        var full = Edge(200, 40);
        var recipe = new PictureRecipe(200, 40) { Effects = PictureEffects.None with { Blur = 10 } };
        var exact = PictureProcessor.Render(full, recipe);
        var (reduced, scale) = PictureProcessor.Reduce(full, 100);
        var preview = PictureProcessor.Render(reduced, recipe, scale);
        var shrunkExact = PixelResampler.Resample(exact, preview.Width, preview.Height);
        Assert.True(MeanDifference(shrunkExact, preview) < 6, "the preview does not look like the result");
        // a blur smaller than a third of a pixel on the preview is dropped, not rounded up
        var tiny = new PictureRecipe(200, 40) { Effects = PictureEffects.None with { Blur = 0.5 } };
        Assert.Same(reduced, PictureProcessor.Render(reduced, tiny, scale));
    }

    [Fact]
    public void Rendering_can_be_cancelled_between_the_steps()
    {
        var src = Gradient(200, 200);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => PictureProcessor.Render(src, new PictureRecipe(200, 200) { Effects = PictureEffects.None with { Blur = 3 } }, 1, cts.Token));
    }
}
