using DeSignMage.Core.Model;
using UglyToad.PdfPig.Tokens;

namespace DeSignMage.Formats.Vector.Pdf;

/// <summary>A PDF color space: how component values become a <see cref="DocColor"/>. CMYK stays CMYK, a spot color keeps its name.</summary>
internal abstract class PdfColorSpace
{
    public abstract int Components { get; }

    /// <summary>The color a new color space starts with.</summary>
    public virtual double[] Initial => new double[Components];

    public virtual bool IsPattern => false;

    /// <summary>The color for these components; null when the space paints nothing (a "None" separation).</summary>
    public abstract DocColor? ToColor(ReadOnlySpan<double> c);

    public static readonly PdfColorSpace Gray = new DeviceGray();
    public static readonly PdfColorSpace Rgb = new DeviceRgb();
    public static readonly PdfColorSpace Cmyk = new DeviceCmyk();
    public static readonly PdfColorSpace PatternSpace = new PatternCs(null);

    private static byte B(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    private sealed class DeviceGray : PdfColorSpace
    {
        public override int Components => 1;
        public override DocColor? ToColor(ReadOnlySpan<double> c) { byte g = B(c.Length > 0 ? c[0] : 0); return new DocColor(g, g, g); }
    }

    private sealed class DeviceRgb : PdfColorSpace
    {
        public override int Components => 3;
        public override DocColor? ToColor(ReadOnlySpan<double> c) => c.Length < 3 ? DocColor.Black : new DocColor(B(c[0]), B(c[1]), B(c[2]));
    }

    private sealed class DeviceCmyk : PdfColorSpace
    {
        public override int Components => 4;
        public override double[] Initial => [0, 0, 0, 1];
        public override DocColor? ToColor(ReadOnlySpan<double> c) =>
            c.Length < 4 ? DocColor.Black : DocColor.FromCmyk(Math.Clamp(c[0], 0, 1) * 100, Math.Clamp(c[1], 0, 1) * 100, Math.Clamp(c[2], 0, 1) * 100, Math.Clamp(c[3], 0, 1) * 100);
    }

    private sealed class PatternCs(PdfColorSpace? under) : PdfColorSpace
    {
        public PdfColorSpace? Under { get; } = under;
        public override int Components => Under?.Components ?? 0;
        public override bool IsPattern => true;
        public override DocColor? ToColor(ReadOnlySpan<double> c) => Under?.ToColor(c);
    }

    private sealed class Lab(double[] white, double[] range) : PdfColorSpace
    {
        public override int Components => 3;
        public override DocColor? ToColor(ReadOnlySpan<double> c)
        {
            if (c.Length < 3) return DocColor.Black;
            double l = Math.Clamp(c[0], 0, 100), a = Math.Clamp(c[1], range[0], range[1]), b = Math.Clamp(c[2], range[2], range[3]);
            double m = (l + 16) / 116, x = m + a / 500, z = m - b / 200;
            static double G(double t) => t >= 6.0 / 29 ? t * t * t : 108.0 / 841 * (t - 4.0 / 29);
            double X = white[0] * G(x), Y = white[1] * G(m), Z = white[2] * G(z);
            // Bradford-less D50 → sRGB approximation (the matrix for XYZ relative to D50)
            double r = 3.1338561 * X - 1.6168667 * Y - 0.4906146 * Z;
            double g = -0.9787684 * X + 1.9161415 * Y + 0.0334540 * Z;
            double bb = 0.0719453 * X - 0.2289914 * Y + 1.4052427 * Z;
            static double Gamma(double v) => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(Math.Max(v, 0), 1 / 2.4) - 0.055;
            return new DocColor(B(Gamma(r)), B(Gamma(g)), B(Gamma(bb)));
        }
    }

    private sealed class Indexed(PdfColorSpace baseSpace, byte[] table, int hival) : PdfColorSpace
    {
        public override int Components => 1;
        public override DocColor? ToColor(ReadOnlySpan<double> c)
        {
            int i = Math.Clamp((int)Math.Round(c.Length > 0 ? c[0] : 0), 0, hival);
            int n = baseSpace.Components;
            var comps = new double[n];
            for (int k = 0; k < n; k++) comps[k] = i * n + k < table.Length ? table[i * n + k] / 255.0 : 0;
            if (baseSpace is Lab) { comps[0] *= 100; }
            return baseSpace.ToColor(comps);
        }
    }

    private sealed class Tint(string[] names, PdfColorSpace alternate, PdfFunction? function) : PdfColorSpace
    {
        public override int Components => names.Length;
        public override double[] Initial => Enumerable.Repeat(1.0, names.Length).ToArray();

        public override DocColor? ToColor(ReadOnlySpan<double> c)
        {
            if (names.Length == 1 && names[0] == "None") return null;
            if (names.Length == 1 && names[0] == "All")
            {
                byte g = B(1 - (c.Length > 0 ? c[0] : 1));
                return new DocColor(g, g, g);
            }
            var inputs = c.ToArray();
            double[] alt = function is null ? Enumerable.Repeat(0.0, alternate.Components).ToArray() : function.Eval(inputs);
            if (alt.Length < alternate.Components) alt = alt.Concat(Enumerable.Repeat(0.0, alternate.Components - alt.Length)).ToArray();
            var color = alternate.ToColor(alt);
            if (color is null) return null;
            // a spot color keeps its name at full strength; a one-ink tint of it is just a color
            if (names.Length == 1 && c.Length > 0 && c[0] >= 0.999 && !string.IsNullOrWhiteSpace(names[0]))
                return color.Value with { Name = names[0] };
            return color;
        }
    }

    // ---------------------------------------------------------------- reading

    /// <summary>Reads a color space object or name. <paramref name="resources"/> is searched for named spaces.</summary>
    public static PdfColorSpace Read(PdfFile file, IToken? token, DictionaryToken? resources, int depth = 0)
    {
        if (depth > 6) return Gray;
        token = file.Resolve(token);
        if (token is NameToken name) return FromName(file, name.Data, resources, depth);
        if (token is not ArrayToken arr || arr.Length == 0) return Gray;
        string? family = file.Name(arr[0]);
        switch (family)
        {
            case "DeviceGray" or "G" or "CalGray": return Gray;
            case "DeviceRGB" or "RGB" or "CalRGB": return Rgb;
            case "DeviceCMYK" or "CMYK": return Cmyk;
            case "Lab":
            {
                var d = file.Dict(arr.Length > 1 ? arr[1] : null);
                var white = file.Numbers(file.Get(d, "WhitePoint")) ?? [0.9642, 1, 0.8249];
                var range = file.Numbers(file.Get(d, "Range")) ?? [-100, 100, -100, 100];
                return new Lab(white.Length >= 3 ? white : [0.9642, 1, 0.8249], range.Length >= 4 ? range : [-100, 100, -100, 100]);
            }
            case "ICCBased":
            {
                var stream = arr.Length > 1 ? file.Stream(arr[1]) : null;
                var dict = stream?.StreamDictionary;
                int n = (int)file.GetNumber(dict, "N", 3);
                if (file.Get(dict, "Alternate") is { } alt && n is not (1 or 3 or 4)) return Read(file, alt, resources, depth + 1);
                return n switch { 1 => Gray, 4 => Cmyk, _ => Rgb };
            }
            case "Indexed" or "I":
            {
                if (arr.Length < 4) return Gray;
                var baseSpace = Read(file, arr[1], resources, depth + 1);
                int hival = (int)(file.Number(arr[2]) ?? 255);
                byte[]? table = file.StringBytes(arr[3]);
                if (table is null && file.Stream(arr[3]) is { } ts) table = file.Decode(ts, out _);
                return new Indexed(baseSpace, table ?? [], Math.Clamp(hival, 0, 255));
            }
            case "Separation":
            {
                if (arr.Length < 4) return Gray;
                string ink = file.Name(arr[1]) ?? "";
                var alternate = Read(file, arr[2], resources, depth + 1);
                return new Tint([ink], alternate, PdfFunction.Read(file, arr[3]));
            }
            case "DeviceN":
            {
                if (arr.Length < 4) return Gray;
                var names = file.Array(arr[1])?.Data.Select(n => file.Name(n) ?? "").ToArray() ?? [""];
                var alternate = Read(file, arr[2], resources, depth + 1);
                return new Tint(names, alternate, PdfFunction.Read(file, arr[3]));
            }
            case "Pattern":
                return new PatternCs(arr.Length > 1 ? Read(file, arr[1], resources, depth + 1) : null);
            default:
                return Gray;
        }
    }

    private static PdfColorSpace FromName(PdfFile file, string name, DictionaryToken? resources, int depth)
    {
        switch (name)
        {
            case "DeviceGray" or "G" or "CalGray": return Gray;
            case "DeviceRGB" or "RGB" or "CalRGB": return Rgb;
            case "DeviceCMYK" or "CMYK": return Cmyk;
            case "Pattern": return PatternSpace;
        }
        var spaces = file.GetDict(resources, "ColorSpace");
        var entry = file.Get(spaces, name);
        return entry is null ? Gray : Read(file, entry, resources, depth + 1);
    }
}
