using DeSignMage.App.Shell;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
using DeSignMage.Imaging;

namespace DeSignMage.App;

/// <summary>
/// Edit ▸ Paste and Ctrl+V with the system clipboard: objects copied in this program come first, then a picture (PNG, else a DIB with
/// alpha), then a list of files copied in Explorer (imported like File ▸ Import). Copying a single picture also offers it to other programs as a PNG.
/// </summary>
public partial class MainWindow
{
    /// <summary>The system clipboard. Tests swap in a fake.</summary>
    internal ISystemClipboard SystemClipboard { get; set; } = new WindowsClipboard();

    /// <summary>The newest paste or copy-out job (completed when done). Tests await it.</summary>
    internal Task ClipboardIdle { get; private set; } = Task.CompletedTask;

    /// <summary>What the last paste said went wrong (null after a clean one). For tests.</summary>
    internal string? LastPasteError { get; private set; }

    private enum PasteSource { None, Objects, Picture, Files }

    /// <summary>Pictures above this many pixels are offered to other programs as PNG only (a DIB copy would take hundreds of megabytes).</summary>
    private const long MaxDibPixels = 16_000_000;

    /// <summary>
    /// What Paste would use right now. Objects copied here win while the clipboard is still tagged as theirs; anything copied elsewhere since
    /// (a screenshot, an image from a browser, files) is used instead; with nothing usable on the clipboard the in-app copy remains.
    /// </summary>
    private PasteSource ResolvePasteSource()
    {
        var available = SystemClipboard.Available();
        if ((available & ClipboardContent.Own) != 0 && _session.CanPaste && SystemClipboard.OwnToken() == _session.ClipboardToken.ToString())
            return PasteSource.Objects;
        if ((available & (ClipboardContent.Png | ClipboardContent.Dib)) != 0) return PasteSource.Picture;
        if ((available & ClipboardContent.Files) != 0) return PasteSource.Files;
        return _session.CanPaste ? PasteSource.Objects : PasteSource.None;
    }

    private bool CanPasteNow() => ResolvePasteSource() != PasteSource.None;

    private Task PasteAsync() => ClipboardIdle = RunPasteAsync();

    private async Task RunPasteAsync()
    {
        LastPasteError = null;
        switch (ResolvePasteSource())
        {
            case PasteSource.Objects:
                _session.Paste();
                break;
            case PasteSource.Files:
            {
                var pictures = SystemClipboard.GetFiles().Where(ImageCodec.IsImageFile).ToList();
                if (pictures.Count > 0) await ImportImagesAsync(pictures, null, hangOnCursor: true);
                break;
            }
            case PasteSource.Picture:
                await PastePictureAsync();
                break;
        }
    }

    private async Task PastePictureAsync()
    {
        var session = _session;
        var doc = session.Document;
        byte[]? png = SystemClipboard.GetPng();
        byte[]? dib = SystemClipboard.GetDib();

        // Reading, converting and storing a big screenshot takes long enough to show: do it on a worker, with the progress window.
        ImportedImage? imported;
        try
        {
            imported = await BackgroundWork.RunAsync<ImportedImage?>(this, "Paste", "Reading the picture from the clipboard...", ct =>
            {
                // A PNG is best (it keeps alpha); if it is unreadable, the bitmap form of the same picture may still be fine.
                if (png is not null)
                {
                    try { return ImageImporter.Import(doc, png, "Pasted Picture"); }
                    catch (InvalidDataException e) { AppLog.Warn("file", $"clipboard PNG unreadable: {e.Message}"); }
                }
                ct.ThrowIfCancellationRequested();
                if (dib is not null)
                {
                    try { return ImageImporter.Import(doc, ClipboardImages.DibToPng(dib), "Pasted Picture"); }
                    catch (InvalidDataException e) { AppLog.Warn("file", $"clipboard bitmap unreadable: {e.Message}"); }
                }
                return null;
            }, cancellable: true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ReferenceEquals(session, _session)) return;   // another document was opened meanwhile
        if (imported is null)
        {
            LastPasteError = "The picture on the clipboard could not be read.";
            AppLog.Warn("file", "paste: " + LastPasteError);
            _ = ShowDialogAsync("Paste", LastPasteError, "OK", null);
            return;
        }
        var picture = ImagePlacement.CreatePasted(imported.Pixels, imported.Dpi, doc.Page, session.Viewport.VisibleDocRect);
        session.BeginPlacing([picture], "Paste Picture");   // hangs on the cursor until the next click
        AppLog.Info("file", $"pasted a {imported.Pixels.PixelWidth} x {imported.Pixels.PixelHeight} picture");
    }

    // ---------------------------------------------------------------- Copy: tag the clipboard, offer a single picture to other programs

    private void OnSessionClipboardChanged(EditorSession session)
    {
        string token = session.ClipboardToken.ToString();
        var objects = session.ClipboardObjects;
        // The tag goes on at once (it also drops whatever was copied before); the picture follows when it is converted.
        SystemClipboard.SetOwn(token, null);
        if (objects is [ImageObject image] && session.Document.Assets.Get(image.AssetId) is { } asset)
            ClipboardIdle = OfferPictureAsync(session, token, asset);
    }

    private async Task OfferPictureAsync(EditorSession session, string token, Asset asset)
    {
        ClipboardPicture? picture;
        try
        {
            picture = await Task.Run(() =>
            {
                var png = ClipboardImages.ToPng(asset.Data, asset.MediaType);
                var probe = ImageCodec.Probe(png);
                if ((long)probe.Width * probe.Height > MaxDibPixels) return new ClipboardPicture(png, []);
                return new ClipboardPicture(png, ClipboardImages.ToDibV5(ImageCodec.Decode(png)));
            });
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            AppLog.Warn("file", $"could not offer the copied picture to other programs: {e.Message}");
            return;
        }
        // Only if nothing was copied meanwhile (here or elsewhere).
        if (session.ClipboardToken.ToString() == token && SystemClipboard.OwnToken() == token)
            SystemClipboard.SetOwn(token, picture);
    }
}
