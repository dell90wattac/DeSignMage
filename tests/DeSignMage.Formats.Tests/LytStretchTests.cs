using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Lyt;
using DeSignMage.TestSupport;
using Xunit.Abstractions;

namespace DeSignMage.Formats.Tests;

/// <summary>
/// A record's second scale value stretches it horizontally (width = nodes × scale × stretch, height = nodes × scale). It was applied
/// to the height, which made every stretched box too narrow and tall and every stretched text too tall and narrow.
/// </summary>
public class LytStretchTests(ITestOutputHelper output)
{
    private static LytNode Move(float x, float y) => new(0x1, LytNodeKind.Move, x, y, 0, 0, 0);
    private static LytNode Line(float x, float y, uint extra = 0) => new(0x2 | extra, LytNodeKind.Line, x, y, 0, 0, 0);

    [Fact]
    public void A_stretched_rectangle_is_wider_not_taller()
    {
        // 14 x 1.4 node units, 1.2 in per unit, stretch 0.9: 15.12 in wide and 1.68 in tall.
        var nodes = new[] { Move(0, 0), Line(0, 1.4f), Line(14, 1.4f), Line(14, 0), Line(0, 0, 0x10) };
        var record = new LytObject
        {
            Name = "Rectangle", OrgX = 3, OrgY = 4, Scale = 1.2, XScale = 0.9, Nodes = nodes, NodeCount = nodes.Length,
            Style = new LytStyle { Color = new LytColor("", "", 1, 255, 188, 17, 0) },
        };
        var file = new LytFile { FormatVersion = 0x4A, VersionName = "7.0", PageWidth = 20, PageHeight = 10, DeclaredObjectCount = 1 };
        file.Layers.Add(new LytLayer(0, "Layer 1", default, 0));
        file.Objects.Add(record);

        var bounds = Assert.Single(LytImporter.Import(file).Document.AllObjects()).GetWorldBounds();

        Assert.Equal(14 * 1.2 * 0.9 * 72, bounds.Width, 3);
        Assert.Equal(1.4 * 1.2 * 72, bounds.Height, 3);
    }

    /// <summary>
    /// A group record stores the extent of what it holds. Every group of unrotated parts of the real layout (the flyers' yellow boxes sit in
    /// them) must come out that size; with the stretch on the wrong axis dozens of them were off by a fifth or more.
    /// </summary>
    [LocalTheory("layouts/art-d.lyt", "layouts/art-e.lyt")]
    [InlineData("art-d.lyt", 100)]
    [InlineData("art-e.lyt", 5)]   // the pin design: slanted star, stretched text; few groups hold only unrotated plain parts
    public void Every_group_comes_out_the_size_its_record_says(string name, int atLeast)
    {
        var result = LytImporter.Open(TestFiles.Layout(name), new LytImportOptions { PreserveGroups = false });
        var parts = result.Source.Objects.Where(o => !o.IsGroup).ToList();
        Assert.Equal(0, result.SkippedCount);
        var imported = result.Document.AllObjects().ToList();
        Assert.Equal(parts.Count, imported.Count);

        var groups = result.Source.Objects.Where(o => o.IsGroup).GroupBy(o => o.GroupId).ToDictionary(g => g.Key, g => g.First());
        var members = new Dictionary<int, List<int>>();       // group id -> indexes of its direct parts
        for (int i = 0; i < parts.Count; i++)
            {
            int g = parts[i].ParentGroup;
            if (g == 0 || !groups.ContainsKey(g)) continue;
            if (!members.TryGetValue(g, out var list)) members[g] = list = [];
            list.Add(i);
        }
        // Groups that hold other groups are left out: their stored extent is not always kept up to date with what is inside.
        var parentsOfGroups = result.Source.Objects.Where(o => o.IsGroup).Select(o => o.ParentGroup).ToHashSet();

        int checkedCount = 0, wrong = 0;
        foreach (var (id, indexes) in members)
        {
            if (parentsOfGroups.Contains(id)) continue;
            var rec = groups[id];
            if (rec.SizeW <= 0 || rec.SizeH <= 0 || indexes.Any(i => Math.Abs(parts[i].Rotation) > 1e-6 || parts[i].Style?.Fill is { Type: 4 } || imported[i] is TextObject { IsFontMissing: true })) continue;   // a fallback font's letters are not the stored ones
            var union = imported[indexes[0]].GetWorldBounds();
            foreach (var i in indexes.Skip(1)) union = union.Union(imported[i].GetWorldBounds());
            checkedCount++;
            if (Math.Abs(union.Width - rec.SizeW * 72) > 0.04 * rec.SizeW * 72 + 2 || Math.Abs(union.Height - rec.SizeH * 72) > 0.04 * rec.SizeH * 72 + 2)
            {
                wrong++;
                output.WriteLine($"group {id} '{rec.Name}': record {rec.SizeW:0.00} x {rec.SizeH:0.00} in, imported {union.Width / 72:0.00} x {union.Height / 72:0.00} in ({indexes.Count} parts)");
            }
        }
        output.WriteLine($"{checkedCount} groups checked, {wrong} differ");
        Assert.True(checkedCount >= atLeast, $"only {checkedCount} groups could be checked");
        Assert.True(wrong == 0, $"{wrong} of {checkedCount} groups differ from their record's size");
    }

    [LocalFact("layouts/art-d.lyt")]
    public void The_yellow_bars_behind_the_flyer_lines_have_the_size_the_file_gives_them()
    {
        var doc = LytImporter.Open(TestFiles.Layout("art-d.lyt")).Document;
        var bars = doc.AllObjects().OfType<PathObject>()
            .Where(o => o.Name == "Rectangle" && o.Style.Fill is SolidPaint { Color.Name: "ARLON 83 Bright Orange" })
            .Select(o => o.GetWorldBounds()).ToList();

        // Two stretched copies of one bar: 14.24 x 1.38 units at scale 1.2267 / stretch 0.9286 and at 0.7436 / 1.5319.
        Assert.Contains(bars, b => Math.Abs(b.Width / 72 - 16.226) < 0.01 && Math.Abs(b.Height / 72 - 1.693) < 0.01);
        Assert.Contains(bars, b => Math.Abs(b.Width / 72 - 16.226) < 0.01 && Math.Abs(b.Height / 72 - 1.026) < 0.01);
    }

    [LocalFact("layouts/art-d.lyt")]
    public void A_stretched_live_text_is_wider_than_it_is_tall_like_the_file_says()
    {
        var result = LytImporter.Open(TestFiles.Layout("art-d.lyt"));
        var texts = result.Document.AllObjects().OfType<TextObject>().Where(t => t.Text == "at" && Math.Abs(t.Transform.M11 - 1.432) < 0.01).ToList();

        // The file's "at" nodes are 10.5 wide and 9.5 tall (unstretched); stretched they are 1.432 times as wide.
        Assert.NotEmpty(texts);
        Assert.All(texts, t =>
        {
            var b = t.GetWorldBounds();
            Assert.InRange(b.Width / b.Height, 1.45, 1.75);
        });
    }
}


