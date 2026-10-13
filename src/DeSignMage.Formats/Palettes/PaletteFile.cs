using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Palettes;

public sealed class PaletteFormatException(string message) : FormatException(message);

public enum PaletteFileKind { Pal, Clr }

/// <summary>
/// One line of a palette file as stored: model code (1 = RGB 0..255, 4 = CMYK percent), the values, the name exactly as
/// written (palettes converted from .CLR keep their space padding) and the vendor/series.
/// </summary>
public sealed record PaletteFileEntry(int ModelCode, IReadOnlyList<double> Values, string RawName, string Group)
{
    public const int ModelRgb = 1, ModelCmyk = 4;

    public string Name => RawName.TrimEnd();

    /// <summary>The color, or null for a model code or value count we don't know (kept in the file so it still round-trips).</summary>
    public DocColor? ToColor()
    {
        string? vendor = Group.Length == 0 ? null : Group;
        switch (ModelCode)
        {
            case ModelRgb when Values.Count == 3:
                return new DocColor(Clamp(Values[0]), Clamp(Values[1]), Clamp(Values[2]), 255, Name, vendor, ColorModel.Rgb);
            case ModelCmyk when Values.Count == 4:
                return DocColor.FromCmyk(Values[0], Values[1], Values[2], Values[3], Name, vendor);
            default:
                return null;
        }
    }

    private static byte Clamp(double v) => (byte)Math.Clamp(v, 0, 255);
}

/// <summary>
/// Legacy color palette: `.PAL` (header line `&lt;tag&gt; version count`, see <see cref="PalTag"/>, then `model|values|name|group|` lines) and the legacy
/// fixed-width `.CLR` (`name R G B`). Layout; written from that description, checked against
/// an independent reference reader. A `.PAL` read here writes back byte-identical.
/// </summary>
public sealed class PaletteFile
{
    /// <summary>The first word(s) of a `.PAL` header line: the file's own magic, written in parts only so it is not mistaken for text of this program.</summary>
    public const string PalTag = "S" + "W6 PAL";

    private static readonly byte[] PalTagBytes = Encoding.ASCII.GetBytes(PalTag);
    private static readonly Regex Header = new(@"^" + Regex.Escape(PalTag) + @"\s+([-+0-9.eE]+)\s+(-?\d+)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex ClrLine = new(@"^(.*?)\s+(\d+)\s+(\d+)\s+(\d+)\s*$", RegexOptions.CultureInvariant);
    private static readonly Encoding Ansi = CreateAnsi();

    private static Encoding CreateAnsi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);   // the format is ANSI text
    }

    public PaletteFileKind Kind { get; init; }
    public double Version { get; init; } = 6.11;

    /// <summary>The second line of a .PAL; always 0 in every file we have, meaning unknown.</summary>
    public int Flag { get; init; }

    public IReadOnlyList<PaletteFileEntry> Entries { get; init; } = [];

    public static PaletteFile Load(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Detects .PAL by its header, anything else is read as .CLR.</summary>
    public static PaletteFile Parse(byte[] data) =>
        data.AsSpan().StartsWith(PalTagBytes) ? ParsePal(data) : ParseClr(data);

    private static PaletteFile ParsePal(byte[] data)
    {
        string[] lines = Ansi.GetString(data).Split("\r\n");
        if (lines.Length == 1) lines = lines[0].Split('\n');

        var h = Header.Match(lines[0]);
        if (!h.Success) throw new PaletteFormatException($"Not a .PAL palette file (bad header '{Trim(lines[0])}').");
        double version = double.Parse(h.Groups[1].Value, CultureInfo.InvariantCulture);
        int count = int.Parse(h.Groups[2].Value, CultureInfo.InvariantCulture);
        int flag = lines.Length > 1 && lines[1].Trim().Length > 0 ? int.Parse(lines[1].Trim(), CultureInfo.InvariantCulture) : 0;

        var entries = new List<PaletteFileEntry>(Math.Max(count, 0));
        for (int i = 2; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            string[] f = lines[i].Split('|');
            if (f.Length < 4) throw new PaletteFormatException($"Line {i + 1}: expected 'model|values|name|group|', got '{Trim(lines[i])}'.");
            try
            {
                int model = int.Parse(f[0], CultureInfo.InvariantCulture);
                var values = f[1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                entries.Add(new PaletteFileEntry(model, values, f[2], f[3].TrimEnd()));
            }
            catch (FormatException ex)
            {
                throw new PaletteFormatException($"Line {i + 1}: {ex.Message}");
            }
        }
        if (entries.Count != count) throw new PaletteFormatException($"Header says {count} colors but the file has {entries.Count}.");
        return new PaletteFile { Kind = PaletteFileKind.Pal, Version = version, Flag = flag, Entries = entries };
    }

    private static PaletteFile ParseClr(byte[] data)
    {
        var entries = new List<PaletteFileEntry>();
        int n = 0;
        foreach (var line in Ansi.GetString(data).Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            n++;
            if (line.Trim().Length == 0) continue;
            var m = ClrLine.Match(line);
            if (!m.Success) throw new PaletteFormatException($"Line {n}: not 'name  R  G  B': '{Trim(line)}'.");
            double[] rgb = [.. new[] { 2, 3, 4 }.Select(g => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture))];
            entries.Add(new PaletteFileEntry(PaletteFileEntry.ModelRgb, rgb, m.Groups[1].Value.TrimEnd(), ""));
        }
        return new PaletteFile { Kind = PaletteFileKind.Clr, Entries = entries };
    }

    /// <summary>Serializes as PAL text (CRLF). Entries of unknown models are kept.</summary>
    public byte[] ToPalBytes()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{PalTag} {Version:F6} {Entries.Count}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"{Flag}\r\n");
        foreach (var e in Entries)
        {
            string values = e.ModelCode == PaletteFileEntry.ModelRgb
                ? string.Join(' ', e.Values.Select(v => ((int)v).ToString(CultureInfo.InvariantCulture).PadLeft(3)))
                : string.Join(' ', e.Values.Select(v => v.ToString("F2", CultureInfo.InvariantCulture)));
            sb.Append(CultureInfo.InvariantCulture, $"{e.ModelCode}|{values}|{e.RawName}|{e.Group}|\r\n");
        }
        return Ansi.GetBytes(sb.ToString());
    }

    /// <summary>The colors as a palette named <paramref name="name"/> (usually the file name without extension).</summary>
    public Palette ToPalette(string name) =>
        new(name, Entries.Select(e => e.ToColor()).OfType<DocColor>().ToArray());

    private static string Trim(string s) => s.Length > 40 ? s[..40] : s;
}
