using DeSignMage.App.Shell;
using DeSignMage.Core.Geometry;
using DeSignMage.Feedback;

namespace DeSignMage.App;

/// <summary>
/// Dragging a PDF, Illustrator, EPS or SVG file onto the window (R30): the Import dialog shows what would come in; after Import the objects arrive
/// selected-to-be-placed like a paste (R18): they hang on the pointer (at the drop point until it moves) and the next click puts them down as one
/// undo step, Esc cancels. File ▸ Open of the same files makes a new document instead (<c>LoadFileAsync</c>). Our own files and legacy layouts
/// dropped here still open (<c>HandleDroppedFiles</c>).
/// </summary>
public partial class MainWindow
{
    private ImportSettings _importSettings = new();

    /// <summary>Tests answer the Import dialog without showing it (null result = cancelled).</summary>
    internal Func<string, ImportChoice?>? ImportChoiceOverride { get; set; }

    /// <summary>The last Import dialog that was shown (for tests).</summary>
    internal ImportDialog? LastImportDialog { get; set; }

    /// <summary>The path of the last vector file whose objects were handed over for placing. For tests.</summary>
    internal string? LastImportedPath { get; private set; }

    /// <summary>
    /// Shows the Import dialog for <paramref name="path"/> and, after Import, lets the user place the objects with a click. False if nothing is
    /// being placed (no reader, cancelled, or nothing to import). <paramref name="dropPoint"/> is where the file was dropped (document points), if known.
    /// </summary>
    internal async Task<bool> ImportVectorFileAsync(string path, Vec2? dropPoint)
    {
        string name = Path.GetFileName(path);
        if (VectorFiles.Importer is not { } importer || !importer.CanRead(path))
        {
            AppLog.Warn("file", $"no reader for {name}");
            _ = ShowDialogAsync($"Cannot import {name}", VectorFiles.CannotRead(path), "OK", null);
            return false;
        }

        ImportChoice? choice;
        if (ImportChoiceOverride is { } hook) choice = hook(path);
        else
        {
            var dialog = new ImportDialog(path, importer, _importSettings);
            LastImportDialog = dialog;
            await dialog.ShowDialog(this);
            _importSettings = dialog.CurrentSettings();
            choice = dialog.Result;
        }
        if (choice is null) return false;

        var items = ImportDialog.BuildItems(choice.Plan, choice.AsGroup, Path.GetFileNameWithoutExtension(name));
        if (items.Count == 0) return false;
        LastImportedPath = path;
        // They hang on the pointer like a paste (R18); until it moves they hang where the file was dropped.
        _session.BeginPlacing(items, $"Import {name}", dropPoint is { } d ? _session.Viewport.ToScreen(d) : null);
        AppLog.Info("file", $"importing {name}: {items.Count} object{(items.Count == 1 ? "" : "s")} waiting to be placed");
        Surface.Focus();
        return true;
    }
}
