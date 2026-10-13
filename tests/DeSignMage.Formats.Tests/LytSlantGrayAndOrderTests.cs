using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Lyt;
using DeSignMage.TestSupport;
using Xunit.Abstractions;

namespace DeSignMage.Formats.Tests;

/// <summary>
/// Three things a second client layout ("art-e": a pin design with numbered stars, a slanted star and a color card) needed:
/// the slant of a part, the gray color model, and the drawing order of objects that sit between the members of a group.
/// </summary>
public class LytSlantGrayAndOrderTests(ITestOutputHelper output)
{
    private static LytNode Move(float x, float y) => new(0x1, LytNodeKind.Move, x, y, 0, 0, 0);
    private static LytNode Line(float x, float y, uint extra = 0) => new(0x2 | extra, LytNodeKind.Line, x, y, 0, 0, 0);

    private static LytObject Box(string name, double orgX, double orgY, double size, LytColor color, int parent = 0, double italic = 0, double rotation = 0, string font = "")
    {
        LytNode[] nodes = [Move(0, 0), Line(0, 1), Line(1, 1), Line(1, 0), Line(0, 0, 0x10)];
        return new LytObject
        {
            Name = name, OrgX = orgX, OrgY = orgY, Scale = size, XScale = 1, Rotation = rotation, Italic = italic, ParentGroup = parent,
            Nodes = nodes, NodeCount = nodes.Length, Style = new LytStyle { Color = color, FontFile = font },
        };
    }

    private static LytObject Group(int id, int parent = 0) => new() { Name = "Group", IsGroup = true, GroupId = id, ParentGroup = parent, Scale = 1, XScale = 1 };

    private static LytFile File(params LytObject[] objects)
    {
        var f = new LytFile { FormatVersion = 0x4A, VersionName = "7.0", PageWidth = 20, PageHeight = 10, DeclaredObjectCount = objects.Length };
        f.Layers.Add(new LytLayer(0, "Layer 1", default, 0));
        f.Objects.AddRange(objects);
        return f;
    }

    private static readonly LytColor Red = new("", "", 1, 255, 0, 0, 0), Blue = new("", "", 1, 0, 0, 255, 0);

    // ---------------------------------------------------------------- slant

    [Fact]
    public void A_slanted_shape_leans_right_around_its_baseline_and_keeps_its_height()
    {
        double angle = 0.3;
        var xf = LytImporter.PartTransform(Box("star", 1, 1, 1, Red, italic: angle), 720);

        // Node (0, 1) is one unit above the origin: 72 pt up, and the slant moves it 72 * tan(angle) to the right. The bottom stays put.
        var top = xf.Transform(new Vec2(0, -1));
        var bottom = xf.Transform(new Vec2(0, 0));
        Assert.Equal(72 + 72 * Math.Tan(angle), top.X, 6);
        Assert.Equal(72, bottom.X, 6);
        Assert.Equal(720 - 72 - 72, top.Y, 6);
    }

    [Fact]
    public void Only_rotated_shapes_and_text_are_not_slanted()
    {
        // A rotated shape stores italic 0; rotated text stores its rotation there too. Neither is a slant.
        var shape = LytImporter.PartTransform(Box("a", 1, 1, 1, Red, rotation: 0.5), 720);
        Assert.Equal(Math.Cos(-0.5) * 72, shape.M11, 9);
        Assert.Equal(0, Math.Abs(shape.M21 + Math.Sin(-0.5) * 72) < 1e-9 ? 0 : 1);

        var text = Box("a", 1, 1, 1, Red, italic: 0.5, rotation: 0.5, font: "_Arial.ptt");
        Assert.Equal(Affine2.Scale(72, 72) * Affine2.Rotation(-0.5) * Affine2.Translation(72, 720 - 72), LytImporter.PartTransform(text, 720));

        // Text with a slant of its own: what is left after the rotation.
        var slanted = Box("a", 1, 1, 1, Red, italic: 0.8, rotation: 0.5, font: "_Arial.ptt");
        Assert.NotEqual(Affine2.Scale(72, 72) * Affine2.Rotation(-0.5) * Affine2.Translation(72, 720 - 72), LytImporter.PartTransform(slanted, 720));
    }

