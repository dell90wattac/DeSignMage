using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>The recipe geometry of the Picture Editor: canvas, straighten angle, inscribed crop, where the edited picture sits on the page.</summary>
public class PictureGeometryTests
{
    [Fact]
    public void The_canvas_is_the_picture_when_it_is_not_turned()
    {
        Assert.Equal((400, 300), PictureGeometry.CanvasSize(400, 300, 0));
        Assert.Equal((400, 300), PictureGeometry.CanvasSize(400, 300, 1e-9));
    }

    [Theory]
    [InlineData(90, 300, 400)]
    [InlineData(-90, 300, 400)]
    [InlineData(180, 400, 300)]
    public void Turning_by_a_right_angle_adds_no_pixels(double degrees, int w, int h) =>
        Assert.Equal((w, h), PictureGeometry.CanvasSize(400, 300, degrees));

    [Fact]
    public void A_turned_canvas_holds_the_whole_turned_picture()
    {
        var (w, h) = PictureGeometry.CanvasSize(100, 100, 45);
        Assert.Equal((142, 142), (w, h));            // 100 * sqrt(2) = 141.4, rounded up
        var (w2, h2) = PictureGeometry.CanvasSize(400, 300, 10);
        Assert.True(w2 > 400 && h2 > 300);
        Assert.Equal(PictureGeometry.CanvasSize(400, 300, 10), PictureGeometry.CanvasSize(400, 300, -10));
    }

    [Fact]
    public void A_line_that_slopes_down_to_the_right_turns_the_picture_counter_clockwise()
    {
        double a = PictureGeometry.StraightenFromLine(new Vec2(0, 0), new Vec2(100, 8.7489), 0);   // 5 degrees clockwise on screen
        Assert.Equal(-5, a, 3);
        double up = PictureGeometry.StraightenFromLine(new Vec2(0, 100), new Vec2(100, 91.2511), 0);
        Assert.Equal(5, up, 3);
    }

    [Fact]
    public void A_nearly_upright_line_is_made_plumb_and_the_angle_adds_to_the_current_one()
    {
        double a = PictureGeometry.StraightenFromLine(new Vec2(0, 0), new Vec2(8.7489, 100), 0);   // leans 5 degrees from upright
        Assert.Equal(5, a, 3);
        double b = PictureGeometry.StraightenFromLine(new Vec2(0, 0), new Vec2(100, 0), 3);          // already level on screen: nothing to add
        Assert.Equal(3, b, 9);
        double c = PictureGeometry.StraightenFromLine(new Vec2(0, 0), new Vec2(100, 17.6327), 2);   // 10 degrees more on top of 2
        Assert.Equal(-8, c, 3);
    }

    [Fact]
    public void The_angle_wraps_around_at_180_degrees_and_a_dot_changes_nothing()
    {
        Assert.Equal(78.6598, PictureGeometry.StraightenFromLine(new Vec2(0, 0), new Vec2(100, -80), 40), 3);   // 38.66 more on top of 40: free rotation goes further than 45
        Assert.Equal(-175, PictureGeometry.StraightenFromLine(new Vec2(0, 0), new Vec2(100, -17.6327), 175), 2);       // 10 more than 175 wraps to -175
        Assert.Equal(7, PictureGeometry.StraightenFromLine(new Vec2(5, 5), new Vec2(5, 5), 7), 9);
    }

    [Theory]
    [InlineData(190, -170)]
    [InlineData(-190, 170)]
    [InlineData(360, 0)]
    [InlineData(45, 45)]
    [InlineData(180, 180)]
    public void Angles_are_wrapped_into_minus_180_to_180(double degrees, double expected) =>
        Assert.Equal(expected, PictureGeometry.WrapAngle(degrees), 9);

    [Fact]
    public void A_picture_turned_a_quarter_has_no_corners_to_remove_and_one_turned_a_bit_more_only_the_rest_counts()
    {
        Assert.Equal(new RectI(0, 0, 300, 400), PictureGeometry.InscribedCrop(400, 300, 90));
        var crop = PictureGeometry.InscribedCrop(400, 300, 100);          // 10 degrees beyond a quarter: the picture stands upright, 300 x 400
        var (cw, ch) = PictureGeometry.CanvasSize(400, 300, 100);
        Assert.True(crop.Right <= cw && crop.Bottom <= ch && crop.Height > crop.Width);   // proportions of the upright picture (taller than wide)
        Assert.Equal(300.0 / 400.0, (double)crop.Width / crop.Height, 2);
    }

