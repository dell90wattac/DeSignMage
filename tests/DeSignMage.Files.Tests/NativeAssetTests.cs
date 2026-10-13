using DeSignMage.Core.Editing;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using DeSignMage.Imaging;
using SkiaSharp;
using System.IO.Compression;
using Xunit;

namespace DeSignMage.Files.Tests;

/// <summary>Placed pictures in the file: only what is still shown is stored, and as the original bytes when never edited.</summary>
public class NativeAssetTests
{
    private static byte[] Jpeg()
    {
        using var bmp = new SKBitmap(40, 30);
        bmp.Erase(new SKColor(30, 120, 200));
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    private static (EditorSession Session, ImageObject Image) Place(byte[] bytes, string name)
    {
        var doc = new Document(PageSetup.FromInches(4, 3));
        var picture = ImageImporter.Import(doc, bytes, name).Pixels;
        var image = new ImageObject(picture.AssetId, 40, 30, picture.PixelWidth, picture.PixelHeight, name);
        doc.Insert(doc.Layers[0], -1, image);
        var session = new EditorSession(doc) { ImageService = new SkiaImageService() };
        session.Selection.Set(image);
        return (session, image);
    }

    private static List<string> AssetEntries(byte[] dsm)
    {
        using var zip = new ZipArchive(new MemoryStream(dsm), ZipArchiveMode.Read);
        return zip.Entries.Where(e => e.FullName.StartsWith("assets/")).Select(e => e.FullName).ToList();
    }

    [Fact]
    public void Pictures_replaced_by_edits_are_not_saved_only_the_one_still_shown()
    {
        var (session, image) = Place(SampleDocuments.TinyPng(), "logo.png");
        var original = image.AssetId;

        Assert.True(session.ApplyImageOp(new ImageOp.Rotate90(true), "Rotate 90") > 0);
        Assert.True(session.ApplyImageOp(new ImageOp.Rotate90(true), "Rotate 90") > 0);

        Assert.True(session.Document.Assets.All.Count() >= 3);          // the store keeps old versions for undo
        Assert.NotEqual(original, image.AssetId);
        var bytes = DsmWriter.SaveToBytes(session.Document);
        Assert.Equal([$"assets/{image.AssetId}"], AssetEntries(bytes));  // ...the file does not

        var loaded = DsmReader.Load(new MemoryStream(bytes)).Document;
        var reopened = loaded.AllObjects().OfType<ImageObject>().Single();
        Assert.Equal(image.AssetId, reopened.AssetId);
        Assert.Equal(session.Document.Assets.Get(image.AssetId)!.Data, loaded.Assets.Get(reopened.AssetId)!.Data);
        Assert.Equal((image.PixelWidth, image.PixelHeight), (reopened.PixelWidth, reopened.PixelHeight));

        // Undo after reopening has nothing to go back to, and nothing breaks.
        var again = new EditorSession(loaded) { ImageService = new SkiaImageService() };
        Assert.False(again.History.CanUndo);
        again.Selection.Set(reopened);
        Assert.True(again.ApplyImageOp(new ImageOp.Flip(true), "Flip") > 0);
        again.Undo();
        Assert.Equal(image.AssetId, reopened.AssetId);
    }

    [Fact]
    public void An_unedited_jpeg_is_stored_as_the_original_bytes()
    {
        var jpeg = Jpeg();
        var (session, image) = Place(jpeg, "photo.jpg");

        var loaded = DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(session.Document))).Document;

        var asset = loaded.Assets.Get(loaded.AllObjects().OfType<ImageObject>().Single().AssetId)!;
        Assert.Equal("image/jpeg", asset.MediaType);
        Assert.Equal(jpeg, asset.Data);
        Assert.Equal("photo.jpg", loaded.AllObjects().OfType<ImageObject>().Single().SourceName);
    }

    [Fact]
    public void A_deleted_picture_leaves_no_asset_behind_and_a_picture_inside_a_combined_object_is_kept()
    {
        var (session, image) = Place(SampleDocuments.TinyPng(), "a.png");
        var (_, other) = Place(Jpeg(), "b.jpg");
        var doc = new Document(PageSetup.FromInches(2, 2));
        var png = doc.Assets.Add(SampleDocuments.TinyPng(), "image/png");
        doc.Assets.Add(Jpeg(), "image/jpeg");                              // nothing shows this one
        var combined = new CombinedObject([new RectangleObject(5, 5), new ImageObject(png, 4, 3)]);
        doc.Insert(doc.Layers[0], -1, combined);

        var entries = AssetEntries(DsmWriter.SaveToBytes(doc));

        Assert.Equal([$"assets/{png}"], entries);
        Assert.Empty(AssetEntries(DsmWriter.SaveToBytes(new Document(PageSetup.FromInches(1, 1)))));
    }
}


