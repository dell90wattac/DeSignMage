using System.IO.Compression;
using DeSignMage.Core.Model;
using UglyToad.PdfPig.Tokens;

namespace DeSignMage.Formats.Vector.Pdf;

/// <summary>A picture of a PDF as a file a viewer can open: JPEG pictures as they are, everything else decoded to a PNG.</summary>
internal static class PdfImages
{
    private const long MaxPixels = 60_000_000;

    internal sealed record Result(byte[] Data, string MediaType, int Width, int Height);

    /// <summary>The picture, or null with the reason in <paramref name="problem"/>. <paramref name="fill"/> colors a stencil mask.</summary>
    public static Result? Build(PdfFile file, StreamToken stream, DictionaryToken? resources, DocColor fill, double alpha, out string? problem)
    {
        problem = null;
        var dict = stream.StreamDictionary;
        int w = (int)file.GetNumber(dict, "Width", 0), h = (int)file.GetNumber(dict, "Height", 0);
        if (w <= 0 || h <= 0) { problem = "a picture has no size"; return null; }
        if ((long)w * h > MaxPixels) { problem = "a picture is too large (more than 60 million pixels)"; return null; }

        byte[] data;
        string? codec;
        try { data = file.Decode(stream, out codec); }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            problem = "a picture's data could not be decoded";
            return null;
        }
        if (codec is not null)
        {
            if (codec is "DCTDecode" or "DCT")
            {
                if (file.Get(dict, "SMask") is not null || file.Get(dict, "Mask") is not null) problem = "a JPEG picture's transparency mask was dropped";
                return new Result(data, "image/jpeg", w, h);
            }
            problem = codec is "JPXDecode" ? "a JPEG 2000 picture is not supported" : $"a picture compressed with {codec} is not supported";
            return null;
        }

        bool stencil = file.GetBool(dict, "ImageMask", false);
        int bpc = stencil ? 1 : (int)file.GetNumber(dict, "BitsPerComponent", 8);
        if (bpc is not (1 or 2 or 4 or 8 or 16)) { problem = "a picture has an unusual bit depth"; return null; }

