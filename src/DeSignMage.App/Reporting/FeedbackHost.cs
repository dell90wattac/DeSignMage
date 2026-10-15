using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DeSignMage.Feedback;

namespace DeSignMage.App.Reporting;

/// <summary>
/// The program-wide feedback service. <see cref="StartForReal"/> (from <c>Program.Main</c>) is the only way a real sender ever gets
/// created; until then (every test, every headless run) <see cref="Get"/> hands out an inert service that can only write to a temp folder.
/// </summary>
internal static class FeedbackHost
{
    private static FeedbackService? _current;

    /// <summary>The service in use; tests assign their own (with a fake sender).</summary>
    public static FeedbackService? Current
    {
        get => _current;
        set => _current = value;
    }

    /// <summary>True only in a normal run of the program: the main window then picks up crash reports and starts the background retries.</summary>
    public static bool BackgroundWork { get; private set; }

    /// <summary>True after <see cref="StartForReal"/>: the program is running for real (never in tests), so UI-thread errors are caught and reported.</summary>
    public static bool IsReal { get; private set; }

    /// <summary>Raised on the UI thread when an unhandled error was caught and the program keeps running.</summary>
    public static event Action? UiErrorCaught;

    public static FeedbackService Get() => _current ??= new FeedbackService(
        new AppPaths(Path.Combine(Path.GetTempPath(), $"DeSignMage-inert-{Environment.ProcessId}")), sender: null);

    /// <summary>
    /// Normal startup: log file, the relay sender, outbox, crash hooks. Never throws: feedback is optional.
    /// </summary>
    public static FeedbackService StartForReal(bool backgroundWork)
    {
        var paths = AppPaths.Default;
        try { AppLog.Initialize(paths.Logs); }
        catch (Exception) { }

        IReportSender? sender = null;
        try
        {
            var config = RelayConfig.FromSettings(new SettingsStore(paths.SettingsFile));   // read once, for the optional address override
            sender = new RelaySender(config, RelaySender.CreateClient());
            AppLog.Info("feedback", $"Reports go to the relay at {config.Url.Host}");
        }
        catch (Exception ex)
        {
            AppLog.Error("feedback", "Setting up the feedback relay failed", ex);
        }

        var service = new FeedbackService(paths, sender, new ReportBuilderOptions { ShrinkScreenshot = ScreenshotTools.Shrink });
        service.Crash.FatalNotice = ShowFatalNotice;
        service.Crash.Install();
        _current = service;
        BackgroundWork = backgroundWork;
        IsReal = true;
        return service;
    }

    // ---------------------------------------------------------------- Avalonia's dispatcher

    private static readonly Queue<long> RecentUiErrors = new();

    internal static void ResetUiErrorWindow()
    {
        lock (RecentUiErrors) RecentUiErrors.Clear();
    }

    /// <summary>
    /// An exception escaped on the UI thread. Write the crash report (and an emergency save), keep the program running, and tell the
    /// window. Returns false (let it crash) only when errors come in a flood, so a broken redraw cannot loop forever.
    /// </summary>
    public static bool HandleUiException(Exception ex)
    {
        var record = _current?.Crash.Report(ex, "Dispatcher", fatal: false);
        if (record is null) AppLog.Error("ui", "Unhandled exception on the UI thread (repeat)", ex);

        long now = Stopwatch.GetTimestamp();
        lock (RecentUiErrors)
        {
            RecentUiErrors.Enqueue(now);
            while (RecentUiErrors.Count > 0 && Stopwatch.GetElapsedTime(RecentUiErrors.Peek(), now).TotalSeconds > 10) RecentUiErrors.Dequeue();
            if (RecentUiErrors.Count > 25) return false;
        }
        if (ex is OutOfMemoryException or StackOverflowException) return false;
        UiErrorCaught?.Invoke();
        return true;
    }

    // ---------------------------------------------------------------- fatal crash

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>The program is about to die: say so, and say where her design went. (Nothing is sent now: the next start asks whether to send the report.)</summary>
    private static void ShowFatalNotice(CrashRecord record)
    {
        try
        {
            string where = record.Recovered.Count > 0
                ? $"Your design was saved to:\n{record.Directory}\n\n"
                : "";
            string later = _current?.AlwaysSendCrashes == true
                ? "A report will be sent the next time you start the program."
                : "The next time you start the program it will ask whether to send a report.";
            MessageBox(IntPtr.Zero,
                $"DeSign Mage had an unexpected problem and has to close.\n\n{where}{later}",
                "DeSign Mage", 0x10 /* MB_ICONERROR */);
        }
        catch (Exception) { }
    }

    public static string AppVersionText => typeof(FeedbackHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
}
