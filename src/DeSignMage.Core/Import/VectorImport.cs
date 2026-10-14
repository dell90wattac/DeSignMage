using DeSignMage.Core.Model;

namespace DeSignMage.Core.Import;

/// <summary>What reading a vector file (PDF, AI, EPS, SVG) gave: the drawing as a document of its own, what could not be carried over, and how many pages the file has.</summary>
/// <param name="Document">The file's page as a document: page size from the file, its shapes, text and pictures as ordinary objects (units: points, y down, origin at the page's top left).</param>
/// <param name="Warnings">Things that were skipped or approximated, in plain words (shown to the user, never fatal).</param>
/// <param name="PageCount">Pages in the file (1 for formats without pages); <see cref="Document"/> holds only the page that was asked for.</param>
public sealed record VectorReadResult(Document Document, IReadOnlyList<string> Warnings, int PageCount = 1);

/// <summary>
/// Reads vector files that are not our own (PDF, Illustrator, EPS, SVG). File ▸ Open turns the result into a new document; dragging the file onto
/// the window shows the Import dialog and puts the objects into the current design. An implementation lives next to the other file readers; the
/// application plugs it in (<c>VectorFiles.Importer</c>).
/// </summary>
public interface IVectorImporter
{
    /// <summary>True for files this importer knows how to read (decided by the extension, not by reading).</summary>
    bool CanRead(string path);

    /// <summary>
    /// Reads page <paramref name="page"/> (1-based; ignored by formats without pages). Throws <see cref="IOException"/> for a file that cannot be read and
    /// <see cref="InvalidDataException"/> for one that is damaged or of a kind it cannot make sense of; the message is shown to the user as it is.
    /// </summary>
    VectorReadResult Read(string path, int page = 1);
}

/// <summary>The file types of the vector formats that open as a new document and import by drag and drop.</summary>
public static class VectorFileTypes
{
    public static readonly IReadOnlyList<string> Extensions = [".pdf", ".ai", ".eps", ".svg"];

    public static bool IsVectorFile(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
