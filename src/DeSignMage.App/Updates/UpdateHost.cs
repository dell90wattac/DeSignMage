using System.Reflection;
using DeSignMage.App.Shell;
using DeSignMage.Feedback;

namespace DeSignMage.App.Updates;

/// <summary>Where the installed copy lives, and why the person's own files can never be in it.</summary>
internal static class UpdatePaths
{
    /// <summary>The id the installer package carries (one place: <c>UpdatePackId</c> in Directory.Build.props, copied into the assembly).</summary>
    public static string PackId =>
        typeof(UpdatePaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "UpdatePackId")?.Value ?? "";

    /// <summary>The installer puts the program in <c>%LocalAppData%\&lt;pack id&gt;</c> and removes that whole folder on uninstall.</summary>
    public static string InstallRoot(string packId) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), packId);

    /// <summary>True when neither folder is the other or inside the other.</summary>
    public static bool AreSeparate(string installRoot, string dataRoot)
    {
        string a = Normalize(installRoot), b = Normalize(dataRoot);
        return !a.Equals(b, StringComparison.OrdinalIgnoreCase)
            && !a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
            && !b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/') + "\\";
}

/// <summary>
/// The program-wide update setup. <see cref="Begin"/> (from <c>Program.Main</c>) is the only way the real updater ever gets created;
/// until then (every test, every headless run) the program counts as "not installed" and nothing is checked.
/// </summary>
internal static class UpdateHost
{
    public const string SourceEnvironmentVariable = "DESIGNMAGE_UPDATE_SOURCE";
    public const string SourceSetting = "updates.source";

    private static BackgroundUpdater? _background;
    private static CancellationTokenSource? _cts;

    /// <summary>The updater in use; tests assign their own fake.</summary>
    public static IUpdateClient? Client { get; set; }

    public static IInstanceGuard? Guard { get; set; }

    /// <summary>The answer to "may this start require an update?"; null when updates are not in play (portable copy, development run, tests).</summary>
    public static Task<GateDecision>? StartupDecision { get; private set; }

    public static bool IsInstalled
    {
        get { try { return Client?.IsInstalled == true; } catch (Exception) { return false; } }
    }

    public static UpdateStatus Status => _background?.Status ?? (IsInstalled ? UpdateStatus.Unknown : new UpdateStatus(UpdateState.NotInstalled));

    /// <summary>What was typed on the command line, to hand to the new version after an update (a file to open).</summary>
    public static IReadOnlyList<string> RestartArgs { get; private set; } = [];

    public static event Action? StatusChanged;

    /// <summary>
    /// First thing a real start does (after the log exists): finds out whether this is an installed copy and a fresh start, and starts
    /// asking the update server while the window is being built. Never throws.
    /// </summary>
    public static void Begin(string[] args, bool isRealSession)
    {
        if (!isRealSession) return;
        try
        {
            RestartArgs = args;
            var guard = new InstanceGuard();
            Guard = guard;
            string? configured = Environment.GetEnvironmentVariable(SourceEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(configured)) configured = Reporting.FeedbackHost.Get().Settings.Get<string?>(SourceSetting, null);
            var client = new VelopackUpdateClient(UpdateSourceSpec.Resolve(configured));
            Client = client;
            if (client.IsInstalled)
                AppLog.Info("update", $"installed copy {client.CurrentVersion}, updates from {UpdateSourceSpec.Resolve(configured)}; {(guard.IsOnlyInstance ? "fresh start" : "another copy is running")}");
            else
                AppLog.Info("update", "not an installed copy (portable or development run): no automatic updates");
            StartupDecision = new UpdateGate(client, guard, m => AppLog.Warn("update", m)).DecideAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("update", "Setting up the updater failed; the program starts without it", ex);
            Client = null;
            StartupDecision = null;
        }
    }

    /// <summary>After the window is up: look for new versions now and then and download them quietly.</summary>
    public static void StartBackground()
    {
        if (Client is not { } client || _background is not null) return;
        _cts = new CancellationTokenSource();
        _background = new BackgroundUpdater(client, m => AppLog.Info("update", m));
        _background.Changed += () => StatusChanged?.Invoke();
        _ = Task.Run(() => _background.RunAsync(_cts.Token));
    }

    /// <summary>The window is closing: stop looking (a download in progress is dropped and starts again next time).</summary>
    public static void StopBackground() => _cts?.Cancel();
}
