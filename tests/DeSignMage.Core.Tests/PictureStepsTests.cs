using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>The steps added to the recipe after crop and adjust: quarter turns and flips (orientation), the free angle, and the effects.</summary>
public class PictureOrientationTests
{
    private static PictureRecipe Recipe(RectI? crop = null, double angle = 0) => new PictureRecipe(400, 300) { Crop = crop, Straighten = angle }.Normalized();

    [Fact]
    public void A_quarter_turn_swaps_width_and_height_and_four_make_nothing()
    {
        var r = Recipe().RotatedRight();
        Assert.Equal(1, r.Quarter);
        Assert.Equal((300, 400), r.OrientedSize);
        Assert.Equal((300, 400), r.CanvasSize);
        Assert.True(Recipe().RotatedRight().RotatedRight().RotatedRight().RotatedRight().IsIdentity);
        Assert.True(Recipe().RotatedLeft().RotatedRight().IsIdentity);
        Assert.Equal(3, Recipe().RotatedLeft().Quarter);
    }

    [Fact]
    public void Flipping_twice_makes_nothing_and_both_flips_make_a_half_turn()
    {
        Assert.True(Recipe().FlippedHorizontally().FlippedHorizontally().IsIdentity);
        Assert.True(Recipe().FlippedVertically().FlippedVertically().IsIdentity);
        var both = Recipe().FlippedHorizontally().FlippedVertically();
        Assert.False(both.Mirror);
        Assert.Equal(2, both.Quarter);
    }

    [Fact]
    public void A_flip_after_a_turn_is_a_mirror_and_the_opposite_turn_the_flip_goes_the_other_way_round()
    {
        // turn right, then mirror left-right = mirror first, then turn left (the transpose)
        var r = Recipe().RotatedRight().FlippedHorizontally();
        Assert.True(r.Mirror);
        Assert.Equal(3, r.Quarter);
    }

    [Fact]
    public void A_crop_goes_along_with_every_turn_and_flip()
    {
        var crop = new RectI(100, 50, 200, 100);                        // on the 400 x 300 canvas
        Assert.Equal(new RectI(150, 100, 100, 200), Recipe(crop).RotatedRight().Crop);
        Assert.Equal(new RectI(50, 100, 100, 200), Recipe(crop).RotatedLeft().Crop);
        Assert.Equal(new RectI(100, 50, 200, 100), Recipe(crop).FlippedHorizontally().Crop);   // centered: same place
        Assert.Equal(new RectI(100, 150, 200, 100), Recipe(new RectI(100, 50, 200, 100)).FlippedVertically().Crop);
        Assert.Equal(new RectI(10, 20, 30, 40), Recipe(new RectI(10, 20, 30, 40)).FlippedHorizontally().FlippedHorizontally().Crop);
        var back = Recipe(new RectI(10, 20, 30, 40)).RotatedRight().RotatedLeft();
        Assert.Equal(new RectI(10, 20, 30, 40), back.Crop);
    }

    [Fact]
    public void A_flip_reverses_the_free_angle_and_a_quarter_turn_keeps_it()
    {
        Assert.Equal(-12, Recipe(angle: 12).FlippedHorizontally().Straighten, 9);
        Assert.Equal(-12, Recipe(angle: 12).FlippedVertically().Straighten, 9);
        Assert.Equal(12, Recipe(angle: 12).RotatedRight().Straighten, 9);
    }

    [Fact]
    public void The_original_orientation_is_found_again_with_the_crop_and_angle_following()
    {
        var r = Recipe(new RectI(100, 50, 200, 100), 7).RotatedRight().FlippedHorizontally().RotatedLeft().FlippedVertically();
        var back = r.InOriginalOrientation();
        Assert.False(back.Mirror);
        Assert.Equal(0, back.Quarter);
        Assert.Equal((400, 300), back.OrientedSize);
        Assert.Equal(back.CanvasSize, PictureGeometry.CanvasSize(400, 300, back.Straighten));
    }

