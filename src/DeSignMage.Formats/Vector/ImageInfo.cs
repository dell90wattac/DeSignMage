using System.Buffers.Binary;

namespace DeSignMage.Formats.Vector;

/// <summary>What the first bytes of a picture file say: its kind and size in pixels (no decoding).</summary>
internal static class ImageInfo
{
    public static bool TryRead(byte[] d, out string mediaType, out int width, out int height)
    {
        mediaType = ""; width = height = 0;
        if (d.Length > 24 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G')
        {
            mediaType = "image/png";
            width = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(16));
            height = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(20));
            return true;
        }
        if (d.Length > 4 && d[0] == 0xFF && d[1] == 0xD8)
        {
            mediaType = "image/jpeg";
            int i = 2;
            while (i + 9 < d.Length)
            {
                if (d[i] != 0xFF) { i++; continue; }
                byte m = d[i + 1];
                if (m == 0xFF) { i++; continue; }
                if (m is 0xD8 or 0x01 || (m >= 0xD0 && m <= 0xD7)) { i += 2; continue; }
                int len = (d[i + 2] << 8) | d[i + 3];
                if (m is >= 0xC0 and <= 0xCF && m is not (0xC4 or 0xC8 or 0xCC))
                {
                    height = (d[i + 5] << 8) | d[i + 6];
                    width = (d[i + 7] << 8) | d[i + 8];
                    return true;
                }
                i += 2 + len;
            }
            return true;   // a JPEG whose size could not be found
        }
        if (d.Length > 10 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F')
        {
            mediaType = "image/gif";
            width = d[6] | (d[7] << 8); height = d[8] | (d[9] << 8);
            return true;
        }
        if (d.Length > 26 && d[0] == 'B' && d[1] == 'M')
        {
            mediaType = "image/bmp";
            width = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(18));
            height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(22)));
            return true;
        }
        if (d.Length > 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P')
        {
            mediaType = "image/webp";
            return true;
        }
        return false;
    }
}
