using System.IO.Compression;
using System.Text.Json;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Native;

/// <summary>
/// Reads a <c>.dsm</c> file (or a document from earlier builds: same container). Never crashes on bad input: a file that is not a usable document throws
/// <see cref="DsmFormatException"/> (with a message for the user); anything smaller than that (an unknown object kind from a
/// newer version, a damaged object) is skipped and reported in <see cref="DsmLoadResult.Warnings"/>.
/// <para>
/// Versions: <c>formatVersion</c> is what the writer used; <c>minReaderVersion</c> is the oldest reader that can still
/// make sense of the file. A file with a higher formatVersion but an acceptable minReaderVersion opens with a warning. Older
/// files are upgraded here: when version 2 exists, add a step that rewrites the version-1 JSON (see <see cref="Migrate"/>).
/// </para>
/// </summary>
public static class DsmReader
{
    public static DsmLoadResult Load(string path, DsmCodecRegistry? registry = null)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Load(stream, registry);
        }
        catch (DsmFormatException) { throw; }
        catch (InvalidDataException ex) { throw NotADocument(ex); }
    }

    public static DsmLoadResult Load(Stream stream, DsmCodecRegistry? registry = null)
    {
        registry ??= DsmCodecRegistry.Default;
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            return Load(zip, registry);
        }
        catch (InvalidDataException ex) { throw NotADocument(ex); }
        catch (JsonException ex) { throw new DsmFormatException($"The document data is damaged ({ex.Message}).", ex); }
    }

    private static DsmFormatException NotADocument(Exception inner) =>
        new("This is not a DeSign Mage document, or the file is damaged.", inner);

    private static DsmLoadResult Load(ZipArchive zip, DsmCodecRegistry registry)
    {
        var entry = zip.GetEntry(DsmFormat.DocumentEntry)
            ?? throw new DsmFormatException("This is not a DeSign Mage document: it has no document.json inside.");

        JsonDocument parsed;
        using (var es = entry.Open())
            parsed = JsonDocument.Parse(es, new JsonDocumentOptions { MaxDepth = 1000, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new DsmFormatException("The document data is damaged (it is not an object).");

            var warnings = new List<string>();
            if (!DsmFormat.IsFormatMarker(root.Str("format")))
                throw new DsmFormatException("This is not a DeSign Mage document (wrong format marker).");

            int version = root.Int("formatVersion", 0);
            int minReader = root.Int("minReaderVersion", version);
            string appVersion = root.TryGet("app", out var app) ? app.Str("version") ?? "" : "";
            if (version < 1) throw new DsmFormatException("The document has no valid format version.");
            if (minReader > DsmFormat.CurrentVersion)
                throw new DsmFormatException($"This document was saved by a newer version of DeSign Mage{(appVersion.Length > 0 ? $" ({appVersion})" : "")} and cannot be opened by this one. Please update the program.");
            if (version > DsmFormat.CurrentVersion)
                warnings.Add($"This document was saved by a newer version of DeSign Mage{(appVersion.Length > 0 ? $" ({appVersion})" : "")} (file format {version}; this version reads {DsmFormat.CurrentVersion}). Anything this version does not know about was left out; saving over the original would lose it.");

            var migrated = Migrate(root, version, out var migratedDoc);
            using var _ = migratedDoc;
            root = migrated;

            if (!root.TryGet("page", out var page)) throw new DsmFormatException("The document has no page size.");
            double width = page.Num("width"), height = page.Num("height");
            if (!(width > 0 && height > 0 && double.IsFinite(width) && double.IsFinite(height)))
                throw new DsmFormatException("The document's page size is not valid.");

            double pageX = page.Num("x"), pageY = page.Num("y");   // absent = 0: a page that was never resized from its left or top edge
            if (!double.IsFinite(pageX) || !double.IsFinite(pageY)) pageX = pageY = 0;
            var doc = new Document(new PageSetup(width, height, pageX, pageY));

            // Assets first, so image objects can find their pictures.
            var assetIds = new Dictionary<string, string>();
            foreach (var a in root.Items("assets"))
            {
                try
                {
                    var id = a.Str("id") ?? throw new DsmFormatException("An asset has no id.");
                    var data = zip.GetEntry(DsmFormat.AssetFolder + id) is { } ae ? ReadAll(ae) : null;
                    if (data is null)
                    {
                        warnings.Add($"A picture's data is missing from the file (asset {id[..Math.Min(8, id.Length)]}…).");
                        continue;
                    }
                    var actual = doc.Assets.Add(data, a.Str("mediaType") ?? "application/octet-stream");
                    if (actual != id)
                    {
                        warnings.Add($"Asset {id[..Math.Min(8, id.Length)]}… did not match its checksum (the file may be damaged); it is used as is.");
                        assetIds[id] = actual;
                    }
                }
                catch (Exception ex) when (ex is DsmFormatException or InvalidDataException)
                {
                    warnings.Add($"An embedded picture could not be read: {ex.Message}");
                }
            }

            var ctx = new DsmReadContext(doc, registry, assetIds);

            // Format 2: the groups (a group enters the document with its first member). Format 1 nests the members inside group objects.
            if (root.TryGet("groups", out var groupList))
            {
                try { ctx.ReadGroups(groupList); }
                catch (DsmFormatException ex) { ctx.Warn($"The groups could not be read: {ex.Message}"); }
            }

            // Layers. The new document starts with one empty layer; replace it with the file's layers.
            var defaultLayer = doc.Layers[0];
            var layers = new List<Layer>();
            foreach (var l in root.Items("layers"))
            {
                try
                {
                    var layer = new Layer(l.Str("name") ?? $"Layer {layers.Count + 1}")
                    {
                        Visible = l.Bool("visible", true),
                        Locked = l.Bool("locked"),
                        Printable = l.Bool("printable", true),
                        Color = l.TryGet("color", out var c) ? DsmJson.ReadColor(c) : new DocColor(0, 0, 0),
                    };
                    doc.InsertLayer(doc.Layers.Count, layer);
                    layers.Add(layer);
                    if (l.TryGet("objects", out var objects)) ctx.ReadObjectsInto(layer, objects);
                }
                catch (Exception ex) when (ex is DsmFormatException or InvalidOperationException or ArgumentException)
                {
                    ctx.Warn($"A layer could not be read completely: {ex.Message}");
                }
            }
            if (layers.Count > 0)
            {
                doc.ActiveLayer = layers[0];   // so the default layer can go
                doc.RemoveLayer(defaultLayer);
                int active = root.Int("activeLayer", 0);
                doc.ActiveLayer = layers[Math.Clamp(active, 0, layers.Count - 1)];
            }

            foreach (var g in root.Items("guides"))
            {
                try
                {
                    var o = g.Doubles("origin");
                    if (o.Length != 2) throw new DsmFormatException("A guide needs 'origin' with two numbers.");
                    new AddGuideCommand(new Guide(new Vec2(o[0], o[1]), g.Num("angle"), g.Bool("locked"))).Do(doc);
                }
                catch (Exception ex) when (ex is DsmFormatException or InvalidOperationException)
                {
                    ctx.Warn($"A guide was skipped: {ex.Message}");
                }
            }

            // Live text whose font is not installed here: tell the user once per font instead of per object.
            var missing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in doc.AllObjects().OfType<TextObject>().Concat(doc.AllObjects().OfType<CombinedObject>().SelectMany(c => c.Parts.OfType<TextObject>())))
                if (t.IsFontMissing) missing[t.FontFamily] = missing.GetValueOrDefault(t.FontFamily) + 1;
            foreach (var (font, count) in missing.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                ctx.Warn($"The font '{font}' is not installed, so {count} text{(count == 1 ? "" : "s")} will be shown in a substitute font. The text and its font name are kept.");

            warnings.AddRange(ctx.Warnings);
            byte[]? thumbnail = zip.GetEntry(DsmFormat.ThumbnailEntry) is { } te ? ReadAll(te) : null;
            return new DsmLoadResult(doc, version, appVersion, warnings)
            {
                MissingFonts = missing,
                SkippedObjects = ctx.SkippedObjects,
                Thumbnail = thumbnail,
            };
        }
    }

    /// <summary>
    /// Upgrades the JSON of an older format version to the current layout. Version 2 only added things (the <c>groups</c> list and the
    /// <c>group</c> number of objects); version 1's nested groups are read as they are, in the same drawing order, so nothing is rewritten.
    /// The (possibly rewritten) root is returned; <paramref name="owner"/> is the document to dispose, if one was created.
    /// </summary>
    private static JsonElement Migrate(JsonElement root, int fromVersion, out JsonDocument? owner)
    {
        owner = null;
        return root;
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
