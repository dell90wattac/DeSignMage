using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;

namespace DeSignMage.Imaging.Tests;

/// <summary>The pixel work of Image ▸ Resample.</summary>
public class PixelResamplerTests
{
    private static PixelBuffer Flat(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var p = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) p.Set(x, y, r, g, b, a);
        return p;
    }

    [Fact]
    public void The_same_size_is_an_exact_copy()
    {
        var src = Samples.Grid();
        var copy = PixelResampler.Resample(src, 3, 2);
        Samples.AssertSame(src, copy);
        Assert.NotSame(src.Rgba, copy.Rgba);
    }

    [Theory]
    [InlineData(100, 100, 37, 41)]       // down, not by an even factor
    [InlineData(100, 100, 50, 50)]       // down by two
    [InlineData(20, 10, 80, 40)]         // up
    [InlineData(20, 10, 33, 71)]         // up one way, ...
    [InlineData(64, 64, 1, 1)]           // to a single pixel
    public void A_flat_color_stays_exactly_that_color(int w, int h, int nw, int nh)
    {
        var src = Flat(w, h, 200, 100, 50);
        var r = PixelResampler.Resample(src, nw, nh);
        Assert.Equal((nw, nh), (r.Width, r.Height));
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                var (cr, cg, cb, ca) = r.Get(x, y);
                Assert.InRange(cr, 199, 201);
                Assert.InRange(cg, 99, 101);
                Assert.InRange(cb, 49, 51);
                Assert.Equal(255, ca);
            }
    }

    [Fact]
    public void Shrinking_fine_stripes_gives_an_average_not_jagged_black_and_white()
    {
        var stripes = new PixelBuffer(400, 400);
        for (int y = 0; y < 400; y++) for (int x = 0; x < 400; x++) { byte v = (byte)(x % 2 == 0 ? 0 : 255); stripes.Set(x, y, v, v, v); }

        var r = PixelResampler.Resample(stripes, 50, 50);

        for (int x = 5; x < 45; x += 3) Assert.InRange(r.Get(x, 20).R, 100, 155);
    }

    [Fact]
    public void Enlarging_is_smooth_between_neighbours_and_keeps_the_corners()
    {
        var src = new PixelBuffer(2, 1);
        src.Set(0, 0, 0, 0, 0);
        src.Set(1, 0, 255, 255, 255);

        var r = PixelResampler.Resample(src, 16, 4);

        int previous = -1;
        for (int x = 0; x < 16; x++)
        {
            int v = r.Get(x, 1).R;
            Assert.True(v >= previous, $"not rising at {x}: {previous} -> {v}");   // a ramp (bicubic may overshoot only at the ends, which clamp)
            previous = v;
        }
        Assert.InRange(r.Get(0, 1).R, 0, 20);
        Assert.InRange(r.Get(15, 1).R, 235, 255);
        Assert.InRange(r.Get(8, 1).R, 100, 160);                                    // the middle is gray: it was blended, not stepped
    }

    [Fact]
    public void The_edge_of_a_cut_out_has_no_dark_fringe()
    {
        // Left half opaque red, right half fully transparent black: blending without alpha would darken the edge.
        var src = new PixelBuffer(8, 8);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) if (x < 4) src.Set(x, y, 255, 0, 0, 255); else src.Set(x, y, 0, 0, 0, 0);

        var r = PixelResampler.Resample(src, 32, 32);

        bool sawPartial = false;
        for (int x = 0; x < 32; x++)
        {
            var (cr, cg, cb, ca) = r.Get(x, 16);
            if (ca > 10) Assert.True(cr >= 240 && cg <= 15 && cb <= 15, $"x={x}: ({cr},{cg},{cb},{ca}) is not red");
            if (ca is > 20 and < 235) sawPartial = true;
        }
        Assert.True(sawPartial, "the edge should be a soft transition");
        Assert.Equal(255, r.Get(2, 16).A);
        Assert.Equal(0, r.Get(30, 16).A);
    }

    [Fact]
    public void Transparent_pixels_stay_transparent()
    {
        var r = PixelResampler.Resample(Flat(10, 10, 9, 9, 9, 0), 5, 5);
        Assert.All(Enumerable.Range(0, 25), i => Assert.Equal(0, r.Rgba[i * 4 + 3]));
    }

    [Fact]
    public void Down_then_up_keeps_the_general_picture()
    {
        var src = new PixelBuffer(64, 64);
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) src.Set(x, y, (byte)(x * 4), (byte)(y * 4), 128);
        var round = PixelResampler.Resample(PixelResampler.Resample(src, 16, 16), 64, 64);
        for (int i = 8; i < 56; i += 8)
        {
            Assert.InRange(round.Get(i, 32).R, src.Get(i, 32).R - 12, src.Get(i, 32).R + 12);
            Assert.InRange(round.Get(32, i).G, src.Get(32, i).G - 12, src.Get(32, i).G + 12);
        }
    }

    [Fact]
    public void A_picture_of_nothing_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelResampler.Resample(Samples.Grid(), 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelResampler.Resample(Samples.Grid(), 4, 0));
    }

    [Fact]
    public void A_large_resample_works()
    {
        var big = Flat(3000, 2000, 10, 20, 30);
        var r = PixelResampler.Resample(big, 750, 500);
        Assert.Equal((750, 500), (r.Width, r.Height));
        Assert.InRange(r.Get(375, 250).G, 19, 21);
    }
}

public class ResampleServiceTests
{
    private readonly SkiaImageService _svc = new();
    private readonly Document _doc = Samples.Doc();

    [Fact]
    public void The_service_stores_the_resampled_picture_as_a_new_asset_and_keeps_the_original()
    {
        var src = Samples.Grid();
        string id = _doc.Assets.Add(ImageCodec.EncodePng(src), "image/png");

        var px = _svc.Apply(_doc, id, new ImageOp.Resample(6, 4))!;

        Assert.Equal((6, 4), (px.PixelWidth, px.PixelHeight));
        Assert.NotEqual(id, px.AssetId);
        Assert.Equal("image/png", _doc.Assets.Get(px.AssetId)!.MediaType);
        var back = ImageCodec.Decode(_doc.Assets.Get(px.AssetId)!.Data);
        Assert.Equal((6, 4), (back.Width, back.Height));
        // The original pixels are untouched, byte for byte, which is what Undo relies on.
        Samples.AssertSame(src, ImageCodec.Decode(_doc.Assets.Get(id)!.Data));
    }

    [Fact]
    public void Resampling_back_to_the_same_size_stores_identical_pixels()
    {
        var src = Samples.Grid();
        string id = _doc.Assets.Add(ImageCodec.EncodePng(src), "image/png");
        var px = _svc.Apply(_doc, id, new ImageOp.Resample(3, 2))!;
        Samples.AssertSame(src, ImageCodec.Decode(_doc.Assets.Get(px.AssetId)!.Data));
    }

    [Fact]
    public void A_jpeg_is_resampled_too()
    {
        string id = _doc.Assets.Add(CodecTests.EncodeJpeg(Flat(40, 40)), "image/jpeg");
        var px = _svc.Apply(_doc, id, new ImageOp.Resample(10, 10))!;
        Assert.Equal((10, 10), (px.PixelWidth, px.PixelHeight));
    }

    private static PixelBuffer Flat(int w, int h)
    {
        var p = new PixelBuffer(w, h);
        for (int i = 0; i < p.Rgba.Length; i += 4) { p.Rgba[i] = 90; p.Rgba[i + 1] = 120; p.Rgba[i + 2] = 200; p.Rgba[i + 3] = 255; }
        return p;
    }
}