    [Theory]
    [InlineData(400, 300, 10)]
    [InlineData(400, 300, -3.5)]
    [InlineData(200, 600, 25)]
    [InlineData(100, 100, 45)]
    public void The_inscribed_crop_has_no_empty_corner_and_the_proportions_of_the_picture(int w, int h, double degrees)
    {
        var crop = PictureGeometry.InscribedCrop(w, h, degrees);
        var (cw, ch) = PictureGeometry.CanvasSize(w, h, degrees);
        Assert.True(crop.X >= 0 && crop.Y >= 0 && crop.Right <= cw && crop.Bottom <= ch);
        // every corner of the kept rectangle lies inside the turned picture (a small tolerance for whole pixels)
        double a = degrees * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        foreach (var (x, y) in new[] { (crop.X, crop.Y), (crop.Right, crop.Y), (crop.Right, crop.Bottom), (crop.X, crop.Bottom) })
        {
            double dx = x - cw / 2.0, dy = y - ch / 2.0;
            double u = dx * c + dy * s, v = -dx * s + dy * c;       // back into the picture's own axes
            Assert.True(Math.Abs(u) <= w / 2.0 + 1.0 && Math.Abs(v) <= h / 2.0 + 1.0, $"corner ({x},{y}) is outside the turned picture");
        }
        Assert.Equal((double)w / h, (double)crop.Width / crop.Height, 1);
        Assert.True(crop.Width > w * 0.4);
    }

    [Fact]
    public void Without_a_turn_the_inscribed_crop_is_everything() =>
        Assert.Equal(new RectI(0, 0, 640, 480), PictureGeometry.InscribedCrop(640, 480, 0));

    // ---- the frame on the page ----

    [Fact]
    public void A_crop_keeps_the_kept_pixels_exactly_where_they_were_on_the_page()
    {
        var recipe = new PictureRecipe(400, 300) { Crop = new RectI(100, 50, 200, 100) };
        var baseT = Affine2.Translation(10, 20);
        var frame = PictureGeometry.FrameFor(recipe, 200, 150, baseT);    // half a point per pixel

        Assert.Equal((100.0, 50.0), (frame.Width, frame.Height));
        Assert.Equal(new Vec2(60, 45), frame.Transform.Transform(Vec2.Zero));            // source pixel (100, 50) = (50, 25) pt from the frame corner, plus (10, 20)
        Assert.Equal(new Vec2(160, 95), frame.Transform.Transform(new Vec2(frame.Width, frame.Height)));
    }

    [Fact]
    public void Straightening_alone_keeps_the_picture_centered_on_the_page()
    {
        var recipe = new PictureRecipe(400, 300) { Straighten = 10 };
        var frame = PictureGeometry.FrameFor(recipe, 200, 150, Affine2.Translation(10, 20));
        var center = frame.Transform.Transform(new Vec2(frame.Width / 2, frame.Height / 2));
        Assert.Equal(110, center.X, 6);                                    // the original frame's center: (10 + 100, 20 + 75)
        Assert.Equal(95, center.Y, 6);
        Assert.True(frame.Width > 200 && frame.Height > 150);              // the canvas is bigger than the picture
    }

    [Fact]
    public void The_dpi_does_not_change_with_a_recipe()
    {
        var doc = NewDoc();
        var recipe = new PictureRecipe(600, 300) { Straighten = 7, Crop = new RectI(40, 30, 300, 200) };
        var baseT = Affine2.Translation(100, 100);
        var frame = PictureGeometry.FrameFor(recipe, 144, 72, baseT);                // 600 px over 2 inches = 300 dpi
        var image = new ImageObject("r", frame.Width, frame.Height, recipe.EffectiveCrop.Width, recipe.EffectiveCrop.Height) { Transform = frame.Transform };
        doc.Insert(doc.ActiveLayer, -1, image);
        Assert.Equal(300, image.EffectiveDpiX, 6);
        Assert.Equal(300, image.EffectiveDpiY, 6);
    }

