using DeSignMage.Core.Editing;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Shell;

/// <summary>
/// The link between one picture in the design and its copy in another program (Picture Editor ▸ Edit in...): the original is written to a temporary file, the
/// other program opens it, and <see cref="Changed"/> is raised with the file's new content whenever it is saved there. The file is looked at once a second
/// (<see cref="Poll"/>); a change counts when the file has stopped changing (same time and size on two looks in a row) and can be read, so a program that
/// writes the file in pieces is never read half way.
/// </summary>
internal sealed class ExternalPictureEdit : IDisposable
{
    private (DateTime Written, long Length) _applied, _pending;
    private bool _hasPending;

    public ExternalPictureEdit(EditorSession session, ImageObject image, byte[] original, string mediaType)
    {
        Session = session;
        Image = image;
        string stem = SafeName(Path.GetFileNameWithoutExtension(image.SourceName ?? image.Name ?? ""));
        FileName = (stem.Length > 0 ? stem : "picture") + ExtensionOf(mediaType);
        Folder = Path.Combine(Path.GetTempPath(), "DeSignMage", "edit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Folder);
        FilePath = Path.Combine(Folder, FileName);
        File.WriteAllBytes(FilePath, original);
        _applied = Stamp();
    }

    public EditorSession Session { get; }
    public ImageObject Image { get; }
    public string FileName { get; }
    public string Folder { get; }
    public string FilePath { get; }

    /// <summary>When the last change was taken over (null = none yet).</summary>
    public DateTime? LastUpdate { get; private set; }

    /// <summary>The file was saved in the other program; the argument is its new content.</summary>
    public event Action<byte[]>? Changed;

    private (DateTime, long) Stamp()
    {
        var info = new FileInfo(FilePath);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length) : default;
    }

    /// <summary>Looks at the file once. Called every second by the window (and directly by tests).</summary>
    public void Poll()
    {
        var stamp = Stamp();
        if (stamp == default || stamp == _applied) { _hasPending = false; return; }
        if (!_hasPending || _pending != stamp)
        {
            _pending = stamp;
            _hasPending = true;      // wait for one more look: is it still being written?
            return;
        }
        byte[] data;
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            data = new byte[stream.Length];
            stream.ReadExactly(data);
        }
        catch (IOException) { return; }          // still locked: look again
        catch (UnauthorizedAccessException) { return; }
        _applied = stamp;
        _hasPending = false;
        LastUpdate = DateTime.Now;
        Changed?.Invoke(data);
    }

    public void Dispose()
    {
        try { Directory.Delete(Folder, recursive: true); }
        catch (IOException) { }                  // the other program may still have it open; the temporary folder is cleaned up by Windows later
        catch (UnauthorizedAccessException) { }
    }

    private static string ExtensionOf(string mediaType) => mediaType switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        _ => ".png",
    };

    private static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !bad.Contains(c)).ToArray()).Trim();
        return clean.Length > 40 ? clean[..40] : clean;
    }
}
