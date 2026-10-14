using System.Globalization;
using System.Text;

namespace DeSignMage.Formats.Vector.Pdf;

/// <summary>A PDF name object (<c>/Name</c>), as an operand of a content stream operator.</summary>
internal readonly record struct PdfName(string Value)
{
    public override string ToString() => "/" + Value;
}

/// <summary>One operator of a content stream with the operands in front of it. Operands: double, <see cref="PdfName"/>, byte[] (a string), bool, null, List&lt;object?&gt;, Dictionary&lt;string, object?&gt;.</summary>
internal sealed class PdfOp(string name, List<object?> operands)
{
    public string Name { get; } = name;
    public List<object?> Operands { get; } = operands;

    /// <summary>For BI: the image dictionary and its raw data.</summary>
    public Dictionary<string, object?>? InlineDict;
    public byte[]? InlineData;

    public double Num(int i) => i < Operands.Count && Operands[i] is double d ? d : 0;
    public PdfName? NameAt(int i) => i < Operands.Count && Operands[i] is PdfName n ? n : null;
    public bool Has(int count) => Operands.Count >= count;
}

/// <summary>Reads the operators of a page or form content stream.</summary>
internal static class PdfContent
{
    public static IEnumerable<PdfOp> Parse(byte[] data)
    {
        int p = 0;
        var operands = new List<object?>();
        while (true)
        {
            SkipSpace(data, ref p);
            if (p >= data.Length) yield break;
            byte c = data[p];
            if (c is (byte)'/' or (byte)'(' or (byte)'<' or (byte)'[' || c == '+' || c == '-' || c == '.' || (c >= '0' && c <= '9'))
            {
                if (TryValue(data, ref p, out object? v)) operands.Add(v);
                else p++;
                continue;
            }
            if (c is (byte)']' or (byte)'>' or (byte)')' or (byte)'}' or (byte)'{') { p++; continue; }

            string op = ReadKeyword(data, ref p);
            if (op.Length == 0) { p++; continue; }
            switch (op)
            {
                case "true": operands.Add(true); continue;
                case "false": operands.Add(false); continue;
                case "null": operands.Add(null); continue;
            }
            if (op == "BI")
            {
                var bi = new PdfOp("BI", operands);
                ReadInlineImage(data, ref p, bi);
                operands = [];
                yield return bi;
                continue;
            }
            yield return new PdfOp(op, operands);
            operands = [];
        }
    }

    // ---------------------------------------------------------------- lexing

    private static bool IsSpace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static void SkipSpace(byte[] d, ref int p)
    {
        while (p < d.Length)
        {
            if (IsSpace(d[p])) p++;
            else if (d[p] == '%') { while (p < d.Length && d[p] != 10 && d[p] != 13) p++; }
            else break;
        }
    }

    private static string ReadKeyword(byte[] d, ref int p)
    {
        int start = p;
        while (p < d.Length && !IsSpace(d[p]) && !IsDelimiter(d[p])) p++;
        return Encoding.Latin1.GetString(d, start, p - start);
    }

    private static bool TryValue(byte[] d, ref int p, out object? value)
    {
        value = null;
        byte c = d[p];
        switch (c)
        {
            case (byte)'/':
                p++;
                value = new PdfName(ReadName(d, ref p));
                return true;
            case (byte)'(':
                value = ReadLiteralString(d, ref p);
                return true;
            case (byte)'<':
                if (p + 1 < d.Length && d[p + 1] == '<') { p += 2; value = ReadDict(d, ref p); return true; }
                value = ReadHexString(d, ref p);
                return true;
            case (byte)'[':
            {
                p++;
                var list = new List<object?>();
                while (true)
                {
                    SkipSpace(d, ref p);
                    if (p >= d.Length) break;
                    if (d[p] == ']') { p++; break; }
                    if (TryValue(d, ref p, out var item)) list.Add(item);
                    else
                    {
                        string kw = ReadKeyword(d, ref p);
                        if (kw == "true") list.Add(true); else if (kw == "false") list.Add(false); else if (kw == "null") list.Add(null);
                        else if (kw.Length == 0) p++;
                    }
                }
                value = list;
                return true;
            }
            default:
            {
                int start = p;
                if (c is (byte)'+' or (byte)'-') p++;
                while (p < d.Length && d[p] == '-') p++;   // "--5" is -5 in real files
                int digits = 0;
                bool dot = false;
                while (p < d.Length)
                {
                    byte b = d[p];
                    if (b >= '0' && b <= '9') { digits++; p++; }
                    else if (b == '.' && !dot) { dot = true; p++; }
                    else break;
                }
                if (p == start) return false;
                string s = Encoding.ASCII.GetString(d, start, p - start).Replace("--", "-");
                value = digits > 0 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : 0.0;
                return true;
            }
        }
    }

    private static string ReadName(byte[] d, ref int p)
    {
        var sb = new StringBuilder();
        while (p < d.Length && !IsSpace(d[p]) && !IsDelimiter(d[p]))
        {
            if (d[p] == '#' && p + 2 < d.Length && Uri.IsHexDigit((char)d[p + 1]) && Uri.IsHexDigit((char)d[p + 2]))
            {
                sb.Append((char)Convert.ToInt32(Encoding.ASCII.GetString(d, p + 1, 2), 16));
                p += 3;
            }
            else sb.Append((char)d[p++]);
        }
        return sb.ToString();
    }