    [Fact]
    public void The_unedited_frame_is_found_again_from_the_edited_one_even_after_it_was_moved_and_scaled()
    {
        var recipe = new PictureRecipe(400, 300) { Straighten = -4, Crop = new RectI(30, 20, 250, 180) };
        var baseT = Affine2.Scale(1.5, 1.5) * Affine2.Rotation(0.3) * Affine2.Translation(70, 40);
        var frame = PictureGeometry.FrameFor(recipe, 200, 150, baseT);
        var res = recipe.EffectiveCrop;

        var found = PictureGeometry.BaseFrameFor(recipe, frame.Width, frame.Height, res.Width, res.Height, frame.Transform);
        Assert.True(found.Transform.ApproximatelyEquals(baseT, 1e-9));
        Assert.Equal((200, 150), (found.Width, found.Height), new FramePrecision());

        // the user moved the edited picture by (30, -10) and then the unedited frame moved with it
        var moved = frame.Transform * Affine2.Translation(30, -10);
        var foundMoved = PictureGeometry.BaseFrameFor(recipe, frame.Width, frame.Height, res.Width, res.Height, moved);
        Assert.True(foundMoved.Transform.ApproximatelyEquals(baseT * Affine2.Translation(30, -10), 1e-9));
    }

    private sealed class FramePrecision : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) a, (double, double) b) => Math.Abs(a.Item1 - b.Item1) < 1e-9 && Math.Abs(a.Item2 - b.Item2) < 1e-9;
        public int GetHashCode((double, double) v) => 0;
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(1, true)]
    public void The_unedited_frame_is_found_again_after_quarter_turns_and_a_free_angle(int quarter, bool mirror)
    {
        var recipe = new PictureRecipe(400, 300) { Quarter = quarter, Mirror = mirror, Straighten = 12, Crop = new RectI(30, 20, 150, 120) };
        var baseT = Affine2.Scale(1.5, 1.5) * Affine2.Rotation(0.3) * Affine2.Translation(70, 40);
        var frame = PictureGeometry.FrameFor(recipe, 200, 150, baseT);
        var res = recipe.EffectiveCrop;
        var found = PictureGeometry.BaseFrameFor(recipe, frame.Width, frame.Height, res.Width, res.Height, frame.Transform);
        Assert.True(found.Transform.ApproximatelyEquals(baseT, 1e-9), "the transform of the unedited frame");
        Assert.Equal(200, found.Width, 9);
        Assert.Equal(150, found.Height, 9);
    }

    [Fact]
    public void A_quarter_turn_swaps_the_frame_around_its_center_and_keeps_the_dpi()
    {
        var recipe = new PictureRecipe(400, 300) { Quarter = 1 };
        var frame = PictureGeometry.FrameFor(recipe, 200, 150, Affine2.Translation(10, 20));
        Assert.Equal((150.0, 200.0), (frame.Width, frame.Height));
        var center = frame.Transform.Transform(new Vec2(frame.Width / 2, frame.Height / 2));
        Assert.Equal(110, center.X, 9);                  // the same center as the unturned frame: (10 + 100, 20 + 75)
        Assert.Equal(95, center.Y, 9);
        Assert.Equal(0.5, frame.Width / recipe.CanvasSize.Width, 9);      // half a point per pixel as before
    }
}

public class PictureRecipeTests
{
    [Fact]
    public void A_new_recipe_changes_nothing()
    {
        var r = new PictureRecipe(640, 480);
        Assert.True(r.IsIdentity);
        Assert.Equal((640, 480), r.CanvasSize);
        Assert.Equal(new RectI(0, 0, 640, 480), r.EffectiveCrop);
        Assert.Equal((640, 480), r.ResultSize);
    }

    [Fact]
    public void Any_step_makes_it_a_recipe()
    {
        var r = new PictureRecipe(640, 480);
        Assert.False((r with { Straighten = 0.5 }).IsIdentity);
        Assert.False((r with { Crop = new RectI(1, 1, 10, 10) }).IsIdentity);
        Assert.False((r with { Adjust = PictureAdjust.None with { Contrast = 5 } }).IsIdentity);
    }

