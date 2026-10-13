using System.Buffers.Binary;
using System.Text;

namespace DeSignMage.Formats.Lyt;

/// <summary>The file is damaged, truncated, or not a layout this reader understands.</summary>
public sealed class LytFormatException(string message) : Exception(message);

/// <summary>Little-endian reader over a byte array. Positions are absolute offsets into the array.</summary>
internal sealed class ByteReader(byte[] data, int position, int end)
{
    private static readonly Encoding Ansi = GetAnsi();

    private static Encoding GetAnsi()
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

    public byte[] Data { get; } = data;
    public int Position { get; set; } = position;
    public int End { get; } = end;
    public int Remaining => End - Position;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || Position + n > End) throw new LytFormatException($"Unexpected end of data at offset {Position} (wanted {n} bytes).");
        var s = new ReadOnlySpan<byte>(Data, Position, n);
        Position += n;
        return s;
    }

    public void Skip(int n) => Take(n);
    public byte U8() => Take(1)[0];
    public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
    public byte[] Bytes(int n) => Take(n).ToArray();

    /// <summary>A fixed-size text buffer: the string stops at the first NUL; the rest is leftover memory.</summary>
    public string FixedString(int n)
    {
        var s = Take(n);
        int len = s.IndexOf((byte)0);
        return Ansi.GetString(len < 0 ? s : s[..len]);
    }

    /// <summary>i16 length followed by that many bytes, no terminator.</summary>
    public string String16()
    {
        int n = I16();
        return n > 0 ? Ansi.GetString(Take(n)) : "";
    }

    /// <summary>Length-prefixed with a NUL inside the length (object names).</summary>
    public string StringWithNul(int n)
    {
        var s = Take(n);
        int len = s.IndexOf((byte)0);
        return Ansi.GetString(len < 0 ? s : s[..len]);
    }
}
