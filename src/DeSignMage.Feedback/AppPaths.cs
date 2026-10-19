namespace DeSignMage.Feedback;

/// <summary>Where the program keeps its own files: logs, crash reports and the feedback outbox (all under <c>%LOCALAPPDATA%\DeSignMage</c>).</summary>
public sealed class AppPaths(string root)
{
    public string Root { get; } = root;
    public string Logs => Path.Combine(Root, "logs");
    public string Crash => Path.Combine(Root, "crash");
    /// <summary>Recovery copies of designs with unsaved changes (auto-save); outside the installed program's folder, so updates leave it alone.</summary>
    public string Recovery => Path.Combine(Root, "recovery");
    public string FeedbackFolder => Path.Combine(Root, "feedback");
    public string Outbox => Path.Combine(FeedbackFolder, "outbox");
    /// <summary>Reports that could not be delivered for a month; kept (never deleted) so nothing is lost silently.</summary>
    public string Expired => Path.Combine(FeedbackFolder, "expired");
    public string SettingsFile => Path.Combine(FeedbackFolder, "settings.json");

    public static AppPaths Default { get; } =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeSignMage"));
}