    [Fact]
    public void A_crop_of_everything_is_no_crop_and_a_crop_is_kept_inside_the_canvas()
    {
        var r = new PictureRecipe(640, 480) { Crop = new RectI(0, 0, 640, 480) };
        Assert.Null(r.Normalized().Crop);
        Assert.True(r.Normalized().IsIdentity);

        var outside = new PictureRecipe(640, 480) { Crop = new RectI(600, 400, 500, 500) };
        Assert.Equal(new RectI(140, 0, 500, 480), outside.EffectiveCrop);   // moved in and shrunk to fit, never larger than the canvas
        var tiny = new PictureRecipe(640, 480) { Crop = new RectI(10, 10, 0, -3) };
        Assert.Equal((1, 1), (tiny.EffectiveCrop.Width, tiny.EffectiveCrop.Height));
    }

    [Fact]
    public void The_result_size_is_the_crop_in_the_turned_canvas()
    {
        var r = new PictureRecipe(400, 300) { Straighten = 5, Crop = new RectI(10, 10, 300, 200) };
        Assert.Equal((300, 200), r.ResultSize);
        var whole = new PictureRecipe(400, 300) { Straighten = 5 };
        Assert.Equal(whole.CanvasSize, whole.ResultSize);
    }

    [Fact]
    public void Recipes_compare_by_value()
    {
        var a = new PictureRecipe(10, 10) { Straighten = 2, Crop = new RectI(1, 2, 3, 4), Adjust = PictureAdjust.None with { Hue = 10 } };
        var b = new PictureRecipe(10, 10) { Straighten = 2, Crop = new RectI(1, 2, 3, 4), Adjust = PictureAdjust.None with { Hue = 10 } };
        Assert.Equal(a, b);
        Assert.NotEqual(a, b with { Adjust = PictureAdjust.None });
    }
}

public class AdjustMathTests
{
    private static PictureAdjust A(Func<PictureAdjust, PictureAdjust> change) => change(PictureAdjust.None);

    [Fact]
    public void Neutral_values_leave_every_gray_alone()
    {
        Assert.True(PictureAdjust.None.IsIdentity);
        for (int i = 0; i < 256; i++)
            foreach (int c in new[] { 0, 1, 2 }) Assert.Equal(i, AdjustMath.Curve(PictureAdjust.None, c)[i]);
        Assert.Equal((10, 120, 250), AdjustMath.Apply(PictureAdjust.None, 10, 120, 250));
    }

    [Fact]
    public void Brightness_adds_to_every_value_and_stops_at_black_and_white()
    {
        var curve = AdjustMath.Curve(A(a => a with { Brightness = 20 }), 0);
        Assert.Equal(151, curve[100]);                       // 20 % of 255 = 51
        Assert.Equal(255, curve[240]);
        var dark = AdjustMath.Curve(A(a => a with { Brightness = -20 }), 0);
        Assert.Equal(49, dark[100]);
        Assert.Equal(0, dark[30]);
    }

    [Fact]
    public void Contrast_spreads_values_about_the_middle_gray()
    {
        var more = AdjustMath.Curve(A(a => a with { Contrast = 50 }), 1);
        Assert.InRange(more[128], 127, 129);                 // the middle stays
        Assert.True(more[64] < 64 && more[192] > 192);
        var flat = AdjustMath.Curve(A(a => a with { Contrast = -100 }), 1);
        Assert.All(new[] { 0, 64, 200, 255 }, v => Assert.InRange(flat[v], 127, 128));
        var less = AdjustMath.Curve(A(a => a with { Contrast = -50 }), 1);
        Assert.True(less[64] > 64 && less[192] < 192);
    }

    [Fact]
    public void Gamma_above_one_brightens_the_mid_tones_but_not_black_or_white()
    {
        var curve = AdjustMath.Curve(A(a => a with { Gamma = 2 }), 2);
        Assert.Equal(0, curve[0]);
        Assert.Equal(255, curve[255]);
        Assert.InRange(curve[64], 127, 129);                 // 0.25 ^ (1 / 2) = 0.5
        var darker = AdjustMath.Curve(A(a => a with { Gamma = 0.5 }), 2);
        Assert.InRange(darker[128], 62, 66);                 // 0.5 ^ 2 = 0.25
    }

    [Fact]
    public void Levels_stretch_the_range_between_the_black_and_white_points()
    {
        var curve = AdjustMath.Curve(A(a => a with { LowR = 50, HighR = 200 }), 0);
        Assert.Equal(0, curve[50]);
        Assert.Equal(0, curve[10]);
        Assert.Equal(255, curve[200]);
        Assert.Equal(255, curve[250]);
        Assert.InRange(curve[125], 127, 128);
        Assert.Equal(100, AdjustMath.Curve(A(a => a with { LowR = 50, HighR = 200 }), 1)[100]);   // the other channels are not touched
    }

