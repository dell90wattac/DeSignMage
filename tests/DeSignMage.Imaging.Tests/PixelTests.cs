using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using SkiaSharp;

namespace DeSignMage.Imaging.Tests;

internal static class Samples
{
    /// <summary>3 x 2: every pixel different, so any misplacement shows. Row 0: A B C, row 1: D E F.</summary>
    public static PixelBuffer Grid()
    {
        var p = new PixelBuffer(3, 2);
        byte n = 0;
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++, n++)
                p.Set(x, y, (byte)(10 + n * 20), (byte)(200 - n * 10), (byte)(n * 3), (byte)(255 - n * 15));
        return p;
    }

    public static void AssertSame(PixelBuffer expected, PixelBuffer actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        Assert.True(expected.Rgba.AsSpan().SequenceEqual(actual.Rgba), "pixels differ");
    }

    public static Document Doc() => new(PageSetup.FromInches(24, 12));
}

public class PixelOpsTests
{
    [Fact]
    public void Rotate_clockwise_moves_the_top_left_pixel_to_the_top_right()
    {
        var src = Samples.Grid();                      // A B C / D E F
        var r = PixelOps.Rotate90(src, clockwise: true);   // D A / E B / F C

        Assert.Equal((2, 3), (r.Width, r.Height));
        Assert.Equal(src.Get(0, 1), r.Get(0, 0));      // D
        Assert.Equal(src.Get(0, 0), r.Get(1, 0));      // A
        Assert.Equal(src.Get(1, 1), r.Get(0, 1));      // E
        Assert.Equal(src.Get(1, 0), r.Get(1, 1));      // B
        Assert.Equal(src.Get(2, 1), r.Get(0, 2));      // F
        Assert.Equal(src.Get(2, 0), r.Get(1, 2));      // C
    }

    [Fact]
    public void Rotate_counter_clockwise_moves_the_top_left_pixel_to_the_bottom_left()
    {
        var src = Samples.Grid();                      // A B C / D E F
        var r = PixelOps.Rotate90(src, clockwise: false);  // C F / B E / A D

        Assert.Equal((2, 3), (r.Width, r.Height));
        Assert.Equal(src.Get(2, 0), r.Get(0, 0));      // C
        Assert.Equal(src.Get(2, 1), r.Get(1, 0));      // F
        Assert.Equal(src.Get(0, 0), r.Get(0, 2));      // A
        Assert.Equal(src.Get(0, 1), r.Get(1, 2));      // D
    }

    [Fact]
    public void Rotating_one_way_then_back_or_four_times_gives_the_original()
    {
        var src = Samples.Grid();
        Samples.AssertSame(src, PixelOps.Rotate90(PixelOps.Rotate90(src, true), false));
        var four = src;
        for (int i = 0; i < 4; i++) four = PixelOps.Rotate90(four, true);
        Samples.AssertSame(src, four);
    }

    [Fact]
    public void Flip_horizontal_mirrors_left_and_right()
    {
        var src = Samples.Grid();
        var f = PixelOps.Flip(src, horizontal: true);   // C B A / F E D
        Assert.Equal((3, 2), (f.Width, f.Height));
        Assert.Equal(src.Get(2, 0), f.Get(0, 0));
        Assert.Equal(src.Get(0, 0), f.Get(2, 0));
        Assert.Equal(src.Get(1, 1), f.Get(1, 1));
        Assert.Equal(src.Get(2, 1), f.Get(0, 1));
    }

    [Fact]
    public void Flip_vertical_mirrors_top_and_bottom()
    {
        var src = Samples.Grid();
        var f = PixelOps.Flip(src, horizontal: false);  // D E F / A B C
        Assert.Equal(src.Get(0, 1), f.Get(0, 0));
        Assert.Equal(src.Get(2, 1), f.Get(2, 0));
        Assert.Equal(src.Get(1, 0), f.Get(1, 1));
    }

    [Fact]
    public void Flipping_twice_is_the_identity()
    {
        var src = Samples.Grid();
        Samples.AssertSame(src, PixelOps.Flip(PixelOps.Flip(src, true), true));
        Samples.AssertSame(src, PixelOps.Flip(PixelOps.Flip(src, false), false));
    }

