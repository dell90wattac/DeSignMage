using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Lyt;
using DeSignMage.Rendering;
using SkiaSharp;
using Xunit.Abstractions;
using DeSignMage.TestSupport;

namespace DeSignMage.Formats.Tests;

/// <summary>Renders imported documents with the app's own renderer, to catch what numbers alone would miss.</summary>
public class LytRenderTests(ITestOutputHelper output)
{
    internal static SKBitmap Render(Document doc, Rect2 area, double pixelsPerPoint)
    {
        int w = Math.Max(1, (int)Math.Ceiling(area.Width * pixelsPerPoint));
        int h = Math.Max(1, (int)Math.Ceiling(area.Height * pixelsPerPoint));
        var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        canvas.Scale((float)pixelsPerPoint);
        canvas.Translate((float)-area.Left, (float)-area.Top);
        using var renderer = new SceneRenderer(doc);
        var pic = renderer.GetDocumentPicture(null);
        canvas.DrawPicture(pic.Picture);
        pic.Release();
        return bmp;
    }

    internal static void Save(SKBitmap bmp, string name)
    {
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(Path.Combine(TestFiles.Artifacts, name), data.ToArray());
    }

    [LocalFact("layouts/sample.lyt")]
    public void Sample_renders_the_logo_as_stored()
    {
        var doc = LytImporter.Open(TestFiles.Sample).Document;
        using var bmp = Render(doc, doc.Page.Bounds, 0.5);
        Save(bmp, "sample-page.png");

        // Page is 1818 x 670 pt; at 0.5 px/pt a point (x, y) of the page is pixel (x/2, y/2).
        var black = bmp.GetPixel(60, 100);                 // inside the black background, left of the mark
        Assert.True(black is { Red: < 30, Green: < 30, Blue: < 30 }, $"background pixel was {black}");

        var margin = bmp.GetPixel(5, 5);                   // outside the black rectangle, on the white page
        Assert.True(margin is { Red: > 240, Green: > 240, Blue: > 240 }, $"margin pixel was {margin}");

        // The gold mark and the white lettering must both be present on the black background.
        int gold = 0, white = 0;
        for (int y = 90; y < 250; y++)
            for (int x = 15; x < 900; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Red > 200 && p.Green is > 150 and < 215 && p.Blue < 80) gold++;
                else if (p.Red > 230 && p.Green > 230 && p.Blue > 230) white++;
            }
        output.WriteLine($"gold pixels {gold}, white pixels {white}");
        Assert.True(gold > 800, $"expected the gold mark, found {gold} gold pixels");
        Assert.True(white > 2000, $"expected the white lettering, found {white} white pixels");
    }

    [LocalTheory("layouts/art-b.lyt", "layouts/art-c.lyt")]
    [InlineData("art-c.lyt", 14)]
    [InlineData("art-b.lyt", 3)]
    public void Placed_photos_come_through_as_real_pictures(string relativePath, int expectedImages)
    {
        var result = LytImporter.Open(TestFiles.Layout(relativePath));
        var images = result.Document.AllObjects().OfType<ImageObject>().ToList();

        Assert.Equal(expectedImages, images.Count);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("extra parameter block"));
        foreach (var img in images)
        {
            var asset = result.Document.Assets.Get(img.AssetId)!;
            using var decoded = SKBitmap.Decode(asset.Data);
            Assert.NotNull(decoded);
            Assert.True(decoded.Width >= 8 && decoded.Height >= 8, $"{decoded.Width}x{decoded.Height}");

            // The picture's shape matches its frame (no stretching to the wrong box).
            double picture = decoded.Width / (double)decoded.Height, frame = img.Width / img.Height;
            Assert.InRange(frame / picture, 0.5, 2.0);
        }
    }

    [LocalFact("layouts/art-c.lyt")]
    public void The_largest_photo_draws_real_picture_content_where_the_file_places_it()
    {
        var doc = LytImporter.Open(TestFiles.Layout("art-c.lyt")).Document;
        var photo = doc.AllObjects().OfType<ImageObject>().OrderByDescending(i => i.GetWorldBounds().Width * i.GetWorldBounds().Height).First();
        var area = photo.GetWorldBounds();

        using var bmp = Render(doc, area, 0.5);
        Save(bmp, "largest-photo.png");

        // A picture has many different tones; an empty frame or a flat colour would not.
        var lum = new List<double>();
        for (int y = 0; y < bmp.Height; y += 3)
            for (int x = 0; x < bmp.Width; x += 3)
            {
                var p = bmp.GetPixel(x, y);
                lum.Add(0.299 * p.Red + 0.587 * p.Green + 0.114 * p.Blue);
            }
        double mean = lum.Average(), sd = Math.Sqrt(lum.Average(v => (v - mean) * (v - mean)));
        output.WriteLine($"largest photo {area.Width / 72:0.0} x {area.Height / 72:0.0} in, luminance sd {sd:0.0}");
        Assert.True(sd > 20, $"the photo area looks flat (sd {sd:0.0})");
    }

    [LocalFact("layouts/sample.lyt")]
    public void Whole_sample_including_the_pasteboard_renders()
    {
        var doc = LytImporter.Open(TestFiles.Sample).Document;
        var area = doc.GetContentBounds().Union(doc.Page.Bounds).Inflate(10);
        using var bmp = Render(doc, area, 0.25);
        Save(bmp, "sample-all.png");
        Assert.True(bmp.Width > 100 && bmp.Height > 100);
    }

    /// <summary>
    /// Grouping is only membership (R22): every object keeps its own place in the drawing order, so an import that keeps all the groups
    /// must draw exactly what an import without any group draws, pixel for pixel. The pin design (art-e) and the flyers (art-d) have groups
    /// whose members have other objects drawn between them; earlier those groups were given up to keep the picture right.
    /// </summary>
    [LocalTheory("layouts/art-a.lyt", "layouts/sample.lyt", "layouts/art-d.lyt", "layouts/art-e.lyt")]
    [InlineData("sample.lyt")]
    [InlineData("art-a.lyt")]
    [InlineData("art-d.lyt")]
    [InlineData("art-e.lyt")]
    public void Keeping_the_groups_does_not_change_the_picture_at_all(string relativePath)
    {
        var path = TestFiles.Layout(relativePath);
        var grouped = LytImporter.Open(path).Document;
        var flat = LytImporter.Open(path, new LytImportOptions { PreserveGroups = false }).Document;
        Assert.True(grouped.GroupCount > 0);
        Assert.Equal(0, flat.GroupCount);

        var area = grouped.GetContentBounds().Union(grouped.Page.Bounds).Inflate(5);
        double scale = Math.Min(0.4, 2500 / Math.Max(area.Width, area.Height));   // big layouts at a smaller scale: at most 2500 pixels across
        using var a = Render(grouped, area, scale);
        using var b = Render(flat, area, scale);
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        int differing = 0;
        var pa = a.GetPixelSpan(); var pb = b.GetPixelSpan();
        for (int i = 0; i < pa.Length; i++) if (pa[i] != pb[i]) differing++;
        output.WriteLine($"{Path.GetFileName(path)}: {grouped.GroupCount} groups, {a.Width} x {a.Height} px, {differing} bytes differ");
        Save(a, Path.GetFileNameWithoutExtension(path) + "-grouped.png");
        Save(b, Path.GetFileNameWithoutExtension(path) + "-flat.png");
        Assert.Equal(0, differing);
    }
}
