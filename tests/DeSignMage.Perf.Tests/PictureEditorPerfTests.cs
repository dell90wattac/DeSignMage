using System.Diagnostics;
using DeSignMage.Core.Imaging;
using DeSignMage.Imaging;
using Xunit.Abstractions;

namespace DeSignMage.Perf.Tests;

/// <summary>
/// The Picture Editor must stay responsive with a big photo on an older PC (8th-gen Intel): previews are made from a copy reduced to at most
/// <see cref="PictureProcessor.PreviewSide"/> pixels, so what one preview costs does not depend on the size of the photo; only opening the editor
/// (decoding, reducing once) and the final OK at full resolution grow with it. Each check takes the best of several runs.
/// </summary>
[Collection("perf")]
public class PictureEditorPerfTests(ITestOutputHelper output)
{
    private static PixelBuffer Photo(int w, int h)
    {
        var p = new PixelBuffer(w, h);
        var d = p.Rgba;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                d[i] = (byte)(x * 255L / w); d[i + 1] = (byte)(y * 255L / h); d[i + 2] = (byte)((x ^ y) & 255); d[i + 3] = 255;
            }
        return p;
    }

    private static double Best(int runs, Func<double> run)
    {
        double best = double.MaxValue;
        for (int i = 0; i < runs; i++) best = Math.Min(best, run());
        return best;
    }

    private static double Time(Action a)
    {
        var watch = Stopwatch.StartNew();
        a();
        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Everything the editor can do to a picture at once: turn, crop, and every adjustment.</summary>
    private static PictureRecipe Everything(int w, int h) => new PictureRecipe(w, h)
    {
        Straighten = 2.5,
        Crop = new RectI(w / 10, h / 10, w * 7 / 10, h * 7 / 10),
        Adjust = PictureAdjust.None with { Brightness = 10, Contrast = 15, Gamma = 1.2, Saturation = 20, Hue = 15, BalanceR = 5, LowR = 5, HighG = 250 },
    };

    [Fact]
    public void A_preview_of_a_24_megapixel_photo_is_made_from_a_reduced_copy_and_is_quick()
    {
        var photo = Photo(6000, 4000);
        PixelBuffer work = null!;
        double scale = 1;
        double reduce = Time(() => (work, scale) = PictureProcessor.Reduce(photo));
        Assert.True(Math.Max(work.Width, work.Height) <= PictureProcessor.PreviewSide);
        var recipe = Everything(6000, 4000);

        double preview = Best(3, () => Time(() => PictureProcessor.Render(work, recipe, scale)));
        double sliderOnly = Best(3, () => Time(() => PictureProcessor.Render(work, new PictureRecipe(6000, 4000) { Adjust = recipe.Adjust }, scale)));
        output.WriteLine($"24 MP photo: reduce once {reduce:0} ms; preview of every edit at once {preview:0} ms; an adjust-only preview {sliderOnly:0} ms (working copy {work.Width} x {work.Height})");

        Assert.True(preview < 1500, $"a preview took {preview:0} ms");
        Assert.True(sliderOnly < 600, $"an adjust-only preview took {sliderOnly:0} ms");
        Assert.True(reduce < 8000, $"reducing the photo once took {reduce:0} ms");
    }

    [Fact]
    public void What_a_preview_costs_does_not_depend_on_the_size_of_the_photo()
    {
        var small = Photo(2400, 1600);
        var big = Photo(6000, 4000);
        var (workSmall, scaleSmall) = PictureProcessor.Reduce(small);
        var (workBig, scaleBig) = PictureProcessor.Reduce(big);
        double a = Best(3, () => Time(() => PictureProcessor.Render(workSmall, Everything(2400, 1600), scaleSmall)));
        double b = Best(3, () => Time(() => PictureProcessor.Render(workBig, Everything(6000, 4000), scaleBig)));
        output.WriteLine($"preview from 2400 x 1600: {a:0} ms; from 6000 x 4000: {b:0} ms");
        Assert.True(b < Math.Max(a * 2.5, 150), $"{b:0} ms for the big photo against {a:0} ms for the small one");
    }

    [Fact]
    public void The_real_result_at_full_resolution_of_a_24_megapixel_photo_finishes_in_reasonable_time()
    {
        var photo = Photo(6000, 4000);
        var recipe = Everything(6000, 4000);
        PixelBuffer result = null!;
        double render = Time(() => result = PictureProcessor.Render(photo, recipe));
        double encode = Time(() => ImageCodec.EncodePng(result));
        output.WriteLine($"full resolution: render {render:0} ms, PNG {encode:0} ms ({result.Width} x {result.Height})");
        Assert.Equal(recipe.ResultSize, (result.Width, result.Height));
        Assert.True(render < 30000, $"rendering took {render:0} ms");
    }

    [Fact]
    public void Dragging_an_effects_slider_on_a_24_megapixel_photo_costs_one_preview_of_the_reduced_copy_only()
    {
        var (work, scale) = PictureProcessor.Reduce(Photo(6000, 4000));
        var cache = new PictureProcessor.RenderCache();
        var baseRecipe = Everything(6000, 4000);
        PictureProcessor.RenderParts(work, baseRecipe, scale, default, cache);   // what the window already showed: turned, cropped, adjusted

        // The slider gives a new value at every step (n counts the steps), so the filter of the step before cannot be reused.
        double Drag(string name, Func<int, PictureEffects> effects)
        {
            int n = 0;
            double ms = Best(3, () =>
            {
                var recipe = baseRecipe with { Effects = effects(++n) };
                return Time(() => PictureProcessor.RenderParts(work, recipe, scale, default, cache));
            });
            output.WriteLine($"{name}: {ms:0} ms");
            return ms;
        }

        double looks = Drag("grayscale + sepia + invert", n => PictureEffects.None with { Grayscale = true, Sepia = 40 + n, Invert = true });
        double threshold = Drag("black & white", n => PictureEffects.None with { Threshold = 120 + n });
        double posterize = Drag("posterize", n => PictureEffects.None with { Posterize = 3 + n });
        double blur = Drag("blur radius 12", n => PictureEffects.None with { Blur = 12 + n * 0.1 });
        double sharpen = Drag("sharpen 80", n => PictureEffects.None with { Sharpen = 80 + n });
        double noise = Drag("noise reduction 40", n => PictureEffects.None with { NoiseReduction = 40 + n });
        double noise2 = Drag("noise reduction 90 (two passes)", n => PictureEffects.None with { NoiseReduction = 90 + n });
        double all = Drag("all effects at once", n => new PictureEffects { NoiseReduction = 60 + n, Blur = 3, Sharpen = 50, Grayscale = true, Sepia = 30, Threshold = 128 });

        // Generous: these are Debug-build timings on a developer machine; the point is that a drag step never waits for the 24 MP original.
        Assert.True(looks < 400 && threshold < 400 && posterize < 400, $"looks {looks:0} / {threshold:0} / {posterize:0} ms");
        Assert.True(blur < 1000, $"blur {blur:0} ms");
        Assert.True(sharpen < 1200, $"sharpen {sharpen:0} ms");
        Assert.True(noise < 1500 && noise2 < 3000, $"noise reduction {noise:0} / {noise2:0} ms");
        Assert.True(all < 4000, $"everything {all:0} ms");
    }

    [Fact]
    public void An_effects_only_change_does_not_repeat_the_turn_the_crop_or_the_colors()
    {
        var (work, scale) = PictureProcessor.Reduce(Photo(6000, 4000));
        var cache = new PictureProcessor.RenderCache();
        var recipe = Everything(6000, 4000);
        double cold = Time(() => PictureProcessor.RenderParts(work, recipe, scale, default, cache));
        double warm = Best(3, () => Time(() => PictureProcessor.RenderParts(work, recipe with { Effects = PictureEffects.None with { Invert = true } }, scale, default, cache)));
        output.WriteLine($"first preview {cold:0} ms, then an effects-only change {warm:0} ms");
        Assert.True(warm < cold / 2 + 20, $"{warm:0} ms against {cold:0} ms");
    }

    [Fact]
    public void The_real_result_with_every_effect_at_full_resolution_of_a_24_megapixel_photo_finishes_in_reasonable_time()
    {
        var photo = Photo(6000, 4000);
        var recipe = Everything(6000, 4000) with
        {
            Quarter = 1, Mirror = true,
            Effects = new PictureEffects { NoiseReduction = 60, Blur = 3, Sharpen = 60, Grayscale = true, Sepia = 20, Invert = true, Threshold = 0, Posterize = 8 },
        };
        PixelBuffer result = null!;
        double render = Time(() => result = PictureProcessor.Render(photo, recipe));
        output.WriteLine($"full resolution with every effect: {render:0} ms ({result.Width} x {result.Height})");
        Assert.Equal(recipe.ResultSize, (result.Width, result.Height));
        Assert.True(render < 90000, $"rendering took {render:0} ms");
    }

    [Fact]
    public void Auto_trim_and_auto_levels_on_the_reduced_copy_are_instant()
    {
        var (work, _) = PictureProcessor.Reduce(Photo(6000, 4000));
        double trim = Best(3, () => Time(() => PictureProcessor.AutoTrim(work)));
        double levels = Best(3, () => Time(() => PictureProcessor.AutoLevels(work, PictureAdjust.None)));
        output.WriteLine($"auto-trim {trim:0} ms, auto levels {levels:0} ms");
        Assert.True(trim < 500 && levels < 500);
    }
}
