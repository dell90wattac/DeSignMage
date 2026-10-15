using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Tests;

/// <summary>A picture edited in the Picture Editor is written into a legacy layout as the finished picture (the recipe is baked in).</summary>
public class LytEditedPictureTests
{
    [Fact]
    public void The_layout_holds_the_finished_picture_not_the_original()
    {
        var doc = WriterTestKit.NewDoc();
        var original = WriterTestKit.Jpeg(48, 32);
        string originalId = doc.Assets.Add(original, "image/jpeg");
        var finished = WriterTestKit.Jpeg(24, 16);
        string finishedId = doc.Assets.Add(finished, "image/jpeg");
        doc.Insert(doc.ActiveLayer, -1, new ImageObject(finishedId, 144, 96, 24, 16, "photo.jpg")
        {
            Transform = Affine2.Translation(100, 100),
            SourceAssetId = originalId,
            Recipe = new PictureRecipe(48, 32) { Crop = new RectI(0, 0, 24, 16) },
        });

        var (bytes, _) = WriterTestKit.Write(doc);
        var imported = WriterTestKit.Import(bytes);

        var pictures = imported.Document.AllObjects().OfType<ImageObject>().ToList();
        Assert.Single(pictures);
        Assert.Equal((24, 16), (pictures[0].PixelWidth, pictures[0].PixelHeight));
        Assert.Null(pictures[0].Recipe);                                  // the layout format has no recipe: what it holds is the result
    }
}
