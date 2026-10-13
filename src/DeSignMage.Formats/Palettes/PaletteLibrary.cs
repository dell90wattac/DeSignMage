using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Palettes;

/// <summary>Finds and loads palettes: those next to the exe, in the per-user folder, and any folder or file the user picks.</summary>
public static class PaletteLibrary
{
    /// <summary>The palette selected first when it exists (the Color Mixer and the Palette Strip).</summary>
    public const string DefaultPaletteName = "Designer";

    /// <summary>Settings key under which the app remembers the palette folders the user added (joined with '|').</summary>
    public const string FoldersSetting = "palettes.folders";

    public static string BundledFolder => Path.Combine(AppContext.BaseDirectory, "palettes");

    /// <summary>The per-user folder: palettes put here are found at every start (<c>%LOCALAPPDATA%\DeSignMage\palettes</c>).</summary>
    public static string UserFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeSignMage", "palettes");

    /// <summary>
    /// Loads several folders in order (see <see cref="LoadFolder"/>); a palette with the same name as an earlier one replaces it.
    /// Folders that do not exist are skipped.
    /// </summary>
    public static IReadOnlyList<Palette> LoadFolders(IEnumerable<string> folders, ICollection<string>? problems = null) =>
        Merge([], folders.Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(f => LoadFolder(f, problems)));

    /// <summary>
    /// <paramref name="existing"/> plus <paramref name="added"/>: a palette whose name (ignoring case) is already there is replaced by the
    /// added one. Sorted by name.
    /// </summary>
    public static IReadOnlyList<Palette> Merge(IEnumerable<Palette> existing, IEnumerable<Palette> added)
    {
        var byName = new Dictionary<string, Palette>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in existing) byName[p.Name] = p;
        foreach (var p in added) byName[p.Name] = p;
        var list = byName.Values.ToList();
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>Loads one palette file (<c>.PAL</c> or <c>.CLR</c>); the palette is named after the file.</summary>
    public static Palette LoadFile(string path) => PaletteFile.Load(path).ToPalette(Path.GetFileNameWithoutExtension(path));

    /// <summary>
    /// Loads every `.PAL` in <paramref name="folder"/>, plus any `.CLR` that has no `.PAL` of the same name; sorted by name.
    /// A file that can't be read is skipped and described in <paramref name="problems"/>.
    /// </summary>
    public static IReadOnlyList<Palette> LoadFolder(string folder, ICollection<string>? problems = null)
    {
        var result = new List<Palette>();
        if (!Directory.Exists(folder)) return result;
        var files = Directory.EnumerateFiles(folder)
            .Where(f => Path.GetExtension(f).Equals(".pal", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetExtension(f).Equals(".clr", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetExtension(f).Equals(".pal", StringComparison.OrdinalIgnoreCase) ? 0 : 1)   // .PAL wins over a legacy .CLR twin
            .ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (!seen.Add(name)) continue;
            try
            {
                result.Add(PaletteFile.Load(file).ToPalette(name));
            }
            catch (Exception ex) when (ex is PaletteFormatException or IOException or UnauthorizedAccessException)
            {
                problems?.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>A small built-in palette, used when no palette files are found.</summary>
    public static Palette Starter() => new("Starter",
        new[]
        {
            ("Black", "#000000"), ("Dark Grey", "#404040"), ("Grey", "#808080"), ("Light Grey", "#C0C0C0"), ("White", "#FFFFFF"),
            ("Burgundy", "#7B1F1F"), ("Red", "#E53935"), ("Pink", "#F06292"), ("Magenta", "#D81B60"), ("Purple", "#8E24AA"),
            ("Navy", "#1A237E"), ("Blue", "#1E88E5"), ("Sky Blue", "#039BE5"), ("Teal", "#00ACC1"), ("Green Teal", "#00897B"),
            ("Green", "#2E7D32"), ("Light Green", "#7CB342"), ("Yellow", "#FDD835"), ("Gold", "#F2B705"), ("Orange", "#FB8C00"),
            ("Brown", "#6D4C41"), ("Tan", "#D7B98E"),
        }.Select(c => DocColor.FromHex(c.Item2, c.Item1)).ToArray());
}
