using System.Globalization;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Lyt;

/// <summary>Document colors as the 57-byte color records of a layout.</summary>
internal static class LytColors
{
    /// <summary>
    /// A named color keeps its name and vendor; an unnamed one carries its values as text ("0,25,100,4"), as the layouts do.
    /// A CMYK color is written as CMYK (percent × 100); everything else as RGB. Names longer than the record holds are cut.
    /// </summary>
    public static LytColor From(DocColor c)
    {
        string vendor = LytEncoding.Sanitize(c.Vendor ?? "");
        string? name = string.IsNullOrWhiteSpace(c.Name) ? null : LytEncoding.Sanitize(c.Name);
        if (c.Model == ColorModel.Cmyk && c.Cmyk is { } k)
        {
            return new LytColor(name ?? $"{Num(k.C)},{Num(k.M)},{Num(k.Y)},{Num(k.K)}", vendor, LytColor.ModelCmyk,
                Hundredths(k.C), Hundredths(k.M), Hundredths(k.Y), Hundredths(k.K));
        }
        return new LytColor(name ?? $"{c.R},{c.G},{c.B}", vendor, LytColor.ModelRgb, c.R, c.G, c.B, 0);
    }

    private static ushort Hundredths(double percent) => (ushort)Math.Clamp(Math.Round(percent * 100), 0, 10000);

    private static string Num(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
