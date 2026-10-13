using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace DeSignMage.Rendering.Export;

/// <summary>Small helpers for writing PostScript text: numbers and the ASCII85 + Flate encoding of picture data.</summary>
internal static class PostScriptEncoding
{
    /// <summary>A number with at most 5 decimals, no exponent, no trailing zeros.</summary>
    public static string Num(double v)
    {
        var s = v.ToString("0.#####", CultureInfo.InvariantCulture);
        return s is "-0" or "" ? "0" : s;
    }

    /// <summary>The bytes as Flate-compressed, ASCII85-encoded text ending with <c>~&gt;</c>, in lines of 72 characters.</summary>
    /// <remarks>
    /// Every line starts with one space, which the ASCII85 filter ignores: a line of picture data that began with <c>%%</c> would otherwise look like
    /// a structuring comment to programs that read the file line by line.
    /// </remarks>
    public static void AppendFlateAscii85(StringBuilder sb, ReadOnlySpan<byte> data)
    {
        byte[] deflated;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
            deflated = ms.ToArray();
        }
        AppendAscii85(sb, deflated);
    }

    public static void AppendAscii85(StringBuilder sb, ReadOnlySpan<byte> data)
    {
        const int lineLength = 72;
        int column = 0;
        void Put(char c)
        {
            if (column == 0) sb.Append(' ');
            sb.Append(c);
            if (++column >= lineLength)
            {
                sb.Append('\n');
                column = 0;
            }
        }

        Span<char> group = stackalloc char[5];
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            uint v = (uint)(data[i] << 24 | data[i + 1] << 16 | data[i + 2] << 8 | data[i + 3]);
            if (v == 0)
            {
                Put('z');
                continue;
            }
            for (int k = 4; k >= 0; k--)
            {
                group[k] = (char)('!' + v % 85);
                v /= 85;
            }
            foreach (var c in group) Put(c);
        }
        int rest = data.Length - i;
        if (rest > 0)
        {
            uint v = 0;
            for (int k = 0; k < 4; k++) v = v << 8 | (k < rest ? data[i + k] : (byte)0);
            for (int k = 4; k >= 0; k--)
            {
                group[k] = (char)('!' + v % 85);
                v /= 85;
            }
            for (int k = 0; k <= rest; k++) Put(group[k]);
        }
        Put('~');
        Put('>');
        if (column != 0) sb.Append('\n');
    }

    /// <summary>Hex digits in lines of 64 characters (for the sampled color function of a shading).</summary>
    public static void AppendHex(StringBuilder sb, ReadOnlySpan<byte> data)
    {
        int column = 0;
        foreach (var b in data)
        {
            sb.Append("0123456789abcdef"[b >> 4]).Append("0123456789abcdef"[b & 15]);
            column += 2;
            if (column >= 64)
            {
                sb.Append('\n');
                column = 0;
            }
        }
        if (column != 0) sb.Append('\n');
    }

    /// <summary>Text for a DSC comment: ASCII only, no line breaks, no parentheses trouble.</summary>
    public static string Comment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Untitled";
        var sb = new StringBuilder();
        foreach (var c in text)
            sb.Append(c is >= ' ' and <= '~' ? c : '?');
        var s = sb.ToString().Trim();
        return s.Length > 120 ? s[..120] : s;
    }
}
