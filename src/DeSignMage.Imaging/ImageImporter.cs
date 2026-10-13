using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;

namespace DeSignMage.Imaging;

/// <summary>A picture added to a document's asset store, ready to be placed.</summary>
public sealed record ImportedImage(ImagePixels Pixels, double? Dpi, string FileName);

/// <summary>Reads a picture file into a document's asset store (File ▸ Import, drag and drop).</summary>
public static class ImageImporter
{
    /// <summary>
    /// Stores the file's bytes as an asset. PNG, JPEG, WebP, GIF and BMP files are kept as they are (no recompression of
    /// photos); a file with an EXIF rotation, or in a format only Skia's decoder understands (ICO), is turned upright
    /// and stored as a lossless PNG instead, so the stored picture never needs an orientation tag.
    /// Throws <see cref="InvalidDataException"/> if the file is not a readable picture.
    /// </summary>
    public static ImportedImage Import(Document doc, byte[] data, string fileName)
    {
        var probe = ImageCodec.Probe(data);
        bool keep = !probe.NeedsReorient && probe.MediaType is "image/png" or "image/jpeg" or "image/webp" or "image/gif" or "image/bmp";
        if (keep)
        {
            return new ImportedImage(new ImagePixels(doc.Assets.Add(data, probe.MediaType), probe.Width, probe.Height), probe.Dpi, fileName);
        }
        var upright = ImageCodec.Decode(data, applyOrigin: true);
        var png = ImageCodec.EncodePng(upright);
        return new ImportedImage(new ImagePixels(doc.Assets.Add(png, "image/png"), upright.Width, upright.Height), probe.Dpi, fileName);
    }

    public static ImportedImage Import(Document doc, string path) => Import(doc, File.ReadAllBytes(path), Path.GetFileName(path));
}