    private static byte[] ReadLiteralString(byte[] d, ref int p)
    {
        p++;
        var bytes = new List<byte>();
        int depth = 1;
        while (p < d.Length)
        {
            byte c = d[p++];
            if (c == '\\' && p < d.Length)
            {
                byte e = d[p++];
                switch (e)
                {
                    case (byte)'n': bytes.Add(10); break;
                    case (byte)'r': bytes.Add(13); break;
                    case (byte)'t': bytes.Add(9); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case 13: if (p < d.Length && d[p] == 10) p++; break;
                    case 10: break;
                    default:
                        if (e >= '0' && e <= '7')
                        {
                            int v = e - '0';
                            for (int k = 0; k < 2 && p < d.Length && d[p] >= '0' && d[p] <= '7'; k++) v = v * 8 + (d[p++] - '0');
                            bytes.Add((byte)v);
                        }
                        else bytes.Add(e);
                        break;
                }
            }
            else if (c == '(') { depth++; bytes.Add(c); }
            else if (c == ')') { if (--depth == 0) break; bytes.Add(c); }
            else bytes.Add(c);
        }
        return bytes.ToArray();
    }

    private static byte[] ReadHexString(byte[] d, ref int p)
    {
        p++;
        var bytes = new List<byte>();
        int hi = -1;
        while (p < d.Length)
        {
            byte c = d[p++];
            if (c == '>') break;
            if (!Uri.IsHexDigit((char)c)) continue;
            int v = Convert.ToInt32(((char)c).ToString(), 16);
            if (hi < 0) hi = v; else { bytes.Add((byte)(hi * 16 + v)); hi = -1; }
        }
        if (hi >= 0) bytes.Add((byte)(hi * 16));
        return bytes.ToArray();
    }

    private static Dictionary<string, object?> ReadDict(byte[] d, ref int p)
    {
        var dict = new Dictionary<string, object?>();
        while (true)
        {
            SkipSpace(d, ref p);
            if (p >= d.Length) break;
            if (d[p] == '>' && p + 1 < d.Length && d[p + 1] == '>') { p += 2; break; }
            if (d[p] != '/') { p++; continue; }
            p++;
            string key = ReadName(d, ref p);
            SkipSpace(d, ref p);
            if (p >= d.Length) break;
            if (TryValue(d, ref p, out var v)) dict[key] = v;
            else
            {
                string kw = ReadKeyword(d, ref p);
                dict[key] = kw switch { "true" => true, "false" => false, _ => null };
                if (kw.Length == 0) p++;
            }
        }
        return dict;
    }

    // ---------------------------------------------------------------- inline images

    private static void ReadInlineImage(byte[] d, ref int p, PdfOp op)
    {
        var dict = new Dictionary<string, object?>();
        while (true)
        {
            SkipSpace(d, ref p);
            if (p >= d.Length) return;
            if (d[p] == '/')
            {
                p++;
                string key = ReadName(d, ref p);
                SkipSpace(d, ref p);
                if (p < d.Length && TryValue(d, ref p, out var v)) dict[key] = v;
                else
                {
                    string kw = ReadKeyword(d, ref p);
                    dict[key] = kw switch { "true" => true, "false" => false, _ => null };
                }
                continue;
            }
            string word = ReadKeyword(d, ref p);
            if (word == "ID") break;
            if (word.Length == 0) p++;
        }
        if (p < d.Length && IsSpace(d[p])) p++;   // one whitespace byte after ID
        op.InlineDict = dict;

        // The data ends at "EI" with white space around it; for unfiltered data the length is known.
        int start = p;
        int known = KnownLength(dict);
        if (known >= 0 && start + known <= d.Length)
        {
            op.InlineData = d[start..(start + known)];
            p = start + known;
            SkipSpace(d, ref p);
            if (p + 1 < d.Length && d[p] == 'E' && d[p + 1] == 'I') p += 2;
            return;
        }
        for (int i = start; i + 1 < d.Length; i++)
        {
            if (d[i] == 'E' && d[i + 1] == 'I' && (i == start || IsSpace(d[i - 1])) && (i + 2 >= d.Length || IsSpace(d[i + 2]) || IsDelimiter(d[i + 2])))
            {
                int end = i;
                if (end > start && IsSpace(d[end - 1])) end--;
                op.InlineData = d[start..end];
                p = i + 2;
                return;
            }
        }
        op.InlineData = d[start..];
        p = d.Length;
    }

    private static int KnownLength(Dictionary<string, object?> dict)
    {
        if (dict.ContainsKey("F") || dict.ContainsKey("Filter")) return -1;
        int W = Int(dict, "W", "Width"), H = Int(dict, "H", "Height");
        if (W <= 0 || H <= 0) return -1;
        bool mask = Bool(dict, "IM", "ImageMask");
        int bpc = mask ? 1 : Int(dict, "BPC", "BitsPerComponent");
        if (bpc <= 0) bpc = 8;
        int comps = 1;
        if (!mask)
        {
            object? cs = Get(dict, "CS", "ColorSpace");
            if (cs is PdfName n) comps = n.Value switch { "G" or "DeviceGray" or "CalGray" => 1, "RGB" or "DeviceRGB" or "CalRGB" => 3, "CMYK" or "DeviceCMYK" => 4, "I" or "Indexed" => 1, _ => -1 };
            else if (cs is List<object?> l && l.Count > 0 && l[0] is PdfName n0 && n0.Value is "I" or "Indexed") comps = 1;
            else comps = -1;
        }
        if (comps < 0) return -1;
        return ((W * comps * bpc + 7) / 8) * H;
    }

    private static object? Get(Dictionary<string, object?> d, string a, string b) => d.TryGetValue(a, out var v) ? v : d.TryGetValue(b, out var w) ? w : null;
    private static int Int(Dictionary<string, object?> d, string a, string b) => Get(d, a, b) is double v ? (int)v : 0;
    private static bool Bool(Dictionary<string, object?> d, string a, string b) => Get(d, a, b) is true;
}
