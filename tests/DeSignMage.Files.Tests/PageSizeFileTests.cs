using System.IO.Compression;
using System.Text;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using DeSignMage.Rendering.Export;
using DeSignMage.TestSupport;
using Xunit;

namespace DeSignMage.Files.Tests;

/// <summary>The page rectangle (size and, once the page was resized from its left or top edge, position) in the native file and in exports (R20).</summary>
public class PageSizeFileTests
{
    private static DsmLoadResult RoundTrip(Document doc) => DsmReader.Load(new MemoryStream(DsmWriter.SaveToBytes(doc)));

    private static string DocumentJson(byte[] file)
    {
        using var zip = new ZipArchive(new MemoryStream(file));
        using var reader = new StreamReader(zip.GetEntry(DsmFormat.DocumentEntry)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void A_resized_page_keeps_its_size_and_position_through_save_and_load()
    {
        var doc = new Document(PageSetup.FromInches(4, 3));
        doc.Insert(doc.ActiveLayer, -1, new RectangleObject(50, 20) { Transform = Affine2.Translation(100, 80) });
        new ResizePageCommand(new PageSetup(500.5, 260.25, -36, 12.5)).Do(doc);

        var loaded = RoundTrip(doc).Document;

        Assert.Equal(new PageSetup(500.5, 260.25, -36, 12.5), loaded.Page);
        Assert.Equal(new Rect2(-36, 12.5, 464.5, 272.75), loaded.Page.Bounds);
        Assert.Equal(new Rect2(100, 80, 150, 100), loaded.ActiveLayer.Children[0].GetWorldBounds());   // objects keep their place
    }

    [Fact]
    public void A_page_that_was_only_resized_from_the_right_or_bottom_writes_no_position()
    {
        var doc = new Document(new PageSetup(300, 200));
        new ResizePageCommand(new PageSetup(400, 250)).Do(doc);
        var json = DocumentJson(DsmWriter.SaveToBytes(doc));
        Assert.Contains("\"width\": 400", json);
        Assert.Contains("\"height\": 250", json);
        Assert.DoesNotContain("\"x\":", json.Split("\"layers\"")[0]);   // the page object has no position fields

        Assert.Equal(new PageSetup(400, 250), RoundTrip(doc).Document.Page);
    }

    [Fact]
    public void Files_written_before_the_page_could_move_open_with_the_page_at_the_origin()
    {
        var bytes = Zip.Single("document.json", Encoding.UTF8.GetBytes("{\"format\":\"DeSign Mage document\",\"formatVersion\":1,\"page\":{\"width\":144,\"height\":72}}"));
        Assert.Equal(new PageSetup(144, 72), DsmReader.Load(new MemoryStream(bytes)).Document.Page);
    }

    [Fact]
    public void Undoing_the_resize_before_saving_saves_the_old_page()
    {
        var doc = new Document(new PageSetup(300, 200));
        var cmd = new ResizePageCommand(new PageSetup(100, 100, 5, 5));
        cmd.Do(doc);
        cmd.Undo(doc);
        Assert.Equal(new PageSetup(300, 200), RoundTrip(doc).Document.Page);
    }

    [Fact]
    public void Exports_use_the_resized_page_as_their_area()
    {
        var doc = new Document(new PageSetup(300, 200));
        doc.Insert(doc.ActiveLayer, -1, new RectangleObject(50, 20) { Transform = Affine2.Translation(100, 80), Style = ObjectStyle.FillOnly(DocColor.Black) });
        new ResizePageCommand(new PageSetup(400, 250, -50, 20)).Do(doc);

        var svg = SvgExporter.Render(doc, new ExportOptions());

        Assert.Contains("viewBox=\"-50 20 400 250\"", svg);
        Assert.Contains("M100 80L150 80L150 100L100 100Z", svg);   // the object is exactly where it was in the document
    }
}
