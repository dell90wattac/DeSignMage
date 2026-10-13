using System.Buffers.Binary;

namespace DeSignMage.Imaging;

/// <summary>Reads the print resolution a picture file declares (PNG pHYs, JPEG JFIF/EXIF, BMP). Null when it declares none.</summary>
public static class ImageMetadata
{
    public static double? ReadDpi(byte[] d)
    {
        try
        {
            double? dpi = null;
            if (d.Length > 8 && d[0] == 0x89 && d[1] == (byte)'P' && d[2] == (byte)'N' && d[3] == (byte)'G') dpi = Png(d);
            else if (d.Length > 4 && d[0] == 0xFF && d[1] == 0xD8) dpi = Jpeg(d);
            else if (d.Length > 38 && d[0] == (byte)'B' && d[1] == (byte)'M') dpi = Bmp(d);
            return dpi is { } v && v >= 1 && v <= 100_000 && !double.IsNaN(v) ? v : null;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;   // a truncated or odd header just means "unknown"
        }
    }

    private static double? Png(byte[] d)
    {
        int p = 8;
        while (p + 12 <= d.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            string type = System.Text.Encoding.ASCII.GetString(d, p + 4, 4);
            if (type == "pHYs" && len >= 9)
            {
                uint ppu = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 8));
                return d[p + 16] == 1 ? ppu * 0.0254 : null;   // unit 1 = metre; 0 = unknown (aspect only)
            }
            if (type is "IDAT" or "IEND") return null;        // pHYs must precede the pixel data
            p += 12 + (int)len;
        }
        return null;
    }

    private static double? Bmp(byte[] d)
    {
        int perMeter = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(38));
        return perMeter > 0 ? perMeter * 0.0254 : null;
    }

    private static double? Jpeg(byte[] d)
    {
        double? jfif = null, exif = null;
        int p = 2;
        while (p + 4 <= d.Length && d[p] == 0xFF)
        {
            byte marker = d[p + 1];
            if (marker == 0xFF) { p++; continue; }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { p += 2; continue; }
            if (marker is 0xDA or 0xD9) break;                     // start of scan / end: no more headers
            int len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p + 2));
            int body = p + 4;
            if (marker == 0xE0 && len >= 16 && Match(d, body, "JFIF\0"))
            {
                int units = d[body + 7];
                int xd = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(body + 8));
                if (units == 1) jfif = xd;
                else if (units == 2) jfif = xd * 2.54;
            }
            else if (marker == 0xE1 && len >= 14 && Match(d, body, "Exif\0\0"))
            {
                exif = ExifDpi(d, body + 6, body + len - 2);
            }
            p += 2 + len;
        }
        // A camera's EXIF value is the real one when the JFIF block only says "1 pixel = 1 aspect unit".
        return jfif ?? exif;
    }

    private static double? ExifDpi(byte[] d, int tiff, int end)
    {
        bool le = d[tiff] == (byte)'I';
        ushort U16(int o) => le ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o));
        uint U32(int o) => le ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
        if (U16(tiff + 2) != 42) return null;
        int ifd = tiff + (int)U32(tiff + 4);
        int count = U16(ifd);
        double? x = null;
        int unit = 2;
        for (int i = 0; i < count; i++)
        {
            int e = ifd + 2 + i * 12;
            if (e + 12 > end) break;
            ushort tag = U16(e);
            if (tag == 282)                                         // XResolution: a rational stored elsewhere
            {
                int o = tiff + (int)U32(e + 8);
                uint num = U32(o), den = U32(o + 4);
                if (den != 0) x = (double)num / den;
            }
            else if (tag == 296) unit = U16(e + 8);                 // 2 = inch, 3 = centimetre
        }
        return x is null ? null : unit == 3 ? x * 2.54 : unit == 2 ? x : null;
    }

    private static bool Match(byte[] d, int at, string text)
    {
        if (at + text.Length > d.Length) return false;
        for (int i = 0; i < text.Length; i++) if (d[at + i] != text[i]) return false;
        return true;
    }
}