    [Fact]
    public void Color_balance_shifts_the_mid_tones_of_one_channel_only()
    {
        var a = A(x => x with { BalanceR = 100 });
        Assert.Equal(0, AdjustMath.Curve(a, 0)[0]);
        Assert.Equal(255, AdjustMath.Curve(a, 0)[255]);
        Assert.InRange(AdjustMath.Curve(a, 0)[128], 190, 194);            // +25 % at the middle
        Assert.Equal(128, AdjustMath.Curve(a, 1)[128]);
        var negative = AdjustMath.Curve(A(x => x with { BalanceB = -100 }), 2);
        Assert.InRange(negative[128], 62, 66);
    }

    [Fact]
    public void Saturation_minus_100_gives_gray_and_the_matrix_is_neutral_at_zero()
    {
        var gray = AdjustMath.Apply(A(a => a with { Saturation = -100 }), 200, 40, 40);
        Assert.Equal(gray.R, gray.G);
        Assert.Equal(gray.G, gray.B);
        Assert.InRange(gray.R, 73, 75);                                    // the luma of (200, 40, 40): 0.213 * 200 + 0.715 * 40 + 0.072 * 40
        var m = AdjustMath.ColorMatrix(PictureAdjust.None);
        Assert.Equal([1, 0, 0, 0, 1, 0, 0, 0, 1], m.Select(v => Math.Round(v, 9)).ToArray());
    }

    [Fact]
    public void More_saturation_pushes_colors_away_from_gray_and_keeps_grays()
    {
        var more = AdjustMath.Apply(A(a => a with { Saturation = 100 }), 150, 100, 100);
        Assert.True(more.R > 150 && more.G < 100);
        Assert.Equal((128, 128, 128), AdjustMath.Apply(A(a => a with { Saturation = 100 }), 128, 128, 128));
    }

    [Fact]
    public void Hue_turns_the_colors_and_keeps_grays_and_a_turn_of_360_is_nothing()
    {
        var gray = AdjustMath.Apply(A(a => a with { Hue = 90 }), 100, 100, 100);
        Assert.InRange(gray.R, 99, 101); Assert.InRange(gray.G, 99, 101); Assert.InRange(gray.B, 99, 101);

        var red = AdjustMath.Apply(A(a => a with { Hue = 180 }), 255, 0, 0);
        Assert.Equal(0, red.R);
        Assert.InRange(red.G, 105, 112);
        Assert.InRange(red.B, 105, 112);                                   // red turned half way round is a blue-green

        var full = AdjustMath.Apply(A(a => a with { Hue = 180 }), 255, 0, 0);
        Assert.Equal(full, AdjustMath.Apply(A(a => a with { Hue = -180 }), 255, 0, 0));
    }

    [Fact]
    public void The_matrix_rows_of_a_hue_turn_each_add_up_to_one()
    {
        var m = AdjustMath.ColorMatrix(A(a => a with { Hue = 70, Saturation = 30 }));
        for (int row = 0; row < 3; row++) Assert.Equal(1, m[row * 3] + m[row * 3 + 1] + m[row * 3 + 2], 3);
    }

    [Fact]
    public void Auto_levels_cuts_the_empty_ends_of_each_channel_and_ignores_a_flat_one()
    {
        var hist = new[] { new long[256], new long[256], new long[256] };
        for (int v = 20; v <= 230; v++) { hist[0][v] = 100; hist[1][v] = 100; }     // red and green use 20..230
        hist[2][100] = 1000; hist[2][104] = 1000;                                   // blue has almost no range
        var levels = AdjustMath.AutoLevels(PictureAdjust.None with { Brightness = 12 }, hist);

        Assert.InRange(levels.LowR, 20, 24);
        Assert.InRange(levels.HighR, 226, 230);
        Assert.InRange(levels.LowG, 20, 24);
        Assert.Equal(0, levels.LowB);                                               // left alone
        Assert.Equal(255, levels.HighB);
        Assert.Equal(12, levels.Brightness);                                        // the rest of the adjustments is kept
    }

