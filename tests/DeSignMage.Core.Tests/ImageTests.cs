using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using static DeSignMage.Core.Tests.DocumentTests;

namespace DeSignMage.Core.Tests;

/// <summary>An image service that only does the bookkeeping (sizes and asset ids), so the editor logic can be tested without pixels.</summary>
internal sealed class FakeImageService : IImageService
{
    private readonly Dictionary<string, (int W, int H)> _sizes = [];
    private int _next;
    public List<(string Asset, ImageOp Op)> Calls { get; } = [];
    public List<(Rect2 Bounds, int W, int H)> Rasterized { get; } = [];

    public string Register(Document doc, int w, int h)
    {
        var id = doc.Assets.Add([(byte)w, (byte)h, (byte)_next++], "image/png");
        _sizes[id] = (w, h);
        return id;
    }

    public (int Width, int Height)? GetPixelSize(Document doc, string assetId) => _sizes.TryGetValue(assetId, out var s) ? s : null;

    public ImagePixels? Apply(Document doc, string assetId, ImageOp op)
    {
        Calls.Add((assetId, op));
        if (!_sizes.TryGetValue(assetId, out var s)) return null;
        var (w, h) = op switch
        {
            ImageOp.Rotate90 => (s.H, s.W),
            ImageOp.Crop c => (c.Width, c.Height),
            ImageOp.Resample r => (r.Width, r.Height),
            _ => s,
        };
        return new ImagePixels(Register(doc, w, h), w, h);
    }

    /// <summary>The recipes rendered (the original's asset and the recipe), in order.</summary>
    public List<(string Source, PictureRecipe Recipe)> Recipes { get; } = [];

    public ImagePixels? RenderRecipe(Document doc, string sourceAssetId, PictureRecipe recipe)
    {
        Recipes.Add((sourceAssetId, recipe));
        if (!_sizes.ContainsKey(sourceAssetId)) return null;
        var (w, h) = recipe.ResultSize;
        return new ImagePixels(Register(doc, w, h), w, h);
    }

    public ImagePixels? Rasterize(Document doc, IReadOnlyList<DocObject> objects, Rect2 bounds, int pixelWidth, int pixelHeight)
    {
        Rasterized.Add((bounds, pixelWidth, pixelHeight));
        return new ImagePixels(Register(doc, pixelWidth, pixelHeight), pixelWidth, pixelHeight);
    }
}

public class ImageObjectTests
{
    [Fact]
    public void Clone_keeps_pixel_size_and_file_name()
    {
        var img = new ImageObject("a", 144, 72, 600, 300, "photo.jpg") { Name = "Sunset" };
        var c = Assert.IsType<ImageObject>(img.Clone());
        Assert.Equal((600, 300, "photo.jpg", "Sunset"), (c.PixelWidth, c.PixelHeight, c.SourceName, c.Name));
    }

    [Fact]
    public void Dpi_is_pixels_per_inch_as_placed_including_scale()
    {
        var doc = NewDoc();
        var img = new ImageObject("a", 144, 72, 600, 300);   // 144 pt = 2 in -> 300 dpi
        doc.Insert(doc.ActiveLayer, -1, img);
        Assert.Equal(300, img.EffectiveDpiX, 6);
        Assert.Equal(300, img.EffectiveDpiY, 6);

        img.Transform = Affine2.Scale(2, 2);                  // twice as big on the page -> half the dpi
        Assert.Equal(150, img.EffectiveDpiX, 6);
    }

    [Fact]
    public void Unknown_pixel_size_gives_no_dpi()
    {
        var img = new ImageObject("a", 100, 100);
        Assert.False(img.HasPixelSize);
        Assert.Equal(0, img.EffectiveDpiX);
    }

