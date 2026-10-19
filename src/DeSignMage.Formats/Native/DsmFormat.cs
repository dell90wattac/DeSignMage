using System.Reflection;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Native;

/// <summary>
/// The native DeSign Mage document (<c>.dsm</c>): a zip container.
/// <code>
/// document.json        the design (versioned, human-readable JSON)
/// assets/&lt;sha256&gt;      embedded binary assets (placed images, later fonts), named by their content hash
/// thumbnail.png        optional preview of the page
/// </code>
/// <para>
/// <c>document.json</c> starts with a header (<c>format</c>, <c>formatVersion</c>, <c>minReaderVersion</c>, <c>app</c>, <c>savedUtc</c>),
/// then <c>page</c> (points, y down; <c>width</c>, <c>height</c> and, only when not 0, the top-left corner <c>x</c>, <c>y</c>), <c>activeLayer</c>, <c>layers</c> (each with its objects, bottom to top, groups
/// nested), <c>guides</c> and the list of <c>assets</c>. Values equal to their defaults are omitted. Earlier builds wrote the same
/// container with another extension (<see cref="LegacyExtension"/>); those files are still read (see <see cref="LegacyExtension"/> and <see cref="IsFormatMarker"/>).
/// </para>
/// </summary>
public static class DsmFormat
{
    public const string Extension = ".dsm";

    /// <summary>The extension earlier builds used for the same container. Opened, never written.</summary>
    public const string LegacyExtension = ".dsw";

    public const string FormatName = "DeSign Mage document";

    private static readonly System.Text.RegularExpressions.Regex MarkerPattern =
        new(@"^DeSign \w+ document$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>True for this format's marker and for the marker earlier builds wrote ("DeSign &lt;product&gt; document").</summary>
    public static bool IsFormatMarker(string? marker) => marker is not null && MarkerPattern.IsMatch(marker);

    /// <summary>True for a file name this program opens as a native document: <c>.dsm</c>, or the extension earlier builds used.</summary>
    public static bool IsNativeFile(string path) =>
        Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(LegacyExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The version this build writes. Bump it when the JSON layout changes and add a migration to <see cref="DsmReader"/>.
    /// <list type="bullet">
    /// <item>1: groups were objects of the layer that held their members (<c>kind: "group"</c> with <c>children</c>), so members were always next to each other in the drawing order.</item>
    /// <item>2 (R22): grouping and drawing order are separate. The layers list every drawn object in drawing order; groups are listed once, in <c>groups</c>,
    /// and objects (and groups) name the group they belong to with <c>group</c>. Version 1 files are read as before.</item>
    /// <item>3 (Picture Editor): a picture edited non-destructively has <c>source</c> (the untouched original, an asset) and <c>recipe</c> (crop, straighten,
    /// colors); its <c>asset</c> is the finished picture, so a reader that knows nothing of recipes still shows the right picture (and warns about the newer
    /// version). Files without these (versions 1 and 2) open as before.</item>
    /// <item>4 (live effects): an effect object that follows its source has <c>live</c> (which effect, its settings, the source's number, a frame) and its
    /// source has <c>linkId</c>. Older readers ignore both and show the effect as the plain object it also is; older files open with plain effects.</item>
    /// </list>
    /// </summary>
    public const int CurrentVersion = 4;

    /// <summary>
    /// Written as <c>minReaderVersion</c>: the oldest format version a reader must understand to open what we write.
    /// Only raised for changes an older reader cannot even approximately handle; additive changes leave it alone.
    /// (A version 1 reader opens a version 2 file with its objects in the right order, without the groups.)
    /// </summary>
    public const int MinReaderVersion = 1;

    public const string DocumentEntry = "document.json";
    public const string ThumbnailEntry = "thumbnail.png";
    public const string AssetFolder = "assets/";

    /// <summary>Nesting limit for groups (guards against hostile or corrupted files blowing the stack).</summary>
    public const int MaxGroupDepth = 256;

    /// <summary>The program's release number (only the program carries it; the libraries keep a fixed identity, see Directory.Build.props).</summary>
    internal static string DefaultAppVersion =>
        ((Assembly.GetEntryAssembly() ?? typeof(DsmFormat).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0").Split('+')[0];
}

/// <summary>A file that is not a usable DeSign Mage document (damaged, wrong type, or needs a newer program). The message is for the user.</summary>
public sealed class DsmFormatException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record DsmSaveOptions
{
    /// <summary>Written to the header so a later reader knows who wrote the file.</summary>
    public string AppVersion { get; init; } = DsmFormat.DefaultAppVersion;

    /// <summary>Optional PNG preview of the page (<c>thumbnail.png</c>).</summary>
    public byte[]? Thumbnail { get; init; }

    /// <summary>Time stamp written to the header; null = now. Tests pin it for byte-stable output.</summary>
    public DateTime? SavedUtc { get; init; }

    /// <summary>Write the JSON without indentation: smaller and quicker, for files nobody reads by eye (recovery copies).</summary>
    public bool Compact { get; init; }
}

public sealed class DsmLoadResult(Document document, int formatVersion, string appVersion, IReadOnlyList<string> warnings)
{
    public Document Document { get; } = document;

    /// <summary>The <c>formatVersion</c> stored in the file.</summary>
    public int FormatVersion { get; } = formatVersion;

    /// <summary>The program version that wrote the file ("" if not recorded).</summary>
    public string AppVersion { get; } = appVersion;

    /// <summary>Things that were skipped or approximated; shown to the user, never fatal.</summary>
    public IReadOnlyList<string> Warnings { get; } = warnings;

    /// <summary>Fonts that live text asks for but that are not installed (shown in a fallback), with how many texts use each.</summary>
    public IReadOnlyDictionary<string, int> MissingFonts { get; init; } = new Dictionary<string, int>();

    /// <summary>Objects (of any kind) that could not be read and were left out.</summary>
    public int SkippedObjects { get; init; }

    /// <summary>The saved preview, if the file has one.</summary>
    public byte[]? Thumbnail { get; init; }
}
