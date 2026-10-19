using System.IO.Compression;
using System.Text.Json;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Native;

/// <summary>Writes a <see cref="Document"/> as a <c>.dsm</c> file (see <see cref="DsmFormat"/>).</summary>
public static class DsmWriter
{
    /// <summary>
    /// Saves to <paramref name="path"/>. The file is written next to the target first and then moved over it, so a
    /// failed save (disk full, crash) never destroys the previous version.
    /// </summary>
    public static void Save(Document document, string path, DsmSaveOptions? options = null, DsmCodecRegistry? registry = null)
    {
        var full = Path.GetFullPath(path);
        var temp = full + ".saving";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                Save(document, stream, options, registry);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static byte[] SaveToBytes(Document document, DsmSaveOptions? options = null, DsmCodecRegistry? registry = null)
    {
        using var ms = new MemoryStream();
        Save(document, ms, options, registry);
        return ms.ToArray();
    }

    public static void Save(Document document, Stream stream, DsmSaveOptions? options = null, DsmCodecRegistry? registry = null) =>
        Snapshot(document, options, registry).WriteTo(stream);

    /// <summary>
    /// The quick half of a save, for a document that is still being edited: reads the whole design into memory (its JSON text, plus
    /// references to the pictures it shows, which never change) so the slow half, packing the zip, can run on another thread while the
    /// design goes on changing. Call it on the thread that owns the document.
    /// </summary>
    public static DsmSnapshot Snapshot(Document document, DsmSaveOptions? options = null, DsmCodecRegistry? registry = null)
    {
        options ??= new DsmSaveOptions();
        registry ??= DsmCodecRegistry.Default;

        // The asset store also keeps pictures that an edit (rotate, crop, ...) replaced, because undo needs them. Only pictures some object
        // still shows go into the file, otherwise every edit would bloat it; undo history does not survive a save anyway.
        var referenced = BitmapStats.ReferencedAssetIds(document);
        var assets = document.Assets.All.Where(a => referenced.Contains(a.Id)).OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
        using var json = new MemoryStream();
        WriteDocumentJson(document, json, options, registry, assets);
        return new DsmSnapshot(json.ToArray(), assets, options.Thumbnail);
    }

    private static void WriteDocumentJson(Document doc, Stream output, DsmSaveOptions options, DsmCodecRegistry registry, List<Asset> assets)
    {
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = !options.Compact, MaxDepth = 1000 });
        var ctx = new DsmWriteContext(json, registry);

        json.WriteStartObject();
        json.WriteString("format", DsmFormat.FormatName);
        json.WriteNumber("formatVersion", DsmFormat.CurrentVersion);
        json.WriteNumber("minReaderVersion", DsmFormat.MinReaderVersion);
        json.WriteStartObject("app");
        json.WriteString("name", "DeSign Mage");
        json.WriteString("version", options.AppVersion);
        json.WriteEndObject();
        json.WriteString("savedUtc", (options.SavedUtc ?? DateTime.UtcNow).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));

        json.WriteString("units", "points (1/72 inch), y down, origin at the page's top-left");
        json.WriteStartObject("page");
        json.WriteNum("width", doc.Page.Width);
        json.WriteNum("height", doc.Page.Height);
        if (doc.Page.X != 0) json.WriteNum("x", doc.Page.X);   // where the page's top-left is, once the page was resized from its left or top edge
        if (doc.Page.Y != 0) json.WriteNum("y", doc.Page.Y);
        json.WriteEndObject();

        json.WriteNumber("activeLayer", Math.Max(0, doc.Layers.ToList().IndexOf(doc.ActiveLayer)));

        // Groups are membership only (R22): they are listed here, the group around a group first, and every object (and group) names the group
        // it belongs to. The layers list the drawn objects in drawing order, members of groups in their place among the rest.
        var groups = new List<GroupObject>();
        void AddGroup(GroupObject g)
        {
            if (ctx.GroupIndexes.ContainsKey(g)) return;
            if (g.Parent is GroupObject outer) AddGroup(outer);
            ctx.GroupIndexes[g] = groups.Count;
            groups.Add(g);
        }
        foreach (var g in doc.Groups) AddGroup(g);

        // Live effects (format 4): every source gets a number its effects refer to.
        foreach (var effect in DeSignMage.Core.Effects.LiveEffects.All(doc))
            if (doc.Contains(effect.Live!.Source) && !ctx.LinkIds.ContainsKey(effect.Live.Source)) ctx.LinkIds[effect.Live.Source] = ctx.LinkIds.Count;
        if (groups.Count > 0) ctx.WriteObjectList("groups", groups);

        json.WriteStartArray("layers");
        foreach (var layer in doc.Layers)
        {
            json.WriteStartObject();
            json.WriteString("name", layer.Name);
            if (!layer.Visible) json.WriteBoolean("visible", false);
            if (layer.Locked) json.WriteBoolean("locked", true);
            if (!layer.Printable) json.WriteBoolean("printable", false);
            json.WriteColor("color", layer.Color);
            ctx.WriteObjectList("objects", layer.Children);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartArray("guides");
        foreach (var g in doc.Guides)
        {
            json.WriteStartObject();
            json.WriteNumbers("origin", [g.Origin.X, g.Origin.Y]);
            json.WriteNum("angle", g.AngleDegrees);
            if (g.Locked) json.WriteBoolean("locked", true);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartArray("assets");
        foreach (var a in assets)
        {
            json.WriteStartObject();
            json.WriteString("id", a.Id);
            json.WriteString("mediaType", a.MediaType);
            json.WriteNumber("size", a.Data.Length);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteEndObject();
    }
}



