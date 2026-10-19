using System.IO.Compression;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Native;

/// <summary>
/// A design read into memory by <see cref="DsmWriter.Snapshot"/>: the JSON text and the pictures it shows (immutable). It does not look at the
/// document again, so <see cref="WriteTo"/> and <see cref="SaveTo"/> may run on any thread while the design keeps changing.
/// </summary>
public sealed class DsmSnapshot
{
    private readonly byte[] _json;
    private readonly IReadOnlyList<Asset> _assets;
    private readonly byte[]? _thumbnail;

    internal DsmSnapshot(byte[] json, IReadOnlyList<Asset> assets, byte[]? thumbnail)
    {
        _json = json;
        _assets = assets;
        _thumbnail = thumbnail;
    }

    /// <summary>About how many bytes the file will hold before packing: the JSON text plus every picture (what a size limit compares with).</summary>
    public long UncompressedSize => _json.LongLength + _assets.Sum(a => (long)a.Data.Length) + (_thumbnail?.LongLength ?? 0);

    /// <summary>Packs the design as a <c>.dsm</c> zip into <paramref name="stream"/> (left open).</summary>
    public void WriteTo(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var entry = zip.CreateEntry(DsmFormat.DocumentEntry, CompressionLevel.Optimal);
        using (var es = entry.Open())
            es.Write(_json);

        foreach (var asset in _assets)
        {
            // Pictures are already compressed; storing them is faster and not bigger.
            var level = asset.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && asset.MediaType != "image/bmp"
                ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
            using var s = zip.CreateEntry(DsmFormat.AssetFolder + asset.Id, level).Open();
            s.Write(asset.Data);
        }

        if (_thumbnail is { Length: > 0 } thumb)
        {
            using var s = zip.CreateEntry(DsmFormat.ThumbnailEntry, CompressionLevel.NoCompression).Open();
            s.Write(thumb);
        }
    }

    /// <summary>Writes the file next to <paramref name="path"/> first and moves it over, like <see cref="DsmWriter.Save(Document, string, DsmSaveOptions?, DsmCodecRegistry?)"/>.</summary>
    public void SaveTo(string path)
    {
        var full = Path.GetFullPath(path);
        var temp = full + ".saving";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                WriteTo(stream);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
