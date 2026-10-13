using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using DeSignMage.App.Shell;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
using DeSignMage.Formats.Lyt;
using DeSignMage.Formats.Native;

namespace DeSignMage.App;

/// <summary>Opening files: File &gt; Open, Open Recent, drag and drop, and a path on the command line. Reads native documents (also those from earlier builds) and legacy .lyt layouts.</summary>
public partial class MainWindow
{
    private string _documentTitle = "Untitled";

    /// <summary>What the last failed open said (null after a success). For tests.</summary>
    internal string? LastOpenError { get; private set; }

    /// <summary>Things the last open skipped or approximated. For tests.</summary>
    internal IReadOnlyList<string> LastOpenWarnings { get; private set; } = [];

    /// <summary>Tests replace the message and confirmation dialogs; null shows the real ones.</summary>
    internal Func<string, string, Task<bool>>? DialogOverride { get; set; }

    /// <summary>Tests answer the "save changes?" question (title, message); null shows the real one.</summary>
    internal Func<string, string, Task<SaveChoice>>? SaveChangesOverride { get; set; }

    /// <summary>Tests choose the file to open instead of the picker (null result = cancel).</summary>
    internal Func<Task<string?>>? OpenPathOverride { get; set; }

    internal enum SaveChoice { Save, Discard, Cancel }

