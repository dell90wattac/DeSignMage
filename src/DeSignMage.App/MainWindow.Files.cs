using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DeSignMage.App.Shell;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
using DeSignMage.Core.Geometry;
using DeSignMage.Formats.Lyt;
using DeSignMage.Formats.Native;
using DeSignMage.Rendering.Export;

namespace DeSignMage.App;

/// <summary>File ▸ New / Save / Save As / Export, Open Recent, and the "save changes?" question when closing .</summary>
public partial class MainWindow
{
    /// <summary>The .dsm this design is saved as; null for a new design or one imported from a legacy file (Save then asks for a name).</summary>
    private string? _documentPath;

    /// <summary>The legacy layout (or earlier-build document) this design was imported from. Never written to; only used to suggest a name for Save As.</summary>
    private string? _sourceLayoutPath;

    private readonly RecentFiles _recent = new();
    private bool _closeConfirmed;
    private ExportSettings _exportSettings = new();
    private string? _flash;
    private DispatcherTimer? _flashTimer;

    internal string? DocumentPath => _documentPath;

    /// <summary>What the last failed save or export said (null after a success). For tests.</summary>
    internal string? LastSaveError { get; private set; }

    /// <summary>Tests choose the Save As file instead of the picker: gets the suggested file name, returns the path (null = cancel).</summary>
    internal Func<string, string?>? SavePathOverride { get; set; }

    /// <summary>Tests answer the Export dialog and file picker in one go (null result = cancel).</summary>
    internal Func<(ExportRequest Request, string Path)?>? ExportOverride { get; set; }

    internal IReadOnlyList<string> RecentPaths => _recent.Items;

    // ---------------------------------------------------------------- New

    private async Task NewDocumentAsync()
    {
        if (!await ConfirmDiscardAsync("creating a new design")) return;
        NewDocument();
    }

    private void NewDocument()
    {
        AppLog.Info("file", "new design");
        Surface.Session = null;
        _documentTitle = "Untitled";
        _documentPath = null;
        _sourceLayoutPath = null;
        LoadSession(new EditorSession(DemoContent.NewDocument()));
    }

    // ---------------------------------------------------------------- Save

    /// <summary>
    /// File ▸ Save: writes the current .dsm. A new design, or one imported from a legacy .lyt or an earlier-build document, asks for a
    /// name first (so those files are never overwritten). False if nothing was saved.
    /// </summary>
    internal Task<bool> SaveAsync() => SaveCoreAsync(saveAs: false);

    internal Task<bool> SaveAsAsync() => SaveCoreAsync(saveAs: true);

    private async Task<bool> SaveCoreAsync(bool saveAs)
    {
        var path = _documentPath;
        if (saveAs || path is null)
        {
            path = await PickSavePathAsync();
            if (path is null) return false;
        }
        return await SaveToAsync(path);
    }

    private string SuggestedFileName()
    {
        var basis = _documentPath ?? _sourceLayoutPath;
        var name = basis is not null ? Path.GetFileNameWithoutExtension(basis) : _documentTitle;
        return (string.IsNullOrWhiteSpace(name) ? "Untitled" : name) + DsmFormat.Extension;
    }

