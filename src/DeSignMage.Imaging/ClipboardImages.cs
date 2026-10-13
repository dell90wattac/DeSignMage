using System.Buffers.Binary;

namespace DeSignMage.Imaging;

/// <summary>
/// Pictures on the Windows clipboard travel as a PNG, or as a device-independent bitmap (a DIB: a BMP file without its 14-byte file
/// header). This turns a DIB into a normal picture and builds one for copying out.
/// </summary>
public static class ClipboardImages
{
    /// <summary>The PNG bytes of a clipboard DIB (CF_DIB / CF_DIBV5). Throws <see cref="InvalidDataException"/> if it cannot be read.</summary>
    public static byte[] DibToPng(byte[] dib) => ImageCodec.EncodePng(DibToPixels(dib));

    public static PixelBuffer DibToPixels(byte[] dib)
    {
        var pixels = ImageCodec.Decode(DibToBmpFile(dib));
        // A 32-bit DIB's fourth byte is often left at 0 although the picture is opaque; a picture that is fully see-through is never intended.
        bool anyVisible = false;
        for (int i = 3; i < pixels.Rgba.Length; i += 4)
            if (pixels.Rgba[i] != 0) { anyVisible = true; break; }
        if (!anyVisible)
            for (int i = 3; i < pixels.Rgba.Length; i += 4) pixels.Rgba[i] = 255;
        return pixels;
    }

    /// <summary>A BMP file image of a DIB: the same bytes behind a file header that says where the pixels start.</summary>
    public static byte[] DibToBmpFile(byte[] dib)
    {
        if (dib.Length < 40) throw new InvalidDataException("The clipboard picture is too short to be a bitmap.");
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (headerSize < 40 || headerSize > dib.Length) throw new InvalidDataException("The clipboard picture has an unknown bitmap header.");
        int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        int compression = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16));
        int colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(32));

        // Channel masks follow a plain 40-byte header when the pixels are bit fields; later header versions hold them inside.
        int masks = headerSize == 40 && compression == 3 ? 12 : headerSize == 40 && compression == 6 ? 16 : 0;
        int palette = bitCount <= 8 ? (colorsUsed > 0 ? colorsUsed : 1 << bitCount) * 4 : colorsUsed * 4;
        int offset = 14 + headerSize + masks + palette;
        if (offset - 14 > dib.Length) throw new InvalidDataException("The clipboard picture is cut short.");

        // Windows builds the V5 form it synthesizes from a plain bitmap with the three channel masks repeated after the header although
        // the header holds them: the pixels start 12 bytes later than the header size says. The size of the pixel data gives it away.
        int width = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4)), height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8)));
        if (headerSize >= 108 && compression == 3 && width > 0 && height > 0 && bitCount is 16 or 32)
        {
            long pixelBytes = (long)((width * bitCount + 31) / 32) * 4 * height;
            if (dib.Length - (offset - 14) == pixelBytes + 12) offset += 12;
        }

        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B'; file[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), offset);
        dib.CopyTo(file, 14);
        return file;
    }

    /// <summary>
    /// A CF_DIBV5 for copying a picture out: 32 bits, bottom-up, with an alpha channel (BGRA, not premultiplied) and sRGB.
    /// </summary>
    public static byte[] ToDibV5(PixelBuffer p)
    {
        const int header = 124;
        int stride = p.Width * 4;
        var dib = new byte[header + stride * p.Height];
        var s = dib.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s, header);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], p.Width);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], p.Height);          // positive: rows run bottom to top
        BinaryPrimitives.WriteInt16LittleEndian(s[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 3);                // BI_BITFIELDS
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], stride * p.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], 0x00FF0000);      // red mask
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], 0x0000FF00);      // green
        BinaryPrimitives.WriteUInt32LittleEndian(s[48..], 0x000000FF);      // blue
        BinaryPrimitives.WriteUInt32LittleEndian(s[52..], 0xFF000000);      // alpha
        BinaryPrimitives.WriteUInt32LittleEndian(s[56..], 0x73524742);      // LCS_sRGB
        BinaryPrimitives.WriteInt32LittleEndian(s[108..], 4);               // LCS_GM_IMAGES
        for (int y = 0; y < p.Height; y++)
        {
            int src = y * stride, dst = header + (p.Height - 1 - y) * stride;
            for (int x = 0; x < p.Width; x++, src += 4, dst += 4)
            {
                dib[dst] = p.Rgba[src + 2]; dib[dst + 1] = p.Rgba[src + 1]; dib[dst + 2] = p.Rgba[src]; dib[dst + 3] = p.Rgba[src + 3];
            }
        }
        return dib;
    }

    /// <summary>The PNG for a stored picture of any supported format (a PNG is passed through untouched).</summary>
    public static byte[] ToPng(byte[] encoded, string mediaType) =>
        mediaType == "image/png" ? encoded : ImageCodec.EncodePng(ImageCodec.Decode(encoded));
}
