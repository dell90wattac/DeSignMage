using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>
/// Swaps what a placed picture shows: its stored pixels, size and frame, and the Picture Editor's recipe with the original it belongs to. All
/// Image-menu operations (rotate, flip, invert, crop, resample) and the Picture Editor are one of these, so undo only puts the old asset ids,
/// recipe and frame back (the old pixels stay in the asset store while a step needs them).
/// The Image-menu operations work on the pixels as they are shown, so they end the recipe: the edited picture becomes the picture
/// (<paramref name="sourceAssetId"/> and <paramref name="recipe"/> default to none); undo brings the recipe back.
/// </summary>
public sealed class ReplaceImageCommand : IDocumentCommand, IUndoMemory
{
    private readonly ImageObject _image;
    private readonly State _old, _new;

    public long ApproximateBytes => 256;

    /// <summary>All versions: undo needs the old pixels (and their original), redo the new ones.</summary>
    public IEnumerable<string> HeldAssetIds =>
        new[] { _old.AssetId, _new.AssetId, _old.SourceAssetId, _new.SourceAssetId }.OfType<string>().Distinct();

    private readonly record struct State(string AssetId, string? SourceAssetId, PictureRecipe? Recipe, int PixelWidth, int PixelHeight, double Width, double Height, Affine2 Transform);

    public ReplaceImageCommand(string description, ImageObject image, ImagePixels pixels, ImageFrame frame, string? sourceAssetId = null, PictureRecipe? recipe = null)
    {
        Description = description;
        _image = image;
        _old = new State(image.AssetId, image.SourceAssetId, image.Recipe, image.PixelWidth, image.PixelHeight, image.Width, image.Height, image.Transform);
        _new = new State(pixels.AssetId, sourceAssetId, recipe, pixels.PixelWidth, pixels.PixelHeight, frame.Width, frame.Height, frame.Transform);
    }

    public string Description { get; }

    public void Do(Document doc) => Apply(_new);
    public void Undo(Document doc) => Apply(_old);

    private void Apply(State s)
    {
        _image.AssetId = s.AssetId;
        _image.SourceAssetId = s.SourceAssetId;
        _image.Recipe = s.Recipe;
        _image.PixelWidth = s.PixelWidth;
        _image.PixelHeight = s.PixelHeight;
        _image.Width = s.Width;
        _image.Height = s.Height;
        _image.Transform = s.Transform;
    }
}
