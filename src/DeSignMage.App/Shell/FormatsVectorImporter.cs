using DeSignMage.Core.Geometry;
using DeSignMage.Core.Import;
using DeSignMage.Formats.Vector;

namespace DeSignMage.App.Shell;

/// <summary>
/// The reader of PDF, Illustrator, EPS and SVG files the program uses: <see cref="VectorImport"/> of the Formats project behind the
/// <see cref="IVectorImporter"/> the shell knows. Pages are counted from 1 here (the reader counts from 0); a file it cannot make sense of
/// becomes an <see cref="InvalidDataException"/> with the reader's own message. Clipping needs the boolean service of the editor, if there is one.
/// </summary>
internal sealed class FormatsVectorImporter(Func<IBooleanService?> booleans) : IVectorImporter
{
    public bool CanRead(string path) => VectorImport.CanRead(path);

    public VectorReadResult Read(string path, int page = 1)
    {
        try
        {
            var result = VectorImport.Read(path, new VectorImportOptions { PageIndex = Math.Max(0, page - 1), BooleanService = booleans() });
            return new VectorReadResult(result.Document, result.Warnings, result.PageCount);
        }
        catch (VectorImportException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
    }
}
