using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Lyt;
using DeSignMage.TestSupport;
using SkiaSharp;
using Xunit.Abstractions;

namespace DeSignMage.Formats.Tests;

/// <summary>
/// Gradient and fade fills of legacy layouts: the pin design (art-e) has 15 plain-color layers that fade through a transparency ramp
/// (the rays behind the numbers, the pink swoosh, the two layers of the title face).
/// </summary>
public class LytGradientTests(ITestOutputHelper output)
{
    [LocalFact("layouts/art-e.lyt")]
    public void The_pin_design_fades_come_in_as_gradient_fills()
    {
        var doc = LytImporter.Open(TestFiles.Layout("art-e.lyt")).Document;
        var gradients = doc.AllObjects().Where(o => o.Style.Fill is GradientPaint).ToList();
        output.WriteLine($"{gradients.Count} gradient fills");
        Assert.Equal(15, gradients.Count);   // 13 linear (rays, swoosh) + 2 elliptical (the title face layers)

        Assert.Equal(13, gradients.Count(o => ((GradientPaint)o.Style.Fill!).Kind == GradientKind.Linear));
        Assert.Equal(2, gradients.Count(o => ((GradientPaint)o.Style.Fill!).Kind == GradientKind.Ellipse));
        foreach (var o in gradients)
        {
            var g = (GradientPaint)o.Style.Fill!;
            Assert.Equal(2, g.Stops.Count);
            // the same color on both stops, only the opacity differs: a color fading to nothing
            Assert.Equal(g.Stops[0].Color with { A = 255 }, g.Stops[1].Color with { A = 255 });
            Assert.NotEqual(g.Stops[0].Color.A, g.Stops[1].Color.A);
        }
    }

    // ---- the mapping rules, on made-up blocks ----

    private static readonly DocColor Navy = new(9, 28, 80, 255, "Dark Blue MB97", "Designers Colors");

    /// <summary>A 10 x 10 outline in node space (y up), so local space is x 0..10 and y -10..0.</summary>
    private static PathGeometry Square() => new PathBuilder().MoveTo(0, 0).LineTo(10, 0).LineTo(10, -10).LineTo(0, -10).Close().Build();

    private static LytColor Lyt(byte r, byte g, byte b) => new("", "", LytColor.ModelRgb, r, g, b, 0);

    private static LytBlend Fade(uint transType, double min = 0, double max = 1, bool swap = false, params float[] points)
    {
        var b = new LytBlend { GradType = 0, TransType = transType, TransMin = (float)min, TransMax = (float)max, TransSwap = swap };
        for (int i = 0; i < points.Length; i++) b.TransPoints[i] = points[i];
        return b;
    }

    [Fact]
    public void A_linear_fade_is_clear_at_the_start_and_as_opaque_as_one_minus_min_at_the_end()
    {
        var g = LytGradients.TryCreate(Fade(2, min: 0.44, max: 1, swap: false, 0, 0, 10, 0), Navy, Square())!;
        Assert.Equal(GradientKind.Linear, g.Kind);
        Assert.Equal([new Vec2(0, 1), new Vec2(1, 1)], g.Points);                           // node (x, y) is local (x, -y), then relative to the bounds
        Assert.Equal(0, g.Stops[0].Color.A);
        Assert.Equal((int)Math.Round(255 * 0.56), g.Stops[1].Color.A);
        Assert.Equal(Navy with { A = 0 }, g.Stops[0].Color);                                // the same named color, only its opacity differs
    }

    [Fact]
    public void Swap_exchanges_a_linear_fade_and_inverts_a_radial_one_and_max_lifts_the_floor()
    {
        var swapped = LytGradients.TryCreate(Fade(2, 0, 1, true, 0, 0, 10, 0), Navy, Square())!;
        Assert.Equal([255, 0], swapped.Stops.Select(s => (int)s.Color.A));

        var floor = LytGradients.TryCreate(Fade(2, 0, 0.5, false, 0, 0, 10, 0), Navy, Square())!;   // never more than 50 % transparent
        Assert.Equal(128, floor.Stops[0].Color.A);
        Assert.Equal(255, floor.Stops[1].Color.A);

        // a circle or ellipse is opaque in the middle by default (as in the pin design) and Swap turns it upside down
        var circle = LytGradients.TryCreate(Fade(3, 0.44, 1, false, 5, 5, 10, 5), Navy, Square())!;
        Assert.Equal(GradientKind.Circle, circle.Kind);
        Assert.Equal(new Vec2(0.5, 0.5), circle.Points[0]);
        Assert.Equal([143, 0], circle.Stops.Select(s => (int)s.Color.A));
        var inverted = LytGradients.TryCreate(Fade(3, 0.44, 1, true, 5, 5, 10, 5), Navy, Square())!;
        Assert.Equal([112, 255], inverted.Stops.Select(s => (int)s.Color.A));
    }

