using Avalonia.Platform.Storage;
using DeSignMage.App.Shell;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
using DeSignMage.Formats.Palettes;

namespace DeSignMage.App;

/// <summary>File ▸ Load Color Palettes / Load Color Palette File: color palettes (.PAL, .CLR) from any folder or file.</summary>
public partial class MainWindow
{
    /// <summary>Tests choose the folder instead of the picker (null = cancel).</summary>
    internal Func<string?>? PaletteFolderOverride { get; set; }

    /// <summary>Tests choose the files instead of the picker (null or empty = cancel).</summary>
    internal Func<IReadOnlyList<string>?>? PaletteFilesOverride { get; set; }

    private async Task LoadPaletteFolderAsync()
    {
        string? folder;
        if (PaletteFolderOverride is { } hook) folder = hook();
        else
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Load color palettes from a folder", AllowMultiple = false });
            folder = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        }
        if (folder is null) return;

        var problems = new List<string>();
        var palettes = PaletteLibrary.LoadFolder(folder, problems);
        if (palettes.Count > 0) ColorUiState.RememberFolder(folder);
        ReportLoadedPalettes(palettes, problems, $"No palette files (.PAL or .CLR) found in {folder}");
    }

    private async Task LoadPaletteFilesAsync()
    {
        IReadOnlyList<string>? paths;
        if (PaletteFilesOverride is { } hook) paths = hook();
        else
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Load color palette files",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("Color palettes (*.PAL, *.CLR)") { Patterns = ["*.pal", "*.clr", "*.PAL", "*.CLR"] },
                    FilePickerFileTypes.All,
                ],
            });
            paths = picked.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        }
        if (paths is not { Count: > 0 }) return;

        var palettes = new List<Palette>();
        var problems = new List<string>();
        foreach (var path in paths)
        {
            try { palettes.Add(PaletteLibrary.LoadFile(path)); }
            catch (Exception e) when (e is PaletteFormatException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetFileName(path)}: {e.Message}");
            }
        }
        ReportLoadedPalettes(palettes, problems, "None of those files could be read as a color palette");
    }

    private void ReportLoadedPalettes(IReadOnlyList<Palette> palettes, List<string> problems, string nothingMessage)
    {
        foreach (var p in problems) AppLog.Warn("palette", $"palette not loaded: {p}");
        int added = _colorState!.AddPalettes(palettes);
        AppLog.Info("palette", $"loaded {added} palettes, {problems.Count} problems");
        LastPaletteProblems = problems;
        if (added == 0) { Flash(nothingMessage); return; }
        Flash(problems.Count == 0
            ? $"Loaded {added} color palette{(added == 1 ? "" : "s")}"
            : $"Loaded {added} color palette{(added == 1 ? "" : "s")}; {problems.Count} file{(problems.Count == 1 ? "" : "s")} could not be read");
    }

    /// <summary>What the last palette load could not read (for tests).</summary>
    internal IReadOnlyList<string> LastPaletteProblems { get; private set; } = [];
}
