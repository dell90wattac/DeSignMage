using System.Text.Json;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;

namespace DeSignMage.App.Shell;

/// <summary>
/// The saved gradients of the Gradient Blend bar's drop-down. Shapes are relative to the object, so a preset fits any object. Kept in one JSON
/// file (<c>presets/gradients.json</c> under the program's data folder) that is rewritten whole after every change, like the other settings files;
/// a missing or damaged file is simply an empty list (a damaged one is left as it is until the next change).
/// </summary>
public sealed class GradientPresets
{
    private readonly string _path;
    private readonly List<GradientPaint> _items = [];

    public GradientPresets(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>The program's own list.</summary>
    public static GradientPresets Default
    {
        get => _default ??= new(Path.Combine(DeSignMage.Feedback.AppPaths.Default.Root, "presets", "gradients.json"));
        internal set => _default = value;   // tests point it at a temporary file
    }

    private static GradientPresets? _default;

    public IReadOnlyList<GradientPaint> Items => _items;

    public event EventHandler? Changed;

    public void Add(GradientPaint g)
    {
        _items.Add(g);
        Save();
    }

    public void Update(int index, GradientPaint g)
    {
        if (index < 0 || index >= _items.Count) { Add(g); return; }
        _items[index] = g;
        Save();
    }

    public void Delete(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        _items.RemoveAt(index);
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
            foreach (var e in doc.RootElement.Items("gradients"))
            {
                try { _items.Add(GradientJson.Read(e)); }
                catch (Exception ex) when (ex is DsmFormatException or InvalidOperationException or ArgumentException) { /* a preset that cannot be read is dropped */ }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or DsmFormatException)
        {
            _items.Clear();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var stream = File.Create(_path))
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("version", 1);
                w.WriteStartArray("gradients");
                foreach (var g in _items)
                {
                    w.WriteStartObject();
                    GradientJson.WriteFields(w, g);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeSignMage.Feedback.AppLog.Warn("gradients", $"could not save the gradient presets: {ex.Message}");
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