    [Fact]
    public void Invert_flips_color_but_keeps_alpha()
    {
        var src = Samples.Grid();
        var inv = PixelOps.Invert(src);
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++)
            {
                var (r, g, b, a) = src.Get(x, y);
                Assert.Equal(((byte)(255 - r), (byte)(255 - g), (byte)(255 - b), a), inv.Get(x, y));
            }
        Samples.AssertSame(src, PixelOps.Invert(inv));
    }

    [Fact]
    public void Operations_do_not_change_their_input()
    {
        var src = Samples.Grid();
        var copy = (byte[])src.Rgba.Clone();
        PixelOps.Invert(src); PixelOps.Rotate90(src, true); PixelOps.Flip(src, true); PixelOps.Crop(src, 1, 0, 2, 2);
        Assert.True(copy.AsSpan().SequenceEqual(src.Rgba));
    }

    [Fact]
    public void Crop_keeps_the_rectangle()
    {
        var src = Samples.Grid();
        var c = PixelOps.Crop(src, 1, 0, 2, 2);         // B C / E F
        Assert.Equal((2, 2), (c.Width, c.Height));
        Assert.Equal(src.Get(1, 0), c.Get(0, 0));
        Assert.Equal(src.Get(2, 0), c.Get(1, 0));
        Assert.Equal(src.Get(1, 1), c.Get(0, 1));
        Assert.Equal(src.Get(2, 1), c.Get(1, 1));
    }

    [Fact]
    public void Crop_to_one_pixel_and_clamping()
    {
        var src = Samples.Grid();
        var one = PixelOps.Crop(src, 2, 1, 1, 1);
        Assert.Equal(src.Get(2, 1), one.Get(0, 0));
        var clamped = PixelOps.Crop(src, 2, 1, 50, 50);   // asks for more than there is
        Assert.Equal((1, 1), (clamped.Width, clamped.Height));
        var whole = PixelOps.Crop(src, 0, 0, 3, 2);
        Samples.AssertSame(src, whole);
    }

    [Fact]
    public void Reorient_follows_the_exif_tags()
    {
        var src = Samples.Grid();                         // A B C / D E F
        Samples.AssertSame(PixelOps.Rotate90(src, true), ImageCodec.Reorient(src, SKEncodedOrigin.RightTop));
        Samples.AssertSame(PixelOps.Rotate90(src, false), ImageCodec.Reorient(src, SKEncodedOrigin.LeftBottom));
        Samples.AssertSame(PixelOps.Flip(src, true), ImageCodec.Reorient(src, SKEncodedOrigin.TopRight));
        Samples.AssertSame(PixelOps.Flip(src, false), ImageCodec.Reorient(src, SKEncodedOrigin.BottomLeft));
        Samples.AssertSame(src, ImageCodec.Reorient(src, SKEncodedOrigin.TopLeft));

        var upsideDown = ImageCodec.Reorient(src, SKEncodedOrigin.BottomRight);   // F E D / C B A
        Assert.Equal(src.Get(2, 1), upsideDown.Get(0, 0));
        Assert.Equal(src.Get(0, 0), upsideDown.Get(2, 1));

        var transposed = ImageCodec.Reorient(src, SKEncodedOrigin.LeftTop);       // x and y swapped
        Assert.Equal((2, 3), (transposed.Width, transposed.Height));
        Assert.Equal(src.Get(2, 1), transposed.Get(1, 2));
        Assert.Equal(src.Get(2, 0), transposed.Get(0, 2));

        var transverse = ImageCodec.Reorient(src, SKEncodedOrigin.RightBottom);
        Assert.Equal((2, 3), (transverse.Width, transverse.Height));
        Assert.Equal(src.Get(0, 0), transverse.Get(1, 2));
        Assert.Equal(src.Get(2, 1), transverse.Get(0, 0));
    }
}

public class CodecTests
{
    [Fact]
    public void Png_round_trip_is_exact_including_alpha()
    {
        var src = Samples.Grid();
        var back = ImageCodec.Decode(ImageCodec.EncodePng(src));
        Samples.AssertSame(src, back);
    }

    [Fact]
    public void Probe_reads_size_and_format()
    {
        var png = ImageCodec.EncodePng(Samples.Grid());
        var p = ImageCodec.Probe(png);
        Assert.Equal((3, 2, "image/png"), (p.Width, p.Height, p.MediaType));
        Assert.False(p.NeedsReorient);
        Assert.Null(p.Dpi);
    }

