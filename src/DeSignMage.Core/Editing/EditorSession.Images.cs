using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

// Placed pictures (C.4): import placement, the Image menu operations, crop, 1:1 zoom and rasterize.
public sealed partial class EditorSession
{
    /// <summary>Does the pixel work (Skia in the app). Without one, image commands are unavailable.</summary>
    public IImageService? ImageService { get; set; }

    /// <summary>Device pixels per screen DIP (the monitor's scaling); the shell sets it. Used by 1:1 zoom.</summary>
    public double ScreenScale { get; set; } = 1;

    /// <summary>Why the last image command did nothing (a picture that cannot be decoded), or null.</summary>
    public string? LastImageError { get; private set; }

    /// <summary>The pictures in the selection, including those inside selected groups.</summary>
    public IReadOnlyList<ImageObject> SelectedImages()
    {
        var list = new List<ImageObject>();
        foreach (var o in Selection.Items)
        {
            if (o is ImageObject img) list.Add(img);
            else if (o is GroupObject g) list.AddRange(Document.Walk(g).OfType<ImageObject>());
        }
        return list;
    }

    public bool CanEditImages => ImageService is not null && SelectedImages().Count > 0;

    /// <summary>A single picture is selected (what the crop tool and 1:1 zoom work on).</summary>
    public bool HasSingleImageSelected => Selection.Count == 1 && Selection.Items[0] is ImageObject;

    public bool CanCrop => ImageService is not null && HasSingleImageSelected;

    /// <summary>Makes sure <paramref name="image"/> knows its pixel size (pictures from .lyt files start without).</summary>
    private bool EnsurePixelSize(ImageObject image)
    {
        if (image.HasPixelSize) return true;
        if (ImageService?.GetPixelSize(Document, image.AssetId) is not { } size)
        {
            LastImageError = "The picture could not be read.";
            return false;
        }
        image.PixelWidth = size.Width;
        image.PixelHeight = size.Height;
        return true;
    }

    /// <summary>
    /// Applies a rotate / flip / invert to every selected picture as one undo step. Returns how many pictures changed.
    /// Rotation keeps each picture's center and swaps its width and height.
    /// </summary>
    public int ApplyImageOp(ImageOp op, string description)
    {
        if (ImageService is null) return 0;
        LastImageError = null;
        if (Background is not null && SelectedImages() is { Count: > 0 } selected && selected.Any(IsBig))
        {
            // A large photo takes long enough to freeze the window: decode, rotate and re-encode on a worker with progress shown.
            // The caller gets "will be done" (true) at once; the undo step appears when the worker is finished.
            _ = ApplyImageOpInBackgroundAsync(op, description, selected.ToList());
            return selected.Count;
        }
        var commands = new List<IDocumentCommand>();
        foreach (var image in SelectedImages())
        {
            if (!EnsurePixelSize(image)) continue;
            var next = RecipeAfter(image, op);
            if (Compute(ImageService, Document, image.AssetId, image.SourceAssetId, next, op) is not { } pixels)
            {
                LastImageError = "The picture could not be read.";
                continue;
            }
            commands.Add(OpCommand(image, op, description, pixels, next));
        }
        if (commands.Count == 0) return 0;
        using (History.BeginTransaction(description))
            foreach (var c in commands) History.Execute(c);
        return commands.Count;
    }

    // Rotate, Flip and Invert on a picture that has an editing recipe (Edit Picture) change the recipe instead of its pixels: the original stays, so
    // nothing the user did in the editor is lost, and the finished picture is rebuilt from the original. On a plain picture they stay what they
    // always were (lossless, on the pixels; no second copy of the picture is kept). Crop and Resample work on the finished pixels and end the recipe.

    /// <summary>The recipe <paramref name="image"/> would have after <paramref name="op"/>, or null when the operation works on the pixels.</summary>
    private static PictureRecipe? RecipeAfter(ImageObject image, ImageOp op)
    {
        if (image.Recipe is not { } r || image.SourceAssetId is null) return null;
        PictureRecipe? next = null;
        if (op is ImageOp.Rotate90 turn) next = turn.Clockwise ? r.RotatedRight() : r.RotatedLeft();
        else if (op is ImageOp.Flip flip) next = flip.Horizontal ? r.FlippedHorizontally() : r.FlippedVertically();
        else if (op is ImageOp.Invert) next = r with { Effects = r.Effects with { Invert = !r.Effects.Invert } };
        return next?.Normalized();
    }

    /// <summary>The new pixels: the operation on the stored picture, or (<paramref name="next"/> not null) the new recipe rendered from the original. Runs on a worker for big pictures.</summary>
    private static ImagePixels? Compute(IImageService service, Document doc, string assetId, string? sourceAssetId, PictureRecipe? next, ImageOp op) =>
        next is null ? service.Apply(doc, assetId, op)
        : next.IsIdentity ? new ImagePixels(sourceAssetId!, next.SourceWidth, next.SourceHeight)   // back to the plain original
        : service.RenderRecipe(doc, sourceAssetId!, next);