    [Fact]
    public void Auto_levels_on_a_picture_that_already_fills_the_range_changes_nothing()
    {
        var hist = new[] { new long[256], new long[256], new long[256] };
        for (int c = 0; c < 3; c++) for (int v = 0; v < 256; v++) hist[c][v] = 10;
        var levels = AdjustMath.AutoLevels(PictureAdjust.None, hist);
        Assert.InRange(levels.LowR, 0, 2);
        Assert.InRange(levels.HighR, 253, 255);
    }

    [Fact]
    public void An_empty_histogram_is_left_alone()
    {
        var hist = new[] { new long[256], new long[256], new long[256] };
        Assert.True(AdjustMath.AutoLevels(PictureAdjust.None, hist).IsIdentity);
    }
}

public class CropFrameTests
{
    private const int W = 400, H = 300;

    [Fact]
    public void Moving_stays_inside_the_canvas()
    {
        var start = new RectI(100, 100, 100, 50);
        Assert.Equal(new RectI(130, 90, 100, 50), CropFrame.Move(start, 30, -10, W, H));
        Assert.Equal(new RectI(0, 0, 100, 50), CropFrame.Move(start, -500, -500, W, H));
        Assert.Equal(new RectI(300, 250, 100, 50), CropFrame.Move(start, 500, 500, W, H));
    }

    [Fact]
    public void Dragging_a_corner_moves_that_corner_only()
    {
        var start = new RectI(100, 100, 100, 50);
        Assert.Equal(new RectI(100, 100, 150, 120), CropFrame.Resize(start, Handle.BottomRight, new Vec2(250, 220), W, H, false));
        Assert.Equal(new RectI(60, 70, 140, 80), CropFrame.Resize(start, Handle.TopLeft, new Vec2(60, 70), W, H, false));
    }

    [Fact]
    public void Dragging_an_edge_moves_that_edge_only_and_dragging_past_the_other_flips_the_frame()
    {
        var start = new RectI(100, 100, 100, 50);
        Assert.Equal(new RectI(100, 100, 150, 50), CropFrame.Resize(start, Handle.Right, new Vec2(250, 999), W, H, false));
        var flipped = CropFrame.Resize(start, Handle.Right, new Vec2(40, 0), W, H, false);
        Assert.Equal(new RectI(40, 100, 60, 50), flipped);
    }

    [Fact]
    public void The_frame_never_leaves_the_canvas_and_keeps_at_least_one_pixel()
    {
        var start = new RectI(100, 100, 100, 50);
        var big = CropFrame.Resize(start, Handle.BottomRight, new Vec2(9999, 9999), W, H, false);
        Assert.Equal(new RectI(100, 100, 300, 200), big);
        var thin = CropFrame.Resize(start, Handle.Left, new Vec2(200, 0), W, H, false);
        Assert.True(thin.Width >= 1);
    }

    [Fact]
    public void With_proportions_kept_a_corner_drag_keeps_the_opposite_corner_and_the_ratio()
    {
        var start = new RectI(100, 100, 200, 100);                      // 2 : 1
        var r = CropFrame.Resize(start, Handle.BottomRight, new Vec2(400, 160), W, H, keepProportions: true);
        Assert.Equal((100, 100), (r.X, r.Y));
        Assert.Equal(2.0, (double)r.Width / r.Height, 1);
        Assert.Equal(300, r.Width);                                     // the wider of the two asked-for sizes decides
        var up = CropFrame.Resize(start, Handle.TopLeft, new Vec2(40, 80), W, H, keepProportions: true);
        Assert.Equal(300, up.Right);                                    // the bottom-right corner stays at (300, 200)
        Assert.Equal(200, up.Bottom);
        Assert.Equal(2.0, (double)up.Width / up.Height, 1);
    }

    [Fact]
    public void With_proportions_kept_the_frame_is_shrunk_to_fit_the_canvas_not_squashed()
    {
        var start = new RectI(100, 100, 200, 100);
        var r = CropFrame.Resize(start, Handle.BottomRight, new Vec2(9999, 9999), W, H, keepProportions: true);
        Assert.True(r.Right <= W && r.Bottom <= H);
        Assert.Equal(2.0, (double)r.Width / r.Height, 1);
        Assert.Equal(300, r.Width);                                     // as wide as the canvas allows from x = 100
    }

