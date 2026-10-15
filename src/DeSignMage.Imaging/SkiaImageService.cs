using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using DeSignMage.Rendering;
using SkiaSharp;

namespace DeSignMage.Imaging;

/// <summary>
/// The SkiaSharp implementation of <see cref="IImageService"/>: decodes a stored picture, applies the operation exactly
/// (lossless, on RGBA) and stores the result as a PNG asset.
/// </summary>
public sealed class SkiaImageService : IImageService
{
    public (int Width, int Height)? GetPixelSize(Document doc, string assetId)
    {
        if (doc.Assets.Get(assetId) is not { } asset) return null;
        try
        {
            var p = ImageCodec.Probe(asset.Data);
            return (p.Width, p.Height);
        }
        catch (InvalidDataException) { return null; }
    }

    public ImagePixels? Apply(Document doc, string assetId, ImageOp op)
    {
        if (doc.Assets.Get(assetId) is not { } asset) return null;
        PixelBuffer source;
        try { source = ImageCodec.Decode(asset.Data); }
        catch (InvalidDataException) { return null; }

        var result = op switch
        {
            ImageOp.Rotate90 r => PixelOps.Rotate90(source, r.Clockwise),
            ImageOp.Flip f => PixelOps.Flip(source, f.Horizontal),
            ImageOp.Invert => PixelOps.Invert(source),
            ImageOp.Crop c => PixelOps.Crop(source, c.X, c.Y, c.Width, c.Height),
            ImageOp.Resample r => PixelResampler.Resample(source, r.Width, r.Height),
            _ => throw new ArgumentException($"Unknown image operation {op}."),
        };
        return Store(doc, result);
    }

    public ImagePixels? RenderRecipe(Document doc, string sourceAssetId, PictureRecipe recipe)
    {
        if (doc.Assets.Get(sourceAssetId) is not { } asset) return null;
        PixelBuffer source;
        try { source = ImageCodec.Decode(asset.Data); }
        catch (InvalidDataException) { return null; }
        return Store(doc, PictureProcessor.Render(source, recipe));
    }

    public ImagePixels? Rasterize(Document doc, IReadOnlyList<DocObject> objects, Rect2 bounds, int pixelWidth, int pixelHeight)
    {
        var info = new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface is null) return null;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // The scene renderer draws in document space; this maps the bounds onto the picture.
        var view = Affine2.Translation(-bounds.Left, -bounds.Top) * Affine2.Scale(pixelWidth / bounds.Width, pixelHeight / bounds.Height);
        using (var scene = new SceneRenderer(doc))
            foreach (var o in Core.Commands.Stacking.LeavesBottomToTop(doc, objects))   // as drawn, whatever lies between the members of a group
                if (o.IsShown) scene.DrawObject(canvas, o, view, exclude: null, cache: false);

        var pixels = new PixelBuffer(pixelWidth, pixelHeight);
        var unpremul = new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels.Rgba, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var snapshot = surface.Snapshot();
            if (!snapshot.ReadPixels(unpremul, handle.AddrOfPinnedObject(), unpremul.RowBytes)) return null;
        }
        finally { handle.Free(); }
        return Store(doc, pixels);
    }

    private static ImagePixels Store(Document doc, PixelBuffer pixels) =>
        new(doc.Assets.Add(ImageCodec.EncodePng(pixels), "image/png"), pixels.Width, pixels.Height);
}
