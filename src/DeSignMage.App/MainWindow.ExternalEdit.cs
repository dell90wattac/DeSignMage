using Avalonia.Threading;
using DeSignMage.App.Shell;
using DeSignMage.Core.Model;
using DeSignMage.Imaging;

namespace DeSignMage.App;

/// <summary>
/// Picture Editor ▸ Edit in...: the picture's original is written to a temporary file and opened in another program; when the file is saved there, the picture
/// comes back with every edit kept. The links live here (one per picture) and are looked at once a second while any exists. A link ends when its picture leaves
/// the design, another design is opened, or the window closes.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<ImageObject, ExternalPictureEdit> _externalEdits = [];
    private DispatcherTimer? _externalTimer;

    /// <summary>The pictures that are being edited in another program (for tests).</summary>
    internal IReadOnlyCollection<ExternalPictureEdit> ExternalEdits => _externalEdits.Values;

    /// <summary>The Edit in... button of the open Picture Editor was pressed.</summary>
    internal void StartExternalEditFor(PictureEditorDialog dialog)
    {
        var session = dialog.Session;
        if (_session.Document.Assets.Get(session.SourceAssetId) is not { } asset) return;
        var error = StartExternalEdit(session.Image, asset);
        if (error is null) dialog.ShowEditInStarted();
        else _ = ShowDialogAsync("Edit in...", error, "OK", null);
    }

    /// <summary>Writes the original to a temporary file, opens it in the chosen program and starts watching it. Returns a plain-words error, or null.</summary>
    internal string? StartExternalEdit(ImageObject image, Asset original)
    {
        if (_externalEdits.Remove(image, out var old)) old.Dispose();
        ExternalPictureEdit link;
        try { link = new ExternalPictureEdit(_session, image, original.Data, original.MediaType); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "The picture could not be saved for the other program: " + e.Message;
        }
        if (PictureSettings.Launch(link.FilePath) is { } error)
        {
            link.Dispose();
            return error;
        }
        link.Changed += data => _ = ApplyExternalChangeAsync(link, data);
        _externalEdits[image] = link;
        if (_externalTimer is null)
        {
            _externalTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _externalTimer.Tick += (_, _) => PollExternalEdits();
            Closed += (_, _) => StopExternalEdits();
        }
        _externalTimer.Start();
        return null;
    }

    /// <summary>Looks at every watched file once (the timer does this every second); ends links whose picture or design is gone.</summary>
    internal void PollExternalEdits()
    {
        foreach (var (image, link) in _externalEdits.ToList())
        {
            if (!ReferenceEquals(link.Session, _session) || !link.Session.Document.Contains(image))
            {
                _externalEdits.Remove(image);
                link.Dispose();
                continue;
            }
            link.Poll();
        }
        if (_externalEdits.Count == 0) _externalTimer?.Stop();
    }

    private void StopExternalEdits()
    {
        _externalTimer?.Stop();
        foreach (var link in _externalEdits.Values) link.Dispose();
        _externalEdits.Clear();
    }

    /// <summary>
    /// The file was saved in the other program. If the Picture Editor is open on that picture, the editor takes the new original (the edits stay; OK makes it one
    /// undo step, Cancel puts everything back). Otherwise the picture is updated at once as one undo step.
    /// </summary>
    private async Task ApplyExternalChangeAsync(ExternalPictureEdit link, byte[] data)
    {
        var session = link.Session;
        if (!ReferenceEquals(session, _session) || !session.Document.Contains(link.Image)) return;
        ImportedImage imported;
        try { imported = ImageImporter.Import(session.Document, data, link.FileName); }
        catch (InvalidDataException)
        {
            Flash($"The picture saved in {PictureSettings.EditorName} could not be read. Save it as PNG or JPEG.");
            return;
        }
        bool editorOpen = OpenPictureEditor is { } open && ReferenceEquals(open.Session.Image, link.Image);
        string current = editorOpen ? OpenPictureEditor!.Session.SourceAssetId : link.Image.SourceAssetId ?? link.Image.AssetId;
        if (imported.Pixels.AssetId == current) return;   // saved without a change: the same picture, nothing to update
        if (OpenPictureEditor is { } dialog && editorOpen)
        {
            PixelBuffer pixels;
            try
            {
                var asset = session.Document.Assets.Get(imported.Pixels.AssetId)!;
                pixels = await BackgroundWork.RunAsync(this, "Picture Editor", "Reading the changed picture...", _ => ImageCodec.Decode(asset.Data));
            }
            catch (InvalidDataException) { Flash("The changed picture could not be read."); return; }
            if (OpenPictureEditor is { } still && ReferenceEquals(still, dialog)) dialog.ReplaceOriginal(imported.Pixels.AssetId, pixels);
            return;
        }
        if (await session.ReplacePictureOriginalAsync(link.Image, imported.Pixels, "Update Picture")) Flash($"Picture updated from {PictureSettings.EditorName}.");
        else if (session.LastImageError is { } error) Flash(error);
        QueueRefresh();
    }
}
