using System.Text.RegularExpressions;
using DeSignMage.Core.Text;

namespace DeSignMage.Formats.Vector;

/// <summary>Maps the font names of other programs' files (CSS font lists, PDF base font names) to installed font families.</summary>
internal static partial class FontNames
{
    private static readonly string[] Serif = ["Times New Roman", "Georgia", "Cambria", "Palatino Linotype"];
    private static readonly string[] Sans = ["Arial", "Segoe UI", "Calibri", "Verdana", "Tahoma"];
    private static readonly string[] Mono = ["Courier New", "Consolas", "Lucida Console"];

    private static bool Installed(string family) => TextEngine.Fonts.IsInstalled(family);

    private static string FirstInstalled(string[] candidates) => candidates.FirstOrDefault(Installed) ?? candidates[0];

    /// <summary>The family of a CSS <c>font-family</c> list: the first installed one, else the first named (generic names mapped to a common font).</summary>
    public static (string Family, bool Installed) FromCss(string list)
    {
        string? firstNamed = null;
        foreach (var raw in list.Split(','))
        {
            string name = raw.Trim().Trim('\'', '"').Trim();
            if (name.Length == 0) continue;
            string mapped = name.ToLowerInvariant() switch
            {
                "serif" => FirstInstalled(Serif),
                "sans-serif" or "system-ui" or "ui-sans-serif" => FirstInstalled(Sans),
                "monospace" or "ui-monospace" => FirstInstalled(Mono),
                "cursive" => "Comic Sans MS",
                "fantasy" => "Impact",
                "helvetica" or "helvetica neue" => Installed("Helvetica") ? "Helvetica" : FirstInstalled(Sans),
                "times" => FirstInstalled(Serif),
                "courier" => FirstInstalled(Mono),
                _ => name,
            };
            firstNamed ??= mapped;
            if (Installed(mapped)) return (mapped, true);
        }
        return (firstNamed ?? FirstInstalled(Sans), firstNamed is not null && Installed(firstNamed));
    }

    /// <summary>A PDF base font name ("EAAAAA+Arial-BoldMT", "TimesNewRomanPS-ItalicMT", "Helvetica-Bold") as a family, weight and slant.</summary>
    public static (string Family, int Weight, bool Italic) FromPdf(string baseFont, bool bold, bool italic)
    {
        string name = baseFont.Trim().TrimStart('/');
        int plus = name.IndexOf('+');
        if (plus == 6 && name[..6].All(char.IsAsciiLetterUpper)) name = name[7..];
        name = name.Replace(',', '-').Replace(' ', '-');
        string familyPart = name, style = "";
        int dash = name.IndexOf('-');
        if (dash > 0) { familyPart = name[..dash]; style = name[(dash + 1)..]; }

        // a style glued to the family ("ArialBold", "TimesNewRomanBoldItalic")
        foreach (var suffix in new[] { "BoldItalic", "BoldOblique", "Bold", "Italic", "Oblique" })
            if (familyPart.Length > suffix.Length + 2 && familyPart.EndsWith(suffix, StringComparison.Ordinal)) { style = suffix + style; familyPart = familyPart[..^suffix.Length]; break; }
        foreach (var suffix in new[] { "PSMT", "MT", "PS" })
            if (familyPart.Length > suffix.Length + 2 && familyPart.EndsWith(suffix, StringComparison.Ordinal)) { familyPart = familyPart[..^suffix.Length]; break; }
        style = Regex.Replace(style, @"(PS)?MT$", "");

        string family = CamelSpaces().Replace(familyPart, " ").Replace("  ", " ").Trim();
        family = family.ToLowerInvariant() switch
        {
            "helvetica" => Installed("Helvetica") ? "Helvetica" : FirstInstalled(Sans),
            "times" or "times roman" or "timesroman" => FirstInstalled(Serif),
            "courier" => FirstInstalled(Mono),
            _ => family,
        };

        string st = style.ToLowerInvariant();
        bool isItalic = italic || st.Contains("italic") || st.Contains("oblique") || st.EndsWith("it", StringComparison.Ordinal) && st.Length > 2 && !st.Contains("semibold");
        int weight = 400;
        if (st.Contains("thin") || st.Contains("hairline")) weight = 100;
        else if (st.Contains("extralight") || st.Contains("ultralight")) weight = 200;
        else if (st.Contains("light")) weight = 300;
        else if (st.Contains("medium")) weight = 500;
        else if (st.Contains("semibold") || st.Contains("demi")) weight = 600;
        else if (st.Contains("extrabold") || st.Contains("ultrabold")) weight = 800;
        else if (st.Contains("black") || st.Contains("heavy")) weight = 900;
        else if (st.Contains("bold")) weight = 700;
        if (bold && weight < 700) weight = 700;
        return (family, weight, isItalic);
    }

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex CamelSpaces();
}