    private async Task<string?> PickSavePathAsync()
    {
        string suggested = SuggestedFileName();
        string? chosen;
        if (SavePathOverride is { } hook) chosen = hook(suggested);
        else
        {
            var basis = _documentPath ?? _sourceLayoutPath;
            IStorageFolder? start = basis is not null && Path.GetDirectoryName(basis) is { } dir ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save As",
                SuggestedFileName = suggested,
                DefaultExtension = "dsm",
                ShowOverwritePrompt = true,
                SuggestedStartLocation = start,
                FileTypeChoices = [new FilePickerFileType("DeSign Mage documents (*.dsm)") { Patterns = ["*.dsm"] }],
            });
            chosen = file?.TryGetLocalPath();
        }
        if (chosen is null) return null;

        // Only .dsm is ever written here: a typed ".lyt" (or any other ending) never turns into a file other programs would mistake for theirs.
        var ext = Path.GetExtension(chosen);
        string fixedPath = ext.Equals(DsmFormat.Extension, StringComparison.OrdinalIgnoreCase) ? chosen
            : ext.Equals(".lyt", StringComparison.OrdinalIgnoreCase) || ext.Equals(DsmFormat.LegacyExtension, StringComparison.OrdinalIgnoreCase)
                ? Path.ChangeExtension(chosen, DsmFormat.Extension)
            : chosen + DsmFormat.Extension;
        if (fixedPath != chosen && File.Exists(fixedPath) &&
            !await ShowDialogAsync("Save As", $"{Path.GetFileName(fixedPath)} already exists. Replace it?", "Replace", "Cancel"))
            return null;
        return fixedPath;
    }

    /// <summary>True for a legacy layout (.lyt). Those files are never written over, by Save, Save As or Export: the program can't yet keep everything they hold.</summary>
    internal static bool IsLegacyLayout(string path) => Path.GetExtension(path).Equals(".lyt", StringComparison.OrdinalIgnoreCase);

    private async Task<bool> SaveToAsync(string path)
    {
        // Last line of defense: Save only ever writes .dsm, whatever path reached here.
        if (!Path.GetExtension(path).Equals(DsmFormat.Extension, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Error("file", $"refused to save over {Path.GetFileName(path)}: not a {DsmFormat.Extension} file");
            LastSaveError = $"Only {DsmFormat.Extension} files are saved; {Path.GetFileName(path)} was left untouched.";
            _ = ShowDialogAsync("Cannot save", LastSaveError, "OK", null);
            return false;
        }
        var document = _session.Document;
        long revision = document.Revision;
        try
        {
            // Packing a design with photos and drawing its thumbnail can take seconds: on a worker thread, with progress shown if slow.
            await BackgroundWork.RunAsync(this, "Save", $"Saving {Path.GetFileName(path)}...",
                _ => DsmWriter.Save(document, path, new DsmSaveOptions { Thumbnail = MakeThumbnail(document) }));
        }
        catch (Exception e)
        {
            AppLog.Error("file", $"save failed for {Path.GetFileName(path)}", e);
            LastSaveError = e is IOException or UnauthorizedAccessException or DsmFormatException
                ? e.Message
                : $"Unexpected problem ({e.GetType().Name}: {e.Message}).";
            _ = ShowDialogAsync("Cannot save", $"{Path.GetFileName(path)} could not be saved.{Environment.NewLine}{Environment.NewLine}{LastSaveError}", "OK", null);
            return false;
        }

        LastSaveError = null;
        _documentPath = Path.GetFullPath(path);
        _sourceLayoutPath = null;
        _documentTitle = Path.GetFileName(path);
        // Edited during the first moments of the save (before the progress window appeared)? Then the file is not the current design.
        if (document == _session.Document && document.Revision == revision)
        {
            _session.History.MarkSaved();
            DropRecoveryCopy();   // the file holds everything now
        }
        _recent.Add(path);
        RefreshPanels();
        AppLog.Info("file", $"saved {_documentTitle} ({_session.Document.ObjectCount} objects)");
        Flash($"Saved {_documentTitle}");
        return true;
    }

    /// <summary>A small PNG of the page stored in the file (for file browsers and previews later), with real transparency where nothing is drawn. Never fails a save.</summary>
    private static byte[]? MakeThumbnail(Document document)
    {
        try
        {
            var page = document.Page;
            double dpi = Math.Max(1, 72 * 256 / Math.Max(page.Width, page.Height));
            return DocumentExporter.RenderPng(document, new ExportOptions { Dpi = dpi });   // transparent like every export, so a desktop preview shows whether the transparency worked
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- Export

    internal async Task ExportAsync()
    {
        ExportRequest request;
        string path;
        if (ExportOverride is { } hook)
        {
            if (hook() is not var (r, p)) return;
            request = r;
            path = p;
        }
        else
        {
            var dialog = new ExportDialog(_session.Document, _session.Selection.Items.ToArray(), _exportSettings);
            await dialog.ShowDialog(this);
            if (dialog.Result is not { } chosen) return;
            request = chosen;
            _exportSettings = dialog.CurrentSettings();

            var format = ExportFormats.Get(request.Format);
            var (label, pattern) = (format.PickerLabel, "*" + format.Extension);
            var basis = _documentPath ?? _sourceLayoutPath;
            IStorageFolder? start = basis is not null && Path.GetDirectoryName(basis) is { } dir ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null;
            bool legacy = request.Format == ExportFormat.Lyt;
            string suggested = Path.GetFileNameWithoutExtension(_documentTitle) + request.Extension;
            if (legacy && basis is not null && Path.GetDirectoryName(basis) is { } folder) suggested = Path.GetFileName(FreeLegacyLayoutPath(Path.Combine(folder, suggested)));
            while (true)
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export",
                    SuggestedFileName = suggested,
                    DefaultExtension = request.Extension.TrimStart('.'),
                    ShowOverwritePrompt = !legacy,   // a legacy layout is never replaced, so there is nothing to confirm
                    SuggestedStartLocation = start,
                    FileTypeChoices = [new FilePickerFileType(label) { Patterns = [pattern] }],
                });
                if (file?.TryGetLocalPath() is not { } picked) return;
                path = picked;
                string target = WithExportExtension(request, path);
                if (!IsLegacyLayout(target) || !File.Exists(target)) break;
                await ShowDialogAsync("Export", LegacyLayoutExistsMessage(target), "Choose another name", null);
                suggested = Path.GetFileName(FreeLegacyLayoutPath(target));
                start = Path.GetDirectoryName(target) is { } again ? await StorageProvider.TryGetFolderFromPathAsync(again) : start;
            }
        }
        await RunExportAsync(request, path);
    }

    private static string LegacyLayoutExistsMessage(string path) =>
        $"{Path.GetFileName(path)} already exists. Existing .lyt layouts are never replaced, so nothing in them can be lost.{Environment.NewLine}{Environment.NewLine}Please choose a new name.";

    /// <summary>"name.lyt", or "name (2).lyt", "name (3).lyt"... whichever doesn't exist yet in that folder.</summary>
    internal static string FreeLegacyLayoutPath(string path)
    {
        if (!File.Exists(path)) return path;
        string folder = Path.GetDirectoryName(path) ?? "";
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Writes the export (the file always gets the extension of the chosen format) right here on this thread. True on success. Tests use it; the window uses <see cref="RunExportAsync"/>.</summary>
    internal bool RunExport(ExportRequest request, string path)
    {
        path = WithExportExtension(request, path);
        var warnings = new List<string>();
        try { WriteExport(_session.Document, request, path, ExportTitle(), _session.OffsetService, _session.BooleanService, warnings); }
        catch (Exception e) { return ExportFailed(path, e); }
        return ExportDone(request, path, warnings);
    }

    /// <summary>Same as <see cref="RunExport"/>, but the drawing and writing happen on a worker thread with the progress window if they take a while.</summary>
    internal async Task<bool> RunExportAsync(ExportRequest request, string path)
    {
        path = WithExportExtension(request, path);
        var document = _session.Document;
        string title = ExportTitle();
        var offsets = _session.OffsetService;
        var booleans = _session.BooleanService;
        var warnings = new List<string>();
        try
        {
            await BackgroundWork.RunAsync(this, "Export", $"Exporting {Path.GetFileName(path)}...", _ => WriteExport(document, request, path, title, offsets, booleans, warnings));
        }
        catch (Exception e) { return ExportFailed(path, e); }
        return ExportDone(request, path, warnings);
    }

    private static string WithExportExtension(ExportRequest request, string path) =>
        Path.GetExtension(path).Equals(request.Extension, StringComparison.OrdinalIgnoreCase) ? path : path + request.Extension;

    private string ExportTitle() => Path.GetFileNameWithoutExtension(_documentTitle);

    /// <summary>Draws the design and writes it next to the target, then swaps it in, so a failed export never leaves half a file.</summary>
    private static void WriteExport(Document document, ExportRequest request, string path, string title, IOffsetService? offsets, IBooleanService? booleans, List<string> warnings)
    {
        var temp = path + ".exporting";
        try
        {
            byte[] bytes;
            if (request.Format == ExportFormat.Lyt)
            {
                bytes = LytWriter.ToBytes(document, LegacyLayoutOptions(document, title, offsets, booleans), out var result);
                warnings.AddRange(result.Warnings);
            }
            else bytes = DocumentExporter.Render(document, request.Format, request.Options with { Title = title });
            // An existing legacy layout is never replaced (checked again at the very end, so nothing can slip past the dialog).
            bool legacy = IsLegacyLayout(path);
            if (legacy && File.Exists(path)) throw new IOException(LegacyLayoutExistsMessage(path));
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: !legacy);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private bool ExportFailed(string path, Exception e)
    {
        AppLog.Error("file", $"export failed for {Path.GetFileName(path)}", e);
        LastSaveError = e is ExportException or IOException or UnauthorizedAccessException ? e.Message : $"Unexpected problem ({e.GetType().Name}: {e.Message}).";
        _ = ShowDialogAsync("Cannot export", $"{Path.GetFileName(path)} could not be written.{Environment.NewLine}{Environment.NewLine}{LastSaveError}", "OK", null);
        return false;
    }

    /// <summary>What the legacy layout writer needs: a title, the small JPEG preview stored in the file, both geometry services (so strokes survive as shapes) and a picture re-encoder.</summary>
    private static LytWriteOptions LegacyLayoutOptions(Document document, string title, IOffsetService? offsets, IBooleanService? booleans)
    {
        byte[]? preview = null;
        try
        {
            bool wide = document.Page.Width >= document.Page.Height;
            preview = DocumentExporter.RenderJpeg(document, new ExportOptions { IncludePasteboard = true, PixelWidth = wide ? 400 : null, PixelHeight = wide ? null : 400, JpegQuality = 85 });
        }
        catch (ExportException) { }   // a preview is optional
        return new LytWriteOptions
        {
            Title = title.Length > 40 ? title[..40] : title,
            ThumbnailJpeg = preview,
            OffsetService = offsets,
            BooleanService = booleans,
            ImageEncoder = asset =>
            {
                using var bitmap = SkiaSharp.SKBitmap.Decode(asset.Data);
                if (bitmap is null) return null;
                using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
                return image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
            },
        };
    }

    /// <summary>What the last export had to simplify or leave out (the legacy layout only), in plain words. For tests.</summary>
    internal IReadOnlyList<string> LastExportWarnings { get; private set; } = [];

    private bool ExportDone(ExportRequest request, string path, IReadOnlyList<string> warnings)
    {
        LastSaveError = null;
        LastExportWarnings = warnings.ToList();
        if (warnings.Count > 0)
        {
            string list = string.Join(Environment.NewLine, warnings.Take(12).Select(w => "• " + w));
            if (warnings.Count > 12) list += $"{Environment.NewLine}… and {warnings.Count - 12} more.";
            _ = ShowDialogAsync("Exported", $"{Path.GetFileName(path)} was written, but some things had to be simplified:{Environment.NewLine}{Environment.NewLine}{list}", "OK", null);
        }
        AppLog.Info("file", $"exported {Path.GetFileName(path)} ({request.Extension})");
        Flash($"Exported {Path.GetFileName(path)}");
        return true;
    }

    // ---------------------------------------------------------------- closing

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || !_session.History.IsModified) return;
        e.Cancel = true;   // ask first; the window closes itself again if the answer is Save or Don't Save
        _ = CloseAfterConfirmAsync();
    }

    private async Task CloseAfterConfirmAsync()
    {
        if (!await ConfirmDiscardAsync("closing")) return;
        _closeConfirmed = true;
        Close();
    }

    // ---------------------------------------------------------------- Open Recent

    private void InitRecentMenu()
    {
        if (MainMenu.Items.OfType<MenuItem>().FirstOrDefault() is not { } file) return;
        var recent = new MenuItem { Header = "Open _Recent" };
        recent.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
        recent.SubmenuOpened += (_, _) => RebuildRecentMenu(recent);
        file.Items.Insert(Math.Min(2, file.Items.Count), recent);
        RebuildRecentMenu(recent);
    }

    private void RebuildRecentMenu(MenuItem menu)
    {
        menu.Items.Clear();
        var items = _recent.Items;
        if (items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }
        foreach (var path in items)
        {
            var item = new MenuItem { Header = Path.GetFileName(path).Replace("_", "__") };
            ToolTip.SetTip(item, path);
            item.Click += (_, _) => _ = OpenRecentAsync(path);
            menu.Items.Add(item);
        }
    }

    internal async Task OpenRecentAsync(string path)
    {
        if (!File.Exists(path))
        {
            _recent.Remove(path);
            await ShowDialogAsync("Open Recent", $"{path} no longer exists.", "OK", null);
            return;
        }
        await OpenFileAsync(path);
    }

    // ---------------------------------------------------------------- status flash

    /// <summary>
    /// Shows a short message on the Info Line for a few seconds ("Saved x.dsm"): it takes the place of the counts, the bitmaps and the pointer
    /// position, and they come back when it goes (R38). A message too long for the room is trimmed (the full text is its tooltip).
    /// </summary>
    internal void Flash(string message)
    {
        _flash = message;
        FlashText.Text = message;
        ToolTip.SetTip(FlashText, message);
        FlashText.IsVisible = true;
        _flashTimer?.Stop();
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _flashTimer.Tick += (_, _) => EndFlash();
        _flashTimer.Start();
    }

    /// <summary>Takes the message off the Info Line (the timer does it after six seconds; tests call it).</summary>
    internal void EndFlash()
    {
        _flashTimer?.Stop();
        _flash = null;
        FlashText.IsVisible = false;
    }

    /// <summary>The message shown on the Info Line right now, or null (tests).</summary>
    internal string? CurrentFlash => _flash;
}



