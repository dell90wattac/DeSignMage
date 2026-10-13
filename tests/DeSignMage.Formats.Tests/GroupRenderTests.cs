using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using SkiaSharp;

namespace DeSignMage.Formats.Tests;

/// <summary>R22: making, moving, breaking and saving a group never changes the picture, because it never changes the drawing order.</summary>
public class GroupRenderTests
{
    private static (EditorSession Session, RectangleObject Box, RectangleObject Outline, RectangleObject Number) Pin()
    {
        var s = new EditorSession(new Document(PageSetup.FromInches(4, 4)));
        RectangleObject Add(double x, double y, double w, string color)
        {
            var r = new RectangleObject(w, w) { Transform = Affine2.Translation(x, y), Style = ObjectStyle.FillOnly(DocColor.FromHex(color)) };
            s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, r));
            return r;
        }
        return (s, Add(20, 20, 200, "#CC0000"), Add(60, 60, 120, "#FFFFFF"), Add(80, 80, 80, "#CC0000"));   // a star, its white outline, its number
    }

    private static bool SamePixels(SKBitmap a, SKBitmap b) => a.Width == b.Width && a.Height == b.Height && a.GetPixelSpan().SequenceEqual(b.GetPixelSpan());

    [Fact]
    public void Grouping_the_star_and_the_number_does_not_change_the_picture_and_the_white_outline_stays_between_them()
    {
        var (s, box, outline, number) = Pin();
        var area = new Rect2(0, 0, 260, 260);
        using var before = LytRenderTests.Render(s.Document, area, 1);

        s.Selection.Set([box, number]);
        var group = s.GroupSelection()!;
        using var grouped = LytRenderTests.Render(s.Document, area, 1);
        Assert.True(SamePixels(before, grouped));
        Assert.Equal(new SKColor(0xCC, 0, 0), grouped.GetPixel(100, 100));      // the number is in front of the white outline
        Assert.Equal(SKColors.White, grouped.GetPixel(70, 70));                 // the outline is still over the star

        s.Selection.Set(group);
        s.UngroupSelection();
        using var ungrouped = LytRenderTests.Render(s.Document, area, 1);
        Assert.True(SamePixels(before, ungrouped));
        s.Undo();
        s.Undo();
        using var undone = LytRenderTests.Render(s.Document, area, 1);
        Assert.True(SamePixels(before, undone));
    }

    [Fact]
    public void Moving_a_group_whose_members_are_apart_looks_like_moving_its_members_one_by_one()
    {
        var (s, box, outline, number) = Pin();
        s.Selection.Set([box, number]);
        s.GroupSelection();
        s.TransformSelection("Move", Affine2.Translation(10, 5));
        var area = new Rect2(0, 0, 260, 260);
        using var viaGroup = LytRenderTests.Render(s.Document, area, 1);

        var (t, box2, outline2, number2) = Pin();
        t.Selection.Set(box2); t.TransformSelection("Move", Affine2.Translation(10, 5));
        t.Selection.Set(number2); t.TransformSelection("Move", Affine2.Translation(10, 5));
        using var oneByOne = LytRenderTests.Render(t.Document, area, 1);
        Assert.True(SamePixels(viaGroup, oneByOne));
    }

    [Fact]
    public void An_interleaved_group_survives_saving_and_opening_with_the_same_order_the_same_group_and_the_same_picture()
    {
        var (s, box, outline, number) = Pin();
        s.Selection.Set([box, number]);
        var group = s.GroupSelection()!;
        group.Name = "star + number";
        group.Transform = Affine2.Translation(3, 4);        // a group transform that applies to members drawn on both sides of the outline
        var area = new Rect2(0, 0, 260, 260);
        using var before = LytRenderTests.Render(s.Document, area, 1);

        var bytes = DsmWriter.SaveToBytes(s.Document);
        var loaded = DsmReader.Load(new MemoryStream(bytes));
        var doc = loaded.Document;
        Assert.Empty(loaded.Warnings);
        Assert.Equal(2, loaded.FormatVersion);
        Assert.Equal(3, doc.ActiveLayer.Children.Count);
        Assert.Equal(1, doc.GroupCount);
        var g = Assert.Single(doc.Groups);
        Assert.Equal("star + number", g.Name);
        Assert.Equal(group.Transform, g.Transform);
        var layer = doc.ActiveLayer.Children;
        Assert.Same(g, layer[0].Parent);
        Assert.Same(doc.ActiveLayer, layer[1].Parent);      // the outline is not in the group
        Assert.Same(g, layer[2].Parent);
        Assert.Equal(g.Children, [layer[0], layer[2]]);
        using var after = LytRenderTests.Render(doc, area, 1);
        Assert.True(SamePixels(before, after));

        // saved again, it is the same file content apart from the time stamp
        var again = DsmWriter.SaveToBytes(doc, new DsmSaveOptions { SavedUtc = new DateTime(2026, 1, 1) });
        var first = DsmWriter.SaveToBytes(s.Document, new DsmSaveOptions { SavedUtc = new DateTime(2026, 1, 1) });
        Assert.Equal(first.Length, again.Length);
    }

    [Fact]
    public void A_format_1_file_with_a_nested_group_still_opens_with_its_order_and_group()
    {
        const string json = """
            { "format": "DeSign Mage document", "formatVersion": 1, "minReaderVersion": 1,
              "page": { "width": 288, "height": 288 }, "activeLayer": 0,
              "layers": [ { "name": "Layer 1", "color": { "hex": "#000000" }, "objects": [
                { "kind": "rect", "width": 10, "height": 10 },
                { "kind": "group", "name": "old", "transform": [1,0,0,1,5,0], "children": [
                    { "kind": "rect", "width": 20, "height": 20, "name": "m1" },
                    { "kind": "group", "children": [ { "kind": "rect", "width": 30, "height": 30, "name": "m2" } ] } ] },
                { "kind": "rect", "width": 40, "height": 40 } ] } ],
              "guides": [], "assets": [] }
            """;
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        using (var w = new StreamWriter(zip.CreateEntry("document.json").Open())) w.Write(json);
        ms.Position = 0;

        var loaded = DsmReader.Load(ms);
        var doc = loaded.Document;
        Assert.Empty(loaded.Warnings);
        Assert.Equal(1, loaded.FormatVersion);
        Assert.Equal([10, 20, 30, 40], doc.ActiveLayer.Children.Select(o => ((RectangleObject)o).Width));
        Assert.Equal(2, doc.GroupCount);
        var outer = Assert.Single(doc.Groups, g => g.Name == "old");
        Assert.Same(outer, doc.ActiveLayer.Children[1].Parent);
        Assert.Same(outer, doc.ActiveLayer.Children[2].TopLevel);
        Assert.Equal(5, doc.ActiveLayer.Children[1].GetWorldBounds().Left, 9);
    }
}