    [Fact]
    public void Picks_anywhere_inside_the_frame()
    {
        var doc = NewDoc();
        var img = new ImageObject("a", 100, 50, 10, 5) { Transform = Affine2.Translation(40, 40) };
        doc.Insert(doc.ActiveLayer, -1, img);
        Assert.Same(img, HitTesting.HitTest(doc, new Vec2(90, 65), 2));
        Assert.Null(HitTesting.HitTest(doc, new Vec2(10, 10), 2));
    }
}

public class ImagePlacementTests
{
    private static readonly PageSetup Page = PageSetup.FromInches(24, 12);

    [Fact]
    public void Natural_size_comes_from_the_dpi()
    {
        var (w, h) = ImagePlacement.NaturalSize(600, 300, 300, Page);   // 2 x 1 inch
        Assert.Equal(144, w, 6);
        Assert.Equal(72, h, 6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    [InlineData(-5.0)]
    public void Unusable_dpi_defaults_to_300(double dpi)
    {
        var (w, _) = ImagePlacement.NaturalSize(600, 300, dpi, Page);
        Assert.Equal(144, w, 6);
    }

    [Fact]
    public void A_picture_bigger_than_the_page_is_shrunk_to_fit_with_its_aspect()
    {
        var (w, h) = ImagePlacement.NaturalSize(40000, 10000, 72, Page);   // 555 x 139 in at 72 dpi
        Assert.True(w <= Page.Width + 1e-6 && h <= Page.Height + 1e-6);
        Assert.Equal(Page.Width, w, 6);                                    // the limiting side touches the page
        Assert.Equal(4.0, w / h, 6);
    }

    [Fact]
    public void Created_object_is_centered_on_the_point()
    {
        var img = ImagePlacement.Create(new ImagePixels("x", 600, 300), "logo.png", 300, Page, new Vec2(500, 300));
        var b = img.GetWorldBounds();
        Assert.Equal(500, b.Center.X, 6);
        Assert.Equal(300, b.Center.Y, 6);
        Assert.Equal("logo.png", img.Name);
        Assert.Equal("logo.png", img.SourceName);
        Assert.Equal((600, 300), (img.PixelWidth, img.PixelHeight));
    }

    [Fact]
    public void Raster_size_follows_dpi_and_refuses_huge_pictures()
    {
        var bounds = Rect2.FromXYWH(0, 0, 144, 72);                        // 2 x 1 in
        Assert.Equal((600, 300), ImagePlacement.RasterSize(bounds, 300));
        Assert.Null(ImagePlacement.RasterSize(bounds, 100_000));
        Assert.Null(ImagePlacement.RasterSize(Rect2.Empty, 300));
    }

    [Fact]
    public void Visual_bounds_include_the_stroke_width()
    {
        var r = Rect(10, 10, 20, 20, new ObjectStyle(null, StrokeStyle.Solid(DocColor.Black, 4)));
        var b = ImagePlacement.VisualBounds([r]);
        Assert.Equal(8, b.Left, 6);
        Assert.Equal(32, b.Right, 6);
    }

    [Fact]
    public void Bitmap_total_counts_each_picture_once_by_pixels()
    {
        var doc = NewDoc();
        var a = doc.Assets.Add([1], "image/png");
        var b = doc.Assets.Add([1, 2, 3, 4, 5], "image/png");
        doc.Insert(doc.ActiveLayer, -1, new ImageObject(a, 10, 10, 100, 50));
        doc.Insert(doc.ActiveLayer, -1, new ImageObject(a, 20, 20, 100, 50));   // same picture twice
        doc.Insert(doc.ActiveLayer, -1, new ImageObject(b, 10, 10));            // pixel size unknown: counts stored bytes
        Assert.Equal(100 * 50 * 4 + 5, BitmapStats.TotalBytes(doc));
    }
}

public class ImageFrameTests
{
    private static Vec2 Center(Rect2 r) => r.Center;

    [Fact]
    public void Rotate90_swaps_the_frame_and_keeps_the_center()
    {
        var f = ImageFrames.Rotate90(200, 100, Affine2.Translation(50, 30));
        Assert.Equal((100, 200), (f.Width, f.Height));
        var b = new Rect2(0, 0, f.Width, f.Height).Transform(f.Transform);
        Assert.Equal(150, b.Center.X, 6);       // old center: (50 + 100, 30 + 50) = (150, 80)
        Assert.Equal(80, b.Center.Y, 6);
        Assert.Equal((100, 200), (b.Width, b.Height));
    }

    [Fact]
    public void Rotate90_of_a_stretched_picture_swaps_what_you_see()
    {
        // Frame 100 x 100 stretched to 200 x 100 on the page: after rotating it should look 100 x 200.
        var f = ImageFrames.Rotate90(100, 100, Affine2.Scale(2, 1));
        var b = new Rect2(0, 0, f.Width, f.Height).Transform(f.Transform);
        Assert.Equal(100, b.Width, 6);
        Assert.Equal(200, b.Height, 6);
        Assert.Equal(100, b.Center.X, 6);
        Assert.Equal(50, b.Center.Y, 6);
    }

    [Fact]
    public void Rotate90_of_a_turned_picture_keeps_its_own_axes()
    {
        var t = Affine2.Rotation(Math.PI / 6) * Affine2.Translation(300, 200);
        var f = ImageFrames.Rotate90(200, 100, t);
        var before = new Rect2(0, 0, 200, 100).Transform(t);
        var after = new Rect2(0, 0, f.Width, f.Height).Transform(f.Transform);
        Assert.Equal(Center(before).X, Center(after).X, 6);
        Assert.Equal(Center(before).Y, Center(after).Y, 6);
        // Same turn as before: the local x axis still points the same way.
        var ax = f.Transform.TransformVector(new Vec2(1, 0));
        var bx = t.TransformVector(new Vec2(1, 0));
        Assert.Equal(bx.X, ax.X, 6);
        Assert.Equal(bx.Y, ax.Y, 6);
    }

    [Fact]
    public void Crop_keeps_the_remaining_pixels_where_they_were()
    {
        var t = Affine2.Rotation(0.4) * Affine2.Translation(100, 50);
        var f = ImageFrames.Crop(200, 100, 400, 200, t, 100, 40, 150, 120);     // 0.5 pt per pixel
        Assert.Equal((75, 60), (f.Width, f.Height));
        // The new frame's top-left corner is the old pixel (100, 40), i.e. frame point (50, 20).
        var oldCorner = t.Transform(new Vec2(50, 20));
        var newCorner = f.Transform.Transform(Vec2.Zero);
        Assert.Equal(oldCorner.X, newCorner.X, 6);
        Assert.Equal(oldCorner.Y, newCorner.Y, 6);
        // And its bottom-right is the old pixel (250, 160) = frame point (125, 80).
        var oldFar = t.Transform(new Vec2(125, 80));
        var newFar = f.Transform.Transform(new Vec2(f.Width, f.Height));
        Assert.Equal(oldFar.X, newFar.X, 6);
        Assert.Equal(oldFar.Y, newFar.Y, 6);
    }

    [Fact]
    public void Pixel_rect_rounds_to_pixel_edges_and_keeps_at_least_one_pixel()
    {
        // 100 x 50 points hold 200 x 100 pixels (2 px per point).
        Assert.Equal((20, 10, 40, 20), ImageFrames.PixelRect(new Rect2(10, 5, 30, 15), 100, 50, 200, 100));
        Assert.Equal((21, 11, 1, 1), ImageFrames.PixelRect(new Rect2(10.4, 5.4, 10.4, 5.4), 100, 50, 200, 100));
        Assert.Equal((199, 99, 1, 1), ImageFrames.PixelRect(new Rect2(100, 50, 100, 50), 100, 50, 200, 100));   // at the far corner
        Assert.Equal((0, 0, 200, 100), ImageFrames.PixelRect(new Rect2(-5, -5, 500, 500), 100, 50, 200, 100));  // beyond the picture
    }
}

public class ImageEditingTests
{
    private readonly EditorSession _s = new(NewDoc());
    private readonly FakeImageService _svc = new();

    public ImageEditingTests()
    {
        _s.ImageService = _svc;
        _s.Viewport.SetViewSize(new Vec2(1000, 800));
        _s.Viewport.Set(1, Vec2.Zero);   // screen == document coordinates
    }

    private ImageObject AddImage(double x, double y, double w, double h, int pw, int ph)
    {
        var img = new ImageObject(_svc.Register(_s.Document, pw, ph), w, h, pw, ph) { Transform = Affine2.Translation(x, y) };
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, img));
        return img;
    }

