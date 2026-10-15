using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using DeSignMage.Imaging;
using Xunit;

namespace DeSignMage.Files.Tests;

/// <summary>A picture edited in the Picture Editor in a .dsm file: original, recipe and the finished picture; older files and damaged recipes.</summary>
public class PictureRecipeFileTests
{
    private static DsmLoadResult RoundTrip(Document doc) => DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(doc)));

    private static (Document Doc, ImageObject Image) EditedPicture(PictureRecipe? recipe = null)
    {
        var doc = new Document(PageSetup.FromInches(24, 12));
        var original = new PixelBuffer(8, 6);
        for (int y = 0; y < 6; y++) for (int x = 0; x < 8; x++) original.Set(x, y, (byte)(x * 30), (byte)(y * 40), 99);
        var result = new PixelBuffer(4, 3);
        result.Set(1, 1, 250, 250, 250);
        string originalId = doc.Assets.Add(ImageCodec.EncodePng(original), "image/png");
        string resultId = doc.Assets.Add(ImageCodec.EncodePng(result), "image/png");
        recipe ??= new PictureRecipe(8, 6)
        {
            Straighten = -3.25,
            Crop = new RectI(2, 1, 4, 3),
            Adjust = PictureAdjust.None with { Brightness = 12.5, Contrast = -4, Gamma = 1.25, Saturation = 30, Hue = 45, BalanceR = 10, BalanceG = -20, BalanceB = 5, LowR = 7, LowG = 8, LowB = 9, HighR = 240, HighG = 241, HighB = 242 },
        };
        var image = new ImageObject(resultId, 40, 30, 4, 3, "photo.jpg") { Transform = Affine2.Translation(30, 40), SourceAssetId = originalId, Recipe = recipe };
        doc.Insert(doc.ActiveLayer, -1, image);
        return (doc, image);
    }

    [Fact]
    public void An_edited_picture_comes_back_with_its_original_its_recipe_and_its_finished_pixels()
    {
        var (doc, image) = EditedPicture();
        var loaded = RoundTrip(doc);

        ModelCompare.AssertEqual(doc, loaded.Document);   // every property of the picture, the recipe included
        var back = loaded.Document.AllObjects().OfType<ImageObject>().Single();
        Assert.Equal(image.Recipe, back.Recipe);
        Assert.Equal(image.SourceAssetId, back.SourceAssetId);
        Assert.Equal(image.AssetId, back.AssetId);
        Assert.Equal((4, 3), (back.PixelWidth, back.PixelHeight));
        Assert.True(doc.Assets.Get(image.SourceAssetId!)!.Data.AsSpan().SequenceEqual(loaded.Document.Assets.Get(back.SourceAssetId!)!.Data), "the original is saved byte for byte");
        Assert.True(doc.Assets.Get(image.AssetId)!.Data.AsSpan().SequenceEqual(loaded.Document.Assets.Get(back.AssetId)!.Data));
        Assert.Empty(loaded.Warnings);
    }

    [Fact]
    public void Both_pictures_are_written_even_though_only_one_is_drawn()
    {
        var (doc, _) = EditedPicture();
        using var zip = new ZipArchive(new MemoryStream(DsmWriter.SaveToBytes(doc)));
        Assert.Equal(2, zip.Entries.Count(e => e.FullName.StartsWith("assets/", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_file_names_the_original_and_the_recipe_and_is_a_format_that_older_readers_can_still_open()
    {
        var (doc, _) = EditedPicture();
        using var zip = new ZipArchive(new MemoryStream(DsmWriter.SaveToBytes(doc)));
        using var reader = new StreamReader(zip.GetEntry("document.json")!.Open(), Encoding.UTF8);
        using var json = JsonDocument.Parse(reader.ReadToEnd());
        Assert.Equal(3, DsmFormat.CurrentVersion);
        Assert.Equal(DsmFormat.CurrentVersion, json.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("minReaderVersion").GetInt32());   // the "asset" is the finished picture, so an old reader shows the right picture
        string text = json.RootElement.ToString();
        Assert.Contains("\"source\"", text);
        Assert.Contains("\"recipe\"", text);
    }

    [Fact]
    public void A_picture_that_was_never_edited_writes_no_recipe()
    {
        var doc = new Document(PageSetup.FromInches(24, 12));
        string id = doc.Assets.Add(ImageCodec.EncodePng(new PixelBuffer(2, 2)), "image/png");
        doc.Insert(doc.ActiveLayer, -1, new ImageObject(id, 20, 20, 2, 2));
        using var zip = new ZipArchive(new MemoryStream(DsmWriter.SaveToBytes(doc)));
        using var reader = new StreamReader(zip.GetEntry("document.json")!.Open(), Encoding.UTF8);
        string text = reader.ReadToEnd();
        Assert.DoesNotContain("\"recipe\"", text);
        Assert.DoesNotContain("\"source\"", text);
        var back = RoundTrip(doc).Document.AllObjects().OfType<ImageObject>().Single();
        Assert.Null(back.Recipe);
        Assert.Null(back.SourceAssetId);
    }

    [Fact]
    public void Flips_quarter_turns_a_free_angle_and_every_effect_survive_a_save()
    {
        var recipe = new PictureRecipe(8, 6)
        {
            Mirror = true,
            Quarter = 3,
            Straighten = 135.5,
            Effects = new PictureEffects { NoiseReduction = 40, Blur = 2.5, Sharpen = 60, Grayscale = true, Sepia = 35, Invert = true, Threshold = 99, Posterize = 5 },
        };
        var (doc, image) = EditedPicture(recipe);
        var loaded = RoundTrip(doc);
        ModelCompare.AssertEqual(doc, loaded.Document);
        var back = loaded.Document.AllObjects().OfType<ImageObject>().Single();
        Assert.Equal(recipe, back.Recipe);
        Assert.Equal((true, 3, 135.5), (back.Recipe!.Mirror, back.Recipe.Quarter, back.Recipe.Straighten));
        Assert.Equal(image.Recipe!.Effects, back.Recipe.Effects);
        Assert.Empty(loaded.Warnings);
    }

    [Fact]
    public void A_recipe_of_the_first_version_without_these_steps_still_loads_with_them_off()
    {
        var (doc, _) = EditedPicture(new PictureRecipe(8, 6) { Straighten = 4, Crop = new RectI(1, 1, 4, 3) });
        var back = RoundTrip(doc).Document.AllObjects().OfType<ImageObject>().Single();
        Assert.False(back.Recipe!.Mirror);
        Assert.Equal(0, back.Recipe.Quarter);
        Assert.True(back.Recipe.Effects.IsIdentity);
    }

    [Fact]
    public void Damaged_effects_are_put_back_into_their_range()
    {
        var (doc, _) = EditedPicture(new PictureRecipe(8, 6) { Effects = new PictureEffects { Blur = 5, Threshold = 10 } });
        var bytes = Rewrite(DsmWriter.SaveToBytes(doc), t => t.Replace("\"threshold\": 10", "\"threshold\": 900").Replace("\"blur\": 5", "\"blur\": -4"));
        var back = DsmReader.Load(new MemoryStream(bytes)).Document.AllObjects().OfType<ImageObject>().Single();
        Assert.Equal(255, back.Recipe!.Effects.Threshold);
        Assert.Equal(0, back.Recipe.Effects.Blur);
        Assert.Equal(1, back.Recipe.Effects.Posterize);
    }

    [Fact]
    public void A_flattened_picture_is_saved_without_the_original_or_a_recipe_and_is_smaller()
    {
        var (doc, image) = EditedPicture();
        int before = DsmWriter.SaveToBytes(doc).Length;
        var session = new DeSignMage.Core.Editing.EditorSession(doc);
        session.Selection.Set(image);
        Assert.Equal(1, session.FlattenPictures());

        var bytes = DsmWriter.SaveToBytes(doc);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(1, zip.Entries.Count(e => e.FullName.StartsWith("assets/", StringComparison.Ordinal)));   // only the finished picture
        using var reader = new StreamReader(zip.GetEntry("document.json")!.Open(), Encoding.UTF8);
        string text = reader.ReadToEnd();
        Assert.DoesNotContain("\"recipe\"", text);
        Assert.DoesNotContain("\"source\"", text);
        Assert.True(bytes.Length < before, $"{bytes.Length} bytes after, {before} before");

        var back = DsmReader.Load(new MemoryStream(bytes)).Document.AllObjects().OfType<ImageObject>().Single();
        Assert.Null(back.Recipe);
        Assert.Null(back.SourceAssetId);
        Assert.Equal((4, 3), (back.PixelWidth, back.PixelHeight));
        Assert.True(doc.Assets.Get(image.AssetId)!.Data.AsSpan().SequenceEqual(DsmReader.Load(new MemoryStream(bytes)).Document.Assets.Get(back.AssetId)!.Data));
    }

    [Fact]
    public void Undoing_a_flatten_brings_the_original_back_into_the_next_save()
    {
        var (doc, image) = EditedPicture();
        var session = new DeSignMage.Core.Editing.EditorSession(doc);
        session.Selection.Set(image);
        session.FlattenPictures();
        session.Undo();
        var back = DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(doc))).Document.AllObjects().OfType<ImageObject>().Single();
        Assert.Equal(image.Recipe, back.Recipe);
        Assert.Equal(image.SourceAssetId, back.SourceAssetId);
    }

    [Fact]
    public void A_neutral_recipe_part_is_not_written()
    {
        var (doc, _) = EditedPicture(new PictureRecipe(8, 6) { Crop = new RectI(1, 1, 4, 3) });
        using var zip = new ZipArchive(new MemoryStream(DsmWriter.SaveToBytes(doc)));
        using var reader = new StreamReader(zip.GetEntry("document.json")!.Open(), Encoding.UTF8);
        string text = reader.ReadToEnd();
        Assert.DoesNotContain("\"adjust\"", text);
        Assert.DoesNotContain("\"straighten\"", text);
        var back = RoundTrip(doc).Document.AllObjects().OfType<ImageObject>().Single();
        Assert.Equal(new RectI(1, 1, 4, 3), back.Recipe!.Crop);
        Assert.True(back.Recipe.Adjust.IsIdentity);
    }

    private static byte[] Rewrite(byte[] dsm, Func<string, string> edit)
    {
        using var input = new ZipArchive(new MemoryStream(dsm));
        var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in input.Entries)
            {
                using var src = entry.Open();
                using var ms = new MemoryStream();
                src.CopyTo(ms);
                var data = entry.FullName == "document.json" ? Encoding.UTF8.GetBytes(edit(Encoding.UTF8.GetString(ms.ToArray()))) : ms.ToArray();
                using var dst = zip.CreateEntry(entry.FullName).Open();
                dst.Write(data);
            }
        return output.ToArray();
    }

    [Fact]
    public void A_file_from_before_pictures_had_recipes_opens_as_it_always_did()
    {
        var doc = new Document(PageSetup.FromInches(24, 12));
        string id = doc.Assets.Add(ImageCodec.EncodePng(new PixelBuffer(3, 3)), "image/png");
        doc.Insert(doc.ActiveLayer, -1, new ImageObject(id, 30, 30, 3, 3, "old.png") { Name = "old" });
        var old = Rewrite(DsmWriter.SaveToBytes(doc), t => t.Replace($"\"formatVersion\": {DsmFormat.CurrentVersion}", "\"formatVersion\": 2"));

        var loaded = DsmReader.Load(new MemoryStream(old));

        Assert.Equal(2, loaded.FormatVersion);
        var back = loaded.Document.AllObjects().OfType<ImageObject>().Single();
        Assert.Equal((id, "old.png"), (back.AssetId, back.SourceName));
        Assert.Null(back.Recipe);
        Assert.Empty(loaded.Warnings);
    }

    [Fact]
    public void A_recipe_that_cannot_be_understood_leaves_the_finished_picture_as_a_plain_picture()
    {
        var (doc, image) = EditedPicture();
        var damaged = Rewrite(DsmWriter.SaveToBytes(doc), t => t.Replace("\"sourceWidth\": 8", "\"sourceWidth\": 0"));

        var loaded = DsmReader.Load(new MemoryStream(damaged));

        var back = loaded.Document.AllObjects().OfType<ImageObject>().Single();
        Assert.Equal(image.AssetId, back.AssetId);                       // still drawn
        Assert.Null(back.Recipe);
        Assert.Equal(image.PixelWidth, back.PixelWidth);
    }

    [Fact]
    public void Out_of_range_numbers_in_a_recipe_are_pulled_into_range()
    {
        var (doc, _) = EditedPicture(new PictureRecipe(8, 6) { Straighten = 20, Adjust = PictureAdjust.None with { Brightness = 50, Gamma = 2 } });
        var wild = Rewrite(DsmWriter.SaveToBytes(doc), t => t.Replace("\"brightness\": 50", "\"brightness\": 5000").Replace("\"gamma\": 2", "\"gamma\": 999").Replace("\"straighten\": 20", "\"straighten\": 400"));

        var back = DsmReader.Load(new MemoryStream(wild)).Document.AllObjects().OfType<ImageObject>().Single();

        Assert.Equal(100, back.Recipe!.Adjust.Brightness);
        Assert.Equal(10, back.Recipe.Adjust.Gamma);
        Assert.Equal(180, back.Recipe.Straighten);
    }

    [Fact]
    public void A_missing_original_is_reported_and_the_picture_is_still_shown_but_no_longer_editable_from_its_original()
    {
        var (doc, image) = EditedPicture();
        var bytes = DsmWriter.SaveToBytes(doc);
        // drop the original's asset from the zip
        using var input = new ZipArchive(new MemoryStream(bytes));
        var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in input.Entries.Where(e => !e.FullName.EndsWith(image.SourceAssetId![..16], StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains(image.SourceAssetId!, StringComparison.Ordinal)))
            {
                using var src = entry.Open();
                using var dst = zip.CreateEntry(entry.FullName).Open();
                src.CopyTo(dst);
            }

        var loaded = DsmReader.Load(new MemoryStream(output.ToArray()));

        var back = loaded.Document.AllObjects().OfType<ImageObject>().Single();
        Assert.Equal(image.AssetId, back.AssetId);
        Assert.Null(back.Recipe);
        Assert.Null(back.SourceAssetId);
        Assert.Contains(loaded.Warnings, w => w.Contains("original"));
    }

    [Fact]
    public void Saving_twice_gives_the_same_file_content()
    {
        var (doc, _) = EditedPicture();
        var a = RoundTrip(doc).Document;
        var b = RoundTrip(a).Document;
        var ia = a.AllObjects().OfType<ImageObject>().Single();
        var ib = b.AllObjects().OfType<ImageObject>().Single();
        Assert.Equal(ia.Recipe, ib.Recipe);
        Assert.Equal(ia.SourceAssetId, ib.SourceAssetId);
    }

    [Fact]
    public void Export_draws_the_finished_picture_at_its_full_resolution_not_the_original()
    {
        var doc = new Document(PageSetup.FromInches(1, 1));            // 72 x 72 points
        var original = new PixelBuffer(8, 8);
        var finished = new PixelBuffer(4, 4);
        for (int i = 0; i < 64; i++) original.Set(i % 8, i / 8, 200, 10, 10);       // the untouched original is red ...
        for (int i = 0; i < 16; i++) finished.Set(i % 4, i / 4, 10, 200, 10);       // ... the finished picture green
        string originalId = doc.Assets.Add(ImageCodec.EncodePng(original), "image/png");
        string finishedId = doc.Assets.Add(ImageCodec.EncodePng(finished), "image/png");
        doc.Insert(doc.ActiveLayer, -1, new ImageObject(finishedId, 72, 72, 4, 4)
        {
            SourceAssetId = originalId,
            Recipe = new PictureRecipe(8, 8) { Crop = new RectI(0, 0, 4, 4), Adjust = PictureAdjust.None with { Hue = 120 } },
        });

        var png = DeSignMage.Rendering.Export.DocumentExporter.RenderPng(doc, new DeSignMage.Rendering.Export.ExportOptions { PixelWidth = 4 });

        using var bitmap = SkiaSharp.SKBitmap.Decode(png);
        Assert.Equal((4, 4), (bitmap.Width, bitmap.Height));
        var c = bitmap.GetPixel(1, 1);
        Assert.True(c.Green > 150 && c.Red < 60, $"expected the finished (green) picture, got {c}");
    }
}