using System.Globalization;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Vector.Svg;

/// <summary>The computed (inherited) presentation properties of an SVG element.</summary>
internal sealed record SvgStyle
{
    public static readonly SvgStyle Default = new();

    public string? Fill { get; init; } = "black";
    public string? Stroke { get; init; } = "none";
    public double StrokeWidth { get; init; } = 1;
    public double FillOpacity { get; init; } = 1;
    public double StrokeOpacity { get; init; } = 1;

    /// <summary>The product of the <c>opacity</c> of the element and all its ancestors.</summary>
    public double Opacity { get; init; } = 1;

    public bool EvenOdd { get; init; }
    public LineCap Cap { get; init; } = LineCap.Butt;
    public LineJoin Join { get; init; } = LineJoin.Miter;
    public double MiterLimit { get; init; } = 4;
    public string? Dash { get; init; }
    public DocColor Color { get; init; } = DocColor.Black;
    public string FontFamily { get; init; } = "serif";
    public double FontSize { get; init; } = 16;
    public int FontWeight { get; init; } = 400;
    public bool Italic { get; init; }
    public string Anchor { get; init; } = "start";
    public double LetterSpacing { get; init; }
    public bool Visible { get; init; } = true;

    /// <summary>The vinyl / spot name and vendor an exporter of this program wrote next to a color (own to the element, not inherited).</summary>
    public string? FillName { get; init; }
    public string? FillVendor { get; init; }
    public string? StrokeName { get; init; }
    public string? StrokeVendor { get; init; }

    public SvgStyle Derive(SvgProps p, double viewWidth, double viewHeight)
    {
        var s = this with { FillName = p.GetValueOrDefault("data-fill-name"), FillVendor = p.GetValueOrDefault("data-fill-vendor"), StrokeName = p.GetValueOrDefault("data-stroke-name"), StrokeVendor = p.GetValueOrDefault("data-stroke-vendor") };
        double diagonal = Math.Sqrt((viewWidth * viewWidth + viewHeight * viewHeight) / 2);

        if (p.TryGetValue("font-size", out var fsText)) s = s with { FontSize = FontSizeOf(fsText, s.FontSize) };
        if (p.TryGetValue("color", out var color) && SvgColors.TryParse(color, out var c)) s = s with { Color = c };
        if (p.TryGetValue("fill", out var fill) && fill != "inherit") s = s with { Fill = fill.Trim() };
        if (p.TryGetValue("stroke", out var stroke) && stroke != "inherit") s = s with { Stroke = stroke.Trim() };
        if (p.TryGetValue("stroke-width", out var sw)) s = s with { StrokeWidth = SvgUnits.Length(sw, s.FontSize, diagonal, s.StrokeWidth) };
        if (p.TryGetValue("fill-opacity", out var fo)) s = s with { FillOpacity = Number(fo, s.FillOpacity) };
        if (p.TryGetValue("stroke-opacity", out var so)) s = s with { StrokeOpacity = Number(so, s.StrokeOpacity) };
        if (p.TryGetValue("opacity", out var op)) s = s with { Opacity = s.Opacity * Number(op, 1) };
        if (p.TryGetValue("fill-rule", out var fr)) s = s with { EvenOdd = fr.Trim() == "evenodd" };
        if (p.TryGetValue("clip-rule", out var cr) && !p.ContainsKey("fill-rule")) s = s with { EvenOdd = cr.Trim() == "evenodd" };
        if (p.TryGetValue("stroke-linecap", out var cap)) s = s with { Cap = cap.Trim() switch { "round" => LineCap.Round, "square" => LineCap.Square, _ => LineCap.Butt } };
        if (p.TryGetValue("stroke-linejoin", out var join)) s = s with { Join = join.Trim() switch { "round" => LineJoin.Round, "bevel" => LineJoin.Bevel, _ => LineJoin.Miter } };
        if (p.TryGetValue("stroke-miterlimit", out var ml)) s = s with { MiterLimit = double.TryParse(ml, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mlv) ? Math.Max(1, mlv) : s.MiterLimit };
        if (p.TryGetValue("stroke-dasharray", out var da))
        {
            var d = SvgUnits.Numbers(da);
            s = s with { Dash = d.Count == 0 || d.All(x => x <= 0) || da.Trim() == "none" ? null : da };
        }
        if (p.TryGetValue("font-family", out var ff)) s = s with { FontFamily = ff };
        if (p.TryGetValue("font-weight", out var fw)) s = s with { FontWeight = WeightOf(fw, s.FontWeight) };
        if (p.TryGetValue("font-style", out var fst)) s = s with { Italic = fst.Trim() is "italic" or "oblique" };
        if (p.TryGetValue("text-anchor", out var ta)) s = s with { Anchor = ta.Trim() };
        if (p.TryGetValue("letter-spacing", out var ls)) s = s with { LetterSpacing = ls.Trim() == "normal" ? 0 : SvgUnits.Length(ls, s.FontSize, 0, s.LetterSpacing) };
        if (p.TryGetValue("visibility", out var vis)) s = s with { Visible = vis.Trim() is not ("hidden" or "collapse") };
        return s;
    }

    private static double Number(string text, double fallback)
    {
        text = text.Trim();
        bool pct = text.EndsWith('%');
        if (!double.TryParse(pct ? text[..^1] : text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return fallback;
        return Math.Clamp(pct ? v / 100 : v, 0, 1);
    }

    private static double FontSizeOf(string text, double parent)
    {
        switch (text.Trim())
        {
            case "xx-small": return 9;
            case "x-small": return 10;
            case "small": return 13;
            case "medium": return 16;
            case "large": return 18;
            case "x-large": return 24;
            case "xx-large": return 32;
            case "larger": return parent * 1.2;
            case "smaller": return parent / 1.2;
        }
        double v = SvgUnits.Length(text, parent, parent, parent);
        return v > 0 ? v : parent;
    }

    private static int WeightOf(string text, int parent)
    {
        switch (text.Trim())
        {
            case "normal": return 400;
            case "bold": return 700;
            case "bolder": return Math.Min(900, parent + 300);
            case "lighter": return Math.Max(100, parent - 300);
        }
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) ? Math.Clamp(w, 100, 900) : parent;
    }
}
