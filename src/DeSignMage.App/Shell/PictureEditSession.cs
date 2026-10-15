using DeSignMage.Core.Editing;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using DeSignMage.Imaging;

namespace DeSignMage.App.Shell;

/// <summary>
/// One run of the Picture Editor on one picture: the original pixels, the recipe being edited, the previews, and the live canvas behind the window.
/// <list type="bullet">
/// <item>Previews are made from a reduced copy of the original (<see cref="PictureProcessor.PreviewSide"/>), one at a time on a worker thread, the newest
/// request winning (<see cref="LatestWinsRunner"/>), so dragging a slider on a large photo never queues work up or blocks the window.</item>
/// <item>While the window is open the picture in the design shows the preview (its frame on the page follows the crop). <see cref="Cancel"/> puts back exactly what was there;
/// <see cref="CommitAsync"/> renders the real result at full resolution and makes it one undo step.</item>
/// <item>While the window is open the dialog works on one recipe; the Crop tab changes the orientation, angle and crop, the Adjust tab the colors, the
/// Effects tab the filters and looks; all of them are steps of the same <see cref="PictureRecipe"/> (see there for their fixed order).</item>
/// <item>The picture's <c>AssetId</c> always holds the finished picture; the original and the recipe sit beside it (see <see cref="ImageObject.SourceAssetId"/>), so export,
/// the clipboard, Vectorize and the layout writer use the edited picture without knowing about recipes.</item>
/// </list>
/// </summary>
internal sealed class PictureEditSession : IDisposable
{
    private readonly EditorSession _editor;
    private readonly Document _doc;
    private readonly LatestWinsRunner _runner;

    // What the picture was when the editor opened (restored by Cancel, and the "old" side of the undo step).
    private readonly string _openAssetId;
    private readonly string? _openSourceId;
    private readonly PictureRecipe? _openRecipe;
    private readonly int _openPixelWidth, _openPixelHeight;
    private readonly double _openWidth, _openHeight;
    private readonly Affine2 _openTransform;

    private readonly List<string> _tempAssets = [];
    private readonly PictureProcessor.RenderCache _cache = new();
    private bool _finished;