    [Fact]
    public void Garbage_is_not_a_picture()
    {
        Assert.Throws<InvalidDataException>(() => ImageCodec.Probe([1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.Throws<InvalidDataException>(() => ImageCodec.Decode([1, 2, 3, 4, 5, 6, 7, 8]));
    }

    [Fact]
    public void Image_files_are_recognized_by_extension()
    {
        Assert.True(ImageCodec.IsImageFile(@"C:\x\Photo.JPG"));
        Assert.True(ImageCodec.IsImageFile("logo.png"));
        Assert.False(ImageCodec.IsImageFile("layout.lyt"));
        Assert.False(ImageCodec.IsImageFile("noextension"));
    }

    [Fact]
    public void Jpeg_decodes_to_roughly_the_same_colors()
    {
        var solid = new PixelBuffer(16, 16);
        for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) solid.Set(x, y, 200, 40, 40);
        byte[] jpeg = EncodeJpeg(solid);
        var back = ImageCodec.Decode(jpeg);
        Assert.Equal((16, 16), (back.Width, back.Height));
        var (r, g, b, a) = back.Get(8, 8);
        Assert.InRange(r, 190, 210);
        Assert.InRange(g, 30, 50);
        Assert.Equal(255, a);
    }

    internal static byte[] EncodeJpeg(PixelBuffer p)
    {
        using var bmp = new SKBitmap(new SKImageInfo(p.Width, p.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(p.Rgba, 0, bmp.GetPixels(), p.Rgba.Length);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }
}

public class MetadataTests
{
    [Fact]
    public void Bmp_resolution_is_read_from_the_header()
    {
        // 2 x 2, 24 bit, 11811 pixels per metre = 300 dpi.
        var bmp = new byte[14 + 40 + 16];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(2).CopyTo(bmp, 18);
        BitConverter.GetBytes(2).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
        BitConverter.GetBytes(11811).CopyTo(bmp, 38);
        BitConverter.GetBytes(11811).CopyTo(bmp, 42);

        Assert.Equal(300, ImageMetadata.ReadDpi(bmp)!.Value, 0);
        var probe = ImageCodec.Probe(bmp);
        Assert.Equal((2, 2, "image/bmp"), (probe.Width, probe.Height, probe.MediaType));
        Assert.Equal(300, probe.Dpi!.Value, 0);
    }

    [Fact]
    public void Png_phys_chunk_gives_the_resolution()
    {
        var png = WithPhys(ImageCodec.EncodePng(Samples.Grid()), 5906);   // 150 dpi
        Assert.Equal(150, ImageMetadata.ReadDpi(png)!.Value, 0);
        Assert.Equal(150, ImageCodec.Probe(png).Dpi!.Value, 0);
        Samples.AssertSame(Samples.Grid(), ImageCodec.Decode(png));       // still a valid picture
    }

    [Fact]
    public void Jpeg_jfif_density_gives_the_resolution_and_zero_units_mean_unknown()
    {
        var jpeg = CodecTests.EncodeJpeg(Samples.Grid());
        Assert.Null(ImageMetadata.ReadDpi(jpeg));              // Skia writes an aspect-only JFIF density (units 0): no resolution

        int jfif = IndexOf(jpeg, "JFIF\0");
        Assert.True(jfif > 0);
        jpeg[jfif + 7] = 1;                                   // units: dots per inch
        jpeg[jfif + 8] = 0; jpeg[jfif + 9] = 200;             // x density 200
        jpeg[jfif + 10] = 0; jpeg[jfif + 11] = 200;
        Assert.Equal(200, ImageMetadata.ReadDpi(jpeg)!.Value, 0);

        jpeg[jfif + 7] = 2;                                   // dots per cm
        jpeg[jfif + 9] = 100;
        Assert.Equal(254, ImageMetadata.ReadDpi(jpeg)!.Value, 0);
    }

    [Fact]
    public void Truncated_or_unknown_data_means_no_resolution()
    {
        Assert.Null(ImageMetadata.ReadDpi([]));
        Assert.Null(ImageMetadata.ReadDpi([0xFF, 0xD8, 0xFF]));
        Assert.Null(ImageMetadata.ReadDpi([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0]));
        Assert.Null(ImageMetadata.ReadDpi(new byte[100]));
    }

    private static int IndexOf(byte[] d, string text)
    {
        for (int i = 0; i + text.Length <= d.Length; i++)
        {
            int k = 0;
            while (k < text.Length && d[i + k] == text[k]) k++;
            if (k == text.Length) return i;
        }
        return -1;
    }

    /// <summary>Inserts a pHYs chunk (pixels per metre, both axes) right after IHDR.</summary>
    private static byte[] WithPhys(byte[] png, uint perMeter)
    {
        var chunk = new byte[4 + 4 + 9 + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk, 9);
        "pHYs"u8.CopyTo(chunk.AsSpan(4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8), perMeter);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(12), perMeter);
        chunk[16] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(17), Crc32(chunk.AsSpan(4, 13)));
        int afterIhdr = 8 + 4 + 4 + 13 + 4;
        return [.. png.AsSpan(0, afterIhdr), .. chunk, .. png.AsSpan(afterIhdr)];
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }
}

public class ServiceTests
{
    private readonly SkiaImageService _svc = new();
    private readonly Document _doc = Samples.Doc();

    private string Store(PixelBuffer p) => _doc.Assets.Add(ImageCodec.EncodePng(p), "image/png");

    private PixelBuffer Load(ImagePixels px) => ImageCodec.Decode(_doc.Assets.Get(px.AssetId)!.Data);

    [Fact]
    public void Each_operation_stores_the_exact_result_as_a_new_picture()
    {
        var src = Samples.Grid();
        string id = Store(src);

        var rot = _svc.Apply(_doc, id, new ImageOp.Rotate90(true))!;
        Assert.Equal((2, 3), (rot.PixelWidth, rot.PixelHeight));
        Samples.AssertSame(PixelOps.Rotate90(src, true), Load(rot));

        var ccw = _svc.Apply(_doc, id, new ImageOp.Rotate90(false))!;
        Samples.AssertSame(PixelOps.Rotate90(src, false), Load(ccw));

        Samples.AssertSame(PixelOps.Flip(src, true), Load(_svc.Apply(_doc, id, new ImageOp.Flip(true))!));
        Samples.AssertSame(PixelOps.Flip(src, false), Load(_svc.Apply(_doc, id, new ImageOp.Flip(false))!));
        Samples.AssertSame(PixelOps.Invert(src), Load(_svc.Apply(_doc, id, new ImageOp.Invert())!));

        var crop = _svc.Apply(_doc, id, new ImageOp.Crop(1, 0, 2, 1))!;
        Assert.Equal((2, 1), (crop.PixelWidth, crop.PixelHeight));
        Samples.AssertSame(PixelOps.Crop(src, 1, 0, 2, 1), Load(crop));

        Assert.NotNull(_doc.Assets.Get(id));                  // the original stays (undo needs it)
        Assert.NotEqual(id, rot.AssetId);
    }

    [Fact]
    public void Pixel_size_and_unreadable_pictures()
    {
        string id = Store(Samples.Grid());
        Assert.Equal((3, 2), _svc.GetPixelSize(_doc, id));
        Assert.Null(_svc.GetPixelSize(_doc, "missing"));

        string junk = _doc.Assets.Add([1, 2, 3, 4], "image/png");
        Assert.Null(_svc.GetPixelSize(_doc, junk));
        Assert.Null(_svc.Apply(_doc, junk, new ImageOp.Invert()));
        Assert.Null(_svc.Apply(_doc, "missing", new ImageOp.Invert()));
    }

    [Fact]
    public void Rasterize_draws_the_objects_at_the_requested_resolution()
    {
        // A 72 x 36 pt red ellipse at (10, 10) on a transparent picture of exactly its bounds, 2 pixels per point.
        var ellipse = new EllipseObject(72, 36) { Transform = Affine2.Translation(10, 10), Style = ObjectStyle.FillOnly(DocColor.FromHex("#FF0000")) };
        _doc.Insert(_doc.ActiveLayer, -1, ellipse);
        var bounds = ImagePlacement.VisualBounds([ellipse]);
        var (w, h) = ImagePlacement.RasterSize(bounds, 144)!.Value;
        Assert.Equal((144, 72), (w, h));

        var px = _svc.Rasterize(_doc, [ellipse], bounds, w, h)!;
        var img = Load(px);

        Assert.Equal((144, 72), (img.Width, img.Height));
        Assert.Equal((255, 0, 0, 255), img.Get(72, 36));      // center: solid red
        Assert.Equal(0, img.Get(1, 1).A);                      // corner of the bounds: nothing there
        Assert.Equal(0, img.Get(142, 70).A);
        Assert.True(img.Get(72, 2).A > 200);                   // just inside the top edge
    }

    [Fact]
    public void Rasterize_includes_strokes_and_stacks_objects_bottom_to_top()
    {
        var back = new RectangleObject(40, 40) { Transform = Affine2.Translation(0, 0), Style = ObjectStyle.FillOnly(DocColor.FromHex("#0000FF")) };
        var front = new RectangleObject(20, 20)
        {
            Transform = Affine2.Translation(10, 10),
            Style = new ObjectStyle(new SolidPaint(DocColor.FromHex("#00FF00")), StrokeStyle.Solid(DocColor.FromHex("#FF0000"), 4)),
        };
        _doc.Insert(_doc.ActiveLayer, -1, back);
        _doc.Insert(_doc.ActiveLayer, -1, front);
        var bounds = ImagePlacement.VisualBounds([front, back]);
        Assert.Equal(new Rect2(0, 0, 40, 40), bounds);

        var img = Load(_svc.Rasterize(_doc, [front, back], bounds, 40, 40)!);

        Assert.Equal((0, 0, 255, 255), img.Get(2, 2));         // back only
        Assert.Equal((0, 255, 0, 255), img.Get(20, 20));       // front's fill on top
        Assert.Equal((255, 0, 0, 255), img.Get(10, 20));       // front's stroke centered on its edge at x = 10
    }

    [Fact]
    public void Rasterize_of_a_group_uses_the_group_transform()
    {
        var rect = new RectangleObject(10, 10) { Style = ObjectStyle.FillOnly(DocColor.FromHex("#FF0000")) };
        var group = new GroupObject { Transform = Affine2.Translation(30, 0) };
        _doc.Insert(_doc.ActiveLayer, -1, group);
        _doc.Insert(group, -1, rect);
        var bounds = ImagePlacement.VisualBounds([group]);
        Assert.Equal(new Rect2(30, 0, 40, 10), bounds);

        var img = Load(_svc.Rasterize(_doc, [group], bounds, 10, 10)!);
        Assert.Equal((255, 0, 0, 255), img.Get(5, 5));
    }
}

public class ImportTests
{
    [Fact]
    public void A_png_is_stored_untouched_with_its_size_and_dpi()
    {
        var doc = Samples.Doc();
        var png = ImageCodec.EncodePng(Samples.Grid());

        var r = ImageImporter.Import(doc, png, "grid.png");

        Assert.Equal((3, 2), (r.Pixels.PixelWidth, r.Pixels.PixelHeight));
        Assert.Equal("grid.png", r.FileName);
        Assert.Null(r.Dpi);
        Assert.Equal(png, doc.Assets.Get(r.Pixels.AssetId)!.Data);
        Assert.Equal("image/png", doc.Assets.Get(r.Pixels.AssetId)!.MediaType);
    }

    [Fact]
    public void A_jpeg_is_stored_without_recompression()
    {
        var doc = Samples.Doc();
        var jpeg = CodecTests.EncodeJpeg(Samples.Grid());
        var r = ImageImporter.Import(doc, jpeg, "photo.jpg");
        Assert.Equal(jpeg, doc.Assets.Get(r.Pixels.AssetId)!.Data);
        Assert.Equal("image/jpeg", doc.Assets.Get(r.Pixels.AssetId)!.MediaType);
    }

    [Fact]
    public void A_file_that_is_not_a_picture_is_refused_and_leaves_no_asset()
    {
        var doc = Samples.Doc();
        Assert.Throws<InvalidDataException>(() => ImageImporter.Import(doc, new byte[200], "notes.png"));
        Assert.Empty(doc.Assets.All);
    }

    [Fact]
    public void Import_then_place_gives_the_natural_size_from_the_file_dpi()
    {
        var doc = Samples.Doc();
        var png = ImageCodec.EncodePng(new PixelBuffer(600, 300));
        var r = ImageImporter.Import(doc, png, "wide.png");
        var obj = ImagePlacement.Create(r.Pixels, r.FileName, r.Dpi ?? ImagePlacement.DefaultDpi, doc.Page, new Vec2(500, 300));
        Assert.Equal((144.0, 72.0), (obj.Width, obj.Height));     // 600 px at the default 300 dpi = 2 inches
    }

    [Fact]
    public void Bigger_than_the_page_is_clamped_to_the_page()
    {
        var doc = Samples.Doc();                                     // 24 x 12 in
        var r = new ImagePixels("x", 20000, 10000);                  // 66 x 33 in at 300 dpi
        var obj = ImagePlacement.Create(r, "huge.png", 300, doc.Page, doc.Page.Bounds.Center);
        Assert.True(obj.GetWorldBounds().Width <= doc.Page.Width + 1e-6);
        Assert.True(obj.GetWorldBounds().Height <= doc.Page.Height + 1e-6);
        Assert.Equal(2.0, obj.Width / obj.Height, 6);
    }
}
