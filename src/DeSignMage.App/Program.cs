using Avalonia;
using DeSignMage.App.Reporting;
using DeSignMage.Feedback;

namespace DeSignMage.App;

internal static class Program
{
    /// <summary>
    /// Command line (for development):
    ///   --demo               open with sample artwork
    ///   --stress N           add N random shapes (performance check)
    ///   --guides             add sample ruler guides (one selected)
    ///   --text-scale X       interface text size for this run only (1 = 100%, 2 = 200%; not remembered)
    ///   --window-size WxH    open at this size instead of maximized (e.g. 1920x1080), for screenshots
    ///   --screenshot PATH    render the main window to a PNG after startup, then exit
    ///   --first-frame-exit PATH  write the milliseconds from process start to the first drawn frame into PATH, then exit (startup benchmark)
    ///   --crash-test thread|ui  throw a test exception 2 s after startup (on a worker thread: fatal; on the UI thread: survivable)
    ///   FILE                 open a document or a legacy .lyt layout (also what "Open with" passes)
    /// </summary>
    public static StartupOptions Options { get; private set; } = new();

    /// <summary>Environment.TickCount64 when Main began (startup benchmark).</summary>
    internal static long MainStartTicks { get; private set; }

    private static readonly long MainStartStamp = System.Diagnostics.Stopwatch.GetTimestamp();
    private static readonly List<(string Name, double Ms)> StartupMarks = [];

    /// <summary>Notes how long startup has taken so far (written next to the first-frame time by <c>--first-frame-exit</c>).</summary>
    internal static void Mark(string name)
    {
        lock (StartupMarks) StartupMarks.Add((name, System.Diagnostics.Stopwatch.GetElapsedTime(MainStartStamp).TotalMilliseconds));
    }

    internal static string MarksText()
    {
        lock (StartupMarks) return string.Join(" ", StartupMarks.Select(m => FormattableString.Invariant($"{m.Name}={m.Ms:0}")));
    }

    [STAThread]
    public static void Main(string[] args)
    {
        MainStartTicks = Environment.TickCount64;
        Options = StartupOptions.Parse(args);
        // Settings and logs of earlier builds are carried over before anything reads them (not in a development screenshot or crash-test run).
        var migration = Options.ScreenshotPath is null && Options.CrashTest is null ? LegacyMigration.Run() : null;
        // Log file, feedback setup and crash hooks first, so even a failure while starting is recorded.
        // A development screenshot run is not a session: it does no background sending.
        FeedbackHost.StartForReal(backgroundWork: Options.ScreenshotPath is null && Options.CrashTest is null);
        Mark("feedback-host");
        AppLog.Info("startup", $"DeSign Mage {FeedbackHost.AppVersionText} starting ({args.Length} command-line arguments)");
        if (migration is { Moved: > 0 } or { Skipped: > 0 } or { Error: not null })
            AppLog.Info("startup", $"brought data over from an earlier build: {migration.Moved} files moved, {migration.Skipped} left in place{(migration.Error is null ? "" : ", " + migration.Error)}");
        int exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        AppLog.Info("startup", $"exited normally (code {exitCode})");
        AppLog.Flush();
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

public sealed record StartupOptions(bool Demo = false, int Stress = 0, string? ScreenshotPath = null, bool Guides = false, string? OpenPath = null, string? CrashTest = null, double? TextScale = null, (int Width, int Height)? WindowSize = null, string? FirstFramePath = null)
{
    public static StartupOptions Parse(string[] args)
    {
        var o = new StartupOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--demo": o = o with { Demo = true }; break;
                case "--guides": o = o with { Guides = true }; break;
                case "--crash-test" when i + 1 < args.Length: o = o with { CrashTest = args[++i] }; break;   // development: "thread" (fatal) or "ui" (survivable)
                case "--stress" when i + 1 < args.Length: o = o with { Stress = int.Parse(args[++i]) }; break;
                case "--text-scale" when i + 1 < args.Length: o = o with { TextScale = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture) }; break;   // development: interface text size (1.0-2.5) for this run only, not remembered
                case "--window-size" when i + 1 < args.Length && args[i + 1].Split('x') is [var ww, var hh]: o = o with { WindowSize = (int.Parse(ww), int.Parse(hh)) }; i++; break;   // development: open at this size (e.g. 1920x1080) instead of maximized
                case "--screenshot" when i + 1 < args.Length: o = o with { ScreenshotPath = args[++i] }; break;
                case "--first-frame-exit" when i + 1 < args.Length: o = o with { FirstFramePath = args[++i] }; break;   // development: startup benchmark
                case var path when !path.StartsWith("--"): o = o with { OpenPath = path }; break;   // DeSignMage.exe file
            }
        }
        return o;
    }
}
