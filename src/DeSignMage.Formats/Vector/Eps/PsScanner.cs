using System.Globalization;
using System.Text;

namespace DeSignMage.Formats.Vector.Eps;

/// <summary>A PostScript name: literal (<c>/name</c>) or executable.</summary>
internal sealed class PsName(string value, bool literal)
{
    public string Value { get; } = value;
    public bool Literal { get; } = literal;
    public override string ToString() => (Literal ? "/" : "") + Value;
}

/// <summary>A PostScript string (bytes).</summary>
internal sealed class PsStr(byte[] bytes)
{
    public byte[] Bytes { get; } = bytes;
    public override string ToString() => Encoding.Latin1.GetString(Bytes);
}

/// <summary>An array, or a procedure when <see cref="Executable"/>.</summary>
internal sealed class PsArr(List<object?> items, bool executable)
{
    public List<object?> Items { get; } = items;
    public bool Executable { get; } = executable;
}

internal sealed class PsDict(Dictionary<string, object?>? map = null)
{
    public Dictionary<string, object?> Map { get; } = map ?? [];
}

internal sealed class PsMark
{
    public static readonly PsMark Instance = new();
}

/// <summary>Reads PostScript tokens from a byte array. Comments are handed to <see cref="OnComment"/> (which may ask to skip a region).</summary>
internal sealed class PsScanner(byte[] data, int position, int end)
{
    public byte[] Data { get; } = data;
    public int Position { get; set; } = position;
    public int End { get; } = end;

    /// <summary>Called with the text of every comment line (without the leading %).</summary>
    public Action<string>? OnComment { get; set; }

    public bool AtEnd => Position >= End;

    private static bool IsSpace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    /// <summary>Skips lines until a comment line that starts with <paramref name="prefix"/> (after the %); the scanner stops after that line.</summary>
    public bool SkipUntilComment(string prefix)
    {
        while (Position < End)
        {
            int lineStart = Position;
            while (Position < End && Data[Position] != 10 && Data[Position] != 13) Position++;
            int lineEnd = Position;
            while (Position < End && (Data[Position] == 10 || Data[Position] == 13)) Position++;
            if (lineEnd - lineStart > prefix.Length && Data[lineStart] == '%' && StartsWith(lineStart + 1, prefix)) return true;
        }
        return false;
    }

    private bool StartsWith(int at, string s)
    {
        if (at + s.Length > End) return false;
        for (int i = 0; i < s.Length; i++) if (Data[at + i] != s[i]) return false;
        return true;
    }

    /// <summary>The next token: a double, <see cref="PsName"/>, <see cref="PsStr"/> or the strings "{" and "}" (as <see cref="PsName"/> with those values). Null at the end.</summary>
    public object? Next()
    {
        while (true)
        {
            while (Position < End && IsSpace(Data[Position])) Position++;
            if (Position >= End) return null;
            byte c = Data[Position];
            if (c == '%')
            {
                int s = ++Position;
                while (Position < End && Data[Position] != 10 && Data[Position] != 13) Position++;
                OnComment?.Invoke(Encoding.Latin1.GetString(Data, s, Position - s));
                continue;
            }
            switch (c)
            {
                case (byte)'(': return ReadString();
                case (byte)'<':
                    if (Position + 1 < End && Data[Position + 1] == '<') { Position += 2; return new PsName("<<", false); }
                    if (Position + 1 < End && Data[Position + 1] == '~') return ReadAscii85String();
                    return ReadHexString();
                case (byte)'>':
                    if (Position + 1 < End && Data[Position + 1] == '>') { Position += 2; return new PsName(">>", false); }
                    Position++;
                    continue;
                case (byte)'[': Position++; return new PsName("[", false);
                case (byte)']': Position++; return new PsName("]", false);
                case (byte)'{': Position++; return new PsName("{", false);
                case (byte)'}': Position++; return new PsName("}", false);
                case (byte)')': Position++; continue;
                case (byte)'/':
                {
                    Position++;
                    if (Position < End && Data[Position] == '/') Position++;   // //name: immediately evaluated; read as a literal
                    int s = Position;
                    while (Position < End && !IsSpace(Data[Position]) && !IsDelimiter(Data[Position])) Position++;
                    return new PsName(Encoding.Latin1.GetString(Data, s, Position - s), true);
                }
            }
            int start = Position;
            while (Position < End && !IsSpace(Data[Position]) && !IsDelimiter(Data[Position])) Position++;
            string word = Encoding.Latin1.GetString(Data, start, Position - start);
            if (TryNumber(word, out double n)) return n;
            return new PsName(word, false);
        }
    }

