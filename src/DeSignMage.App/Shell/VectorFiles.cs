using DeSignMage.Core.Import;

namespace DeSignMage.App.Shell;

/// <summary>
/// Vector files that are not our own (PDF, Illustrator, EPS, SVG): File ▸ Open turns one into a new document, dragging one onto the window
/// imports it into the current design through the Import dialog. The reader behind both is <see cref="Importer"/>, plugged in once at start-up.
/// </summary>
internal static class VectorFiles
{
    /// <summary>The reader for these files, or null while none is plugged in (then the program says it cannot read them yet).</summary>
    public static IVectorImporter? Importer { get; set; }

    public static bool IsVectorFile(string path) => VectorFileTypes.IsVectorFile(path);

    /// <summary>The words shown when a vector file is opened or dropped and no reader is available.</summary>
    public static string CannotRead(string path) =>
        $"DeSign Mage cannot read {Path.GetExtension(path).ToUpperInvariant().TrimStart('.')} files yet.";
}

/// <summary>What the Import dialog remembers (for the next import in this session).</summary>
internal sealed record ImportSettings(bool AsGroup = true);

/// <summary>What the user chose in the Import dialog: the page that was read (with the plan of what it holds), and whether it comes in as one group.</summary>
internal sealed record ImportChoice(DeSignMage.Core.Model.Document Source, DeSignMage.Rendering.Export.ExportPlan Plan, bool AsGroup, int Page, string FileName);