    private PointerInput At(double x, double y, int clicks = 1) =>
        new(new Vec2(x, y), _s.Viewport.ToDoc(new Vec2(x, y)), PointerButton.Left, ModifierKeys.None, clicks);

    private void Drag(double x0, double y0, double x1, double y1)
    {
        _s.PointerDown(At(x0, y0));
        _s.PointerMove(At((x0 + x1) / 2, (y0 + y1) / 2));
        _s.PointerMove(At(x1, y1));
        _s.PointerUp(At(x1, y1));
    }

    // ---- Image menu operations ----

    [Fact]
    public void Rotate_clockwise_changes_the_picture_and_undo_restores_everything()
    {
        var img = AddImage(100, 100, 200, 100, 400, 200);
        _s.Selection.Set(img);
        string oldAsset = img.AssetId;
        var oldTransform = img.Transform;

        Assert.Equal(1, _s.ApplyImageOp(new ImageOp.Rotate90(true), "Rotate 90 Degrees Clockwise"));

        Assert.NotEqual(oldAsset, img.AssetId);
        Assert.Equal(100, img.Width);
        Assert.Equal(200, img.Height);
        Assert.Equal((200, 400), (img.PixelWidth, img.PixelHeight));
        Assert.Equal(200, img.GetWorldBounds().Center.X, 6);
        Assert.Equal(150, img.GetWorldBounds().Center.Y, 6);
        Assert.Equal("Rotate 90 Degrees Clockwise", _s.History.UndoDescription);

        _s.Undo();
        Assert.Equal((oldAsset, 200.0, 100.0, 400, 200), (img.AssetId, img.Width, img.Height, img.PixelWidth, img.PixelHeight));
        Assert.Equal(oldTransform, img.Transform);
        _s.Redo();
        Assert.Equal(100, img.Width);
    }