    [Fact]
    public void An_ellipse_fade_takes_its_two_axes_and_horizontal_or_vertical_use_the_whole_object()
    {
        var e = LytGradients.TryCreate(Fade(4, 0, 1, false, 5, 5, 9, 5, 5, 8), Navy, Square())!;
        Assert.Equal(GradientKind.Ellipse, e.Kind);
        Assert.Equal([new Vec2(0.5, 0.5), new Vec2(0.9, 0.5), new Vec2(0.5, 0.2)], e.Points);

        var h = LytGradients.TryCreate(Fade(0xC), Navy, Square())!;
        Assert.Equal(GradientKind.Horizontal, h.Kind);
        Assert.Equal(GradientPaint.DefaultPoints(GradientKind.Horizontal), h.Points);       // left edge to right edge, whatever the block's points say
        var v = LytGradients.TryCreate(Fade(0xD), Navy, Square())!;
        Assert.Equal(GradientKind.Vertical, v.Kind);
    }

    [Fact]
    public void Flat_transparency_and_a_block_without_a_fade_are_not_gradients()
    {
        Assert.Null(LytGradients.TryCreate(Fade(1, 0.5), Navy, Square()));                  // flat opacity is not imported
        Assert.Null(LytGradients.TryCreate(Fade(0), Navy, Square()));
        Assert.Null(LytGradients.TryCreate(Fade(2, 0, 0, false, 0, 0, 10, 0), Navy, Square()));   // Max 0: nothing fades
        Assert.Null(LytGradients.TryCreate(Fade(2, 0, 1, false, 4, 4, 4, 4), Navy, Square()));    // no length
        Assert.Null(LytGradients.TryCreate(Fade(2, 0, 1, false, float.NaN, 0, 10, 0), Navy, Square()));
        Assert.Null(LytGradients.TryCreate(new LytBlend { GradType = 7 }, Navy, Square()));
    }

    [Fact]
    public void A_gradient_block_orders_its_colors_along_the_line_and_keeps_highlights()
    {
        var b = new LytBlend { GradType = 1, NodeCount = 4 };
        float[] pts = [0, 0, 10, 0, 7, 0, 2, 0];                                            // nodes 0 and 1 are the ends, 2 sits at 0.7, 3 at 0.2
        for (int i = 0; i < 8; i++) b.Points[i] = pts[i];
        b.Colors[0] = Lyt(255, 0, 0); b.Colors[1] = Lyt(0, 0, 255); b.Colors[2] = Lyt(0, 255, 0); b.Colors[3] = Lyt(255, 255, 0);
        var g = LytGradients.TryCreate(b, Navy, Square())!;

        Assert.Equal(GradientKind.Linear, g.Kind);
        Assert.Equal([0, 0.2, 0.7, 1], g.Stops.Select(s => Math.Round(s.Offset, 3)));
        Assert.Equal([255, 255, 0], [g.Stops[1].Color.R, g.Stops[1].Color.G, g.Stops[1].Color.B]);
        Assert.Equal(new Vec2(0, 1), g.Points[0]);
        Assert.Equal(new Vec2(1, 1), g.Points[1]);

        // a node beyond the first four, flagged as a highlight
        b.NodeCount = 5;
        b.ExtraNodes.Add(new LytBlendNode(5, 0, Lyt(255, 255, 255), 1));
        var h = LytGradients.TryCreate(b, Navy, Square())!;
        Assert.Equal(5, h.Stops.Count);
        Assert.Single(h.Stops, s => s.Highlight);
        Assert.Equal(0.5, h.Stops.Single(s => s.Highlight).Offset, 3);
    }

    [Fact]
    public void A_color_that_cannot_be_read_falls_back_to_the_objects_color_and_other_kinds_map()
    {
        var b = new LytBlend { GradType = 6, NodeCount = 2 };                               // vertical
        float[] pts = [5, 0, 5, 10];
        for (int i = 0; i < 4; i++) b.Points[i] = pts[i];
        b.Colors[1] = Lyt(10, 20, 30);                                                       // node 0 has no valid color
        var g = LytGradients.TryCreate(b, Navy, Square())!;
        Assert.Equal(GradientKind.Vertical, g.Kind);
        Assert.Equal(Navy, g.Stops[0].Color);
        Assert.Equal(new DocColor(10, 20, 30, 255, null, null), g.Stops[1].Color with { Name = null, Vendor = null });

        var circle = LytGradients.TryCreate(new LytBlend { GradType = 2, NodeCount = 2, Points = { [0] = 5, [1] = 5, [2] = 10, [3] = 5 } }, Navy, Square())!;
        Assert.Equal(GradientKind.Circle, circle.Kind);
    }

    [LocalFact("layouts/art-e.lyt")]
    public void The_pin_design_renders_its_fades()
    {
        var doc = LytImporter.Open(TestFiles.Layout("art-e.lyt")).Document;
        var area = doc.GetContentBounds().Inflate(8);
        double scale = 1170.0 / area.Width;
        using var bmp = LytRenderTests.Render(doc, area, scale);
        LytRenderTests.Save(bmp, "art-e-gradients.png");
        if (Environment.GetEnvironmentVariable("DSM_SHOTS") is { Length: > 0 } dir)
            File.WriteAllBytes(Path.Combine(dir, "effect-gradient-art-e.png"), SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100).ToArray());
        Assert.True(bmp.Width > 500);
    }
}
