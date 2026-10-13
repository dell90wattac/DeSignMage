using System.Text.Json;

namespace DeSignMage.App.Shell;

/// <summary>
/// The most recently opened or saved files (File ▸ Open Recent), kept in <c>%APPDATA%\DeSignMage\recent.json</c>.
/// A failure to read or write the list is never an error: it just starts empty.
/// </summary>
internal sealed class RecentFiles
{
    public const int MaxItems = 10;

    /// <summary>Where the list is stored; null keeps it in memory only. Tests point this at a temp file.</summary>
    public static string? StoragePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeSignMage", "recent.json");

    private List<string>? _items;

    public IReadOnlyList<string> Items => _items ??= Load();

    public void Add(string path)
    {
        var full = Path.GetFullPath(path);
        var list = Load();
        list.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, full);
        if (list.Count > MaxItems) list.RemoveRange(MaxItems, list.Count - MaxItems);
        _items = list;
        Save(list);
    }

    public void Remove(string path)
    {
        var list = Load();
        if (list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0) Save(list);
        _items = list;
    }

    private static List<string> Load()
    {
        try
        {
            if (StoragePath is { } p && File.Exists(p))
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(p))?.Where(s => !string.IsNullOrWhiteSpace(s)).Take(MaxItems).ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return [];
    }

    private static void Save(List<string> list)
    {
        try
        {
            if (StoragePath is not { } p) return;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(list));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
