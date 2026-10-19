using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

/// <summary>Edit in another program (Picture Editor ▸ Edit in...): the picture was changed and saved there and comes back as the new original.</summary>
public static class PictureSwap
{
    /// <summary>
    /// The frame the unedited picture has on the page once its original is replaced by one with another pixel count: the printed size stays, so only the dpi
    /// changes. When the shape changed (the other program cropped it) the width stays and the height follows the new shape.
    /// </summary>
    public static ImageFrame FrameForNewOriginal(ImageFrame frame, int oldPixelWidth, int oldPixelHeight, int newPixelWidth, int newPixelHeight)
    {
        if (oldPixelWidth < 1 || oldPixelHeight < 1 || newPixelWidth < 1 || newPixelHeight < 1) return frame;
        double oldAspect = (double)oldPixelWidth / oldPixelHeight, newAspect = (double)newPixelWidth / newPixelHeight;
        if (Math.Abs(oldAspect - newAspect) / oldAspect < 0.01) return frame;
        return frame with { Height = frame.Width / newAspect };
    }
}

public sealed partial class EditorSession
{
    /// <summary>
    /// Gives <paramref name="image"/> a new original (a picture that was changed and saved in another program). A picture with edits keeps all of them: the crop and the
    /// cut-out outlines are scaled to the new size, and the finished picture is made again from the new original. A picture without edits is simply replaced, at the same
    /// printed size. One undo step; false when nothing could be done (the picture is no longer in the design, or the service cannot render).
    /// </summary>
    public async Task<bool> ReplacePictureOriginalAsync(ImageObject image, ImagePixels original, string description = "Update Picture")
    {
        if (ImageService is null || !Document.Contains(image)) return false;
        LastImageError = null;
        if (image.Recipe is { } recipe && image.SourceAssetId is not null)
        {
            var baseFrame = PictureGeometry.BaseFrameFor(recipe, image.Width, image.Height, image.PixelWidth, image.PixelHeight, image.Transform);
            var frame0 = PictureSwap.FrameForNewOriginal(baseFrame, recipe.SourceWidth, recipe.SourceHeight, original.PixelWidth, original.PixelHeight);
            var next = recipe.WithNewSource(original.PixelWidth, original.PixelHeight);
            if (next.IsIdentity)
            {
                History.Execute(new ReplaceImageCommand(description, image, original, frame0));
                return true;
            }
            var service = ImageService;
            var doc = Document;
            Func<CancellationToken, object?> work = _ => service.RenderRecipe(doc, original.AssetId, next);
            var made = (ImagePixels?)(Background is { } background ? await background("Updating the picture...", work) : work(CancellationToken.None));
            if (made is null) { LastImageError = "The picture could not be read."; return false; }
            if (!Document.Contains(image)) return false;
            History.Execute(new ReplaceImageCommand(description, image, made, PictureGeometry.FrameFor(next, frame0.Width, frame0.Height, frame0.Transform), original.AssetId, next));
            return true;
        }
        EnsurePixelSize(image);
        var frame = PictureSwap.FrameForNewOriginal(new ImageFrame(image.Width, image.Height, image.Transform), image.PixelWidth, image.PixelHeight, original.PixelWidth, original.PixelHeight);
        History.Execute(new ReplaceImageCommand(description, image, original, frame));
        return true;
    }
}
