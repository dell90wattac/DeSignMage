using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>The numbers behind Image ▸ Resample: pixels and dpi change, the printed size does not.</summary>
public class ResampleSizingTests
{
    // 600 x 300 pixels printed 2 x 1 inch = 300 dpi.
    private static ResampleSizing Sizing() => new(600, 300, 2, 1);

    [Fact]
    public void It_starts_at_the_current_size_and_dpi()
    {
        var s = Sizing();
        Assert.Equal((600, 300), (s.Width, s.Height));
        Assert.Equal(300, s.DpiX, 9);
        Assert.Equal(300, s.DpiY, 9);
        Assert.False(s.IsChanged);
        Assert.False(s.CanApply);
        Assert.Null(s.Problem);
    }

    [Fact]
    public void Setting_the_width_with_the_lock_on_keeps_the_proportions_and_updates_the_dpi()
    {
        var s = Sizing();
        s.SetWidth(1200);
        Assert.Equal((1200, 600), (s.Width, s.Height));
        Assert.Equal(600, s.DpiX, 9);
        Assert.True(s.CanApply);

        s.SetWidth(150);
        Assert.Equal((150, 75), (s.Width, s.Height));
        Assert.Equal(75, s.DpiX, 9);
    }

    [Fact]
    public void Setting_the_height_with_the_lock_on_sets_the_width()
    {
        var s = Sizing();
        s.SetHeight(100);
        Assert.Equal((200, 100), (s.Width, s.Height));
        Assert.Equal(100, s.DpiY, 9);
    }

    [Fact]
    public void With_the_lock_off_width_and_height_are_independent_and_the_two_dpis_can_differ()
    {
        var s = Sizing();
        s.LockAspect = false;
        s.SetWidth(1200);
        s.SetHeight(300);
        Assert.Equal((1200, 300), (s.Width, s.Height));
        Assert.Equal(600, s.DpiX, 9);
        Assert.Equal(300, s.DpiY, 9);
    }

    [Fact]
    public void Setting_the_dpi_gives_both_sides_that_many_pixels_per_printed_inch()
    {
        var s = Sizing();
        s.SetDpi(150);
        Assert.Equal((300, 150), (s.Width, s.Height));
        s.SetDpi(72);
        Assert.Equal((144, 72), (s.Width, s.Height));
        s.SetDpi(600);
        Assert.Equal((1200, 600), (s.Width, s.Height));
        Assert.Equal(600, s.DpiX, 9);
        Assert.Equal(600, s.DpiY, 9);
    }

    [Fact]
    public void A_dpi_rounds_to_whole_pixels_and_never_below_one()
    {
        var s = new ResampleSizing(10, 10, 3, 3);
        s.SetDpi(100.4);                                     // 301.2 pixels
        Assert.Equal((301, 301), (s.Width, s.Height));
        s.SetDpi(0.01);
        Assert.Equal((1, 1), (s.Width, s.Height));
    }

    [Fact]
    public void A_dpi_evens_out_non_square_pixels()
    {
        var s = new ResampleSizing(600, 150, 2, 1);          // 300 dpi across, 150 down
        Assert.Equal(300, s.CurrentDpiX, 9);
        Assert.Equal(150, s.CurrentDpiY, 9);
        s.SetDpi(200);
        Assert.Equal((400, 200), (s.Width, s.Height));
        Assert.Equal(s.DpiX, s.DpiY, 9);
    }

    [Fact]
    public void Nonsense_input_changes_nothing_or_is_clamped()
    {
        var s = Sizing();
        s.SetDpi(0); s.SetDpi(-5); s.SetDpi(double.NaN); s.SetDpi(double.PositiveInfinity);
        Assert.Equal((600, 300), (s.Width, s.Height));
        s.SetWidth(0);
        Assert.Equal((1, 1), (s.Width, s.Height));            // at least one pixel
        s.SetWidth(int.MaxValue);                             // must not overflow
        Assert.True(s.Width == int.MaxValue && s.Height >= 1);
    }

    [Fact]
    public void Too_large_a_picture_is_a_problem_not_a_crash()
    {
        var s = Sizing();
        s.SetWidth(40_000);
        Assert.NotNull(s.Problem);
        Assert.False(s.CanApply);
        s.SetWidth(20_000);                                  // 20,000 x 10,000 = 200 million pixels
        Assert.NotNull(s.Problem);
        s.SetWidth(10_000);                                  // 10,000 x 5,000 = 50 million
        Assert.Null(s.Problem);
        Assert.True(s.CanApply);
        Assert.Equal(10_000 * 5_000 * 4.0 / 1048576.0, s.Megabytes, 6);
    }

