using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using DeSignMage.Imaging;

namespace DeSignMage.Imaging.Tests;

/// <summary>Real pictures through the real image service: a long series of edits to a photo must stay under the undo memory cap.</summary>
public class UndoMemoryImageTests
{
    private const long Cap = 10L << 20;   // small, so a few hundred milliseconds of work shows the cap

    private readonly SkiaImageService _svc = new();
    private readonly Document _doc = Samples.Doc();

    /// <summary>Noise does not compress, so every stored copy of the picture is about 0.8 MB.</summary>
    private ImageObject PlacePhoto(CommandHistory history, int size = 450)
    {
        var rnd = new Random(11);
        var pixels = new PixelBuffer(size, size);
        rnd.NextBytes(pixels.Rgba);
        for (int i = 3; i < pixels.Rgba.Length; i += 4) pixels.Rgba[i] = 255;
        string id = _doc.Assets.Add(ImageCodec.EncodePng(pixels), "image/png");
        var img = new ImageObject(id, 300, 300, size, size);
        history.Execute(new AddObjectCommand(_doc.ActiveLayer, img));
        return img;
    }

    private void Edit(CommandHistory history, ImageObject img, ImageOp op)
    {
        var px = _svc.Apply(_doc, img.AssetId, op)!;
        var frame = op is ImageOp.Rotate90
            ? ImageFrames.Rotate90(img.Width, img.Height, img.Transform)
            : new ImageFrame(img.Width, img.Height, img.Transform);
        history.Execute(new ReplaceImageCommand("Edit", img, px, frame));
    }

    [Fact]
    public void A_long_series_of_edits_to_a_photo_stays_under_the_cap_and_undo_works_for_the_kept_steps()
    {
        var h = new CommandHistory(_doc) { MemoryLimitBytes = Cap };
        var img = PlacePhoto(h);
        long peak = 0;
        var shown = new List<string> { img.AssetId };
        for (int i = 0; i < 24; i++)
        {
            // Rotate, and every other time shave one column off, so no two stored copies are the same picture.
            Edit(h, img, new ImageOp.Rotate90(true));
            if (i % 2 == 1) Edit(h, img, new ImageOp.Crop(0, 0, img.PixelWidth - 1, img.PixelHeight));
            shown.Add(img.AssetId);
            peak = Math.Max(peak, h.UndoMemoryBytes);
        }

        Assert.True(peak <= Cap, $"undo memory peaked at {peak / 1048576.0:0.0} MB");
        Assert.True(_doc.Assets.All.Sum(a => a.Data.LongLength) <= Cap + 2_000_000, "stored pictures stay near the cap too");
        Assert.True(h.DroppedSteps > 0);

        // Every kept step can be undone; each one lands on a picture that still decodes.
        int undone = 0;
        while (h.CanUndo)
        {
            h.Undo();
            undone++;
            Assert.NotNull(ImageCodec.Decode(_doc.Assets.Get(img.AssetId)!.Data));
        }
        Assert.True(undone >= 3, $"only {undone} steps could be undone");
        while (h.CanRedo) h.Redo();
        Assert.Equal(shown[^1], img.AssetId);
    }

    [Fact]
    public void Plain_rotations_stay_under_the_cap_too()
    {
        var h = new CommandHistory(_doc) { MemoryLimitBytes = Cap };
        var img = PlacePhoto(h);
        for (int i = 0; i < 60; i++) Edit(h, img, new ImageOp.Rotate90(i % 3 != 0));
        Assert.True(h.UndoMemoryBytes <= Cap);
        int undone = 0;
        while (h.CanUndo) { h.Undo(); undone++; }
        Assert.True(undone >= 3);
        Assert.NotNull(_doc.Assets.Get(img.AssetId));
    }
}
