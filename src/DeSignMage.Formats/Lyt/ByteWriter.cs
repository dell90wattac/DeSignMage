using System.Buffers.Binary;
using System.Text;

namespace DeSignMage.Formats.Lyt;

/// <summary>The legacy text encoding of layout files (Windows-1252, the "ANSI" code page).</summary>
internal static class LytEncoding
{
    public static readonly Encoding Ansi = Create();

    private static Encoding Create()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252);
        }
        catch (Exception)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>True when every character of <paramref name="text"/> survives a round trip through the code page.</summary>
    public static bool CanEncode(string text) => Ansi.GetString(Ansi.GetBytes(text)) == text;

    /// <summary>The text with every character the code page cannot hold replaced by '?'.</summary>
    public static string Sanitize(string text) => CanEncode(text) ? text : Ansi.GetString(Ansi.GetBytes(text));
}

/// <summary>Little-endian writer into a growing buffer; the mirror image of <see cref="ByteReader"/>.</summary>
internal sealed class ByteWriter
{
    private readonly MemoryStream _ms = new();

    public int Position => (int)_ms.Position;

    public byte[] ToArray() => _ms.ToArray();

    public void U8(byte v) => _ms.WriteByte(v);

    public void U16(ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        _ms.Write(b);
    }

    public void I16(short v) => U16((ushort)v);

    public void U32(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        _ms.Write(b);
    }

    public void I32(int v) => U32((uint)v);

    public void F32(float v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, v);
        _ms.Write(b);
    }

    public void F32(double v) => F32((float)v);

    public void F64(double v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(b, v);
        _ms.Write(b);
    }

    public void Bytes(ReadOnlySpan<byte> v) => _ms.Write(v);

    public void Zeros(int n)
    {
        if (n <= 0) return;
        Span<byte> b = n <= 512 ? stackalloc byte[n] : new byte[n];
        b.Clear();
        _ms.Write(b);
    }

    /// <summary>A fixed-size text buffer: the text, a NUL, then zeros. Longer text is cut so the NUL always fits.</summary>
    public void Fixed(string s, int size)
    {
        var bytes = LytEncoding.Ansi.GetBytes(s);
        int n = Math.Min(bytes.Length, size - 1);
        _ms.Write(bytes, 0, n);
        Zeros(size - n);
    }

    /// <summary>i16 length followed by that many bytes, no terminator.</summary>
    public void String16(string s)
    {
        var bytes = LytEncoding.Ansi.GetBytes(s);
        int n = Math.Min(bytes.Length, short.MaxValue);
        I16((short)n);
        _ms.Write(bytes, 0, n);
    }

    /// <summary>The packed 57-byte color record: name[28], vendor[20], four 16-bit values, model.</summary>
    public void Color(LytColor c)
    {
        Fixed(c.Name, 28);
        Fixed(c.Vendor, 20);
        U16(c.V0); U16(c.V1); U16(c.V2); U16(c.V3);
        U8(c.Model);
    }

    /// <summary>An unset color (model 0): what bitmaps and cut lines carry.</summary>
    public void NoColor() => Zeros(57);
}
