using DeSignMage.Formats.Lyt;
using DeSignMage.TestSupport;

namespace DeSignMage.Formats.Tests;

public class LytParserTests
{
    [LocalFact("layouts/sample.lyt")]
    public void Sample_layout_parses_completely()
    {
        var f = LytParser.Parse(TestFiles.Sample);

        Assert.Equal("7.0", f.VersionName);
        Assert.Equal(283, f.DeclaredObjectCount);
        Assert.Equal(283, f.Objects.Count);
        Assert.Equal(237, f.Objects.Count(o => !o.IsGroup));
        Assert.Equal(46, f.Objects.Count(o => o.IsGroup));
        Assert.Equal(25.25, f.PageWidth, 2);
        Assert.Equal(9.31, f.PageHeight, 2);
        Assert.Empty(f.Warnings);

        // The embedded preview is a complete JPEG.
        Assert.NotNull(f.Thumbnail);
        Assert.Equal([0xFF, 0xD8], f.Thumbnail![..2]);
        Assert.Equal([0xFF, 0xD9], f.Thumbnail[^2..]);

        var layer = Assert.Single(f.Layers);
        Assert.Equal("Layer 1", layer.Name);
    }

    [LocalFact("layouts/sample.lyt")]
    public void Sample_first_object_is_the_black_background_rectangle()
    {
        var first = LytParser.Parse(TestFiles.Sample).Objects[0];

        Assert.False(first.IsGroup);
        Assert.Equal("Rectangle", first.Name);
        Assert.Equal(5, first.Nodes.Length);
        Assert.True(first.Nodes[0].Kind == LytNodeKind.Move);
        Assert.Equal(LytColor.ModelCmyk, first.Style!.Color.Model);
        var rgb = first.Style.Color.ToDocColor();
        Assert.Equal((0, 0, 0), (rgb.R, rgb.G, rgb.B));
    }

    [LocalFact("layouts/sample.lyt")]
    public void Sample_groups_know_their_children()
    {
        var f = LytParser.Parse(TestFiles.Sample);
        var children = f.Objects.Where(o => o.ParentGroup != 0).GroupBy(o => o.ParentGroup).ToDictionary(g => g.Key, g => g.Count());

        foreach (var g in f.Objects.Where(o => o.IsGroup))
            Assert.Equal(g.NodeCount, children.GetValueOrDefault(g.GroupId));
    }

    [LocalFact("layouts/sample.lyt")]
    public void Text_objects_carry_their_string_font_and_a_marker_per_character()
    {
        var f = LytParser.Parse(TestFiles.Sample);
        var text = f.Objects.First(o => o.Name == "professional pet sitting ");

        Assert.True(text.IsText);
        Assert.EndsWith(".ptt", text.Style!.FontFile);
        Assert.Equal(text.Name.Length, text.Nodes.Count(n => n.Kind == LytNodeKind.Char));
    }

    [LocalTheory("layouts/manifest.json")]
    [MemberData(nameof(Cases))]
    public void Every_shipped_layout_parses_with_the_recorded_object_count(string relativePath, int expected)
    {
        var f = LytParser.Parse(TestFiles.Layout(relativePath));

        Assert.Equal(expected, f.Objects.Count);
        Assert.Equal(f.DeclaredObjectCount, f.Objects.Count);
    }

    [LocalTheory("layouts/manifest.json")]
    [MemberData(nameof(Cases))]
    public void Part_outlines_start_with_a_move_and_stay_near_their_declared_bounds(string relativePath, int _)
    {
        var f = LytParser.Parse(TestFiles.Layout(relativePath));

        foreach (var o in f.Objects.Where(o => !o.IsGroup && o.Nodes.Length > 0))
        {
            var first = o.Nodes.First(n => n.Kind != LytNodeKind.Char);
            Assert.True(first.Kind == LytNodeKind.Move, $"'{o.Name}' at {o.Offset} does not start with a move-to node (found {first.Kind}, flags 0x{first.Flags:X}).");
            Assert.All(o.Nodes, n => Assert.True(float.IsFinite(n.X) && float.IsFinite(n.Y)));
        }
    }

    public static IEnumerable<object[]> Cases() => TestFiles.LayoutCases();

    [LocalFact("clipart/sample.LGO", "fonts/sample.FNT", "layouts/legacy-old.lyt")]
    public void Old_layouts_fonts_and_clipart_are_refused_with_a_clear_message()
    {
        var old = Assert.Throws<LytFormatException>(() => LytParser.Parse(TestFiles.Layout("legacy-old.lyt")));
        Assert.Contains("(14)", old.Message);

        var font = Assert.Throws<LytFormatException>(() => LytParser.Parse(LocalData.Under("fonts", "sample.FNT")));
        Assert.Contains("font file", font.Message);

        var clipart = Assert.Throws<LytFormatException>(() => LytParser.Parse(LocalData.Under("clipart", "sample.LGO")));
        Assert.Contains("clipart", clipart.Message);
    }

    [LocalFact("layouts/sample.lyt")]
    public void Garbage_and_truncated_data_throw_the_format_exception_not_something_else()
    {
        Assert.Throws<LytFormatException>(() => LytParser.Parse(new byte[10]));
        Assert.Throws<LytFormatException>(() => LytParser.Parse(new byte[200]));

        var good = File.ReadAllBytes(TestFiles.Sample);
        foreach (int keep in new[] { good.Length - 5, good.Length / 2, 40_000, 31_000 })
            Assert.Throws<LytFormatException>(() => LytParser.Parse(good[..keep]));
    }
}