    [Fact]
    public void Reset_goes_back_to_the_current_pixels()
    {
        var s = Sizing();
        s.SetDpi(72);
        s.Reset();
        Assert.Equal((600, 300), (s.Width, s.Height));
        Assert.False(s.IsChanged);
    }

    [Fact]
    public void The_printed_size_includes_the_objects_scale_and_turn()
    {
        var image = new ImageObject("a", 144, 72, 600, 300)    // frame 2 x 1 in
        {
            Transform = Affine2.Scale(2, 2) * Affine2.Rotation(Math.PI / 6) * Affine2.Translation(100, 100),
        };
        var doc = NewDoc();
        doc.Insert(doc.ActiveLayer, -1, image);

        var (w, h) = ImageSizing.PrintedInches(image);
        Assert.Equal(4, w, 6);
        Assert.Equal(2, h, 6);
        var s = ResampleSizing.For(image);
        Assert.Equal(150, s.CurrentDpiX, 6);                  // 600 px over 4 in
        Assert.Equal(150, image.EffectiveDpiX, 6);
    }

    [Fact]
    public void Info_text_is_plain_numbers()
    {
        var doc = NewDoc();
        var image = new ImageObject("a", 144, 72, 600, 300);
        doc.Insert(doc.ActiveLayer, -1, image);
        Assert.Equal("Bitmap: 600 x 300 px, 300 dpi", ImageSizing.InfoText(image));

        image.Transform = Affine2.Scale(1.5, 1.5);
        Assert.Equal("Bitmap: 600 x 300 px, 200 dpi", ImageSizing.InfoText(image));

        image.Transform = Affine2.Scale(1.5, 3);
        Assert.Equal("Bitmap: 600 x 300 px, 200 x 100 dpi", ImageSizing.InfoText(image));

        Assert.Null(ImageSizing.InfoText(new ImageObject("b", 10, 10)));   // pixel size not known yet
        Assert.DoesNotContain("low", ImageSizing.InfoText(image)!, StringComparison.OrdinalIgnoreCase);
    }
}

public class ResampleCommandTests
{
    private readonly EditorSession _s = new(NewDoc());
    private readonly FakeImageService _svc = new();

    public ResampleCommandTests() => _s.ImageService = _svc;

