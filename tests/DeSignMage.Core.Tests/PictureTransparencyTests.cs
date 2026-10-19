using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using Xunit;

namespace DeSignMage.Core.Tests;

/// <summary>The Transparency step of a picture recipe: values, geometry of the cut outlines, replacing the original, and picking see-through pictures.</summary>
public class PictureTransparencyTests
{
    static PictureTransparencyTests()
    {
        // Only pictures whose data starts with "MASK" get a mask (a 4 x 4 grid, the left half visible): other tests' pictures keep their whole frame.
        HitTesting.PictureMaskLoader = data =>
        {
            if (data.Length < 4 || data[0] != 'M' || data[1] != 'A' || data[2] != 'S' || data[3] != 'K') return null;
            var alpha = new byte[16];
            for (int y = 0; y < 4; y++) for (int x = 0; x < 2; x++) alpha[y * 4 + x] = 255;
            return new AlphaMask(4, 4, alpha);
        };
    }

    private static CutShape Square(double x, double y, double size, bool keep = true) =>
        new(new ValueList<Vec2>([new Vec2(x, y), new Vec2(x + size, y), new Vec2(x + size, y + size), new Vec2(x, y + size)]), keep);

    // ---- values ----

    [Fact]
    public void Transparency_is_compared_by_value_so_an_unchanged_recipe_is_equal_to_itself()
    {
        var a = new PictureTransparency { Colors = new ValueList<int>([0xFFFFFF]), Cuts = new ValueList<CutShape>([Square(1, 2, 3)]) };
        var b = new PictureTransparency { Colors = new ValueList<int>([0xFFFFFF]), Cuts = new ValueList<CutShape>([Square(1, 2, 3)]) };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a with { Colors = a.Colors.Add(0) });
        Assert.NotEqual(a, a with { Cuts = a.Cuts.RemoveAt(0) });
    }

    [Fact]
    public void Nothing_to_cut_means_the_recipe_is_unchanged_whatever_the_edge_settings_say()
    {
        Assert.True(PictureTransparency.None.IsIdentity);
        Assert.True((PictureTransparency.None with { Softness = 20, Shrink = 3, Tolerance = 50, EdgeOnly = true }).IsIdentity);
        Assert.False((PictureTransparency.None with { Opacity = 60 }).IsIdentity);
        Assert.False((PictureTransparency.None with { Colors = new ValueList<int>([1]) }).IsIdentity);
        Assert.True((PictureTransparency.None with { Cuts = new ValueList<CutShape>([new CutShape(new ValueList<Vec2>([new Vec2(0, 0), new Vec2(1, 1)]), true)]) }).IsIdentity);   // two points are no outline
        var r = new PictureRecipe(10, 10) { Transparency = PictureTransparency.None with { Softness = 20 } };
        Assert.True(r.IsIdentity);
        Assert.Equal(PictureTransparency.None, r.Normalized().Transparency);
    }

    [Fact]
    public void A_recipe_with_a_cut_out_is_not_the_identity()
    {
        Assert.False(new PictureRecipe(10, 10) { Transparency = new PictureTransparency { Colors = new ValueList<int>([0xFFFFFF]) } }.IsIdentity);
    }

    [Fact]
    public void Numbers_are_put_into_their_range()
    {
        var t = new PictureTransparency { Colors = new ValueList<int>([1]), Tolerance = 500, Shrink = 99, Softness = -4, Opacity = 0 }.Clamped();
        Assert.Equal((100, PictureTransparency.MaxShrink, 0, 1), (t.Tolerance, t.Shrink, t.Softness, t.Opacity));
    }

    // ---- where a point of the original lies on the canvas ----

    [Theory]
    [InlineData(false, 0, 0.0)]
    [InlineData(true, 0, 0.0)]
    [InlineData(false, 1, 0.0)]
    [InlineData(true, 3, 0.0)]
    [InlineData(false, 2, 0.0)]
    [InlineData(false, 0, 17.5)]
    [InlineData(true, 1, -33.0)]
    [InlineData(false, 3, 120.0)]
    [InlineData(true, 2, 180.0)]
    public void The_canvas_and_the_original_map_into_each_other(bool mirror, int quarter, double angle)
    {
        var r = new PictureRecipe(80, 50) { Mirror = mirror, Quarter = quarter, Straighten = angle };
        foreach (var p in new[] { new Vec2(0, 0), new Vec2(80, 50), new Vec2(12.5, 33.25), new Vec2(79, 1) })
        {
            var back = PictureGeometry.CanvasToSource(r, PictureGeometry.SourceToCanvas(r, p));
            Assert.True(Vec2.Distance(p, back) < 1e-9, $"{p} came back as {back}");
        }
    }

    [Fact]
    public void A_quarter_turn_moves_the_top_left_corner_to_the_top_right_and_a_flip_to_the_other_side()
    {
        var turned = new PictureRecipe(80, 50) { Quarter = 1 };
        Assert.True(Vec2.Distance(new Vec2(50, 0), PictureGeometry.SourceToCanvas(turned, new Vec2(0, 0))) < 1e-9);
        Assert.True(Vec2.Distance(new Vec2(0, 80), PictureGeometry.SourceToCanvas(turned, new Vec2(80, 50))) < 1e-9);
        var flipped = new PictureRecipe(80, 50) { Mirror = true };
        Assert.True(Vec2.Distance(new Vec2(80, 0), PictureGeometry.SourceToCanvas(flipped, new Vec2(0, 0))) < 1e-9);
    }

    [Fact]
    public void A_free_rotation_turns_the_picture_about_the_middle_of_the_bigger_canvas()
    {
        var r = new PictureRecipe(100, 100) { Straighten = 90 };
        var (cw, ch) = r.CanvasSize;
        Assert.Equal((100, 100), (cw, ch));
        // a quarter turn clockwise: the top-left corner is top-right
        Assert.True(Vec2.Distance(new Vec2(100, 0), PictureGeometry.SourceToCanvas(r, new Vec2(0, 0))) < 1e-6);
        var r45 = new PictureRecipe(100, 100) { Straighten = 45 };
        var c = PictureGeometry.SourceToCanvas(r45, new Vec2(50, 50));
        Assert.True(Vec2.Distance(new Vec2(r45.CanvasSize.Width / 2.0, r45.CanvasSize.Height / 2.0), c) < 1e-9);   // the middle stays the middle
    }

    // ---- replacing the original ----

    [Fact]
    public void A_replacement_of_the_same_size_changes_nothing()
    {
        var r = new PictureRecipe(100, 80) { Crop = new RectI(10, 10, 50, 40), Transparency = new PictureTransparency { Cuts = new ValueList<CutShape>([Square(5, 5, 20)]) } };
        Assert.Equal(r, r.WithNewSource(100, 80));
    }

    [Fact]
    public void A_replacement_of_another_size_scales_the_crop_and_the_cut_outlines()
    {
        var r = new PictureRecipe(100, 80) { Crop = new RectI(10, 20, 50, 40), Transparency = new PictureTransparency { Cuts = new ValueList<CutShape>([Square(10, 10, 20)]) }, Adjust = PictureAdjust.None with { Contrast = 20 } };
        var bigger = r.WithNewSource(200, 160);
        Assert.Equal((200, 160), (bigger.SourceWidth, bigger.SourceHeight));
        Assert.Equal(new RectI(20, 40, 100, 80), bigger.Crop);
        Assert.Equal(new Vec2(20, 20), bigger.Transparency.Cuts[0].Points[0]);
        Assert.Equal(new Vec2(60, 60), bigger.Transparency.Cuts[0].Points[2]);
        Assert.Equal(20, bigger.Adjust.Contrast);                                   // the other steps are kept
    }

    [Fact]
    public void A_replacement_with_another_shape_scales_each_direction_and_keeps_the_crop_inside_the_picture()
    {
        var r = new PictureRecipe(100, 100) { Quarter = 1, Crop = new RectI(40, 0, 60, 100) };
        var wide = r.WithNewSource(200, 100);   // turned a quarter: the canvas is 100 wide x 200 high
        var (w, h) = wide.CanvasSize;
        Assert.Equal((100, 200), (w, h));
        Assert.NotNull(wide.Crop);
        Assert.True(wide.Crop!.Value.Right <= w && wide.Crop.Value.Bottom <= h);
    }

    // ---- clicks on the see-through part ----

    private static (Document Doc, ImageObject Image) PictureWithMask()
    {
        var doc = new Document(PageSetup.FromInches(10, 10));
        string id = doc.Assets.Add([(byte)'M', (byte)'A', (byte)'S', (byte)'K', 1, 2, 3], "image/png");
        var image = new ImageObject(id, 100, 100, 4, 4) { Transform = Affine2.Translation(100, 100) };
        doc.Insert(doc.ActiveLayer, -1, image);
        return (doc, image);
    }

    [Fact]
    public void A_click_on_the_visible_part_selects_the_picture_and_a_click_on_the_see_through_part_does_not()
    {
        var (doc, image) = PictureWithMask();
        Assert.Same(image, HitTesting.HitTest(doc, new Vec2(125, 150), 2));    // left half visible
        Assert.Null(HitTesting.HitTest(doc, new Vec2(175, 150), 2));            // right half: nothing there
        Assert.Null(HitTesting.HitPainted(doc, new Vec2(175, 150), 2));
        Assert.Same(image, HitTesting.HitPainted(doc, new Vec2(125, 150), 2));
    }

    [Fact]
    public void The_frame_of_a_cut_out_cannot_be_grabbed_where_it_shows_nothing()
    {
        var (doc, _) = PictureWithMask();
        Assert.Null(HitTesting.HitTest(doc, new Vec2(199.5, 150), 2));          // right edge of the frame: see-through
        Assert.Null(HitTesting.HitTest(doc, new Vec2(175, 99.5), 2));           // top edge, above the see-through half
    }

    [Fact]
    public void A_picture_without_see_through_parts_still_takes_clicks_anywhere_in_its_frame()
    {
        var doc = new Document(PageSetup.FromInches(10, 10));
        string id = doc.Assets.Add([1, 2, 3, 4, 5], "image/png");
        var image = new ImageObject(id, 100, 100, 4, 4) { Transform = Affine2.Translation(100, 100) };
        doc.Insert(doc.ActiveLayer, -1, image);
        Assert.Same(image, HitTesting.HitTest(doc, new Vec2(190, 190), 2));
    }

    [Fact]
    public void A_moved_and_scaled_cut_out_is_picked_where_it_shows()
    {
        var (doc, image) = PictureWithMask();
        image.Transform = Affine2.Scale(2, 2);   // 200 x 200 from the origin: visible x 0..100
        Assert.Same(image, HitTesting.HitTest(doc, new Vec2(50, 100), 2));
        Assert.Null(HitTesting.HitTest(doc, new Vec2(150, 100), 2));
    }

    [Fact]
    public void A_replaced_picture_gets_its_mask_worked_out_again()
    {
        var (doc, image) = PictureWithMask();
        Assert.Null(HitTesting.HitTest(doc, new Vec2(175, 150), 2));
        image.AssetId = doc.Assets.Add([1, 2, 3, 4, 5, 6], "image/png");          // no mask now: the whole frame
        Assert.Same(image, HitTesting.HitTest(doc, new Vec2(175, 150), 2));
    }
}
