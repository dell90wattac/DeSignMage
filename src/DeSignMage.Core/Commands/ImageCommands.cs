using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>
/// Swaps what a placed picture shows: its stored pixels, size and frame. All Image-menu operations (rotate, flip, invert, crop)
/// are one of these, so undo only puts the old asset id and frame back (the old pixels stay in the asset store).
/// </summary>
public sealed class ReplaceImageCommand : IDocumentCommand, IUndoMemory
{
    private readonly ImageObject _image;
    private readonly State _old, _new;

    public long ApproximateBytes => 256;
    /// <summary>Both versions: undo needs the old pixels, redo the new ones.</summary>
    public IEnumerable<string> HeldAssetIds => [_old.AssetId, _new.AssetId];
    private readonly record struct State(string AssetId, int PixelWidth, int PixelHeight, double Width, double Height, Affine2 Transform);

    public ReplaceImageCommand(string description, ImageObject image, ImagePixels pixels, ImageFrame frame)
    {
        Description = description;
        _image = image;
        _old = new State(image.AssetId, image.PixelWidth, image.PixelHeight, image.Width, image.Height, image.Transform);
        _new = new State(pixels.AssetId, pixels.PixelWidth, pixels.PixelHeight, frame.Width, frame.Height, frame.Transform);
    }

    public string Description { get; }

    public void Do(Document doc) => Apply(_new);
    public void Undo(Document doc) => Apply(_old);

    private void Apply(State s)
    {
        _image.AssetId = s.AssetId;
        _image.PixelWidth = s.PixelWidth;
        _image.PixelHeight = s.PixelHeight;
        _image.Width = s.Width;
        _image.Height = s.Height;
        _image.Transform = s.Transform;
    }
}