    [LocalFact("layouts/art-e.lyt")]
    public void The_slanted_star_of_the_client_layout_stays_inside_the_group_that_holds_it()
    {
        var result = LytImporter.Open(TestFiles.Layout("art-e.lyt"), new LytImportOptions { PreserveGroups = false });
        var record = result.Source.Objects.Single(o => !o.IsGroup && o.Italic > 0.2 && o.Style?.FontFile == "" && o.Scale > 7);
        int index = result.Source.Objects.Where(o => !o.IsGroup).ToList().IndexOf(record);
        var bounds = result.Document.AllObjects().ElementAt(index).GetWorldBounds();

        // Its group (the star and its two outlines) is 20.29 x 17.45 in; the big star sits well inside it, 90 % as wide as the group's outline star.
        var group = result.Source.Objects.Single(o => o.IsGroup && o.GroupId == record.ParentGroup);
        double left = group.OrgX * 72, right = (group.OrgX + group.SizeW) * 72;
        Assert.True(bounds.Left > left && bounds.Right < right, $"star spans {bounds.Left / 72:0.0}..{bounds.Right / 72:0.0} in, group {left / 72:0.0}..{right / 72:0.0}");
        Assert.InRange(bounds.Width / (group.SizeW * 72), 0.80, 0.95);
    }

    // ---------------------------------------------------------------- gray

    [Fact]
    public void Color_model_4_is_a_gray_given_by_its_black_ink()
    {
        var silver = new LytColor("SILVER METAL", "", LytColor.ModelGray, 0, 0, 0, 2700);
        Assert.True(silver.IsValid);
        var c = silver.ToDocColor();
        Assert.Equal((byte)186, c.R);                   // 100 % - 27 %: the reference program shows 185
        Assert.Equal(c.R, c.G);
        Assert.Equal(c.R, c.B);
        Assert.Equal("SILVER METAL", c.Name);
        Assert.Equal(0, new LytColor("", "", LytColor.ModelGray, 0, 0, 0, 10000).ToDocColor().R);
        Assert.Equal(255, new LytColor("", "", LytColor.ModelGray, 0, 0, 0, 0).ToDocColor().R);
    }

    [LocalFact("layouts/design2.lyt")]
    public void The_gray_of_a_cut_line_is_still_no_color()
    {
        // Cut lines carry a gray (CutPath, 50 %) in the second color record; that is a mark, not a color to print.
        var cut = LytImporter.Open(TestFiles.Layout("design2.lyt")).Document.AllObjects().Single(o => o.Name == "Contour Cut Path");
        Assert.Null(cut.Style.Fill);
    }

    [LocalFact("layouts/art-e.lyt")]
    public void The_four_color_swatches_are_equal_squares_and_the_silver_one_is_gray()
    {
        var doc = LytImporter.Open(TestFiles.Layout("art-e.lyt")).Document;
        // The swatches of the color card are four rectangles of one size (one stretched record, copied) in four colors.
        var sets = doc.AllObjects().OfType<PathObject>().Where(o => o.Name == "Rectangle" && o.Style.Fill is SolidPaint)
            .GroupBy(o => (Math.Round(o.GetWorldBounds().Width), Math.Round(o.GetWorldBounds().Height))).Where(g => g.Count() >= 4).ToList();
        var swatches = Assert.Single(sets).ToList();
        var b = swatches[0].GetWorldBounds();
        output.WriteLine($"{swatches.Count} swatches of {b.Width:0.0} x {b.Height:0.0} pt");
        Assert.InRange(b.Width / b.Height, 0.8, 1.2);
        Assert.Equal(4, swatches.Select(s => ((SolidPaint)s.Style.Fill!).Color).Select(c => (c.R, c.G, c.B)).Distinct().Count());
        Assert.Contains(swatches, s => ((SolidPaint)s.Style.Fill!).Color is { R: >= 180 and <= 190 } c && c.R == c.G && c.G == c.B);
    }
    // ---------------------------------------------------------------- order and groups (R22)

    [Fact]
    public void A_group_with_an_object_drawn_between_its_members_and_over_them_is_kept_and_the_order_stays()
    {
        // The outline (blue) sits between the star and the number (red, in the group) and covers the number. Grouping must not move anything.
        var file = File(
            Box("star", 1, 1, 3, Red, parent: 1),
            Box("outline", 2, 2, 1, Blue),
            Box("number", 2, 2, 1, Red, parent: 1),
            Group(1));
        var result = LytImporter.Import(file);

        Assert.Equal(1, result.GroupCount);
        var layer = result.Document.Layers[0];
        Assert.Equal(["star", "outline", "number"], layer.Children.Select(o => o.Name ?? "").ToArray());
        var group = Assert.Single(result.Document.Groups);
        Assert.Equal(["star", "number"], group.Children.Select(o => o.Name ?? "").ToArray());
        Assert.Same(group, layer.Children[0].Parent);
        Assert.Same(layer, layer.Children[1].Parent);          // the outline is not in the group
        Assert.Same(group, layer.Children[2].Parent);
        Assert.Same(group, layer.Children[2].TopLevel);        // a click on the number still selects the whole group
    }

