using System.Globalization;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Vector.Svg;

/// <summary>CSS / SVG color values: names, #rgb, #rrggbb, #rrggbbaa, rgb(), rgba(), hsl(), hsla().</summary>
internal static class SvgColors
{
    private static readonly Dictionary<string, uint> Named = Build(
        "aliceblue f0f8ff antiquewhite faebd7 aqua 00ffff aquamarine 7fffd4 azure f0ffff beige f5f5dc bisque ffe4c4 black 000000 blanchedalmond ffebcd " +
        "blue 0000ff blueviolet 8a2be2 brown a52a2a burlywood deb887 cadetblue 5f9ea0 chartreuse 7fff00 chocolate d2691e coral ff7f50 " +
        "cornflowerblue 6495ed cornsilk fff8dc crimson dc143c cyan 00ffff darkblue 00008b darkcyan 008b8b darkgoldenrod b8860b darkgray a9a9a9 " +
        "darkgreen 006400 darkgrey a9a9a9 darkkhaki bdb76b darkmagenta 8b008b darkolivegreen 556b2f darkorange ff8c00 darkorchid 9932cc " +
        "darkred 8b0000 darksalmon e9967a darkseagreen 8fbc8f darkslateblue 483d8b darkslategray 2f4f4f darkslategrey 2f4f4f darkturquoise 00ced1 " +
        "darkviolet 9400d3 deeppink ff1493 deepskyblue 00bfff dimgray 696969 dimgrey 696969 dodgerblue 1e90ff firebrick b22222 floralwhite fffaf0 " +
        "forestgreen 228b22 fuchsia ff00ff gainsboro dcdcdc ghostwhite f8f8ff gold ffd700 goldenrod daa520 gray 808080 green 008000 " +
        "greenyellow adff2f grey 808080 honeydew f0fff0 hotpink ff69b4 indianred cd5c5c indigo 4b0082 ivory fffff0 khaki f0e68c lavender e6e6fa " +
        "lavenderblush fff0f5 lawngreen 7cfc00 lemonchiffon fffacd lightblue add8e6 lightcoral f08080 lightcyan e0ffff lightgoldenrodyellow fafad2 " +
        "lightgray d3d3d3 lightgreen 90ee90 lightgrey d3d3d3 lightpink ffb6c1 lightsalmon ffa07a lightseagreen 20b2aa lightskyblue 87cefa " +
        "lightslategray 778899 lightslategrey 778899 lightsteelblue b0c4de lightyellow ffffe0 lime 00ff00 limegreen 32cd32 linen faf0e6 " +
        "magenta ff00ff maroon 800000 mediumaquamarine 66cdaa mediumblue 0000cd mediumorchid ba55d3 mediumpurple 9370db mediumseagreen 3cb371 " +
        "mediumslateblue 7b68ee mediumspringgreen 00fa9a mediumturquoise 48d1cc mediumvioletred c71585 midnightblue 191970 mintcream f5fffa " +
        "mistyrose ffe4e1 moccasin ffe4b5 navajowhite ffdead navy 000080 oldlace fdf5e6 olive 808000 olivedrab 6b8e23 orange ffa500 " +
        "orangered ff4500 orchid da70d6 palegoldenrod eee8aa palegreen 98fb98 paleturquoise afeeee palevioletred db7093 papayawhip ffefd5 " +
        "peachpuff ffdab9 peru cd853f pink ffc0cb plum dda0dd powderblue b0e0e6 purple 800080 rebeccapurple 663399 red ff0000 rosybrown bc8f8f " +
        "royalblue 4169e1 saddlebrown 8b4513 salmon fa8072 sandybrown f4a460 seagreen 2e8b57 seashell fff5ee sienna a0522d silver c0c0c0 " +
        "skyblue 87ceeb slateblue 6a5acd slategray 708090 slategrey 708090 snow fffafa springgreen 00ff7f steelblue 4682b4 tan d2b48c teal 008080 " +
        "thistle d8bfd8 tomato ff6347 turquoise 40e0d0 violet ee82ee wheat f5deb3 white ffffff whitesmoke f5f5f5 yellow ffff00 yellowgreen 9acd32");

    private static Dictionary<string, uint> Build(string table)
    {
        var parts = table.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var d = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < parts.Length; i += 2) d[parts[i]] = uint.Parse(parts[i + 1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return d;
    }

    /// <summary>Parses a color; false for anything else (url(), none, inherit, system colors).</summary>
    public static bool TryParse(string? text, out DocColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        if (s.StartsWith('#'))
        {
            string h = s[1..];
            if (!h.All(Uri.IsHexDigit)) return false;
            switch (h.Length)
            {
                case 3 or 4:
                {
                    int Digit(int i) => Convert.ToInt32(new string(h[i], 2), 16);
                    color = new DocColor((byte)Digit(0), (byte)Digit(1), (byte)Digit(2), h.Length == 4 ? (byte)Digit(3) : (byte)255);
                    return true;
                }
                case 6 or 8:
                {
                    int Byte(int i) => Convert.ToInt32(h.Substring(i * 2, 2), 16);
                    color = new DocColor((byte)Byte(0), (byte)Byte(1), (byte)Byte(2), h.Length == 8 ? (byte)Byte(3) : (byte)255);
                    return true;
                }
                default: return false;
            }
        }
        int paren = s.IndexOf('(');
        if (paren > 0 && s.EndsWith(')'))
        {
            string fn = s[..paren].Trim().ToLowerInvariant();
            var args = s[(paren + 1)..^1].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (fn is "rgb" or "rgba" && args.Length >= 3)
            {
                if (!Component(args[0], 255, out double r) || !Component(args[1], 255, out double g) || !Component(args[2], 255, out double b)) return false;
                double a = 1;
                if (args.Length >= 4 && !Alpha(args[3], out a)) return false;
                color = new DocColor(Clamp(r), Clamp(g), Clamp(b), Clamp(a * 255));
                return true;
            }
            if (fn is "hsl" or "hsla" && args.Length >= 3)
            {
                if (!double.TryParse(args[0].Replace("deg", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double h) ||
                    !Component(args[1], 100, out double sat) || !Component(args[2], 100, out double l)) return false;
                double a = 1;
                if (args.Length >= 4 && !Alpha(args[3], out a)) return false;
                var (rr, gg, bb) = Hsl(h, Math.Clamp(sat / 100, 0, 1), Math.Clamp(l / 100, 0, 1));
                color = new DocColor(rr, gg, bb, Clamp(a * 255));
                return true;
            }
            return false;
        }
        if (s.Equals("transparent", StringComparison.OrdinalIgnoreCase)) { color = DocColor.Transparent; return true; }
        if (Named.TryGetValue(s, out uint v)) { color = new DocColor((byte)(v >> 16), (byte)(v >> 8), (byte)v); return true; }
        return false;
    }

    private static bool Component(string s, double full, out double value)
    {
        bool pct = s.EndsWith('%');
        if (!double.TryParse(pct ? s[..^1] : s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        if (pct) value = value / 100 * (full == 100 ? 100 : full);
        return true;
    }

    private static bool Alpha(string s, out double a)
    {
        bool pct = s.EndsWith('%');
        bool ok = double.TryParse(pct ? s[..^1] : s, NumberStyles.Float, CultureInfo.InvariantCulture, out a);
        if (pct) a /= 100;
        a = Math.Clamp(a, 0, 1);
        return ok;
    }

    private static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    private static (byte, byte, byte) Hsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = (int)(h / 60) switch { 0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x) };
        return (Clamp((r + m) * 255), Clamp((g + m) * 255), Clamp((b + m) * 255));
    }
}