    private void InitFileOpening()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOverFiles);
        AddHandler(DragDrop.DropEvent, OnDropFiles);
    }

    // ---------------------------------------------------------------- File > Open

    private async Task OpenFileDialogAsync()
    {
        if (OpenPathOverride is { } pick)
        {
            if (await pick() is { } chosen) await OpenFileAsync(chosen);
            return;
        }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType($"DeSign Mage documents and legacy layouts (*{DsmFormat.Extension}, *{DsmFormat.LegacyExtension}, *.lyt)") { Patterns = [$"*{DsmFormat.Extension}", $"*{DsmFormat.LegacyExtension}", "*.lyt"] },
                new FilePickerFileType($"DeSign Mage documents (*{DsmFormat.Extension}, *{DsmFormat.LegacyExtension})") { Patterns = [$"*{DsmFormat.Extension}", $"*{DsmFormat.LegacyExtension}"] },
                new FilePickerFileType("Legacy layouts (*.lyt)") { Patterns = ["*.lyt"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count == 0) return;
        if (files[0].TryGetLocalPath() is { } path) await OpenFileAsync(path);
    }

    // ---------------------------------------------------------------- drag and drop

    private void OnDragOverFiles(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropFiles(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        e.Handled = true;
        var paths = files?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
        // Pictures land where they were dropped (when that is over the canvas); layouts open as before (MainWindow.Images.cs).
        var at = e.GetPosition(Surface);
        bool overCanvas = at.X >= 0 && at.Y >= 0 && at.X <= Surface.Bounds.Width && at.Y <= Surface.Bounds.Height;
        HandleDroppedFiles(paths, overCanvas ? _session.Viewport.ToDoc(new DeSignMage.Core.Geometry.Vec2(at.X, at.Y)) : null);
    }

    // ---------------------------------------------------------------- opening

    /// <summary>Opens <paramref name="path"/> in this window, replacing the current document. False if it was not opened.</summary>
    internal async Task<bool> OpenFileAsync(string path)
    {
        if (!await ConfirmDiscardAsync($"opening {Path.GetFileName(path)}")) return false;
        return await LoadFileAsync(path);
    }

    /// <summary>Reads a file on a worker thread (with the progress window when it takes a while), then replaces the current design with it.</summary>
    private async Task<bool> LoadFileAsync(string path)
    {
        string name = Path.GetFileName(path);
        string ext = Path.GetExtension(path);
        bool native = DsmFormat.IsNativeFile(path);
        bool earlierBuild = string.Equals(ext, DsmFormat.LegacyExtension, StringComparison.OrdinalIgnoreCase);
        if (!native && !string.Equals(ext, ".lyt", StringComparison.OrdinalIgnoreCase))
            return Fail(name, $"DeSign Mage opens its own documents ({DsmFormat.Extension}, and {DsmFormat.LegacyExtension} from earlier builds) and legacy layouts (.lyt); {ext} files are not supported yet.");

        Document document;
        IReadOnlyList<string> warnings;
        try
        {
            (document, warnings) = await BackgroundWork.RunAsync<(Document, IReadOnlyList<string>)>(this, "Open", $"Opening {name}...", _ =>
            {
                if (native)
                {
                    var result = DsmReader.Load(path);
                    return (result.Document, result.Warnings);
                }
                var lyt = LytImporter.Open(path);
                return (lyt.Document, lyt.Warnings);
            }, cancellable: true);
        }
        catch (OperationCanceledException)
        {
            return false;   // the user pressed Cancel; the current design stays as it is
        }
        catch (Exception e) when (e is LytFormatException or DsmFormatException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("file", $"open failed for {name}: {e.GetType().Name}: {e.Message}");
            return Fail(name, e.Message);
        }
        catch (Exception e)
        {
            AppLog.Error("file", $"open crashed on {name}", e);
            return Fail(name, $"The file could not be read ({e.GetType().Name}: {e.Message}). It may be damaged.");
        }

        Surface.Session = null;
        _documentTitle = name;
        // A legacy layout or a document from an earlier build is never saved over: Save asks for a new .dsm name instead.
        bool saveInPlace = native && !earlierBuild;
        _documentPath = saveInPlace ? Path.GetFullPath(path) : null;
        _sourceLayoutPath = saveInPlace ? null : Path.GetFullPath(path);
        LoadSession(new EditorSession(document));
        _session.History.MarkSaved();
        LastOpenError = null;
        LastOpenWarnings = warnings;
        _recent.Add(path);
        RefreshPanels();
        AppLog.Info("file", $"opened {name} ({document.ObjectCount} objects, {warnings.Count} warnings)");
        foreach (var w in warnings.Take(10)) AppLog.Warn("file", $"open warning for {name}: {w}");

        if (warnings.Count > 0)
        {
            string text = string.Join(Environment.NewLine, warnings.Take(12).Select(w => "• " + w));
            if (warnings.Count > 12) text += $"{Environment.NewLine}… and {warnings.Count - 12} more.";
            _ = ShowDialogAsync(name, $"Opened, but some things were skipped or approximated:{Environment.NewLine}{Environment.NewLine}{text}", "OK", null);
        }
        return true;
    }

    private bool Fail(string name, string message)
    {
        AppLog.Warn("file", $"could not open {name}");
        LastOpenError = message;
        _ = ShowDialogAsync($"Cannot open {name}", message, "OK", null);
        return false;
    }

    // ---------------------------------------------------------------- unsaved changes

    /// <summary>
    /// Before something throws the current design away (New, Open, closing): if it has unsaved changes, asks Save / Don't Save /
    /// Cancel. True = go ahead (saved or discarded), false = the user cancelled or the save did not happen.
    /// </summary>
    internal async Task<bool> ConfirmDiscardAsync(string what)
    {
        if (!_session.History.IsModified) return true;
        var choice = await AskSaveChangesAsync("Unsaved changes", $"Save the changes to {_documentTitle} before {what}?");
        return choice switch
        {
            SaveChoice.Save => await SaveAsync(),
            SaveChoice.Discard => true,
            _ => false,
        };
    }

    private async Task<SaveChoice> AskSaveChangesAsync(string title, string message)
    {
        if (SaveChangesOverride is { } hook) return await hook(title, message);
        return await ShowChoiceAsync(title, message, "Save", "Don't Save", "Cancel") switch
        {
            0 => SaveChoice.Save,
            1 => SaveChoice.Discard,
            _ => SaveChoice.Cancel,
        };
    }

    // ---------------------------------------------------------------- dialogs

    /// <summary>A small modal message. Returns true for the first button, false for the second (or closing the window).</summary>
    private async Task<bool> ShowDialogAsync(string title, string message, string yes, string? no)
    {
        if (DialogOverride is { } hook) return await hook(title, message);
        return await ShowChoiceAsync(title, message, no is null ? [yes] : [yes, no]) == 0;
    }

    /// <summary>A small modal message with any number of buttons. Returns the index of the one pressed, or -1 if the window was closed.</summary>
    private Task<int> ShowChoiceAsync(string title, string message, params string[] buttons)
    {
        var done = new TaskCompletionSource<int>();
        int answer = -1;
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        DialogScale.Attach(window, 520);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = UiScale.Sp(8) };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var b = new Button { Content = buttons[i], HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = i == 0 }.ScaledMinWidth(80);
            b.Click += (_, _) => { answer = index; window.Close(); };
            row.Children.Add(b);
        }

        var text = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var body = new DockPanel { Margin = UiScale.Space(16) };
        DockPanel.SetDock(row, Dock.Bottom);
        row.Margin = UiScale.Space(0, 14, 0, 0);
        body.Children.Add(row);
        body.Children.Add(new ScrollViewer { Content = text, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        window.Content = body;
        window.Closed += (_, _) => done.TrySetResult(answer);
        window.ShowDialog(this);
        return done.Task;
    }
}