    [Fact]
    public void With_proportions_kept_an_edge_drag_grows_the_other_side_about_the_middle()
    {
        var start = new RectI(100, 100, 200, 100);
        var r = CropFrame.Resize(start, Handle.Right, new Vec2(400, 150), W, H, keepProportions: true);
        Assert.Equal(100, r.X);
        Assert.Equal(300, r.Width);
        Assert.Equal(150, r.Height);
        Assert.Equal(150, r.Y + r.Height / 2.0, 1);                     // still centered across (the middle was y = 150)
    }

    [Fact]
    public void A_new_frame_is_drawn_between_two_points_in_either_direction()
    {
        Assert.Equal(new RectI(50, 60, 100, 40), CropFrame.Draw(new Vec2(50, 60), new Vec2(150, 100), W, H));
        Assert.Equal(new RectI(50, 60, 100, 40), CropFrame.Draw(new Vec2(150, 100), new Vec2(50, 60), W, H));
        Assert.Equal(new RectI(300, 250, 100, 50), CropFrame.Draw(new Vec2(300, 250), new Vec2(999, 999), W, H));
    }

    [Fact]
    public void A_handle_is_found_near_a_corner_or_edge_middle_and_corners_win()
    {
        var f = new RectI(100, 100, 200, 100);
        Assert.Equal(Handle.TopLeft, CropFrame.HandleAt(f, new Vec2(103, 98), 8));
        Assert.Equal(Handle.Right, CropFrame.HandleAt(f, new Vec2(299, 152), 8));
        Assert.Equal(Handle.Top, CropFrame.HandleAt(f, new Vec2(201, 100), 8));
        Assert.Null(CropFrame.HandleAt(f, new Vec2(200, 150), 8));
        Assert.Null(CropFrame.HandleAt(f, new Vec2(10, 10), 8));
    }
}

public class PictureCommandTests
{
    private readonly EditorSession _s = new(NewDoc());