        var rgba = new byte[w * h * 4];
        var decodeArr = file.Numbers(file.Get(dict, "Decode"));
        if (stencil)
        {
            bool invert = decodeArr is { Length: >= 2 } && decodeArr[0] > decodeArr[1];
            int rowBytes = (w + 7) / 8;
            byte fr = fill.R, fg = fill.G, fb = fill.B, fa = (byte)Math.Round(fill.A * alpha);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int bi = y * rowBytes + (x >> 3);
                    int bit = bi < data.Length ? (data[bi] >> (7 - (x & 7))) & 1 : 1;
                    bool paint = invert ? bit == 1 : bit == 0;
                    int o = (y * w + x) * 4;
                    if (paint) { rgba[o] = fr; rgba[o + 1] = fg; rgba[o + 2] = fb; rgba[o + 3] = fa; }
                }
            return new Result(Png.Encode(rgba, w, h, alpha: true), "image/png", w, h);
        }

        var space = PdfColorSpace.Read(file, file.Get(dict, "ColorSpace"), resources);
        int n = space.Components;
        if (n <= 0 || space.IsPattern) { problem = "a picture has no color space"; return null; }
        long rowBits = (long)w * n * bpc;
        int stride = (int)((rowBits + 7) / 8);
        bool indexed = file.Get(dict, "ColorSpace") is ArrayToken { Length: > 0 } cs && file.Name(cs[0]) is "Indexed" or "I";
        double maxSample = (1 << bpc) - 1;
        double[] dmin = new double[n], dmax = new double[n];
        for (int k = 0; k < n; k++)
        {
            dmin[k] = decodeArr is not null && decodeArr.Length >= 2 * k + 2 ? decodeArr[2 * k] : 0;
            dmax[k] = decodeArr is not null && decodeArr.Length >= 2 * k + 2 ? decodeArr[2 * k + 1] : (indexed ? maxSample : 1);
        }

        var sample = new double[n];
        var colorKey = file.Numbers(file.Get(dict, "Mask"));
        var raw = new int[n];
        DocColor[]? palette = indexed && bpc <= 8 ? Enumerable.Range(0, 1 << bpc).Select(i => space.ToColor([i]) ?? DocColor.Black).ToArray() : null;
        bool fast8 = bpc == 8 && decodeArr is null && !indexed;
        bool hasKey = colorKey is not null && colorKey.Length >= 2 * n;
        bool keyMasked = false;
        for (int y = 0; y < h; y++)
        {
            int rowStart = y * stride;
            for (int x = 0; x < w; x++)
            {
                for (int k = 0; k < n; k++)
                {
                    long bitPos = ((long)x * n + k) * bpc;
                    int bytePos = rowStart + (int)(bitPos >> 3);
                    int v;
                    if (bytePos >= data.Length) v = 0;
                    else if (bpc == 8) v = data[bytePos];
                    else if (bpc == 16) v = bytePos + 1 < data.Length ? (data[bytePos] << 8) | data[bytePos + 1] : data[bytePos] << 8;
                    else v = (data[bytePos] >> (8 - bpc - (int)(bitPos & 7))) & ((1 << bpc) - 1);
                    raw[k] = v;
                }
                int o = (y * w + x) * 4;
                DocColor c;
                if (palette is not null) c = palette[Math.Clamp((int)Math.Round(dmin[0] + raw[0] * (dmax[0] - dmin[0]) / maxSample), 0, palette.Length - 1)];
                else if (fast8 && n == 3) { rgba[o] = (byte)raw[0]; rgba[o + 1] = (byte)raw[1]; rgba[o + 2] = (byte)raw[2]; rgba[o + 3] = 255; goto keyed; }
                else if (fast8 && n == 1 && space == PdfColorSpace.Gray) { rgba[o] = rgba[o + 1] = rgba[o + 2] = (byte)raw[0]; rgba[o + 3] = 255; goto keyed; }
                else if (fast8 && n == 4 && space == PdfColorSpace.Cmyk)
                {
                    double ik = 1 - raw[3] / 255.0;
                    rgba[o] = (byte)Math.Round((1 - raw[0] / 255.0) * ik * 255);
                    rgba[o + 1] = (byte)Math.Round((1 - raw[1] / 255.0) * ik * 255);
                    rgba[o + 2] = (byte)Math.Round((1 - raw[2] / 255.0) * ik * 255);
                    rgba[o + 3] = 255;
                    goto keyed;
                }
                else
                {
                    for (int k = 0; k < n; k++) sample[k] = dmin[k] + raw[k] * (dmax[k] - dmin[k]) / maxSample;
                    c = space.ToColor(sample) ?? DocColor.Black;
                }
                rgba[o] = c.R; rgba[o + 1] = c.G; rgba[o + 2] = c.B; rgba[o + 3] = 255;
            keyed:
                if (hasKey)
                {
                    bool all = true;
                    for (int k = 0; k < n && all; k++) all = raw[k] >= colorKey![2 * k] && raw[k] <= colorKey[2 * k + 1];
                    if (all) { rgba[o + 3] = 0; keyMasked = true; }
                }
            }
        }

        bool hasAlpha = keyMasked || alpha < 1;
        if (alpha < 1) for (int i = 3; i < rgba.Length; i += 4) rgba[i] = (byte)Math.Round(rgba[i] * alpha);

        // soft mask or stencil mask: an alpha channel
        if (file.Get(dict, "SMask") is StreamToken smask)
        {
            var a = DecodeAlpha(file, smask, w, h, out string? why);
            if (a is null) problem = why;
            else
            {
                for (int i = 0; i < w * h; i++) rgba[i * 4 + 3] = (byte)(rgba[i * 4 + 3] * a[i] / 255);
                hasAlpha = true;
            }
        }
        else if (file.Get(dict, "Mask") is StreamToken mask)
        {
            var a = DecodeAlpha(file, mask, w, h, out string? why, stencilMask: true);
            if (a is null) problem = why;
            else
            {
                for (int i = 0; i < w * h; i++) rgba[i * 4 + 3] = (byte)(rgba[i * 4 + 3] * a[i] / 255);
                hasAlpha = true;
            }
        }
        return new Result(Png.Encode(rgba, w, h, hasAlpha), "image/png", w, h);
    }

    /// <summary>The alpha values (0..255, w × h) of a soft mask or a stencil mask picture, scaled to the size of the picture it belongs to.</summary>
    private static byte[]? DecodeAlpha(PdfFile file, StreamToken mask, int w, int h, out string? problem, bool stencilMask = false)
    {
        problem = null;
        var d = mask.StreamDictionary;
        int mw = (int)file.GetNumber(d, "Width", 0), mh = (int)file.GetNumber(d, "Height", 0);
        if (mw <= 0 || mh <= 0) { problem = "a picture's transparency mask has no size"; return null; }
        byte[] data;
        try { data = file.Decode(mask, out string? codec); if (codec is not null) { problem = "a picture's transparency mask uses an image codec that is not supported and was dropped"; return null; } }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            problem = "a picture's transparency mask could not be decoded";
            return null;
        }
        int bpc = stencilMask ? 1 : (int)file.GetNumber(d, "BitsPerComponent", 8);
        if (bpc is not (1 or 2 or 4 or 8 or 16)) { problem = "a picture's transparency mask has an unusual bit depth"; return null; }
        var decode = file.Numbers(file.Get(d, "Decode"));
        bool invert = decode is { Length: >= 2 } && decode[0] > decode[1];
        int stride = (mw * bpc + 7) / 8;
        double max = (1 << bpc) - 1;
        var alpha = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int sy = Math.Min(mh - 1, y * mh / h);
            for (int x = 0; x < w; x++)
            {
                int sx = Math.Min(mw - 1, x * mw / w);
                long bit = (long)sx * bpc;
                int bi = sy * stride + (int)(bit >> 3);
                int v;
                if (bi >= data.Length) v = (int)max;
                else if (bpc == 8) v = data[bi];
                else if (bpc == 16) v = data[bi];
                else v = (data[bi] >> (8 - bpc - (int)(bit & 7))) & ((1 << bpc) - 1);
                double a = bpc == 16 ? v / 255.0 : v / max;
                if (stencilMask) a = 1 - a;           // a stencil mask paints where its sample is 0
                if (invert) a = 1 - a;
                alpha[y * w + x] = (byte)Math.Round(Math.Clamp(a, 0, 1) * 255);
            }
        }
        return alpha;
    }

    // ---------------------------------------------------------------- PNG

    internal static class Png
    {
        private static readonly uint[] Table = MakeTable();

        private static uint[] MakeTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in type) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
            foreach (byte b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
        {
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
            s.Write(len);
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(t);
            s.Write(data);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, Crc(t, data));
            s.Write(len);
        }

        /// <summary>An 8-bit PNG from RGBA pixels (top row first); without <paramref name="alpha"/> the alpha channel is left out.</summary>
        public static byte[] Encode(byte[] rgba, int w, int h, bool alpha)
        {
            int bpp = alpha ? 4 : 3;
            var raw = new byte[(w * bpp + 1) * h];
            for (int y = 0; y < h; y++)
            {
                int o = y * (w * bpp + 1);
                raw[o] = 0;
                if (alpha) Buffer.BlockCopy(rgba, y * w * 4, raw, o + 1, w * 4);
                else
                    for (int x = 0; x < w; x++)
                    {
                        int s = (y * w + x) * 4, d = o + 1 + x * 3;
                        raw[d] = rgba[s]; raw[d + 1] = rgba[s + 1]; raw[d + 2] = rgba[s + 2];
                    }
            }
            using var ms = new MemoryStream();
            ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            Span<byte> ihdr = stackalloc byte[13];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)h);
            ihdr[8] = 8; ihdr[9] = (byte)(alpha ? 6 : 2); ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
            Chunk(ms, "IHDR", ihdr);
            using (var z = new MemoryStream())
            {
                using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
                Chunk(ms, "IDAT", z.ToArray());
            }
            Chunk(ms, "IEND", []);
            return ms.ToArray();
        }
    }
}
