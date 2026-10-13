using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Rendering;
using SkiaSharp;

namespace DeSignMage.Imaging.Tests;

/// <summary>How placed pictures are drawn at different zooms (crisp pixels when zoomed in, smooth when zoomed out).</summary>
public class RenderTests
{
    private static readonly byte[] Red = [255, 0, 0, 255], Green = [0, 255, 0, 255], Blue = [0, 0, 255, 255], White = [255, 255, 255, 255];

    /// <summary>2 x 2 checker: red green / blue white.</summary>
    private static PixelBuffer Checker2()
    {
        var p = new PixelBuffer(2, 2);
        void Put(int x, int y, byte[] c) => p.Set(x, y, c[0], c[1], c[2], c[3]);
        Put(0, 0, Red); Put(1, 0, Green); Put(0, 1, Blue); Put(1, 1, White);
        return p;
    }

    private static SKBitmap Render(Document doc, double zoom, int width, int height, SKColor? background = null)
    {
        using var renderer = new SceneRenderer(doc) { ViewScale = zoom };
        var pic = renderer.GetDocumentPicture(null);
        var bmp = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(background ?? SKColors.Gray);
            canvas.Scale((float)zoom);
            canvas.DrawPicture(pic.Picture);
        }
        pic.Release();
        return bmp;
    }

    private static Document DocWith(PixelBuffer pixels, double width, double height, out ImageObject image)
    {
        var doc = new Document(PageSetup.FromInches(24, 12));
        string id = doc.Assets.Add(ImageCodec.EncodePng(pixels), "image/png");
        image = new ImageObject(id, width, height, pixels.Width, pixels.Height);
        doc.Insert(doc.ActiveLayer, -1, image);
        return doc;
    }

    [Fact]
    public void A_picture_zoomed_far_in_shows_crisp_pixels()
    {
        var doc = DocWith(Checker2(), 4, 4, out _);          // 4 pt wide, 2 pixels: 2 pt per pixel
        using var bmp = Render(doc, zoom: 50, 200, 200);     // 100 screen pixels per picture pixel

        Assert.Equal(new SKColor(255, 0, 0), bmp.GetPixel(99, 50));     // right up to the pixel boundary: still pure red
        Assert.Equal(new SKColor(0, 255, 0), bmp.GetPixel(101, 50));    // and pure green just beyond it, no blending
        Assert.Equal(new SKColor(0, 0, 255), bmp.GetPixel(50, 150));
        Assert.Equal(new SKColor(255, 255, 255), bmp.GetPixel(150, 150));
    }

    [Fact]
    public void A_moderately_enlarged_picture_is_smoothed()
    {
        var doc = DocWith(Checker2(), 4, 4, out _);
        using var bmp = Render(doc, zoom: 1.5, 10, 10);      // 3 screen pixels per picture pixel: smoothed, not blocky

        // Somewhere across the boundary between red and green there is a blend of both.
        bool blended = Enumerable.Range(1, 5).Any(x =>
        {
            var c = bmp.GetPixel(x, 1);
            return c.Red is > 40 and < 215 && c.Green is > 40 and < 215;
        });
        Assert.True(blended);
    }

    [Fact]
    public void A_rotated_and_mirrored_frame_still_draws_in_place()
    {
        var doc = DocWith(Checker2(), 4, 4, out var img);
        img.Transform = Affine2.Scale(-1, 1) * Affine2.Translation(4, 0);   // mirrored left-right
        using var bmp = Render(doc, zoom: 50, 200, 200);
        Assert.Equal(new SKColor(0, 255, 0), bmp.GetPixel(50, 50));         // green is now on the left
        Assert.Equal(new SKColor(255, 0, 0), bmp.GetPixel(150, 50));
    }

    [Fact]
    public void A_big_photo_zoomed_out_is_averaged_not_aliased()
    {
        // 4000 x 4000 pixels of one-pixel black/white stripes: any honest downscale is mid-gray; point sampling would be black or white.
        var stripes = new PixelBuffer(4000, 4000);
        for (int y = 0; y < 4000; y++)
            for (int x = 0; x < 4000; x++)
            {
                byte v = (byte)(x % 2 == 0 ? 0 : 255);
                stripes.Set(x, y, v, v, v);
            }
        var doc = DocWith(stripes, 400, 400, out _);         // 10 picture pixels per point
        using var bmp = Render(doc, zoom: 0.25, 100, 100);   // 100 x 100 screen pixels for 4000 x 4000 picture pixels

        for (int x = 20; x < 80; x += 7)
        {
            var c = bmp.GetPixel(x, 50);
            Assert.InRange(c.Red, 90, 165);
        }
    }

    [Fact]
    public void Export_quality_is_used_without_a_view_scale()
    {
        var doc = DocWith(Checker2(), 4, 4, out _);
        using var renderer = new SceneRenderer(doc);          // ViewScale 0: drawing for export, always full quality and smooth
        var pic = renderer.GetDocumentPicture(null);
        using var bmp = new SKBitmap(200, 200);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Gray);
            canvas.Scale(50);
            canvas.DrawPicture(pic.Picture);
        }
        pic.Release();
        var edge = bmp.GetPixel(100, 50);
        Assert.InRange(edge.Green, 40, 215);                   // blended across the boundary (linear), not a hard step
    }

    [Fact]
    public void A_missing_picture_draws_a_crossed_frame_instead_of_failing()
    {
        var doc = new Document(PageSetup.FromInches(24, 12));
        doc.Insert(doc.ActiveLayer, -1, new ImageObject("nothing", 4, 4));
        using var bmp = Render(doc, zoom: 50, 200, 200, SKColors.White);
        Assert.NotEqual(SKColors.White, bmp.GetPixel(100, 100));   // the crossed lines meet in the middle
    }

    [Fact]
    public void Changing_the_zoom_bucket_rerecords_the_document_only_when_it_has_pictures()
    {
        var withImage = DocWith(Checker2(), 4, 4, out _);
        using (var r = new SceneRenderer(withImage) { ViewScale = 1 })
        {
            var a = r.GetDocumentPicture(null);
            r.ViewScale = 1.1;                                 // same half-octave: same picture
            var b = r.GetDocumentPicture(null);
            Assert.Same(a.Picture, b.Picture);
            r.ViewScale = 8;                                   // far away: new picture
            var c = r.GetDocumentPicture(null);
            Assert.NotSame(a.Picture, c.Picture);
            a.Release(); b.Release(); c.Release();
        }

        var plain = new Document(PageSetup.FromInches(24, 12));
        plain.Insert(plain.ActiveLayer, -1, new RectangleObject(10, 10) { Style = ObjectStyle.FillOnly(DocColor.Black) });
        using (var r = new SceneRenderer(plain) { ViewScale = 1 })
        {
            var a = r.GetDocumentPicture(null);
            r.ViewScale = 8;
            var b = r.GetDocumentPicture(null);
            Assert.Same(a.Picture, b.Picture);                 // nothing zoom-dependent: no re-record when zooming
            a.Release(); b.Release();
        }
    }
}