    private static bool TryNumber(string w, out double value)
    {
        value = 0;
        if (w.Length == 0) return false;
        char c = w[0];
        if (!(char.IsAsciiDigit(c) || c is '+' or '-' or '.')) return false;
        int hash = w.IndexOf('#');
        if (hash > 0 && int.TryParse(w[..hash], out int radix) && radix is >= 2 and <= 36)
        {
            long acc = 0;
            foreach (char d in w[(hash + 1)..])
            {
                int v = char.IsAsciiDigit(d) ? d - '0' : char.IsAsciiLetter(d) ? char.ToLowerInvariant(d) - 'a' + 10 : 99;
                if (v >= radix) return false;
                acc = acc * radix + v;
            }
            value = acc;
            return true;
        }
        return double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private PsStr ReadString()
    {
        Position++;
        var bytes = new List<byte>();
        int depth = 1;
        while (Position < End)
        {
            byte c = Data[Position++];
            if (c == '\\' && Position < End)
            {
                byte e = Data[Position++];
                switch (e)
                {
                    case (byte)'n': bytes.Add(10); break;
                    case (byte)'r': bytes.Add(13); break;
                    case (byte)'t': bytes.Add(9); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case 13: if (Position < End && Data[Position] == 10) Position++; break;
                    case 10: break;
                    default:
                        if (e >= '0' && e <= '7')
                        {
                            int v = e - '0';
                            for (int k = 0; k < 2 && Position < End && Data[Position] >= '0' && Data[Position] <= '7'; k++) v = v * 8 + (Data[Position++] - '0');
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
        return new PsStr(bytes.ToArray());
    }

    private PsStr ReadHexString()
    {
        Position++;
        var bytes = new List<byte>();
        int hi = -1;
        while (Position < End)
        {
            byte c = Data[Position++];
            if (c == '>') break;
            if (!Uri.IsHexDigit((char)c)) continue;
            int v = Convert.ToInt32(((char)c).ToString(), 16);
            if (hi < 0) hi = v; else { bytes.Add((byte)(hi * 16 + v)); hi = -1; }
        }
        if (hi >= 0) bytes.Add((byte)(hi * 16));
        return new PsStr(bytes.ToArray());
    }

    private PsStr ReadAscii85String()
    {
        Position += 2;
        int p = Position;
        var bytes = Ascii85.Decode(Data, ref p, End);
        Position = p;
        return new PsStr(bytes);
    }
}

/// <summary>ASCII base-85 decoding (PostScript and PDF).</summary>
internal static class Ascii85
{
    /// <summary>Decodes from <paramref name="pos"/> up to the "~&gt;" marker (which is consumed) or <paramref name="end"/>.</summary>
    public static byte[] Decode(byte[] data, ref int pos, int end)
    {
        var output = new List<byte>();
        var group = new uint[5];
        int n = 0;
        while (pos < end)
        {
            byte c = data[pos++];
            if (c == '~') { if (pos < end && data[pos] == '>') pos++; break; }
            if (c is 0 or 9 or 10 or 12 or 13 or 32) continue;
            if (c == 'z' && n == 0) { output.AddRange(new byte[4]); continue; }
            if (c < '!' || c > 'u') continue;
            group[n++] = (uint)(c - '!');
            if (n == 5)
            {
                uint v = 0;
                for (int i = 0; i < 5; i++) v = v * 85 + group[i];
                output.Add((byte)(v >> 24)); output.Add((byte)(v >> 16)); output.Add((byte)(v >> 8)); output.Add((byte)v);
                n = 0;
            }
        }
        if (n > 1)
        {
            for (int i = n; i < 5; i++) group[i] = 84;
            uint v = 0;
            for (int i = 0; i < 5; i++) v = v * 85 + group[i];
            for (int i = 0; i < n - 1; i++) output.Add((byte)(v >> (24 - 8 * i)));
        }
        return output.ToArray();
    }
}
