using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DeSignMage.App.Shell;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
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

    private async Task<bool> SaveToAsync(string path)
    {
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
        if (document == _session.Document && document.Revision == revision) _session.History.MarkSaved();
        _recent.Add(path);
        RefreshPanels();
        AppLog.Info("file", $"saved {_documentTitle} ({_session.Document.ObjectCount} objects)");
        Flash($"Saved {_documentTitle}");
        return true;
    }

    /// <summary>A small picture of the page stored in the file (for file browsers and previews later). Never fails a save.</summary>
    private static byte[]? MakeThumbnail(Document document)
    {
        try
        {
            var page = document.Page;
            double dpi = Math.Max(1, 72 * 256 / Math.Max(page.Width, page.Height));
            return DocumentExporter.RenderPng(document, new ExportOptions { Dpi = dpi, WhiteBackground = true });   // a thumbnail is shown on paper; only exports are transparent
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
            _exportSettings = ExportDialog.Remember(chosen);

            var (label, pattern) = request.Format switch
            {
                ExportFormat.Svg => ("SVG pictures (*.svg)", "*.svg"),
                ExportFormat.Pdf => ("PDF documents (*.pdf)", "*.pdf"),
                _ => ("PNG pictures (*.png)", "*.png"),
            };
            var basis = _documentPath ?? _sourceLayoutPath;
            IStorageFolder? start = basis is not null && Path.GetDirectoryName(basis) is { } dir ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export",
                SuggestedFileName = Path.GetFileNameWithoutExtension(_documentTitle) + request.Extension,
                DefaultExtension = request.Extension.TrimStart('.'),
                ShowOverwritePrompt = true,
                SuggestedStartLocation = start,
                FileTypeChoices = [new FilePickerFileType(label) { Patterns = [pattern] }],
            });
            if (file?.TryGetLocalPath() is not { } picked) return;
            path = picked;
        }
        await RunExportAsync(request, path);
    }

    /// <summary>Writes the export (the file always gets the extension of the chosen format) right here on this thread. True on success. Tests use it; the window uses <see cref="RunExportAsync"/>.</summary>
    internal bool RunExport(ExportRequest request, string path)
    {
        path = WithExportExtension(request, path);
        try { WriteExport(_session.Document, request, path, ExportTitle()); }
        catch (Exception e) { return ExportFailed(path, e); }
        return ExportDone(request, path);
    }

    /// <summary>Same as <see cref="RunExport"/>, but the drawing and writing happen on a worker thread with the progress window if they take a while.</summary>
    internal async Task<bool> RunExportAsync(ExportRequest request, string path)
    {
        path = WithExportExtension(request, path);
        var document = _session.Document;
        string title = ExportTitle();
        try
        {
            await BackgroundWork.RunAsync(this, "Export", $"Exporting {Path.GetFileName(path)}...", _ => WriteExport(document, request, path, title));
        }
        catch (Exception e) { return ExportFailed(path, e); }
        return ExportDone(request, path);
    }

    private static string WithExportExtension(ExportRequest request, string path) =>
        Path.GetExtension(path).Equals(request.Extension, StringComparison.OrdinalIgnoreCase) ? path : path + request.Extension;

    private string ExportTitle() => Path.GetFileNameWithoutExtension(_documentTitle);

    /// <summary>Draws the design and writes it next to the target, then swaps it in, so a failed export never leaves half a file.</summary>
    private static void WriteExport(Document document, ExportRequest request, string path, string title)
    {
        var temp = path + ".exporting";
        try
        {
            var bytes = DocumentExporter.Render(document, request.Format, request.Options with { Title = title });
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
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

    private bool ExportDone(ExportRequest request, string path)
    {
        LastSaveError = null;
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

    /// <summary>Shows a short message in the help bar for a few seconds ("Saved x.dsm").</summary>
    private void Flash(string message)
    {
        _flash = message;
        HintText.Text = message;
        _flashTimer?.Stop();
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer?.Stop();
            _flash = null;
            HintText.Text = _session.Hint;
        };
        _flashTimer.Start();
    }
}