    private ImageObject AddImage(string asset = "orig", int pw = 400, int ph = 300)
    {
        var img = new ImageObject(asset, 200, 150, pw, ph) { Transform = Affine2.Translation(50, 60) };
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, img));
        return img;
    }

    [Fact]
    public void Committing_a_recipe_swaps_pixels_original_recipe_and_frame_and_undo_puts_them_back()
    {
        var img = AddImage();
        var recipe = new PictureRecipe(400, 300) { Crop = new RectI(100, 50, 200, 100), Adjust = PictureAdjust.None with { Contrast = 10 } };
        var frame = PictureGeometry.FrameFor(recipe, 200, 150, img.Transform);
        var before = img.Transform;

        _s.CommitPicture(img, new ImagePixels("result", 200, 100), frame, "orig", recipe);

        Assert.Equal(("result", "orig", recipe), (img.AssetId, img.SourceAssetId, img.Recipe));
        Assert.Equal((200, 100), (img.PixelWidth, img.PixelHeight));
        Assert.Equal((100.0, 50.0), (img.Width, img.Height));
        Assert.Equal("Edit Picture", _s.History.UndoDescription);

        _s.Undo();
        Assert.Equal(("orig", null, null), (img.AssetId, img.SourceAssetId, (PictureRecipe?)img.Recipe));
        Assert.Equal((400, 300), (img.PixelWidth, img.PixelHeight));
        Assert.Equal((200.0, 150.0), (img.Width, img.Height));
        Assert.Equal(before, img.Transform);

        _s.Redo();
        Assert.Equal("result", img.AssetId);
        Assert.Equal(recipe, img.Recipe);
    }

    [Fact]
    public void Both_pictures_are_in_use_and_both_count_as_held_by_the_undo_step()
    {
        var img = AddImage();
        var recipe = new PictureRecipe(400, 300) { Crop = new RectI(0, 0, 100, 100) };
        _s.CommitPicture(img, new ImagePixels("result", 100, 100), new ImageFrame(50, 50, img.Transform), "orig", recipe);

        Assert.Equal(["orig", "result"], BitmapStats.ReferencedAssetIds(_s.Document).Order().ToArray());
        Assert.Equal(["orig", "result"], UndoMemory.AssetsOf(img).Order().ToArray());
        Assert.Equal(["orig", "result"], UndoMemory.AssetsOf(_s.History.UndoList[^1]).Order().ToArray());
    }

    [Fact]
    public void Copies_of_an_edited_picture_keep_the_original_and_the_recipe()
    {
        var img = AddImage();
        var recipe = new PictureRecipe(400, 300) { Straighten = 3 };
        _s.CommitPicture(img, new ImagePixels("result", 410, 310), new ImageFrame(205, 155, img.Transform), "orig", recipe);

        var copy = Assert.IsType<ImageObject>(img.Clone());
        Assert.Equal(("orig", recipe, "result"), (copy.SourceAssetId, copy.Recipe, copy.AssetId));
    }

    [Fact]
    public void An_image_menu_operation_on_an_edited_picture_ends_its_recipe_and_undo_brings_it_back()
    {
        var img = AddImage();
        var recipe = new PictureRecipe(400, 300) { Adjust = PictureAdjust.None with { Hue = 30 } };
        _s.CommitPicture(img, new ImagePixels("result", 400, 300), new ImageFrame(200, 150, img.Transform), "orig", recipe);

        _s.History.Execute(new ReplaceImageCommand("Rotate", img, new ImagePixels("rotated", 300, 400), new ImageFrame(150, 200, img.Transform)));

        Assert.Null(img.Recipe);
        Assert.Null(img.SourceAssetId);
        Assert.Equal("rotated", img.AssetId);
        _s.Undo();
        Assert.Equal(recipe, img.Recipe);
        Assert.Equal("orig", img.SourceAssetId);
        Assert.Equal("result", img.AssetId);
    }

    [Fact]
    public void Back_to_the_original_is_a_commit_without_a_recipe()
    {
        var img = AddImage();
        var recipe = new PictureRecipe(400, 300) { Crop = new RectI(0, 0, 100, 100) };
        var baseT = img.Transform;
        _s.CommitPicture(img, new ImagePixels("result", 100, 100), new ImageFrame(50, 50, baseT), "orig", recipe);

        _s.CommitPicture(img, new ImagePixels("orig", 400, 300), new ImageFrame(200, 150, baseT), null, null);

        Assert.Equal(("orig", null, null), (img.AssetId, img.SourceAssetId, (PictureRecipe?)img.Recipe));
        _s.Undo();
        Assert.Equal("result", img.AssetId);
    }

    // ---- double-click routing ----

    private readonly List<string> _events = [];

    private void Listen()
    {
        _s.PictureEditRequested += (_, r) => _events.Add("picture");
        _s.ContextMenuRequested += (_, r) => _events.Add("menu:" + r.Target.KindName);
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);
    }

    private void DoubleClick(double x, double y)
    {
        var p = new PointerInput(new Vec2(x, y), new Vec2(x, y), PointerButton.Left, ModifierKeys.None, 1);
        var d = p with { ClickCount = 2 };
        _s.PointerDown(p); _s.PointerUp(p);
        _s.PointerDown(d); _s.PointerUp(d);
    }

    [Fact]
    public void Double_click_on_a_picture_asks_for_the_Picture_Editor_and_not_for_a_menu()
    {
        Listen();
        var img = AddImage();
        DoubleClick(150, 130);

        Assert.Equal(["picture"], _events);
        Assert.Same(img, Assert.Single(_s.Selection.Items));
    }

    [Fact]
    public void Double_click_on_a_group_still_opens_its_menu_and_a_group_holding_a_picture_is_still_a_group()
    {
        Listen();
        var img = AddImage();
        var rect = Rect(400, 400, 50, 50);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, rect));
        _s.GroupObjects([img, rect]);

        DoubleClick(150, 130);

        Assert.Equal(["menu:Group"], _events);
    }

    [Fact]
    public void Double_click_on_a_shape_is_unchanged()
    {
        Listen();
        var rect = Rect(300, 300, 100, 100);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, rect));
        DoubleClick(350, 350);

        Assert.DoesNotContain("picture", _events);
        Assert.Equal("vector", _s.ActiveTool.Id);                 // as before: a single shape opens the Vector Edit tool
    }

    [Fact]
    public void Requesting_the_editor_selects_the_picture()
    {
        Listen();
        var a = AddImage("a");
        var b = new ImageObject("b", 50, 50, 10, 10) { Transform = Affine2.Translation(500, 500) };
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, b));
        _s.Selection.Set([a, b]);

        _s.RequestPictureEdit(b);

        Assert.Same(b, Assert.Single(_s.Selection.Items));
        Assert.Equal(["picture"], _events);
    }
}