    private static ReplaceImageCommand OpCommand(ImageObject image, ImageOp op, string description, ImagePixels pixels, PictureRecipe? next)
    {
        if (next is null)
        {
            var frame = op is ImageOp.Rotate90
                ? ImageFrames.Rotate90(image.Width, image.Height, image.Transform)
                : new ImageFrame(image.Width, image.Height, image.Transform);
            return new ReplaceImageCommand(description, image, pixels, frame);
        }
        var original = PictureGeometry.BaseFrameFor(image.Recipe!, image.Width, image.Height, image.PixelWidth, image.PixelHeight, image.Transform);
        if (next.IsIdentity) return new ReplaceImageCommand(description, image, pixels, original);
        return new ReplaceImageCommand(description, image, pixels, PictureGeometry.FrameFor(next, original.Width, original.Height, original.Transform), image.SourceAssetId, next);
    }

    // ---- Picture Editor ----

    /// <summary>Raised when a picture should open in the Picture Editor (a double-click on it, Image ▸ Edit Picture...); the window shows the editor.</summary>
    public event EventHandler<PictureEditRequest>? PictureEditRequested;

    /// <summary>Edit Picture works on one selected picture.</summary>
    public bool CanEditPicture => HasSingleImageSelected;

    /// <summary>The picture the editor would open, with its pixel size known; null when none or unreadable.</summary>
    public ImageObject? EditPictureTarget => CanEditPicture && Selection.Items[0] is ImageObject image && EnsurePixelSize(image) ? image : null;

    /// <summary>Asks the window to open the Picture Editor on <paramref name="image"/> (which becomes the selection).</summary>
    public void RequestPictureEdit(ImageObject image)
    {
        if (!Selection.Contains(image) || Selection.Count != 1) Selection.Set(image);
        PictureEditRequested?.Invoke(this, new PictureEditRequest(image));
    }

    /// <summary>
    /// The Picture Editor's OK: gives <paramref name="image"/> its new pixels (the finished picture at full resolution, an asset), the original
    /// they were made from and the recipe, with the frame the recipe gives it on the page. One undo step ("Edit Picture"). Without a recipe
    /// (<paramref name="recipe"/> null: everything was reset) the picture is simply the original again.
    /// </summary>
    public void CommitPicture(ImageObject image, ImagePixels result, ImageFrame frame, string? sourceAssetId, PictureRecipe? recipe) =>
        History.Execute(new ReplaceImageCommand("Edit Picture", image, result, frame, sourceAssetId, recipe));

    // ---- Flatten ----

    /// <summary>The selected pictures (also inside selected groups) that carry an editing recipe, so that flattening them changes something.</summary>
    public IReadOnlyList<ImageObject> FlattenablePictures() =>
        SelectedImages().Where(i => i.SourceAssetId is not null && i.Recipe is { IsIdentity: false }).Distinct().ToList();

    /// <summary>Flatten applies when at least one selected picture has a recipe (the Context Bar shows its button only then).</summary>
    public bool CanFlatten => FlattenablePictures().Count > 0;

    /// <summary>
    /// Image ▸ Flatten Picture: bakes the recipe of every selected picture that has one into its pixels, for good. The finished picture (already stored at full
    /// resolution, pixels outside a crop already gone) becomes the whole picture: the original and the recipe are let go, so the file gets smaller and the Picture
    /// Editor starts afresh on it. One undo step; undo brings the original and the recipe back exactly. Returns how many pictures were flattened.
    /// </summary>
    public int FlattenPictures()
    {
        var pictures = FlattenablePictures();
        if (pictures.Count == 0) return 0;
        string description = pictures.Count == 1 ? "Flatten Picture" : "Flatten Pictures";
        using (History.BeginTransaction(description))
            foreach (var image in pictures)
                History.Execute(new ReplaceImageCommand(description, image,
                    new ImagePixels(image.AssetId, image.PixelWidth, image.PixelHeight), new ImageFrame(image.Width, image.Height, image.Transform)));
        return pictures.Count;
    }

    // ---- Resample ----

    /// <summary>Resample works on one selected picture.</summary>
    public bool CanResample => ImageService is not null && HasSingleImageSelected;

    /// <summary>The selected picture Resample would work on, with its pixel size known; null when there is none.</summary>
    public ImageObject? ResampleTarget => CanResample && Selection.Items[0] is ImageObject image && EnsurePixelSize(image) ? image : null;

