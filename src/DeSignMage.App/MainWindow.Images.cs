using System.Globalization;
using Avalonia;
using DeSignMage.Feedback;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using DeSignMage.App.Shell;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Import;
using DeSignMage.Formats.Native;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using DeSignMage.Imaging;

namespace DeSignMage.App;

/// <summary>Pictures (C.4): File ▸ Import, dropping picture files, and the Image menu's rotate, flip, invert, crop, rasterize and 1:1 zoom.</summary>
public partial class MainWindow
{
    private static readonly SkiaImageService ImageService = new();

    /// <summary>Tests replace the file picker: returns the files to import (null = show the real picker).</summary>
    internal Func<Task<IReadOnlyList<string>>>? PickImagesOverride { get; set; }

    /// <summary>Tests replace the Rasterize resolution dialog: gets the page area, returns dpi (null = cancel; null override = real dialog).</summary>
    internal Func<Rect2, Task<double?>>? RasterizeDpiOverride { get; set; }

    /// <summary>What the last import said went wrong (null after a clean one). For tests.</summary>
    internal string? LastImportError { get; private set; }

    private void RegisterImageCommands()
    {
        void Add(string id, string header, Action run, Func<bool> can) =>
            _commands.Add(new AppCommand(id, header, run) { CanExecute = can });
        bool HasImage() => _session.CanEditImages;

        Add("file.import", "_Import...", () => _ = ImportFileDialogAsync(), () => true);

        // Names and order follow the usual Image menu (the two rotate labels are the right way round here).
        // Shortcuts (Alt+V, Ctrl+F7, Alt+1, Ctrl+Alt+I) stay unbound until the user approves them.
        Add("image.crop", "Crop Tool", () => _session.BeginCrop(), () => _session.CanCrop);
        Add("image.flatten", "Flatten Picture", () => _session.FlattenPictures(), () => _session.CanFlatten);
        Add("image.editPicture", "Edit Photo...", () => { if (_session.EditPictureTarget is { } picture) _session.RequestPictureEdit(picture); }, () => _session.CanEditPicture);
        ImageActions.OpenEditor = () => { if (_session.EditPictureTarget is { } picture) _session.RequestPictureEdit(picture); };
        Add("image.resample", "Resample...", () => _ = ResampleSelectionAsync(), () => _session.CanResample);
        ImageActions.OpenResample = () => { if (_session.CanResample) _ = ResampleSelectionAsync(); };
        Add("image.rotateCW", "Rotate 90 degrees clockwise", () => ImageActions.RotateClockwise(_session), HasImage);
        Add("image.rotateCCW", "Rotate 90 degrees counter-clockwise", () => ImageActions.RotateCounterClockwise(_session), HasImage);
        Add("image.flipH", "Flip Horizontal", () => ImageActions.FlipHorizontal(_session), HasImage);
        Add("image.flipV", "Flip Vertical", () => ImageActions.FlipVertical(_session), HasImage);
        Add("image.invert", "Invert", () => ImageActions.Invert(_session), HasImage);
        Add("image.zoom11", "1:1 Zoom", () =>
        {
            _session.ScreenScale = RenderScaling;
            _session.ZoomToImagePixels();
        }, HasImage);

        // Not built yet: shown, disabled.
        // Rasterize is switched off for now (the user): the menu entry is greyed. The session code (EditorSession.RasterizeSelection,
        // which keeps the originals) and RasterizeSelectionAsync below stay so it can be switched back on by registering the command.
        _commands.Add(AppCommand.NotImplemented("image.rasterize", "Rasterize..."));
        _commands.Add(AppCommand.NotImplemented("image.commitRotation", "Commit Rotation"));
        _commands.Add(AppCommand.NotImplemented("image.extractAlpha", "Extract Alpha Channel"));
        _commands.Add(AppCommand.NotImplemented("image.saveAs", "Save As..."));
        _commands.Add(AppCommand.NotImplemented("image.fillLibManage", "Manage Image Fill Library..."));
        _commands.Add(AppCommand.NotImplemented("image.fillLibUpdate", "Update Image Fill Library"));
        _commands.Add(AppCommand.NotImplemented("image.fillLibAdd", "Add to Image Fill Library.."));

        ImageActions.ShowError = message => _ = ShowDialogAsync("Bitmap", message, "OK", null);
    }

    private void AttachImageService(EditorSession session)
    {
        session.ImageService = ImageService;
        session.ScreenScale = RenderScaling;
        session.PictureEditRequested += (_, request) => _ = OpenPictureEditorAsync(session, request.Image);   // a double-click on a picture, Image > Edit Picture...
        session.ClipboardChanged += (_, _) => OnSessionClipboardChanged(session);   // tags the system clipboard (MainWindow.Paste.cs)
        // Photos are decoded, turned and re-encoded on a worker thread with a progress window (BackgroundWork), so the window stays alive.
        session.Background = (title, work) => BackgroundWork.RunAsync<object?>(this, Title ?? "DeSign Mage", title, work);
        session.ImageFinished += (_, _) =>
        {
            if (session.LastImageError is { } error) ShowImageError(error);
            QueueRefresh();
        };
    }

