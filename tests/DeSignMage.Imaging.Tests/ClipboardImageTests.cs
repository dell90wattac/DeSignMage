using System.Buffers.Binary;

namespace DeSignMage.Imaging.Tests;

/// <summary>Clipboard pictures: DIBs from other programs become normal pictures, and copied pictures can be offered as DIBs.</summary>
public class ClipboardImageTests
{
    /// <summary>A plain 40-byte-header DIB: width x height, rows bottom to top, in the given bit depth; <paramref name="rows"/> are the stored rows (already padded).</summary>
    private static byte[] Dib(int width, int height, int bits, byte[] pixels, int compression = 0, int colorsUsed = 0, byte[]? palette = null)
    {
        palette ??= [];
        var dib = new byte[40 + palette.Length + pixels.Length];
        var s = dib.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s, 40);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], height);
        BinaryPrimitives.WriteInt16LittleEndian(s[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[14..], (short)bits);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], compression);
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], pixels.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[32..], colorsUsed);
        palette.CopyTo(dib, 40);
        pixels.CopyTo(dib, 40 + palette.Length);
        return dib;
    }

    [Fact]
    public void A_24_bit_bottom_up_DIB_is_turned_the_right_way_up()
    {
        // Top row red, green; bottom row blue, white. Stored bottom row first, BGR, rows padded to 4 bytes.
        byte[] pixels =
        [
            255, 0, 0,   255, 255, 255, 0, 0,      // bottom: blue, white (+2 padding)
            0, 0, 255,   0, 255, 0,     0, 0,      // top: red, green
        ];
        var p = ClipboardImages.DibToPixels(Dib(2, 2, 24, pixels));

        Assert.Equal((2, 2), (p.Width, p.Height));
        Assert.Equal((255, 0, 0, 255), p.Get(0, 0));
        Assert.Equal((0, 255, 0, 255), p.Get(1, 0));
        Assert.Equal((0, 0, 255, 255), p.Get(0, 1));
        Assert.Equal((255, 255, 255, 255), p.Get(1, 1));
    }

    [Fact]
    public void A_top_down_DIB_keeps_its_row_order()
    {
        byte[] pixels = [0, 0, 255, 0, 255, 0, 0, 0,   255, 0, 0, 255, 255, 255, 0, 0];   // red, green / blue, white
        var dib = Dib(2, -2, 24, pixels);
        var p = ClipboardImages.DibToPixels(dib);
        Assert.Equal((255, 0, 0, 255), p.Get(0, 0));
        Assert.Equal((255, 255, 255, 255), p.Get(1, 1));
    }

    [Fact]
    public void A_32_bit_DIB_with_an_unused_fourth_byte_is_opaque()
    {
        // Bottom row: blue, white; top row: red, green. The fourth byte (alpha) is 0, as many programs write it.
        byte[] pixels = [255, 0, 0, 0,  255, 255, 255, 0,   0, 0, 255, 0,  0, 255, 0, 0];
        var p = ClipboardImages.DibToPixels(Dib(2, 2, 32, pixels));
        Assert.Equal((255, 0, 0, 255), p.Get(0, 0));
        Assert.Equal((0, 0, 255, 255), p.Get(0, 1));
    }

    [Fact]
    public void An_8_bit_DIB_uses_its_palette()
    {
        byte[] palette = [0, 0, 255, 0,   255, 0, 0, 0];            // entry 0: red, entry 1: blue (BGRA)
        byte[] pixels = [0, 1, 0, 0,   1, 0, 0, 0];                 // bottom row: red, blue / top row: blue, red
        var p = ClipboardImages.DibToPixels(Dib(2, 2, 8, pixels, colorsUsed: 2, palette: palette));
        Assert.Equal((0, 0, 255), (p.Get(0, 0).R, p.Get(0, 0).G, p.Get(0, 0).B));    // top-left: blue
        Assert.Equal((255, 0, 0), (p.Get(0, 1).R, p.Get(0, 1).G, p.Get(0, 1).B));    // bottom-left: red
    }

    [Fact]
    public void The_V5_DIB_we_write_reads_back_with_its_alpha()
    {
        var src = new PixelBuffer(3, 2);
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++) src.Set(x, y, (byte)(40 * x + 10), (byte)(100 + 50 * y), (byte)(200 - 30 * x), (byte)(255 - 60 * (x + y)));
        var back = ClipboardImages.DibToPixels(ClipboardImages.ToDibV5(src));

        Assert.Equal((3, 2), (back.Width, back.Height));
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++)
            {
                var (r0, g0, b0, a0) = src.Get(x, y);
                var (r1, g1, b1, a1) = back.Get(x, y);
                Assert.InRange(a1, a0 - 1, a0 + 1);
                Assert.InRange(r1, r0 - 2, r0 + 2);
                Assert.InRange(g1, g0 - 2, g0 + 2);
                Assert.InRange(b1, b0 - 2, b0 + 2);
            }
    }

    [Fact]
    public void A_V5_DIB_with_the_masks_repeated_after_the_header_is_read_correctly()
    {
        // What Windows hands out when it converts a plain bitmap to CF_DIBV5: 12 extra bytes between the header and the pixels.
        var src = new PixelBuffer(3, 2);
        for (int y = 0; y < 2; y++) for (int x = 0; x < 3; x++) src.Set(x, y, (byte)(80 * x), (byte)(120 * y), (byte)(255 - 40 * x - 30 * y));
        var plain = ClipboardImages.ToDibV5(src);
        var windows = new byte[plain.Length + 12];
        plain.AsSpan(0, 124).CopyTo(windows);
        BinaryPrimitives.WriteUInt32LittleEndian(windows.AsSpan(124), 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(windows.AsSpan(128), 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(windows.AsSpan(132), 0x000000FF);
        plain.AsSpan(124).CopyTo(windows.AsSpan(136));
        // That form has no alpha mask in use: the fourth byte is 0.
        BinaryPrimitives.WriteUInt32LittleEndian(windows.AsSpan(52), 0);
        for (int i = 139; i < windows.Length; i += 4) windows[i] = 0;

        var back = ClipboardImages.DibToPixels(windows);

        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++)
            {
                var (r, g, b, _) = src.Get(x, y);
                Assert.Equal((r, g, b, (byte)255), back.Get(x, y));
            }
    }

    [Fact]
    public void Garbage_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => ClipboardImages.DibToPng([1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => ClipboardImages.DibToPng(new byte[200]));
    }

    [Fact]
    public void A_DIB_becomes_a_PNG_that_decodes_to_the_same_pixels()
    {
        byte[] pixels = [255, 0, 0, 255, 255, 255, 0, 0,   0, 0, 255, 0, 255, 0, 0, 0];
        var dib = Dib(2, 2, 24, pixels);
        var viaPng = ImageCodec.Decode(ClipboardImages.DibToPng(dib));
        Samples.AssertSame(ClipboardImages.DibToPixels(dib), viaPng);
    }

    [Fact]
    public void Copying_a_picture_out_passes_a_PNG_through_and_converts_other_formats()
    {
        var png = ImageCodec.EncodePng(Samples.Grid());
        Assert.Same(png, ClipboardImages.ToPng(png, "image/png"));

        var jpeg = CodecTests.EncodeJpeg(Samples.Grid());
        var converted = ClipboardImages.ToPng(jpeg, "image/jpeg");
        Assert.Equal("image/png", ImageCodec.Probe(converted).MediaType);
        Assert.Equal((3, 2), (ImageCodec.Probe(converted).Width, ImageCodec.Probe(converted).Height));
    }
}
