using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DeSignMage.Feedback;

namespace DeSignMage.App.Reporting;

/// <summary>
/// The program-wide feedback service. <see cref="StartForReal"/> (from <c>Program.Main</c>) is the only way a real webhook ever gets
/// loaded; until then (every test, every headless run) <see cref="Get"/> hands out an inert service that can only write to a temp folder.
/// </summary>
internal static class FeedbackHost
{
    private const string EmbeddedName = "DeSignMage.feedback-webhook.txt";

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
    /// Normal startup: log file, webhook (embedded or beside the exe), outbox, crash hooks. Never throws: feedback is optional.
    /// </summary>
    public static FeedbackService StartForReal(bool backgroundWork)
    {
        var paths = AppPaths.Default;
        try { AppLog.Initialize(paths.Logs); }
        catch (Exception) { }

        IReportSender? sender = null;
        try
        {
            var config = WebhookConfig.Load(ReadEmbedded, AppContext.BaseDirectory);
            if (config.Config is { } webhook)
            {
                Redactor.RegisterSecrets(webhook.Secrets());
                sender = new DiscordSender(webhook, DiscordSender.CreateClient());
                AppLog.Info("feedback", $"Sending is set up{(webhook.ForumThreads ? " (forum threads)" : "")}");
            }
            else AppLog.Info("feedback", config.Problem ?? "Sending isn't set up (no feedback-webhook.txt)");
        }
        catch (Exception ex)
        {
            AppLog.Error("feedback", "Reading the feedback setup failed", ex);
        }

        var service = new FeedbackService(paths, sender, new ReportBuilderOptions { ShrinkScreenshot = ScreenshotTools.Shrink });
        service.Crash.FatalNotice = ShowFatalNotice;
        service.Crash.Install();
        _current = service;
        BackgroundWork = backgroundWork;
        IsReal = true;
        return service;
    }

    private static string? ReadEmbedded()
    {
        using var stream = typeof(FeedbackHost).Assembly.GetManifestResourceStream(EmbeddedName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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

    /// <summary>The program is about to die: say so, and say where her design went. (The report itself is sent the next time she starts it.)</summary>
    private static void ShowFatalNotice(CrashRecord record)
    {
        try
        {
            string where = record.Recovered.Count > 0
                ? $"Your design was saved to:\n{record.Directory}\n\n"
                : "";
            MessageBox(IntPtr.Zero,
                $"DeSign Mage had an unexpected problem and has to close.\n\n{where}A report will be sent automatically the next time you start the program.",
                "DeSign Mage", 0x10 /* MB_ICONERROR */);
        }
        catch (Exception) { }
    }

    public static string AppVersionText => typeof(FeedbackHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
}
