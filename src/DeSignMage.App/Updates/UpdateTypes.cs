namespace DeSignMage.App.Updates;

/// <summary>
/// A newer version that can be installed. <see cref="Handle"/> is whatever the <see cref="IUpdateClient"/> needs to find it again;
/// <see cref="Notes"/> are its release notes (Markdown, from docs/release-notes/&lt;version&gt;.md when it was packed), if any.
/// </summary>
internal sealed record UpdateOffer(string Version, object? Handle = null, string? Notes = null);

/// <summary>Release notes cut down to a few plain lines for the "Update required" window.</summary>
internal static class ReleaseNotes
{
    /// <summary>
    /// The first <paramref name="maxLines"/> lines of text of <paramref name="markdown"/> (headings and blank lines left out, list items as
    /// "• ", emphasis, code marks and link targets removed), at most <paramref name="maxChars"/> characters; "…" ends it when something was
    /// cut. Null when there is no text.
    /// </summary>
    public static string? Brief(string? markdown, int maxLines = 4, int maxChars = 280)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;
        var lines = new List<string>();
        bool cut = false;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.Trim('-', '*', '_', ' ').Length == 0) continue;   // blank, heading, rule
            if (lines.Count == maxLines) { cut = true; break; }
            bool item = line.Length > 1 && line[0] is '-' or '*' or '+' && line[1] == ' ';
            if (item) line = line[2..].TrimStart();
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]*)\]\([^)]*\)", "$1");   // [text](url) -> text
            line = line.Replace("**", "").Replace("__", "").Replace("`", "");
            lines.Add(item ? "• " + line : line);
        }
        if (lines.Count == 0) return null;
        string text = string.Join("\n", lines);
        if (text.Length > maxChars)
        {
            int end = text.LastIndexOf(' ', maxChars);
            text = text[..(end > maxChars / 2 ? end : maxChars)].TrimEnd(' ', ',', ';', '.');
            cut = true;
        }
        return cut ? text + " …" : text;
    }
}

/// <summary>
/// What the program needs from the update machinery. The real one is <see cref="VelopackUpdateClient"/>; tests use fakes, so no
/// test ever touches the network or an install.
/// </summary>
internal interface IUpdateClient
{
    /// <summary>True only when the program was installed by the setup program (not the portable zip, not a development run).</summary>
    bool IsInstalled { get; }

    string CurrentVersion { get; }

    /// <summary>An update that was downloaded earlier and waits to be installed (no network needed); null when there is none.</summary>
    UpdateOffer? DownloadedUpdate();

    /// <summary>Asks the update server whether there is a newer version. Null: up to date. Throws when the server cannot be reached.</summary>
    Task<UpdateOffer?> CheckAsync(CancellationToken ct);

    /// <summary>Downloads the update; <paramref name="progress"/> gets 0..100.</summary>
    Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct);

    /// <summary>Closes this program, installs the downloaded update and starts the new version with <paramref name="restartArgs"/>. Does not return when it works.</summary>
    void ApplyAndRestart(UpdateOffer offer, IReadOnlyList<string> restartArgs);
}

/// <summary>Knows whether other copies of the program are running.</summary>
internal interface IInstanceGuard
{
    /// <summary>True when no other DeSign Mage was running when this one started: a "fresh start".</summary>
    bool IsOnlyInstance { get; }

    /// <summary>True while another copy is showing the "Update required" window (a second copy must not start under it).</summary>
    bool UpdateRunningElsewhere { get; }

    /// <summary>This copy is (or is no longer) showing the "Update required" window.</summary>
    void SetUpdating(bool updating);
}
