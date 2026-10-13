using System.Collections.Concurrent;
using Avalonia.Media;
using DeSignMage.Core.Text;
using SkiaSharp;

namespace DeSignMage.App.Shell;

/// <summary>
/// The installed fonts as the font list needs them (R12). Built once, on a background thread shortly after start-up, so that opening
/// the drop-down never waits for the system's font enumeration; per-font facts (is it a symbol font) are worked out only for
/// the rows that are actually shown, and remembered.
/// </summary>
internal static class FontCatalog
{
    private static readonly object Gate = new();
    private static IReadOnlyList<FontItem>? _installed;
    private static readonly ConcurrentDictionary<string, FontFamily> Families = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> Symbols = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The installed families, sorted (built on first use; <see cref="Prewarm"/> does that off the UI thread).</summary>
    public static IReadOnlyList<FontItem> Installed
    {
        get
        {
            lock (Gate)
            {
                return _installed ??= TextEngine.Fonts.Families.Select(f => new FontItem(f, false)).ToArray();
            }
        }
    }

    private static HashSet<string>? _names;

    /// <summary>True when the name is one of the installed family names (not a legacy or missing name).</summary>
    public static bool IsListed(string name)
    {
        lock (Gate)
        {
            _names ??= new HashSet<string>(Installed.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            return _names.Contains(name);
        }
    }

    /// <summary>Starts building the list on a background thread.</summary>
    public static void Prewarm() => Task.Run(() =>
    {
        try { _ = Installed; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
    });

    /// <summary>Test hook: use this list instead of the system's fonts (null goes back to the real ones).</summary>
    internal static void SetInstalledForTests(IReadOnlyList<FontItem>? items)
    {
        lock (Gate) { _installed = items; _names = null; }
    }

    /// <summary>The Avalonia family for a name, cached (the same object for every row that shows it).</summary>
    public static FontFamily FamilyFor(string name) => Families.GetOrAdd(name, n => new FontFamily(n));

    // The OS/2 table's ulCodePageRange1 bit 31 marks a symbol font (Wingdings, Webdings, ...); a few icon fonts don't set it.
    private const uint Os2Tag = 0x4F532F32;   // 'OS/2'
    private static readonly string[] SymbolNameParts = ["Wingdings", "Webdings", "Dingbats", "Symbol", "Marlett", "MDL2", "Fluent Icons", "Emoji"];

    /// <summary>True for fonts whose letters are pictures or symbols, so a plain-font label is shown beside the name. Cached.</summary>
    public static bool IsSymbol(string name) => Symbols.GetOrAdd(name, Detect);

    private static bool Detect(string name)
    {
        foreach (var part in SymbolNameParts)
            if (name.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var typeface = SKTypeface.FromFamilyName(name);
            if (typeface is not null && typeface.TryGetTableData(Os2Tag, out var os2) && os2.Length >= 86 && (os2[0] << 8 | os2[1]) >= 1)
            {
                uint codePages = (uint)(os2[78] << 24 | os2[79] << 16 | os2[80] << 8 | os2[81]);
                return (codePages & 0x80000000) != 0;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        return false;
    }
}
