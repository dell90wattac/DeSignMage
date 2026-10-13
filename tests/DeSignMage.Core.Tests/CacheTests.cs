using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>The caches behind the speed work: a text's layout and every object's world bounds are reused until something they depend on changes.</summary>
public class TextLayoutCacheTests
{
    private static TextObject Make() => new("Hello", "Arial", 24);

    [Fact]
    public void Moving_recoloring_or_renaming_a_text_keeps_its_layout()
    {
        var t = Make();
        var layout = t.Layout;
        t.Transform = Affine2.Translation(50, 20);
        t.Style = ObjectStyle.FillOnly(DocColor.FromHex("#FF0000"));
        t.Name = "Title";
        t.Visible = false;
        t.Locked = true;
        Assert.Same(layout, t.Layout);
        Assert.True(t.Version > 0);   // the object did change; only the layout is still valid
    }

    [Fact]
    public void Anything_that_changes_the_glyphs_makes_a_new_layout()
    {
        var edits = new List<(string What, Action<TextObject> Edit)>
        {
            ("text", t => t.Text = "Hello!"),
            ("family", t => t.FontFamily = "Segoe UI"),
            ("weight", t => t.FontWeight = 700),
            ("italic", t => t.Italic = true),
            ("size", t => t.FontSize = 30),
            ("tracking", t => t.Tracking = 0.1),
            ("kerning", t => t.Kerning = [0.2]),
            ("alignment", t => t.Alignment = TextAlignment.Center),
            ("line spacing", t => t.LineSpacing = 1.5),
        };
        foreach (var (what, edit) in edits)
        {
            var t = Make();
            var before = t.Layout;
            edit(t);
            Assert.False(ReferenceEquals(before, t.Layout), $"changing the {what} must lay the text out again");
        }
    }

    [Fact]
    public void A_clone_has_its_own_layout_with_the_same_look()
    {
        var t = Make();
        var c = (TextObject)t.Clone();
        Assert.Equal(t.Layout.Frame, c.Layout.Frame);
        c.FontSize = 48;
        Assert.NotEqual(t.Layout.Frame, c.Layout.Frame);
    }
}

public class WorldBoundsCacheTests
{
    private static void AssertSame(Rect2 a, Rect2 b) =>
        Assert.True(Math.Abs(a.Left - b.Left) + Math.Abs(a.Top - b.Top) + Math.Abs(a.Right - b.Right) + Math.Abs(a.Bottom - b.Bottom) < 1e-9, $"{a} vs {b}");

    [Fact]
    public void Bounds_follow_the_objects_own_changes()
    {
        var doc = NewDoc();
        var r = Rect(10, 20, 100, 50);
        doc.Insert(doc.ActiveLayer, -1, r);
        AssertSame(new Rect2(10, 20, 110, 70), r.GetWorldBounds());

        r.Transform = Affine2.Translation(30, 0);
        AssertSame(new Rect2(30, 0, 130, 50), r.GetWorldBounds());
        r.Width = 200;
        AssertSame(new Rect2(30, 0, 230, 50), r.GetWorldBounds());
        r.Transform = Affine2.Rotation(Math.PI / 2);   // rotated: the cached box must not be reused
        var b = r.GetWorldBounds();
        Assert.Equal(50, b.Width, 6);
        Assert.Equal(200, b.Height, 6);
    }

    [Fact]
    public void Bounds_follow_a_group_that_moves_and_a_member_that_changes()
    {
        var doc = NewDoc();
        var a = Rect(0, 0, 10, 10);
        var b = Rect(100, 0, 10, 10);
        var g = new GroupObject();
        doc.Insert(doc.ActiveLayer, -1, g);
        doc.Insert(g, -1, a);
        doc.Insert(g, -1, b);
        AssertSame(new Rect2(0, 0, 110, 10), g.GetWorldBounds());
        AssertSame(new Rect2(0, 0, 10, 10), a.GetWorldBounds());

        g.Transform = Affine2.Translation(5, 7);
        AssertSame(new Rect2(5, 7, 115, 17), g.GetWorldBounds());
        AssertSame(new Rect2(5, 7, 15, 17), a.GetWorldBounds());   // the member's cached box notices its parent moved

        b.Width = 300;
        AssertSame(new Rect2(5, 7, 405, 17), g.GetWorldBounds());
    }

    [Fact]
    public void A_path_that_is_edited_has_new_bounds()
    {
        var p = new PathObject(PathGeometry.Rectangle(new Rect2(0, 0, 10, 10)));
        AssertSame(new Rect2(0, 0, 10, 10), p.GetWorldBounds());
        p.Geometry = PathGeometry.Rectangle(new Rect2(0, 0, 50, 5));
        AssertSame(new Rect2(0, 0, 50, 5), p.GetWorldBounds());
    }

    [Fact]
    public void A_combined_object_notices_changes_to_its_parts()
    {
        var doc = NewDoc();
        var part1 = new PathObject(PathGeometry.Rectangle(new Rect2(0, 0, 10, 10)));
        var part2 = new PathObject(PathGeometry.Rectangle(new Rect2(20, 0, 30, 10)));
        var combined = new CombinedObject([part1, part2]);
        doc.Insert(doc.ActiveLayer, -1, combined);
        AssertSame(new Rect2(0, 0, 30, 10), combined.GetWorldBounds());

        var history = new CommandHistory(doc);
        history.Execute(new SetVectorsCommand("Edit", combined, 1, PathGeometry.Rectangle(new Rect2(20, 0, 90, 10))));   // part edit: the combined object's own version may not change
        AssertSame(new Rect2(0, 0, 90, 10), combined.GetWorldBounds());
        history.Undo();
        AssertSame(new Rect2(0, 0, 30, 10), combined.GetWorldBounds());
    }

    [Fact]
    public void Text_bounds_follow_text_and_font_changes_but_not_a_stale_cache()
    {
        var t = new TextObject("Hi", "Arial", 20);
        var narrow = t.GetWorldBounds();
        t.Text = "Hello world";
        Assert.True(t.GetWorldBounds().Width > narrow.Width);
        double left = t.GetWorldBounds().Left;
        t.Transform = Affine2.Translation(100, 0);
        Assert.Equal(left + 100, t.GetWorldBounds().Left, 6);
        t.Transform = Affine2.Identity;
        Assert.Equal(left, t.GetWorldBounds().Left, 6);
    }
}

/// <summary>
/// Installing a font service bumps a program-wide counter that every text layout checks, so this runs on its own (xunit never runs a collection
/// with <c>DisableParallelization</c> next to other tests), or the layout-identity checks of other tests could see the bump.
/// </summary>
[CollectionDefinition("font service generation", DisableParallelization = true)]
public class FontServiceGenerationCollection;

[Collection("font service generation")]
public class FontServiceGenerationTests
{
    [Fact]
    public void Installing_another_font_service_lays_every_text_out_again()
    {
        var t = new TextObject("Hello", "Arial", 24);
        var before = t.Layout;
        Assert.Same(before, t.Layout);
        TextEngine.Fonts = TextEngine.Fonts;   // the generation changes even when it is the same service
        Assert.NotSame(before, t.Layout);
        Assert.Equal(before.Frame, t.Layout.Frame);
    }
}