    [Fact]
    public void An_operation_on_several_pictures_is_one_undo_step()
    {
        var a = AddImage(0, 0, 100, 100, 50, 50);
        var b = AddImage(200, 0, 100, 100, 50, 50);
        _s.Selection.Set([a, b]);
        int before = _s.History.UndoList.Count;

        Assert.Equal(2, _s.ApplyImageOp(new ImageOp.Invert(), "Invert"));

        Assert.Equal(before + 1, _s.History.UndoList.Count);
        _s.Undo();
        Assert.Equal(before, _s.History.UndoList.Count);
    }

    [Fact]
    public void Pictures_inside_a_selected_group_are_included()
    {
        var a = AddImage(0, 0, 100, 100, 50, 50);
        var rect = Rect(300, 0, 10, 10);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, rect));
        var group = _s.GroupObjects([a, rect])!;
        _s.Selection.Set(group);

        Assert.Equal(1, _s.ApplyImageOp(new ImageOp.Flip(Horizontal: true), "Flip Horizontal"));
        Assert.Contains(_svc.Calls, c => c.Op is ImageOp.Flip { Horizontal: true });
    }

    [Fact]
    public void Without_a_picture_or_service_nothing_happens()
    {
        var rect = Rect(0, 0, 10, 10);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, rect));
        _s.Selection.Set(rect);
        Assert.False(_s.CanEditImages);
        Assert.Equal(0, _s.ApplyImageOp(new ImageOp.Invert(), "Invert"));

        var img = AddImage(0, 0, 10, 10, 5, 5);
        _s.Selection.Set(img);
        Assert.True(_s.CanEditImages);
        _s.ImageService = null;
        Assert.False(_s.CanEditImages);
        Assert.Equal(0, _s.ApplyImageOp(new ImageOp.Invert(), "Invert"));
    }

    [Fact]
    public void Pictures_without_a_known_pixel_size_are_measured_first()
    {
        var id = _svc.Register(_s.Document, 300, 150);
        var img = new ImageObject(id, 144, 72);   // as an imported layout creates it
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, img));
        _s.Selection.Set(img);

        _s.ApplyImageOp(new ImageOp.Rotate90(false), "Rotate");

        Assert.Equal((150, 300), (img.PixelWidth, img.PixelHeight));
    }

    // ---- Crop ----

    [Fact]
    public void Crop_tool_drag_then_Enter_crops_and_returns_to_the_select_tool()
    {
        var img = AddImage(100, 100, 200, 100, 400, 200);   // 2 pixels per point
        _s.Selection.Set(img);
        Assert.True(_s.BeginCrop());
        Assert.IsType<CropTool>(_s.ActiveTool);

        Drag(150, 120, 250, 170);                           // frame points (50,20)-(150,70)
        var crop = _s.GetTool<CropTool>();
        Assert.Equal((100, 40, 200, 100), crop.BoxPixels);
        Assert.Equal(new Rect2(50, 20, 150, 70), crop.Box);
        Assert.Empty(_svc.Calls);                           // nothing is cropped before Enter

        Assert.True(_s.KeyDown(EditorKey.Enter, ModifierKeys.None));

        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.Contains(_svc.Calls, c => c.Op is ImageOp.Crop { X: 100, Y: 40, Width: 200, Height: 100 });
        Assert.Equal((100.0, 50.0), (img.Width, img.Height));
        Assert.Equal((200, 100), (img.PixelWidth, img.PixelHeight));
        var b = img.GetWorldBounds();                       // the kept part has not moved
        Assert.Equal(new Rect2(150, 120, 250, 170), b);
        Assert.Equal("Crop Bitmap", _s.History.UndoDescription);
        Assert.Same(img, Assert.Single(_s.Selection.Items));
    }

    [Fact]
    public void Crop_undo_brings_back_the_whole_picture()
    {
        var img = AddImage(100, 100, 200, 100, 400, 200);
        string oldAsset = img.AssetId;
        _s.Selection.Set(img);
        _s.BeginCrop();
        Drag(150, 120, 250, 170);
        _s.KeyDown(EditorKey.Enter, ModifierKeys.None);

        _s.Undo();

        Assert.Equal(oldAsset, img.AssetId);
        Assert.Equal(new Rect2(100, 100, 300, 200), img.GetWorldBounds());
        Assert.Equal((400, 200), (img.PixelWidth, img.PixelHeight));
    }

    [Fact]
    public void Escape_cancels_the_crop_without_changing_anything()
    {
        var img = AddImage(100, 100, 200, 100, 400, 200);
        _s.Selection.Set(img);
        _s.BeginCrop();
        Drag(150, 120, 250, 170);
        int undoCount = _s.History.UndoList.Count;

        Assert.True(_s.KeyDown(EditorKey.Escape, ModifierKeys.None));   // the session leaves the tool

        Assert.IsType<SelectTool>(_s.ActiveTool);
        Assert.Empty(_svc.Calls);
        Assert.Equal(undoCount, _s.History.UndoList.Count);
        Assert.Equal(400, img.PixelWidth);
    }

    [Fact]
    public void Escape_during_a_drag_only_drops_that_box()
    {
        var img = AddImage(100, 100, 200, 100, 400, 200);
        _s.Selection.Set(img);
        _s.BeginCrop();
        _s.PointerDown(At(150, 120));
        _s.PointerMove(At(200, 150));
        Assert.True(_s.KeyDown(EditorKey.Escape, ModifierKeys.None));
        Assert.IsType<CropTool>(_s.ActiveTool);
        Assert.Null(_s.GetTool<CropTool>().Box);
    }

    [Fact]
    public void The_box_cannot_leave_the_picture()
    {
        var img = AddImage(100, 100, 200, 100, 400, 200);
        _s.Selection.Set(img);
        _s.BeginCrop();

        Drag(150, 120, 900, 700);                           // far beyond the bottom-right corner

        Assert.Equal(new Rect2(50, 20, 200, 100), _s.GetTool<CropTool>().Box);
    }

    [Fact]
    public void Dragging_inside_the_box_moves_it_and_stays_inside_the_picture()
    {
        var img = AddImage(100, 100, 200, 100, 400, 200);
        _s.Selection.Set(img);
        _s.BeginCrop();
        Drag(150, 120, 250, 170);                           // box (50,20)-(150,70)

        Drag(200, 145, 230, 155);                           // inside: move by (30, 10)
        Assert.Equal(new Rect2(80, 30, 180, 80), _s.GetTool<CropTool>().Box);

        Drag(230, 155, 1000, 155);                          // push far right: stops at the picture's edge
        Assert.Equal(200, _s.GetTool<CropTool>().Box!.Value.Right, 6);
        Assert.Equal(100, _s.GetTool<CropTool>().Box!.Value.Width, 6);
    }

    [Fact]
    public void Dragging_a_handle_resizes_the_box()
    {
        var img = AddImage(100, 100, 200, 100, 400, 200);
        _s.Selection.Set(img);
        _s.BeginCrop();
        Drag(150, 120, 250, 170);                           // box (50,20)-(150,70); bottom-right handle at doc (250, 170)

        Drag(250, 170, 290, 190);

        Assert.Equal(new Rect2(50, 20, 190, 90), _s.GetTool<CropTool>().Box);
    }

    [Fact]
    public void Dragging_outside_the_box_starts_a_new_one()
    {
        var img = AddImage(100, 100, 200, 100, 400, 200);
        _s.Selection.Set(img);
        _s.BeginCrop();
        Drag(150, 120, 200, 150);
        Drag(120, 180, 160, 195);                           // not inside, not on a handle

        Assert.Equal(new Rect2(20, 80, 60, 95), _s.GetTool<CropTool>().Box);
    }

    [Fact]
    public void Double_click_inside_the_box_applies()
    {
        var img = AddImage(100, 100, 200, 100, 400, 200);
        _s.Selection.Set(img);
        _s.BeginCrop();
        Drag(150, 120, 250, 170);

        _s.PointerDown(At(200, 145, clicks: 2));
        _s.PointerUp(At(200, 145));

        Assert.Equal(200, img.PixelWidth);
        Assert.IsType<SelectTool>(_s.ActiveTool);
    }

    [Fact]
    public void A_plain_click_leaves_no_box_and_Enter_then_does_nothing()
    {
        var img = AddImage(100, 100, 200, 100, 400, 200);
        _s.Selection.Set(img);
        _s.BeginCrop();
        _s.PointerDown(At(150, 120));
        _s.PointerUp(At(150, 120));

        Assert.Null(_s.GetTool<CropTool>().Box);
        _s.KeyDown(EditorKey.Enter, ModifierKeys.None);
        Assert.Empty(_svc.Calls);
    }

    [Fact]
    public void Cropping_a_turned_picture_uses_its_own_axes()
    {
        var img = AddImage(0, 0, 200, 100, 400, 200);
        img.Transform = Affine2.Rotation(Math.PI / 2) * Affine2.Translation(500, 100);   // turned a quarter: frame x runs down the page
        _s.Selection.Set(img);
        _s.BeginCrop();
        var tl = img.GetWorldTransform().Transform(new Vec2(50, 20));
        var br = img.GetWorldTransform().Transform(new Vec2(150, 70));
        Drag(tl.X, tl.Y, br.X, br.Y);

        Assert.Equal((100, 40, 200, 100), _s.GetTool<CropTool>().BoxPixels);
    }

    [Fact]
    public void Crop_that_keeps_everything_changes_nothing()
    {
        var img = AddImage(100, 100, 200, 100, 400, 200);
        _s.Selection.Set(img);
        _s.BeginCrop();
        Drag(100, 100, 300, 200);                           // the whole picture
        int undo = _s.History.UndoList.Count;

        _s.KeyDown(EditorKey.Enter, ModifierKeys.None);

        Assert.Equal(undo, _s.History.UndoList.Count);
        Assert.Empty(_svc.Calls);
    }

    [Fact]
    public void Box_pixel_size_can_be_typed()
    {
        var img = AddImage(100, 100, 200, 100, 400, 200);
        _s.Selection.Set(img);
        _s.BeginCrop();
        Drag(150, 120, 250, 170);

        _s.GetTool<CropTool>().SetBoxPixelSize(300, 50);

        Assert.Equal((100, 40, 300, 50), _s.GetTool<CropTool>().BoxPixels);
    }

    [Fact]
    public void Crop_needs_exactly_one_picture()
    {
        var a = AddImage(0, 0, 10, 10, 5, 5);
        var b = AddImage(50, 0, 10, 10, 5, 5);
        _s.Selection.Set([a, b]);
        Assert.False(_s.CanCrop);
        Assert.False(_s.BeginCrop());
        _s.Selection.Clear();
        Assert.False(_s.BeginCrop());
    }

    [Fact]
    public void The_crop_tool_draws_its_frame_and_box_as_an_overlay()
    {
        var img = AddImage(100, 100, 200, 100, 400, 200);
        _s.Selection.Set(img);
        _s.BeginCrop();
        Assert.Null(Assert.Single(_s.BuildOverlay().OfType<CropOverlay>()).Box);
        Drag(150, 120, 250, 170);
        var o = Assert.Single(_s.BuildOverlay().OfType<CropOverlay>());
        Assert.Equal(new Vec2(150, 120), o.Box![0]);
        Assert.Equal(8, o.Handles.Count);
        Assert.Equal(new Vec2(100, 100), o.Frame[0]);
    }

    // ---- 1:1 zoom ----

    [Fact]
    public void One_to_one_zoom_shows_one_pixel_per_screen_pixel_and_is_a_view_step()
    {
        var img = AddImage(100, 100, 144, 72, 600, 300);    // 600 px over 144 pt
        _s.Selection.Set(img);

        Assert.True(_s.ZoomToImagePixels());

        Assert.Equal(600.0 / 144, _s.Viewport.Zoom, 6);
        Assert.Equal(1, _s.Viewport.ZoomStackDepth);        // right-click returns
        var c = _s.Viewport.ToScreen(img.GetWorldBounds().Center);
        Assert.Equal(500, c.X, 6);
        Assert.Equal(400, c.Y, 6);
    }

    [Fact]
    public void One_to_one_zoom_accounts_for_the_monitor_scaling()
    {
        var img = AddImage(0, 0, 144, 72, 600, 300);
        _s.Selection.Set(img);
        _s.ScreenScale = 2;
        _s.ZoomToImagePixels();
        Assert.Equal(600.0 / 144 / 2, _s.Viewport.Zoom, 6);
    }

    [Fact]
    public void One_to_one_zoom_does_not_touch_the_document()
    {
        var img = AddImage(0, 0, 144, 72, 600, 300);
        _s.Selection.Set(img);
        int undo = _s.History.UndoList.Count;
        long rev = _s.Document.Revision;
        _s.ZoomToImagePixels();
        Assert.Equal(undo, _s.History.UndoList.Count);
        Assert.Equal(rev, _s.Document.Revision);
    }

    // ---- Rasterize ----

    [Fact]
    public void Rasterize_adds_one_picture_on_top_of_the_selection_and_keeps_the_originals()
    {
        var below = Rect(0, 0, 10, 10);
        var r1 = Rect(100, 100, 144, 72);
        var r2 = Rect(300, 100, 72, 72);
        var above = Rect(0, 400, 10, 10);
        foreach (var o in new DocObject[] { below, r1, r2, above }) _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, o));
        _s.Selection.Set([r1, r2]);

        Assert.True(_s.CanRasterize);
        Assert.True(_s.RasterizeSelection(300));

        var kids = _s.Document.ActiveLayer.Children;
        Assert.Equal(5, kids.Count);
        Assert.Same(below, kids[0]);
        Assert.Same(r1, kids[1]);                               // the originals are untouched ...
        Assert.Same(r2, kids[2]);
        var img = Assert.IsType<ImageObject>(kids[3]);          // ... and the picture sits just above the top-most of them
        Assert.Same(above, kids[4]);
        Assert.Equal(new Rect2(100, 100, 372, 172), img.GetWorldBounds());
        Assert.Equal((1133, 300), (img.PixelWidth, img.PixelHeight));   // 272 pt = 3.78 in at 300 dpi; 72 pt = 1 in = 300 px
        Assert.Equal(300, img.EffectiveDpiY, 3);
        Assert.Same(img, Assert.Single(_s.Selection.Items));
        Assert.Equal("Rasterize", _s.History.UndoDescription);

        _s.Undo();                                              // removes only the picture
        Assert.Equal(4, _s.Document.ActiveLayer.Children.Count);
        Assert.Same(r1, _s.Document.ActiveLayer.Children[1]);
        Assert.Same(r2, _s.Document.ActiveLayer.Children[2]);
        Assert.Equal(new Rect2(100, 100, 244, 172), r1.GetWorldBounds());
    }

    [Fact]
    public void Rasterize_is_not_offered_for_pictures_only_or_absurd_sizes()
    {
        var img = AddImage(0, 0, 10, 10, 5, 5);
        _s.Selection.Set(img);
        Assert.False(_s.CanRasterize);

        var big = Rect(0, 0, 1728, 864);
        _s.History.Execute(new AddObjectCommand(_s.Document.ActiveLayer, big));
        _s.Selection.Set(big);
        Assert.True(_s.CanRasterize);
        Assert.False(_s.RasterizeSelection(5000));              // 120,000 x 60,000 px
        Assert.NotNull(_s.LastImageError);
        Assert.Same(big, Assert.Single(_s.Document.ActiveLayer.Children, o => o is RectangleObject));
    }

    // ---- Placing ----

    [Fact]
    public void Placing_pictures_selects_them_and_is_one_undo_step()
    {
        var a = new ImageObject("a", 10, 10, 5, 5);
        var b = new ImageObject("b", 10, 10, 5, 5);
        _s.PlaceImages([a, b]);
        Assert.Equal(2, _s.Selection.Count);
        Assert.Equal(2, _s.Document.ActiveLayer.Children.Count);
        _s.Undo();
        Assert.Empty(_s.Document.ActiveLayer.Children);
    }

    [Fact]
    public void Moving_and_scaling_work_like_for_any_object_and_change_the_dpi()
    {
        var img = AddImage(100, 100, 144, 72, 600, 300);          // 300 dpi
        _s.Selection.Set(img);
        _s.NudgeSelection(new Vec2(10, 0));
        Assert.Equal(110, img.GetWorldBounds().Left, 6);
        _s.SetSelectionBounds(Rect2.FromXYWH(0, 0, 288, 144));    // twice as big
        Assert.Equal(150, img.EffectiveDpiX, 6);
        Assert.Equal((600, 300), (img.PixelWidth, img.PixelHeight));   // scaling never changes pixels
    }
}