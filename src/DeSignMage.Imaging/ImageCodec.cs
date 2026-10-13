using System.Runtime.InteropServices;
using SkiaSharp;

namespace DeSignMage.Imaging;

/// <summary>What a picture file says about itself without decoding the pixels.</summary>
public sealed record ImageProbe(int Width, int Height, string MediaType, SKEncodedOrigin Origin, double? Dpi)
{
    /// <summary>Size as it should appear (EXIF rotation by 90 degrees swaps width and height).</summary>
    public int DisplayWidth => Swaps ? Height : Width;
    public int DisplayHeight => Swaps ? Width : Height;
    public bool Swaps => Origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
    public bool NeedsReorient => Origin != SKEncodedOrigin.TopLeft && Origin != SKEncodedOrigin.Default;
}

/// <summary>Reading and writing pictures with SkiaSharp (PNG, JPEG, BMP, GIF, WebP and ICO; Skia has no TIFF decoder).</summary>
public static class ImageCodec
{
    public static readonly string[] FileExtensions = [".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".webp", ".ico"];

    /// <summary>File picker patterns for <see cref="FileExtensions"/>.</summary>
    public static readonly string[] FilePatterns = FileExtensions.Select(e => "*" + e).ToArray();

    public static bool IsImageFile(string path) =>
        FileExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads size, format, orientation and resolution from the header. Throws <see cref="InvalidDataException"/> if it is not a picture Skia can read.</summary>
    public static ImageProbe Probe(byte[] data)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(data)) ?? throw new InvalidDataException("This is not a picture file DeSign Mage can read.");
        var info = codec.Info;
        return new ImageProbe(info.Width, info.Height, MediaTypeOf(codec.EncodedFormat), codec.EncodedOrigin, ImageMetadata.ReadDpi(data));
    }

    /// <summary>
    /// Decodes to exact RGBA. <paramref name="applyOrigin"/> turns the picture the way its EXIF tag says (photos from cameras);
    /// stored pictures never carry a tag that still needs applying, so the editor passes false for them.
    /// </summary>
    public static PixelBuffer Decode(byte[] data, bool applyOrigin = false)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(data)) ?? throw new InvalidDataException("This is not a picture file DeSign Mage can read.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var buffer = new PixelBuffer(info.Width, info.Height);
        var handle = GCHandle.Alloc(buffer.Rgba, GCHandleType.Pinned);
        try
        {
            var result = codec.GetPixels(info, handle.AddrOfPinnedObject());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                throw new InvalidDataException($"The picture could not be decoded ({result}).");
        }
        finally { handle.Free(); }
        return applyOrigin ? Reorient(buffer, codec.EncodedOrigin) : buffer;
    }

    /// <summary>Lossless PNG (the format of every picture the editor creates).</summary>
    public static byte[] EncodePng(PixelBuffer pixels)
    {
        var info = new SKImageInfo(pixels.Width, pixels.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(pixels.Rgba, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var stream = new SKDynamicMemoryWStream();
            // Moderate compression: photos are big, and speed matters more than the last few percent.
            if (!pixmap.Encode(stream, new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 4)))
                throw new InvalidOperationException("The picture could not be encoded.");
            using var data = stream.DetachAsData();
            return data.ToArray();
        }
        finally { handle.Free(); }
    }

    /// <summary>Turns a picture the way an EXIF orientation tag describes, so that it displays upright.</summary>
    public static PixelBuffer Reorient(PixelBuffer p, SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopRight => PixelOps.Flip(p, horizontal: true),
        SKEncodedOrigin.BottomRight => PixelOps.Rotate90(PixelOps.Rotate90(p, true), true),
        SKEncodedOrigin.BottomLeft => PixelOps.Flip(p, horizontal: false),
        SKEncodedOrigin.LeftTop => PixelOps.Flip(PixelOps.Rotate90(p, true), horizontal: true),     // transpose
        SKEncodedOrigin.RightTop => PixelOps.Rotate90(p, true),
        SKEncodedOrigin.RightBottom => PixelOps.Flip(PixelOps.Rotate90(p, true), horizontal: false), // transverse
        SKEncodedOrigin.LeftBottom => PixelOps.Rotate90(p, false),
        _ => p,
    };

    private static string MediaTypeOf(SKEncodedImageFormat f) => f switch
    {
        SKEncodedImageFormat.Png => "image/png",
        SKEncodedImageFormat.Jpeg => "image/jpeg",
        SKEncodedImageFormat.Gif => "image/gif",
        SKEncodedImageFormat.Bmp => "image/bmp",
        SKEncodedImageFormat.Webp => "image/webp",
        SKEncodedImageFormat.Ico => "image/x-icon",
        _ => "application/octet-stream",
    };
}