    [Fact]
    public void Groups_are_kept_whether_or_not_the_objects_between_their_members_touch_them()
    {
        var apart = File(
            Box("a", 1, 1, 1, Red, parent: 1),
            Box("far", 8, 5, 1, Blue),
            Box("b", 3, 1, 1, Red, parent: 1),
            Group(1));
        Assert.Equal(1, LytImporter.Import(apart).GroupCount);

        var sameColor = File(
            Box("a", 1, 1, 2, Red, parent: 1),
            Box("mate", 2, 2, 1, Red),
            Box("b", 2, 2, 1, Red, parent: 1),
            Group(1));
        Assert.Equal(1, LytImporter.Import(sameColor).GroupCount);
    }

    [Fact]
    public void A_group_inside_a_group_is_kept_too_with_every_object_in_the_order_of_the_file()
    {
        var file = File(
            Box("a", 1, 1, 3, Red, parent: 2),
            Box("mid", 2, 2, 1, Blue, parent: 1),
            Box("b", 2, 2, 1, Red, parent: 2),
            Box("c", 6, 6, 1, Blue, parent: 1),
            Group(2, parent: 1),
            Group(1));
        var result = LytImporter.Import(file);

        Assert.Equal(2, result.GroupCount);
        Assert.Equal(["a", "mid", "b", "c"], result.Document.Layers[0].Children.Select(o => o.Name ?? "").ToArray());
        var outer = Assert.Single(result.Document.Groups, g => g.Parent is Layer);
        var inner = Assert.Single(outer.Children.OfType<GroupObject>());
        Assert.Equal(["mid", "c"], outer.Children.Where(o => o is not GroupObject).Select(o => o.Name!).ToArray());
        Assert.Equal(["a", "b"], inner.Children.Select(o => o.Name!).ToArray());
    }

    [LocalFact("layouts/art-e.lyt")]
    public void On_the_client_layout_each_number_is_drawn_over_its_white_outline_as_in_the_file()
    {
        var result = LytImporter.Open(TestFiles.Layout("art-e.lyt"));
        string[] watched = [.. Enumerable.Range(1, 15).Select(i => i.ToString()), "Dynamic Outline"];
        var inFile = result.Source.Objects.Where(o => !o.IsGroup && watched.Contains(o.Name)).Select(o => o.Name).ToArray();
        var inDocument = result.Document.AllObjects().Where(o => o is not GroupObject && o.Name is not null && watched.Contains(o.Name)).Select(o => o.Name!).ToArray();

        Assert.Equal(11 + 11, inFile.Length);
        Assert.Equal(inFile, inDocument);
    }

    /// <summary>The groups that have at least one member (directly or in a group below): the ones an import makes.</summary>
    private static int GroupsWithMembers(LytFile file)
    {
        var used = new HashSet<int>();
        var records = file.Objects.Where(o => o.IsGroup).GroupBy(o => o.GroupId).ToDictionary(g => g.Key, g => g.First());
        foreach (var part in file.Objects.Where(o => !o.IsGroup))
            for (int g = part.ParentGroup, depth = 0; g != 0 && records.TryGetValue(g, out var rec) && depth++ < 64; g = rec.ParentGroup) used.Add(g);
        return used.Count;
    }

    [LocalTheory("layouts/art-d.lyt", "layouts/art-e.lyt", "layouts/sample.lyt")]
    [InlineData("sample.lyt")]
    [InlineData("art-d.lyt")]
    [InlineData("art-e.lyt")]
    public void Every_group_of_a_layout_is_kept(string name)
    {
        var result = LytImporter.Open(TestFiles.Layout(name));
        int expected = GroupsWithMembers(result.Source);
        output.WriteLine($"{name}: {result.GroupCount} groups kept of {expected} in the file");
        Assert.Equal(expected, result.GroupCount);
        Assert.Equal(expected, result.Document.GroupCount);
    }
}