    /// <summary>
    /// Image ▸ Resample: rebuilds the selected picture with <paramref name="width"/> x <paramref name="height"/> pixels. Its size on the
    /// page stays the same (only the dpi changes). One undo step; undo brings back the original pixels exactly. Big pictures are
    /// processed in the background like the other picture commands.
    /// </summary>
    public bool Resample(int width, int height)
    {
        if (ResampleTarget is not { } image) return false;
        LastImageError = null;
        if (width < 1 || height < 1 || width == image.PixelWidth && height == image.PixelHeight) return false;
        if (width > ResampleSizing.MaxSide || height > ResampleSizing.MaxSide || (long)width * height > ResampleSizing.MaxPixels)
        {
            LastImageError = "That would make the picture too large.";
            return false;
        }
        return ApplyImageOp(new ImageOp.Resample(width, height), "Resample Bitmap") > 0;
    }

    // ---- Slow pictures ----

    /// <summary>
    /// Runs slow work on a worker thread and returns its result on the UI thread; the window plugs in one that shows a progress
    /// window (<c>BackgroundWork</c>). Null = everything runs right here (tests, command line).
    /// </summary>
    public Func<string, Func<CancellationToken, object?>, Task<object?>>? Background { get; set; }

    /// <summary>Pictures above this many pixels (or of unknown size) are processed in the background; smaller ones are too quick to matter.</summary>
    public const long BackgroundPixelThreshold = 600_000;

    private static bool IsBig(ImageObject image) =>
        !image.HasPixelSize || (long)image.PixelWidth * image.PixelHeight > BackgroundPixelThreshold
        || image.Recipe is { } r && image.SourceAssetId is not null && (long)r.SourceWidth * r.SourceHeight > BackgroundPixelThreshold;   // a recipe is rebuilt from the original

    /// <summary>The newest background picture job (completed when done). Tests await it.</summary>
    public Task ImageIdle { get; private set; } = Task.CompletedTask;

    private Task ApplyImageOpInBackgroundAsync(ImageOp op, string description, List<ImageObject> images) =>
        ImageIdle = RunImageOpAsync(op, description, images);

