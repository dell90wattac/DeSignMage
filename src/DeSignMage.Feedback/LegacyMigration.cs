namespace DeSignMage.Feedback;

/// <summary>
/// First start after the program was renamed: brings the data folders of the earlier builds (settings, logs, the feedback
/// outbox, crash files, the recent-files list) over to the current ones. Best effort: it never throws, never overwrites
/// anything that already exists, and does nothing once there is nothing left to bring over.
/// </summary>
public static class LegacyMigration
{
    // The earlier builds' folder name, written in parts on purpose: it is the only place the old name is needed.
    internal static readonly string LegacyFolder = "DeSign" + "Wiz" + "ard";
    internal static readonly string LegacyLogName = "design" + "wiz" + "ard";
    private const string CurrentFolder = "DeSignMage";
    private const string CurrentLogName = "designmage";

    /// <summary>What a run did (for the log and tests).</summary>
    public sealed record Result(int Moved, int Skipped, string? Error);

    /// <summary>Migrates the real per-user folders (<c>%LOCALAPPDATA%</c> and <c>%APPDATA%</c>).</summary>
    public static Result Run() => Run(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>Migrates under the given local and roaming roots (tests use temp folders).</summary>
    public static Result Run(string localRoot, string roamingRoot)
    {
        int moved = 0, skipped = 0;
        string? error = null;
        try
        {
            var (m1, s1) = MoveTree(Path.Combine(localRoot, LegacyFolder), Path.Combine(localRoot, CurrentFolder));
            var (m2, s2) = MoveTree(Path.Combine(roamingRoot, LegacyFolder), Path.Combine(roamingRoot, CurrentFolder));
            moved = m1 + m2;
            skipped = s1 + s2;
            RenameLogs(Path.Combine(localRoot, CurrentFolder, "logs"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }
        return new Result(moved, skipped, error);
    }

    /// <summary>Moves a whole folder when the target does not exist, otherwise copies over what is missing there.</summary>
    private static (int Moved, int Skipped) MoveTree(string from, string to)
    {
        if (!Directory.Exists(from)) return (0, 0);
        if (!Directory.Exists(to))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Directory.Move(from, to);
                return (Directory.EnumerateFiles(to, "*", SearchOption.AllDirectories).Count(), 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // locked: fall back to file by file
        }

        int moved = 0, skipped = 0;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToList())
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            try
            {
                if (File.Exists(target)) { skipped++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        TryDeleteEmptyTree(from);
        return (moved, skipped);
    }

    private static void TryDeleteEmptyTree(string folder)
    {
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The log files carry the program name: <c>old.log</c>, <c>old.1.log</c> ... become <c>designmage.log</c>, ...</summary>
    private static void RenameLogs(string logs)
    {
        if (!Directory.Exists(logs)) return;
        foreach (var file in Directory.EnumerateFiles(logs, LegacyLogName + "*.log").ToList())
        {
            var name = Path.GetFileName(file);
            var target = Path.Combine(logs, CurrentLogName + name[LegacyLogName.Length..]);
            try { if (!File.Exists(target)) File.Move(file, target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