    [Fact]
    public void A_recipe_with_only_a_turn_is_not_the_original()
    {
        Assert.False(new PictureRecipe(10, 10) { Quarter = 1 }.IsIdentity);
        Assert.False(new PictureRecipe(10, 10) { Mirror = true }.IsIdentity);
        Assert.Equal(1, new PictureRecipe(10, 10) { Quarter = 5 }.Normalized().Quarter);
        Assert.Equal(3, new PictureRecipe(10, 10) { Quarter = -1 }.Normalized().Quarter);
    }

    [Fact]
    public void Any_angle_between_minus_180_and_180_is_allowed_and_the_canvas_follows()
    {
        var r = new PictureRecipe(400, 300) { Straighten = 135 }.Normalized();
        Assert.Equal(135, r.Straighten);
        var (w, h) = r.CanvasSize;
        Assert.Equal(PictureGeometry.CanvasSize(400, 300, 45), (w, h));      // 135 turns the picture as 45 does, sizewise
        Assert.Equal(-170, new PictureRecipe(400, 300) { Straighten = 190 }.Normalized().Straighten, 9);
    }
}

public class PictureEffectsTests
{
    [Fact]
    public void No_effects_is_identity_and_each_one_alone_is_not()
    {
        Assert.True(PictureEffects.None.IsIdentity);
        Assert.False((PictureEffects.None with { NoiseReduction = 20 }).IsIdentity);
        Assert.False((PictureEffects.None with { Blur = 2 }).IsIdentity);
        Assert.False((PictureEffects.None with { Sharpen = 5 }).IsIdentity);
        Assert.False((PictureEffects.None with { Grayscale = true }).IsIdentity);
        Assert.False((PictureEffects.None with { Sepia = 5 }).IsIdentity);
        Assert.False((PictureEffects.None with { Invert = true }).IsIdentity);
        Assert.False((PictureEffects.None with { Threshold = 128 }).IsIdentity);
        Assert.False((PictureEffects.None with { Posterize = 4 }).IsIdentity);
        Assert.True(new PictureRecipe(10, 10).IsIdentity);
        Assert.False((new PictureRecipe(10, 10) { Effects = PictureEffects.None with { Invert = true } }).IsIdentity);
    }

    [Fact]
    public void Numbers_are_put_into_their_range()
    {
        var e = new PictureEffects { NoiseReduction = 500, Blur = 900, Sharpen = -4, Sepia = 101, Threshold = 300, Posterize = 99 }.Clamped();
        Assert.Equal((100, PictureEffects.MaxBlur, 0, 100, 255, PictureEffects.MaxPosterize), (e.NoiseReduction, e.Blur, e.Sharpen, e.Sepia, e.Threshold, e.Posterize));
        Assert.Equal(1, new PictureEffects { Posterize = 0 }.Clamped().Posterize);
    }

    [Fact]
    public void Invert_gives_the_negative_and_grayscale_the_lightness()
    {
        Assert.Equal((55, 155, 205), L(PictureEffects.None with { Invert = true }, 200, 100, 50));
        var (r, g, b) = L(PictureEffects.None with { Grayscale = true }, 200, 100, 50);
        Assert.Equal(r, g);
        Assert.Equal(g, b);
        Assert.InRange(r, 117, 119);        // 0.213 * 200 + 0.715 * 100 + 0.072 * 50 = 117.7
    }

    private static (int R, int G, int B) L(PictureEffects e, byte r, byte g, byte b)
    {
        var t = EffectMath.Look(e, r, g, b);
        return (t.R, t.G, t.B);
    }

    [Fact]
    public void Sepia_keeps_black_black_and_tints_the_lights_brown()
    {
        var e = PictureEffects.None with { Sepia = 100 };
        Assert.Equal((0, 0, 0), L(e, 0, 0, 0));
        var (r, g, b) = L(e, 200, 200, 200);
        Assert.True(r > g && g > b, $"{r} {g} {b}");
        Assert.Equal((200, 200, 200), L(PictureEffects.None with { Sepia = 0 }, 200, 200, 200));   // nothing at 0
        var half = L(PictureEffects.None with { Sepia = 50 }, 90, 120, 200);                      // half way between the original and the tone
        Assert.NotEqual((90, 120, 200), half);
    }