    private void ShowImageError(string message) => _ = ShowDialogAsync("Bitmap", message, "OK", null);

    // ---------------------------------------------------------------- File > Import

    private async Task ImportFileDialogAsync()
    {
        IReadOnlyList<string> paths;
        if (PickImagesOverride is { } pick) paths = await pick();
        else
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("Pictures") { Patterns = ImageCodec.FilePatterns },
                    FilePickerFileTypes.All,
                ],
            });
            paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        }
        if (paths.Count > 0) await ImportImagesAsync(paths, null);
    }

    /// <summary>
    /// Places each picture at its natural size (its file's dpi, 300 when it has none), shrunk to fit the page,
    /// centered on <paramref name="center"/> (the middle of the current view when null). More than one is stacked with a small offset.
    /// One undo step. Returns how many were placed; the ones that could not be read are reported together.
    /// </summary>
    internal async Task<int> ImportImagesAsync(IReadOnlyList<string> paths, Vec2? center, bool hangOnCursor = false)
    {
        LastImportError = null;
        var doc = _session.Document;
        var middle = center ?? _session.Viewport.VisibleDocRect.Center;
        var placed = new List<ImageObject>();
        var problems = new List<string>();

        // Reading, decoding and (for formats the program stores as PNG) re-encoding a large photo takes seconds: all of it runs on a
        // worker thread, with the progress window if it takes more than a moment. The result comes back as plain data.
        List<(string Name, ImagePixels? Pixels, double? Dpi, string? Problem)> results;
        try
        {
            results = await BackgroundWork.RunAsync(this, "Import", paths.Count == 1 ? $"Reading {Path.GetFileName(paths[0])}..." : $"Reading {paths.Count} pictures...",
                ct =>
                {
                    var list = new List<(string Name, ImagePixels? Pixels, double? Dpi, string? Problem)>();
                    foreach (var path in paths)
                    {
                        ct.ThrowIfCancellationRequested();
                        string name = Path.GetFileName(path);
                        try
                        {
                            var bytes = File.ReadAllBytes(path);
                            var imported = ImageImporter.Import(doc, bytes, name);
                            list.Add((name, imported.Pixels, imported.Dpi, null));
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                        {
                            list.Add((name, null, null, $"{name}: {e.Message}"));
                            AppLog.Warn("file", $"import failed for {name}: {e.GetType().Name}: {e.Message}");
                        }
                    }
                    return list;
                }, cancellable: true);
        }
        catch (OperationCanceledException)
        {
            return 0;   // the user cancelled the import
        }
        foreach (var (name, pixels, dpi, problem) in results)
        {
            if (pixels is null) { problems.Add(problem!); continue; }
            var at = middle + new Vec2(placed.Count * 18, placed.Count * 18);
            placed.Add(ImagePlacement.Create(pixels, name, dpi ?? ImagePlacement.DefaultDpi, doc.Page, at));
        }

        // A paste of files hangs on the cursor like any paste; File > Import and dropping place at once.
        if (hangOnCursor) _session.BeginPlacing(placed, placed.Count == 1 ? "Paste Picture" : "Paste Pictures");
        else _session.PlaceImages(placed);
        AppLog.Info("file", $"imported {placed.Count} of {paths.Count} picture(s)");
        if (problems.Count > 0)
        {
            LastImportError = string.Join(Environment.NewLine, problems);
            _ = ShowDialogAsync("Import", "Some files could not be imported:" + Environment.NewLine + Environment.NewLine + LastImportError, "OK", null);
        }
        return placed.Count;
    }

    /// <summary>
    /// Files dropped on the window. A layout (.lyt) still opens as before; otherwise picture files are imported at
    /// <paramref name="docPoint"/> (where they were dropped). Anything else gets the usual "cannot open" message.
    /// </summary>
    internal void HandleDroppedFiles(IReadOnlyList<string> paths, Vec2? docPoint)
    {
        if (paths.Count == 0) return;
        // Our own documents and legacy layouts open (R30); a dropped PDF, AI, EPS or SVG is imported into this design through the Import dialog.
        if (paths.FirstOrDefault(p => string.Equals(Path.GetExtension(p), ".lyt", StringComparison.OrdinalIgnoreCase) || DsmFormat.IsNativeFile(p)) is { } document)
        {
            _ = OpenFileAsync(document);
            return;
        }
        var vectors = paths.Where(VectorFileTypes.IsVectorFile).ToList();
        if (vectors.Count > 0)
        {
            if (vectors.Count > 1) Flash($"Importing {Path.GetFileName(vectors[0])}; drop the other {vectors.Count - 1} file{(vectors.Count == 2 ? "" : "s")} one at a time.");
            _ = ImportVectorFileAsync(vectors[0], docPoint);
            return;
        }
        var pictures = paths.Where(ImageCodec.IsImageFile).ToList();
        if (pictures.Count > 0) _ = ImportImagesAsync(pictures, docPoint);
        else _ = OpenFileAsync(paths[0]);   // says what is supported
    }

    // ---------------------------------------------------------------- Picture Editor

    /// <summary>The Picture Editor that is open (for tests); null otherwise.</summary>
    internal PictureEditorDialog? OpenPictureEditor { get; private set; }

    /// <summary>
    /// Opens the Picture Editor on <paramref name="image"/>. The original pixels are decoded off the UI thread (with the progress window for a big photo);
    /// while the window is open the design behind it shows the preview, and OK is one undo step.
    /// </summary>
    internal async Task OpenPictureEditorAsync(EditorSession session, ImageObject image)
    {
        if (OpenPictureEditor is not null || !ReferenceEquals(session, _session) || !session.Document.Contains(image)) return;
        string sourceId = image.SourceAssetId ?? image.AssetId;
        if (session.Document.Assets.Get(sourceId) is not { } asset)
        {
            await ShowDialogAsync("Picture Editor", "The picture data is missing from this document, so it cannot be edited.", "OK", null);
            return;
        }
        PixelBuffer source;
        try
        {
            source = await BackgroundWork.RunAsync(this, "Picture Editor", "Opening the picture...", _ => ImageCodec.Decode(asset.Data));
        }
        catch (InvalidDataException e)
        {
            await ShowDialogAsync("Picture Editor", "The picture could not be read: " + e.Message, "OK", null);
            return;
        }
        if (!ReferenceEquals(session, _session) || !session.Document.Contains(image) || !image.HasPixelSize) return;
        var dialog = new PictureEditorDialog(session, image, source, sourceId);
        OpenPictureEditor = dialog;
        try { await dialog.ShowDialog(this); }
        finally { OpenPictureEditor = null; }
        QueueRefresh();
    }

    // ---------------------------------------------------------------- Resample

    /// <summary>The Resample dialog that is open (for tests); null otherwise.</summary>
    internal ResampleDialog? OpenResampleDialog { get; private set; }

    /// <summary>
    /// Image ▸ Resample...: asks for the new pixel size of the selected picture (its printed size stays) and applies it as one undo step.
    /// Resizing a big photo runs in the background with the progress window, like the other picture commands.
    /// </summary>
    private async Task ResampleSelectionAsync()
    {
        if (_session.ResampleTarget is not { } image) return;
        var dialog = new ResampleDialog(image);
        OpenResampleDialog = dialog;
        try { await dialog.ShowDialog(this); }
        finally { OpenResampleDialog = null; }
        if (dialog.Result is not { } size) return;
        if (!_session.Resample(size.Width, size.Height) && _session.LastImageError is { } error)
            await ShowDialogAsync("Resample", error, "OK", null);
    }

    // ---------------------------------------------------------------- Rasterize

    private async Task RasterizeSelectionAsync()
    {
        if (!_session.CanRasterize) return;
        var bounds = _session.RasterizeBounds;
        double? dpi = RasterizeDpiOverride is { } ask ? await ask(bounds) : await AskRasterizeDpiAsync(bounds);
        if (dpi is not { } value) return;
        if (!_session.RasterizeSelection(value) && _session.LastImageError is { } error)
            await ShowDialogAsync("Rasterize", error, "OK", null);
    }

    private async Task<double?> AskRasterizeDpiAsync(Rect2 bounds)
    {
        var dpiBox = new TextBox { Text = "300", HorizontalContentAlignment = HorizontalAlignment.Right }.ScaledWidth(80);
        var result = new TextBlock { Foreground = Shell.AppTheme.Brush("Ui.Muted") };
        var ok = new Button { Content = "OK", IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(80);
        var cancel = new Button { Content = "Cancel", IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(80);

        bool Parse(out double dpi) =>
            double.TryParse(dpiBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out dpi) && dpi >= 1;
        void Update()
        {
            if (!Parse(out var dpi)) { result.Text = "Enter a resolution in dots per inch."; ok.IsEnabled = false; return; }
            if (ImagePlacement.RasterSize(bounds, dpi) is not { } size)
            {
                result.Text = "That resolution would make the picture too large.";
                ok.IsEnabled = false;
                return;
            }
            result.Text = string.Create(CultureInfo.InvariantCulture, $"Result: {size.Width} x {size.Height} pixels, {size.Width * (long)size.Height * 4 / 1048576.0:0.0} MB");
            ok.IsEnabled = true;
        }
        dpiBox.TextChanged += (_, _) => Update();
        Update();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(8) };
        row.Children.Add(new TextBlock { Text = "Resolution:", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(dpiBox);
        row.Children.Add(new TextBlock { Text = "dpi", VerticalAlignment = VerticalAlignment.Center });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = UiScale.Sp(8) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = UiScale.Space(16), Spacing = UiScale.Sp(12) };
        panel.Children.Add(new TextBlock
        {
            Text = "Rasterize turns the selected objects into one bitmap (transparent where nothing is drawn).",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        panel.Children.Add(row);
        panel.Children.Add(result);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = "Rasterize",
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = DialogScale.Scrollable(panel),
        };
        DialogScale.Attach(window, 400);
        double? answer = null;
        ok.Click += (_, _) => { if (Parse(out var dpi)) answer = dpi; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        await window.ShowDialog(this);
        return answer;
    }
}
