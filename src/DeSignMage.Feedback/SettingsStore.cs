using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeSignMage.Feedback;

/// <summary>
/// The program's small remembered values (the feedback name, interface text size, font-list size, ...), kept as one JSON object in
/// <see cref="AppPaths.SettingsFile"/>. Any part of the program can use it: <c>Get</c> with a fallback, <c>Set</c> to remember.
/// Keys are free-form text; use a dotted prefix per feature ("ui.textScale", "perf.xyz"). Unknown keys are kept as they are, and a
/// file that cannot be read or written is never an error (values then just last until the program closes).
/// </summary>
public sealed class SettingsStore
{
    private readonly object _gate = new();
    private readonly string? _path;
    private JsonObject? _values;

    /// <param name="path">The file; null keeps everything in memory only.</param>
    public SettingsStore(string? path) => _path = path;

    /// <summary>Raised (on the thread that called <see cref="Set"/>) with the key that changed.</summary>
    public event Action<string>? Changed;

    public T Get<T>(string key, T fallback)
    {
        lock (_gate)
        {
            try
            {
                if (Load().TryGetPropertyValue(key, out var node) && node is not null)
                    return node.Deserialize<T>() ?? fallback;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
            return fallback;
        }
    }

    public bool Has(string key)
    {
        lock (_gate) return Load().ContainsKey(key);
    }

    public void Set<T>(string key, T value)
    {
        lock (_gate)
        {
            var values = Load();
            var node = JsonSerializer.SerializeToNode(value);
            if (values.TryGetPropertyValue(key, out var old) && old?.ToJsonString() == node?.ToJsonString()) return;
            values[key] = node;
            Save(values);
        }
        Changed?.Invoke(key);
    }

    private JsonObject Load()
    {
        if (_values is not null) return _values;
        try
        {
            if (_path is not null && File.Exists(_path) && JsonNode.Parse(File.ReadAllText(_path)) is JsonObject obj)
                return _values = obj;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return _values = [];
    }

    private void Save(JsonObject values)
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            // Write beside the file and swap, so a crash in the middle never leaves half a file.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
