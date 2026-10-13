namespace DeSignMage.Core.Model;

/// <summary>
/// Plain device conversions between RGB, CMYK and HSV. Print software converts CMYK through ICC profiles; this is the
/// profile-less version used for on-screen previews until color management exists. The CMYK formula is the same
/// one an independent palette reader uses, so palette previews match it.
/// </summary>
public static class ColorMath
{
    /// <summary>CMYK in percent (0..100) → R, G, B bytes.</summary>
    public static (byte R, byte G, byte B) CmykToRgb(double c, double m, double y, double k)
    {
        c /= 100; m /= 100; y /= 100; k /= 100;
        return (ToByte(255 * (1 - c) * (1 - k)), ToByte(255 * (1 - m) * (1 - k)), ToByte(255 * (1 - y) * (1 - k)));
    }

    public static Cmyk RgbToCmyk(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double k = 1 - Math.Max(rf, Math.Max(gf, bf));
        if (k >= 1) return new Cmyk(0, 0, 0, 100);
        double d = 1 - k;
        return new Cmyk(100 * (1 - rf - k) / d, 100 * (1 - gf - k) / d, 100 * (1 - bf - k) / d, 100 * k);
    }

    /// <summary>H 0..360, S and V 0..100 → R, G, B bytes.</summary>
    public static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 100) / 100;
        v = Math.Clamp(v, 0, 100) / 100;
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return (ToByte((r + m) * 255), ToByte((g + m) * 255), ToByte((b + m) * 255));
    }

    /// <summary>R, G, B bytes → H 0..360, S and V 0..100. Hue is 0 for grays.</summary>
    public static (double H, double S, double V) RgbToHsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf));
        double d = max - min;
        double h = d == 0 ? 0
            : max == rf ? 60 * (((gf - bf) / d % 6 + 6) % 6)
            : max == gf ? 60 * ((bf - rf) / d + 2)
            : 60 * ((rf - gf) / d + 4);
        return (h, max == 0 ? 0 : 100 * d / max, 100 * max);
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