    public PictureEditSession(EditorSession editor, ImageObject image, PixelBuffer source, string sourceAssetId, Action<Action>? post = null)
    {
        _editor = editor;
        _doc = editor.Document;
        Image = image;
        Source = source;
        SourceAssetId = sourceAssetId;
        (Work, WorkScale) = PictureProcessor.Reduce(source);

        _openAssetId = image.AssetId;
        _openSourceId = image.SourceAssetId;
        _openRecipe = image.Recipe;
        _openPixelWidth = image.PixelWidth; _openPixelHeight = image.PixelHeight;
        _openWidth = image.Width; _openHeight = image.Height;
        _openTransform = image.Transform;

        if (image.Recipe is { } r && image.SourceAssetId is not null)
        {
            // Reopening an edited picture: its recipe is the starting point, and the unedited frame is worked out from where the picture is now.
            var original = PictureGeometry.BaseFrameFor(r, image.Width, image.Height, image.PixelWidth, image.PixelHeight, image.Transform);
            BaseWidth = original.Width;
            BaseHeight = original.Height;
            BaseTransform = original.Transform;
            Recipe = r;
        }
        else
        {
            BaseWidth = image.Width;
            BaseHeight = image.Height;
            BaseTransform = image.Transform;
            Recipe = new PictureRecipe(source.Width, source.Height);
        }
        _runner = new LatestWinsRunner(post ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a)));
    }

    public ImageObject Image { get; }

    /// <summary>The untouched original, full resolution.</summary>
    public PixelBuffer Source { get; }

    public string SourceAssetId { get; }

    /// <summary>A reduced copy of the original for previews (the same buffer when the original is small) and the factor it was reduced by.</summary>
    public PixelBuffer Work { get; }
    public double WorkScale { get; }

    /// <summary>The unedited picture's frame on the page and where it is placed.</summary>
    public double BaseWidth { get; }
    public double BaseHeight { get; }
    public Affine2 BaseTransform { get; }

    /// <summary>The recipe being edited.</summary>
    public PictureRecipe Recipe { get; private set; }

    /// <summary>The original, turned by the straighten angle (what the crop frame sits on), at preview size; null until the first preview is ready.</summary>
    public PixelBuffer? CanvasPreview { get; private set; }

    /// <summary>The recipe the previews on show were made from (it can lag a newer <see cref="Recipe"/> by one preview); null until the first one is ready.</summary>
    public PictureRecipe? PreviewRecipe { get; private set; }

    /// <summary>The finished picture at preview size; null until the first preview is ready.</summary>
    public PixelBuffer? ResultPreview { get; private set; }

    /// <summary>Raised on the UI thread when a new preview is ready (<see cref="CanvasPreview"/>, <see cref="ResultPreview"/>).</summary>
    public event Action? PreviewReady;

    /// <summary>How many previews were made (tests, profiling) and the time the last one took.</summary>
    public int PreviewsMade { get; private set; }
    public double LastPreviewMs { get; private set; }

    /// <summary>Completed when no preview is being made or waiting (tests await it).</summary>
    public Task Idle => _runner.Idle;

    /// <summary>Pixels of the original per inch as the picture is printed on the page now (what the crop's inch fields use).</summary>
    public double SourceDpi
    {
        get
        {
            double scale = BaseTransform.TransformVector(new Vec2(1, 0)).Length;
            double inches = BaseWidth * Math.Max(scale, 1e-9) / Units.PointsPerInch;
            return Source.Width / Math.Max(inches, 1e-9);
        }
    }

    /// <summary>True when the recipe now differs from the one the picture had when the editor opened (what OK would change).</summary>
    public bool IsChanged => Recipe.Normalized() != (_openRecipe?.Normalized() ?? new PictureRecipe(Source.Width, Source.Height));

    // ---------------------------------------------------------------- editing the recipe

    /// <summary>Changes the recipe (normalized) and starts a new preview.</summary>
    public void Change(Func<PictureRecipe, PictureRecipe> change)
    {
        var next = change(Recipe).Normalized();
        if (next == Recipe) return;
        Recipe = next;
        RequestPreview();
    }

    public void ResetAll() => Change(r => new PictureRecipe(r.SourceWidth, r.SourceHeight));

    /// <summary>Starts a preview of the current recipe (also done by every change).</summary>
    public void RequestPreview()
    {
        var recipe = Recipe;
        _runner.Submit(ct =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // One preview runs at a time, so the cache is only ever used by one thread; it keeps the turned canvas, the crop and the adjusted
            // picture, so a slider of a late step does not redo the early ones.
            var parts = PictureProcessor.RenderParts(Work, recipe, WorkScale, ct, _cache);
            byte[]? png = recipe.IsIdentity ? null : ImageCodec.EncodePng(parts.Result);   // for the picture in the design behind the window
            return (recipe, canvas: parts.Canvas, result: parts.Result, png, clock.Elapsed.TotalMilliseconds);
        }, done =>
        {
            if (_finished) return;
            PreviewRecipe = done.recipe;
            CanvasPreview = done.canvas;
            ResultPreview = done.result;
            LastPreviewMs = done.Item5;
            PreviewsMade++;
            ShowInDesign(done.recipe, done.result, done.png);
            PreviewReady?.Invoke();
        });
    }

    // ---------------------------------------------------------------- the live canvas

    private void ShowInDesign(PictureRecipe recipe, PixelBuffer preview, byte[]? png)
    {
        var frame = PictureGeometry.FrameFor(recipe, BaseWidth, BaseHeight, BaseTransform);
        string id;
        int pw, ph;
        if (png is null)
        {
            id = SourceAssetId;   // nothing is changed: the original itself
            pw = Source.Width; ph = Source.Height;
        }
        else
        {
            bool existed = _doc.Assets.Get(ImageIdOf(png)) is not null;
            id = _doc.Assets.Add(png, "image/png");
            if (!existed) _tempAssets.Add(id);
            pw = preview.Width; ph = preview.Height;
        }
        var old = _tempAssets.Where(t => t != id).ToList();
        Image.AssetId = id;
        Image.PixelWidth = pw; Image.PixelHeight = ph;
        Image.Width = frame.Width; Image.Height = frame.Height;
        Image.Transform = frame.Transform;
        foreach (var t in old) { _doc.Assets.Remove(t); _tempAssets.Remove(t); }
        _doc.NotifyChanged();
    }

    private static string ImageIdOf(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    private void Restore()
    {
        Image.AssetId = _openAssetId;
        Image.SourceAssetId = _openSourceId;
        Image.Recipe = _openRecipe;
        Image.PixelWidth = _openPixelWidth; Image.PixelHeight = _openPixelHeight;
        Image.Width = _openWidth; Image.Height = _openHeight;
        Image.Transform = _openTransform;
        foreach (var t in _tempAssets) _doc.Assets.Remove(t);
        _tempAssets.Clear();
    }

    /// <summary>Closes the editor without changing the picture: everything is put back exactly as it was.</summary>
    public void Cancel()
    {
        if (_finished) return;
        _finished = true;
        _runner.Cancel();
        Restore();
        _doc.NotifyChanged();
    }

    /// <summary>
    /// OK: the picture gets the recipe's result at full resolution as one undo step (a recipe that changes nothing gives back the original; nothing
    /// at all when nothing differs from when the editor opened). Returns false when nothing was changed.
    /// </summary>
    public async Task<bool> CommitAsync()
    {
        if (_finished) return false;
        _finished = true;
        _runner.Cancel();
        var recipe = Recipe.Normalized();
        Restore();   // the undo step must start from the picture as it was, not from a preview

        if (!IsChangedFrom(recipe))
        {
            _doc.NotifyChanged();
            return false;
        }

        ImagePixels pixels;
        ImageFrame frame;
        if (recipe.IsIdentity)
        {
            // Back to the original: the very same pixels, no new picture.
            pixels = new ImagePixels(SourceAssetId, Source.Width, Source.Height);
            frame = new ImageFrame(BaseWidth, BaseHeight, BaseTransform);
            _editor.CommitPicture(Image, pixels, frame, null, null);
            return true;
        }

        var doc = _doc;
        var source = Source;
        Func<CancellationToken, object?> work = ct =>
        {
            var result = PictureProcessor.Render(source, recipe, 1.0, ct);
            var png = ImageCodec.EncodePng(result);
            return new ImagePixels(doc.Assets.Add(png, "image/png"), result.Width, result.Height);
        };
        // Full resolution can take seconds for a large photo: the window plugs in a worker with a progress window (the session's Background); tests run it right here.
        var made = (ImagePixels)(_editor.Background is { } background ? await background("Applying the edits...", work) : work(CancellationToken.None))!;
        frame = PictureGeometry.FrameFor(recipe, BaseWidth, BaseHeight, BaseTransform);
        _editor.CommitPicture(Image, made, frame, SourceAssetId, recipe);
        return true;
    }

    private bool IsChangedFrom(PictureRecipe normalized) => normalized != (_openRecipe?.Normalized() ?? new PictureRecipe(Source.Width, Source.Height));

    public void Dispose()
    {
        if (!_finished) Cancel();
    }
}