    private ImageObject AddImage(double x, double y, double w, double h, int pw, int ph)
    {
        var img = new ImageObject(_svc.Register(_s.Document, pw, ph), w, h, pw, ph) { Transform = Affine2.Translation(x, y) };
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, img));
        return img;
    }

    [Fact]
    public void Resample_down_keeps_the_picture_exactly_where_and_as_big_as_it_was_on_the_page()
    {
        var img = AddImage(100, 100, 144, 72, 600, 300);
        _s.Selection.Set(img);
        var before = img.GetWorldBounds();

        Assert.True(_s.Resample(300, 150));

        Assert.Equal((300, 150), (img.PixelWidth, img.PixelHeight));
        Assert.Equal(before, img.GetWorldBounds());
        Assert.Equal((144.0, 72.0), (img.Width, img.Height));
        Assert.Equal(150, img.EffectiveDpiX, 6);              // half the pixels, half the dpi
        Assert.Equal(150, img.EffectiveDpiY, 6);
        Assert.Contains(_svc.Calls, c => c.Op is ImageOp.Resample { Width: 300, Height: 150 });
    }

    [Fact]
    public void Resample_up_keeps_the_page_bounds_too()
    {
        var img = AddImage(50, 60, 100, 50, 100, 50);
        _s.Selection.Set(img);
        var before = img.GetWorldBounds();

        Assert.True(_s.Resample(400, 200));

        Assert.Equal((400, 200), (img.PixelWidth, img.PixelHeight));
        Assert.Equal(before, img.GetWorldBounds());
        Assert.Equal(288, img.EffectiveDpiX, 6);              // 400 px over 100 pt = 1.389 in
    }

    [Fact]
    public void A_scaled_and_turned_picture_keeps_its_transform_and_bounds()
    {
        var img = AddImage(0, 0, 144, 72, 600, 300);
        img.Transform = Affine2.Scale(2, 2) * Affine2.Rotation(0.5) * Affine2.Translation(300, 200);
        _s.Selection.Set(img);
        var transform = img.Transform;
        var before = img.GetWorldBounds();

        _s.Resample(150, 75);

        Assert.Equal(transform, img.Transform);
        Assert.Equal(before.Left, img.GetWorldBounds().Left, 9);
        Assert.Equal(before.Bottom, img.GetWorldBounds().Bottom, 9);
        Assert.Equal(37.5, img.EffectiveDpiX, 6);             // 150 px over 4 printed inches (2 in frame, scaled by 2)
    }

    [Fact]
    public void Undo_and_redo_restore_the_original_and_the_new_pixels()
    {
        var img = AddImage(100, 100, 144, 72, 600, 300);
        _s.Selection.Set(img);
        string original = img.AssetId;

        _s.Resample(300, 150);
        string resampled = img.AssetId;
        Assert.NotEqual(original, resampled);
        Assert.Equal("Resample Bitmap", _s.History.UndoDescription);

        _s.Undo();
        Assert.Equal(original, img.AssetId);                  // the very same stored pixels, not a copy
        Assert.Equal((600, 300), (img.PixelWidth, img.PixelHeight));
        Assert.Equal(300, img.EffectiveDpiX, 6);

        _s.Redo();
        Assert.Equal(resampled, img.AssetId);
        Assert.Equal((300, 150), (img.PixelWidth, img.PixelHeight));
    }

    [Fact]
    public void It_is_one_undo_step_and_its_pictures_count_as_undo_memory()
    {
        var img = AddImage(100, 100, 144, 72, 600, 300);
        _s.Selection.Set(img);
        int steps = _s.History.UndoList.Count;
        string original = img.AssetId;

        _s.Resample(300, 150);

        Assert.Equal(steps + 1, _s.History.UndoList.Count);
        var held = UndoMemory.AssetsOf(_s.History.UndoList[^1]).ToList();
        Assert.Contains(original, held);                      // undo needs the old pixels ...
        Assert.Contains(img.AssetId, held);                   // ... and redo the new ones, so the memory cap sees both
    }

    [Fact]
    public void Resample_is_only_for_exactly_one_picture()
    {
        Assert.False(_s.CanResample);
        var a = AddImage(0, 0, 10, 10, 100, 100);
        var b = AddImage(50, 0, 10, 10, 100, 100);
        _s.Selection.Set(a);
        Assert.True(_s.CanResample);
        _s.Selection.Set([a, b]);
        Assert.False(_s.CanResample);
        Assert.False(_s.Resample(50, 50));
        _s.Selection.Set(Rect(0, 0, 10, 10));
        Assert.False(_s.CanResample);
        _s.Selection.Clear();
        Assert.False(_s.CanResample);
        _s.ImageService = null;
        _s.Selection.Set(a);
        Assert.False(_s.CanResample);
    }

    [Fact]
    public void The_same_size_or_a_silly_one_changes_nothing()
    {
        var img = AddImage(0, 0, 100, 100, 200, 200);
        _s.Selection.Set(img);
        int steps = _s.History.UndoList.Count;

        Assert.False(_s.Resample(200, 200));
        Assert.False(_s.Resample(0, 100));
        Assert.False(_s.Resample(100, -3));
        Assert.False(_s.Resample(40_000, 100));
        Assert.NotNull(_s.LastImageError);
        Assert.False(_s.Resample(20_000, 20_000));
        Assert.Equal(steps, _s.History.UndoList.Count);
        Assert.Equal((200, 200), (img.PixelWidth, img.PixelHeight));
    }

    [Fact]
    public void A_picture_loaded_without_a_known_pixel_size_is_measured_first()
    {
        var id = _svc.Register(_s.Document, 300, 150);
        var img = new ImageObject(id, 144, 72);               // as a file import leaves it
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, img));
        _s.Selection.Set(img);

        Assert.Equal((300, 150), (_s.ResampleTarget!.PixelWidth, _s.ResampleTarget.PixelHeight));
        Assert.True(_s.Resample(150, 75));
        Assert.Equal((150, 75), (img.PixelWidth, img.PixelHeight));
    }

    [Fact]
    public void An_unreadable_picture_reports_an_error_and_changes_nothing()
    {
        var img = new ImageObject("missing", 100, 100, 200, 200);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, img));
        _s.Selection.Set(img);
        int steps = _s.History.UndoList.Count;

        Assert.False(_s.Resample(100, 100));
        Assert.Equal(steps, _s.History.UndoList.Count);
        Assert.Equal("missing", img.AssetId);
        Assert.NotNull(_s.LastImageError);
    }
}