    private async Task RunImageOpAsync(ImageOp op, string description, List<ImageObject> images)
    {
        var service = ImageService!;
        var doc = Document;
        long revision = doc.Revision;
        var jobs = images.Select(i => (Image: i, i.AssetId, i.SourceAssetId, Known: i.HasPixelSize, Next: RecipeAfter(i, op))).ToList();
        (ImageObject Image, ImagePixels? Pixels, (int Width, int Height)? Size)[] results;
        try
        {
            var raw = await Background!(description + "...", _ =>
                jobs.Select(j => (j.Image, Compute(service, doc, j.AssetId, j.SourceAssetId, j.Next, op), j.Known ? null : service.GetPixelSize(doc, j.AssetId))).ToArray());
            results = ((ImageObject, ImagePixels?, (int, int)?)[])raw!;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            LastImageError = "The picture could not be processed.";
            return;
        }
        // Back on the UI thread. The window was locked by the progress dialog, but check anyway before touching the design.
        if (doc.Revision != revision) return;
        var commands = new List<IDocumentCommand>();
        foreach (var (image, pixels, size) in results)
        {
            if (!doc.Contains(image)) continue;
            if (size is { } s && !image.HasPixelSize) { image.PixelWidth = s.Width; image.PixelHeight = s.Height; }
            if (pixels is null) { LastImageError = "The picture could not be read."; continue; }
            commands.Add(OpCommand(image, op, description, pixels, jobs.First(j => j.Image == image).Next));
        }
        if (commands.Count == 0) { ImageFinished?.Invoke(this, EventArgs.Empty); return; }
        using (History.BeginTransaction(description))
            foreach (var c in commands) History.Execute(c);
        ImageFinished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised on the UI thread when a background picture job has finished (done or not); the window shows <see cref="LastImageError"/> if set.</summary>
    public event EventHandler? ImageFinished;

    // ---- Crop ----

    /// <summary>Opens the crop tool on the selected picture. False if there is not exactly one.</summary>
    public bool BeginCrop()
    {
        if (!CanCrop) return false;
        var image = (ImageObject)Selection.Items[0];
        if (!EnsurePixelSize(image)) return false;
        SetTool("crop");
        GetTool<CropTool>().SetTarget(image);
        return true;
    }

    /// <summary>Keeps only the pixel rectangle of <paramref name="image"/>. One undo step.</summary>
    internal bool CropImage(ImageObject image, int x, int y, int width, int height)
    {
        if (ImageService is null || !EnsurePixelSize(image)) return false;
        if (x == 0 && y == 0 && width == image.PixelWidth && height == image.PixelHeight) return false;   // nothing to remove
        if (Background is not null && IsBig(image))
        {
            _ = ImageIdle = CropInBackgroundAsync(image, new ImageOp.Crop(x, y, width, height));
            return true;
        }
        if (ImageService.Apply(Document, image.AssetId, new ImageOp.Crop(x, y, width, height)) is not { } pixels)
        {
            LastImageError = "The picture could not be read.";
            return false;
        }
        var frame = ImageFrames.Crop(image.Width, image.Height, image.PixelWidth, image.PixelHeight, image.Transform, x, y, width, height);
        History.Execute(new ReplaceImageCommand("Crop Bitmap", image, pixels, frame));
        return true;
    }

    private async Task CropInBackgroundAsync(ImageObject image, ImageOp.Crop crop)
    {
        var service = ImageService!;
        var doc = Document;
        long revision = doc.Revision;
        string assetId = image.AssetId;
        int pw = image.PixelWidth, ph = image.PixelHeight;
        ImagePixels? pixels;
        try
        {
            pixels = (ImagePixels?)await Background!("Cropping...", _ => service.Apply(doc, assetId, crop));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            pixels = null;
        }
        if (doc.Revision != revision || !doc.Contains(image)) return;
        if (pixels is null)
        {
            LastImageError = "The picture could not be read.";
            ImageFinished?.Invoke(this, EventArgs.Empty);
            return;
        }
        var frame = ImageFrames.Crop(image.Width, image.Height, pw, ph, image.Transform, crop.X, crop.Y, crop.Width, crop.Height);
        History.Execute(new ReplaceImageCommand("Crop Bitmap", image, pixels, frame));
        ImageFinished?.Invoke(this, EventArgs.Empty);
    }

    // ---- 1:1 zoom ----

    /// <summary>
    /// Image ▸ 1:1 Zoom: zooms the view so one picture pixel is one screen pixel, with the (single) selected picture centered.
    /// View only; the zoom is pushed so a right-click returns to the previous view.
    /// </summary>
    public bool ZoomToImagePixels()
    {
        var images = SelectedImages();
        if (images.Count == 0 || !EnsurePixelSize(images[0])) return false;
        var img = images[0];

        double pointsAcross = img.GetWorldTransform().TransformVector(new Vec2(img.Width, 0)).Length;
        if (pointsAcross < 1e-9) return false;
        double devicePixelsPerPoint = img.PixelWidth / pointsAcross;
        Viewport.PushAndSetZoom(devicePixelsPerPoint / Math.Max(ScreenScale, 0.1), img.GetWorldBounds().Center);
        return true;
    }

    // ---- Placing pictures ----

    /// <summary>Puts a new picture on the active layer and selects it (one undo step).</summary>
    public void PlaceImages(IReadOnlyList<ImageObject> images, string? description = null)
    {
        if (images.Count == 0) return;
        using (History.BeginTransaction(description ?? (images.Count == 1 ? "Import Bitmap" : "Import Bitmaps")))
            History.Execute(new AddObjectsCommand(Document.ActiveLayer, images, atBottom: true));   // new items go to the bottom of the layer
        Selection.Set(images);
    }

    // ---- Rasterize ----

    /// <summary>Rasterize needs something to draw and something that is not already just pictures.</summary>
    public bool CanRasterize => ImageService is not null && !Selection.IsEmpty && !Selection.Items.All(o => o is ImageObject);

    /// <summary>The page area the selection would be rasterized from.</summary>
    public Rect2 RasterizeBounds => ImagePlacement.VisualBounds(Selection.Items);

    /// <summary>
    /// Image ▸ Rasterize: adds one picture of the selected objects at <paramref name="dpi"/> on top of them, in the same place;
    /// the originals stay as they are and the picture becomes the selection. One undo step that removes only the picture. False when the picture would be too large or the service failed.
    /// </summary>
    public bool RasterizeSelection(double dpi)
    {
        if (!CanRasterize) return false;
        var items = Selection.Items.ToList();
        var bounds = RasterizeBounds;
        if (ImagePlacement.RasterSize(bounds, dpi) is not { } size)
        {
            LastImageError = "That resolution would make the picture too large.";
            return false;
        }
        if (ImageService!.Rasterize(Document, items, bounds, size.Width, size.Height) is not { } pixels)
        {
            LastImageError = "The selection could not be rasterized.";
            return false;
        }
        var result = new ImageObject(pixels.AssetId, bounds.Width, bounds.Height, pixels.PixelWidth, pixels.PixelHeight, null)
        {
            Name = "Rasterized",
            Transform = Affine2.Translation(bounds.TopLeft),
        };
        // The originals stay untouched; the picture goes just above the top-most one, on its layer (undo removes only it).
        var top = Stacking.LeavesBottomToTop(Document, items).Last();
        using (History.BeginTransaction("Rasterize"))
            History.Execute(new AddObjectCommand(top.Layer!, result, top.Layer!.IndexOf(top) + 1));
        Selection.Set(result);
        return true;
    }
}
