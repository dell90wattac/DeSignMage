using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DeSignMage.Formats.Vector.Svg;

/// <summary>The properties of one element, from its attributes, the style sheet and its <c>style</c> attribute (in that order of strength).</summary>
internal sealed class SvgProps : Dictionary<string, string> { }

/// <summary>A small CSS reader: rules with simple selectors (<c>tag</c>, <c>.class</c>, <c>#id</c>, <c>tag.class</c>, <c>*</c>, lists with commas).</summary>
internal sealed partial class SvgStyleSheet
{
    private sealed record Rule(string Tag, string? Id, string[] Classes, int Specificity, int Order, Dictionary<string, string> Props);

    private readonly List<Rule> _rules = [];

    /// <summary>Selectors (descendant combinators, pseudo classes, attributes) the reader skipped.</summary>
    public int Skipped { get; private set; }

    public void Add(string css)
    {
        css = CommentRegex().Replace(css, "");
        int i = 0;
        while (i < css.Length)
        {
            int open = css.IndexOf('{', i);
            if (open < 0) break;
            int close = css.IndexOf('}', open);
            if (close < 0) break;
            string selectors = css[i..open].Trim();
            var props = ParseDeclarations(css[(open + 1)..close]);
            i = close + 1;
            if (selectors.StartsWith('@')) continue;
            foreach (var raw in selectors.Split(','))
            {
                var sel = raw.Trim();
                if (sel.Length == 0) continue;
                var m = SimpleSelector().Match(sel);
                if (!m.Success || m.Length != sel.Length) { Skipped++; continue; }
                string tag = m.Groups["tag"].Value;
                string? id = m.Groups["id"].Success ? m.Groups["id"].Value : null;
                var classes = m.Groups["cls"].Captures.Select(c => c.Value).ToArray();
                int spec = (id is null ? 0 : 100) + classes.Length * 10 + (tag.Length > 0 && tag != "*" ? 1 : 0);
                _rules.Add(new Rule(tag, id, classes, spec, _rules.Count, props));
            }
        }
    }

    public static Dictionary<string, string> ParseDeclarations(string text)
    {
        var d = new Dictionary<string, string>();
        foreach (var part in text.Split(';'))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0) continue;
            string name = part[..colon].Trim().ToLowerInvariant();
            string value = part[(colon + 1)..].Trim();
            int bang = value.IndexOf("!important", StringComparison.OrdinalIgnoreCase);
            if (bang >= 0) value = value[..bang].Trim();
            if (name.Length > 0) d[name] = value;
        }
        return d;
    }

    /// <summary>Applies the matching rules to <paramref name="props"/> (weakest first), over the attributes already there.</summary>
    public void Apply(XElement e, SvgProps props)
    {
        if (_rules.Count == 0) return;
        string local = e.Name.LocalName;
        string? id = (string?)e.Attribute("id");
        var classes = ((string?)e.Attribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var r in _rules.Where(r =>
                     (r.Tag.Length == 0 || r.Tag == "*" || r.Tag == local) && (r.Id is null || r.Id == id) && r.Classes.All(classes.Contains))
                     .OrderBy(r => r.Specificity).ThenBy(r => r.Order))
            foreach (var (k, v) in r.Props) props[k] = v;
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"^(?<tag>[A-Za-z][\w-]*|\*)?(?:#(?<id>[\w-]+))?(?:\.(?<cls>[\w-]+))*$")]
    private static partial Regex SimpleSelector();
}

/// <summary>Lengths and transform lists of SVG.</summary>
internal static class SvgUnits
{
    /// <summary>A length in user units (CSS pixels): "12", "12px", "9pt", "1in", "2em", "50%" (of <paramref name="percentOf"/>).</summary>
    public static bool TryLength(string? text, double fontSize, double percentOf, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!SvgPathData.TryReadNumber(text, out double n, out string unit)) return false;
        switch (unit.ToLowerInvariant())
        {
            case "" or "px": value = n; return true;
            case "pt": value = n * 4 / 3; return true;
            case "pc": value = n * 16; return true;
            case "in": value = n * 96; return true;
            case "cm": value = n * 96 / 2.54; return true;
            case "mm": value = n * 96 / 25.4; return true;
            case "q": value = n * 96 / 101.6; return true;
            case "em": value = n * fontSize; return true;
            case "ex": value = n * fontSize / 2; return true;
            case "%": value = n / 100 * percentOf; return true;
            default: return false;
        }
    }

    public static double Length(string? text, double fontSize, double percentOf, double fallback) =>
        TryLength(text, fontSize, percentOf, out double v) ? v : fallback;

    /// <summary>The number list of a <c>viewBox</c>, <c>points</c> or <c>stroke-dasharray</c> attribute.</summary>
    public static List<double> Numbers(string? text)
    {
        var list = new List<double>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var part in Regex.Split(text.Trim(), @"[\s,]+"))
            if (part.Length > 0 && double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) list.Add(v);
        return list;
    }

    /// <summary>"translate(10 20) rotate(45)": the matrix as a transform that maps child coordinates to the parent's. Null when it cannot be read.</summary>
    public static DeSignMage.Core.Geometry.Affine2? Transform(string? text)
    {
        var result = DeSignMage.Core.Geometry.Affine2.Identity;
        if (string.IsNullOrWhiteSpace(text)) return result;
        var matches = Regex.Matches(text, @"(?<fn>[a-zA-Z]+)\s*\((?<args>[^)]*)\)");
        if (matches.Count == 0) return null;
        foreach (Match m in matches)
        {
            var a = Numbers(m.Groups["args"].Value);
            DeSignMage.Core.Geometry.Affine2 t;
            switch (m.Groups["fn"].Value)
            {
                case "matrix" when a.Count == 6: t = new(a[0], a[1], a[2], a[3], a[4], a[5]); break;
                case "translate" when a.Count is 1 or 2: t = DeSignMage.Core.Geometry.Affine2.Translation(a[0], a.Count > 1 ? a[1] : 0); break;
                case "scale" when a.Count is 1 or 2: t = DeSignMage.Core.Geometry.Affine2.Scale(a[0], a.Count > 1 ? a[1] : a[0]); break;
                case "rotate" when a.Count is 1 or 3:
                {
                    double r = a[0] * Math.PI / 180;
                    // SVG rotates clockwise on screen for positive angles (y down), which is Affine2.Rotation with the same sign
                    t = a.Count == 3
                        ? DeSignMage.Core.Geometry.Affine2.Rotation(r, new DeSignMage.Core.Geometry.Vec2(a[1], a[2]))
                        : DeSignMage.Core.Geometry.Affine2.Rotation(r);
                    break;
                }
                case "skewX" when a.Count == 1: t = new(1, 0, Math.Tan(a[0] * Math.PI / 180), 1, 0, 0); break;
                case "skewY" when a.Count == 1: t = new(1, Math.Tan(a[0] * Math.PI / 180), 0, 1, 0, 0); break;
                default: return null;
            }
            result = t * result;
        }
        return result;
    }
}