    [Fact]
    public void Black_and_white_splits_at_the_level_and_posterize_snaps_to_few_values()
    {
        var bw = PictureEffects.None with { Threshold = 128 };
        Assert.Equal((255, 255, 255), L(bw, 200, 200, 200));
        Assert.Equal((0, 0, 0), L(bw, 60, 60, 60));
        Assert.Equal((0, 0, 0), L(bw, 0, 0, 255));                  // blue is dark to the eye: 0.072 * 255 = 18
        var two = PictureEffects.None with { Posterize = 2 };
        Assert.Equal((0, 255, 255), L(two, 100, 200, 130));
        var four = PictureEffects.None with { Posterize = 4 };
        Assert.Equal((0, 85, 170), L(four, 20, 90, 160));          // 4 levels: 0, 85, 170, 255
        Assert.Equal(255, L(four, 255, 255, 255).R);
    }

    [Fact]
    public void Invert_comes_before_the_black_and_white_split()
    {
        var e = PictureEffects.None with { Invert = true, Threshold = 128 };
        Assert.Equal((255, 255, 255), L(e, 10, 10, 10));            // a dark pixel is light once inverted
    }

    [Fact]
    public void The_filters_and_the_looks_are_told_apart()
    {
        Assert.True(EffectMath.HasFilter(PictureEffects.None with { Blur = 1 }));
        Assert.False(EffectMath.HasLook(PictureEffects.None with { Blur = 1 }));
        Assert.True(EffectMath.HasLook(PictureEffects.None with { Posterize = 3 }));
        Assert.False(EffectMath.HasFilter(PictureEffects.None with { Posterize = 3 }));
    }
}

/// <summary>Image-menu Rotate, Flip and Invert on pictures that have an editing recipe go through the recipe; Flatten bakes the recipe in.</summary>
public class PictureRecipeMenuAndFlattenTests
{
    private readonly FakeImageService _svc = new();
    private readonly EditorSession _s;

    public PictureRecipeMenuAndFlattenTests() => _s = new EditorSession(NewDoc()) { ImageService = _svc };

