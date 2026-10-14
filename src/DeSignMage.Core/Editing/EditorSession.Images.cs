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
            if (ImageService.Apply(Document, image.AssetId, op) is not { } pixels)
            {
                LastImageError = "The picture could not be read.";
                continue;
            }
            var frame = op is ImageOp.Rotate90
                ? ImageFrames.Rotate90(image.Width, image.Height, image.Transform)
                : new ImageFrame(image.Width, image.Height, image.Transform);
            commands.Add(new ReplaceImageCommand(description, image, pixels, frame));
        }
        if (commands.Count == 0) return 0;
        using (History.BeginTransaction(description))
            foreach (var c in commands) History.Execute(c);
        return commands.Count;
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
        !image.HasPixelSize || (long)image.PixelWidth * image.PixelHeight > BackgroundPixelThreshold;

    /// <summary>The newest background picture job (completed when done). Tests await it.</summary>
    public Task ImageIdle { get; private set; } = Task.CompletedTask;

    private Task ApplyImageOpInBackgroundAsync(ImageOp op, string description, List<ImageObject> images) =>
        ImageIdle = RunImageOpAsync(op, description, images);

    private async Task RunImageOpAsync(ImageOp op, string description, List<ImageObject> images)
    {
        var service = ImageService!;
        var doc = Document;
        long revision = doc.Revision;
        var jobs = images.Select(i => (Image: i, i.AssetId, Known: i.HasPixelSize)).ToList();
        (ImageObject Image, ImagePixels? Pixels, (int Width, int Height)? Size)[] results;
        try
        {
            var raw = await Background!(description + "...", _ =>
                jobs.Select(j => (j.Image, service.Apply(doc, j.AssetId, op), j.Known ? null : service.GetPixelSize(doc, j.AssetId))).ToArray());
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
            var frame = op is ImageOp.Rotate90
                ? ImageFrames.Rotate90(image.Width, image.Height, image.Transform)
                : new ImageFrame(image.Width, image.Height, image.Transform);
            commands.Add(new ReplaceImageCommand(description, image, pixels, frame));
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