    private ImageObject Plain()
    {
        string id = _svc.Register(_s.Document, 400, 300);
        var img = new ImageObject(id, 200, 150, 400, 300) { Transform = Affine2.Translation(50, 60) };
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, img));
        return img;
    }

    private ImageObject Edited(PictureRecipe recipe)
    {
        string original = _svc.Register(_s.Document, 400, 300);
        var frame = PictureGeometry.FrameFor(recipe, 200, 150, Affine2.Translation(50, 60));
        var (w, h) = recipe.ResultSize;
        string result = _svc.Register(_s.Document, w, h);
        var img = new ImageObject(result, frame.Width, frame.Height, w, h) { Transform = frame.Transform, SourceAssetId = original, Recipe = recipe };
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, img));
        return img;
    }

    private static PictureRecipe Cropped() =>
        new PictureRecipe(400, 300) { Crop = new RectI(100, 50, 200, 100), Adjust = PictureAdjust.None with { Contrast = 10 } };

    // ---- the Image menu operations ----

    [Fact]
    public void Rotating_an_edited_picture_changes_its_recipe_and_renders_it_again_from_the_original()
    {
        var img = Edited(Cropped());
        string original = img.SourceAssetId!, before = img.AssetId;
        _s.Selection.Set(img);

        Assert.Equal(1, _s.ApplyImageOp(new ImageOp.Rotate90(true), "Rotate 90 Degrees Clockwise"));

        Assert.Equal(1, img.Recipe!.Quarter);
        Assert.Equal(new RectI(150, 100, 100, 200), img.Recipe.Crop);
        Assert.Equal(original, img.SourceAssetId);                         // the original is kept
        Assert.Equal((original, img.Recipe), _svc.Recipes.Single());      // rendered from it, with the new recipe
        Assert.DoesNotContain(_svc.Calls, c => c.Asset == before);         // the finished picture was not rotated itself
        Assert.Equal((100, 200), (img.PixelWidth, img.PixelHeight));
        Assert.Equal((50.0, 100.0), (img.Width, img.Height));              // half a point per pixel, as before

        // the kept part turned about the middle of the whole picture: (150, 110) about (150, 135) a quarter clockwise is (175, 135)
        var center = img.Transform.Transform(new Vec2(img.Width / 2, img.Height / 2));
        Assert.Equal(175, center.X, 6);
        Assert.Equal(135, center.Y, 6);

        _s.Undo();
        Assert.Equal(0, img.Recipe!.Quarter);
        Assert.Equal(before, img.AssetId);
        Assert.Equal((200, 100), (img.PixelWidth, img.PixelHeight));
    }

    [Fact]
    public void Rotating_back_to_where_it_was_gives_the_picture_a_plain_original_again()
    {
        var recipe = new PictureRecipe(400, 300) { Quarter = 1 };
        var img = Edited(recipe);
        string original = img.SourceAssetId!;
        _s.Selection.Set(img);

        _s.ApplyImageOp(new ImageOp.Rotate90(false), "Rotate 90 Degrees Counter-clockwise");

        Assert.Null(img.Recipe);
        Assert.Null(img.SourceAssetId);
        Assert.Equal(original, img.AssetId);
        Assert.Empty(_svc.Recipes);                                        // nothing to render: it is the original
        Assert.Equal((400, 300), (img.PixelWidth, img.PixelHeight));
        Assert.Equal((200.0, 150.0), (img.Width, img.Height));
        Assert.True(img.Transform.ApproximatelyEquals(Affine2.Translation(50, 60), 1e-9));
    }

    [Fact]
    public void Flip_and_Invert_change_the_recipe_too_and_invert_twice_undoes_itself()
    {
        var img = Edited(Cropped());
        _s.Selection.Set(img);

        _s.ApplyImageOp(new ImageOp.Flip(true), "Flip Horizontal");
        Assert.True(img.Recipe!.Mirror);
        _s.ApplyImageOp(new ImageOp.Invert(), "Invert");
        Assert.True(img.Recipe.Effects.Invert);
        _s.ApplyImageOp(new ImageOp.Invert(), "Invert");
        Assert.False(img.Recipe.Effects.Invert);
        Assert.Equal(3, _svc.Recipes.Count);
        Assert.Equal(img.SourceAssetId, _svc.Recipes.Last().Source);
    }

    [Fact]
    public void A_plain_picture_is_still_rotated_on_its_pixels_and_no_original_is_kept()
    {
        var img = Plain();
        string before = img.AssetId;
        _s.Selection.Set(img);

        _s.ApplyImageOp(new ImageOp.Rotate90(true), "Rotate 90 Degrees Clockwise");

        Assert.Null(img.Recipe);
        Assert.Null(img.SourceAssetId);
        Assert.NotEqual(before, img.AssetId);
        Assert.Equal((before, new ImageOp.Rotate90(true)), _svc.Calls.Single());
        Assert.Empty(_svc.Recipes);
    }

    [Fact]
    public void Crop_and_Resample_still_work_on_the_finished_pixels_and_end_the_recipe()
    {
        var img = Edited(Cropped());
        _s.Selection.Set(img);
        Assert.True(_s.Resample(100, 50));
        Assert.Null(img.Recipe);
        Assert.Null(img.SourceAssetId);
        _s.Undo();
        Assert.NotNull(img.Recipe);                                       // undo brings the recipe back
    }

    [Fact]
    public void A_mixed_selection_rotates_each_picture_its_own_way_in_one_undo_step()
    {
        var plain = Plain();
        var edited = Edited(Cropped());
        _s.Selection.Set([plain, edited]);

        Assert.Equal(2, _s.ApplyImageOp(new ImageOp.Rotate90(true), "Rotate 90 Degrees Clockwise"));
        Assert.Null(plain.Recipe);
        Assert.Equal(1, edited.Recipe!.Quarter);
        _s.Undo();
        Assert.Equal((400, 300), (plain.PixelWidth, plain.PixelHeight));
        Assert.Equal(0, edited.Recipe!.Quarter);
    }

    // ---- Flatten ----

    [Fact]
    public void Flatten_is_offered_only_for_a_picture_that_has_a_recipe()
    {
        var plain = Plain();
        _s.Selection.Set(plain);
        Assert.False(_s.CanFlatten);
        Assert.Equal(0, _s.FlattenPictures());

        var edited = Edited(Cropped());
        _s.Selection.Set(edited);
        Assert.True(_s.CanFlatten);

        _s.Selection.Set([plain, edited]);
        Assert.True(_s.CanFlatten);
        Assert.Same(edited, Assert.Single(_s.FlattenablePictures()));

        _s.Selection.Clear();
        Assert.False(_s.CanFlatten);
    }

    [Fact]
    public void Flatten_makes_the_finished_pixels_the_whole_picture_and_lets_go_of_the_original_and_the_recipe()
    {
        var recipe = Cropped();
        var img = Edited(recipe);
        string result = img.AssetId, original = img.SourceAssetId!;
        var transform = img.Transform;
        _s.Selection.Set(img);

        Assert.Equal(1, _s.FlattenPictures());

        Assert.Equal(result, img.AssetId);                                // the finished picture, as it was
        Assert.Null(img.SourceAssetId);
        Assert.Null(img.Recipe);
        Assert.Equal((200, 100), (img.PixelWidth, img.PixelHeight));
        Assert.Equal((100.0, 50.0), (img.Width, img.Height));
        Assert.Equal(transform, img.Transform);                           // nothing moves
        Assert.Equal("Flatten Picture", _s.History.UndoDescription);
        Assert.False(_s.CanFlatten);
        Assert.DoesNotContain(original, BitmapStats.ReferencedAssetIds(_s.Document));   // the original is no longer in the file
        Assert.Contains(original, UndoMemory.AssetsOf(_s.History.UndoList[^1]));       // but undo still holds it, and it counts in the undo memory

        _s.Undo();
        Assert.Equal(original, img.SourceAssetId);
        Assert.Equal(recipe, img.Recipe);
        Assert.Equal(result, img.AssetId);
        _s.Redo();
        Assert.Null(img.Recipe);
    }

    [Fact]
    public void Flatten_on_several_pictures_is_one_undo_step_and_skips_the_plain_ones()
    {
        var a = Edited(Cropped());
        var plain = Plain();
        var b = Edited(new PictureRecipe(400, 300) { Quarter = 1 });
        _s.Selection.Set([a, plain, b]);
        int steps = _s.History.UndoList.Count;

        Assert.Equal(2, _s.FlattenPictures());

        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        Assert.Equal("Flatten Pictures", _s.History.UndoDescription);
        Assert.Null(a.Recipe);
        Assert.Null(b.Recipe);
        _s.Undo();
        Assert.NotNull(a.Recipe);
        Assert.NotNull(b.Recipe);
        Assert.Equal(steps, _s.History.UndoList.Count);
    }

    [Fact]
    public void Flatten_reaches_pictures_inside_a_selected_group()
    {
        var img = Edited(Cropped());
        var other = Plain();
        _s.Selection.Set([img, other]);
        var group = _s.GroupSelection()!;
        Assert.Same(group, Assert.Single(_s.Selection.Items));
        Assert.True(_s.CanFlatten);
        _s.FlattenPictures();
        Assert.Null(img.Recipe);
        Assert.Contains(img, group.Children);
    }
}
