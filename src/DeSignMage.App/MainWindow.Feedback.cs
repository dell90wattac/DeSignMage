using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using DeSignMage.App.Reporting;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
using DeSignMage.Formats.Native;

namespace DeSignMage.App;

/// <summary>Feedback / Bug button and Help ▸ Send Feedback, automatic diagnostics, and the crash notice.</summary>
public partial class MainWindow
{
    private IReadOnlyList<string> _displayLines = [];
    private string? _noticeFolder;
    private HistoryLogger? _historyLogger;

    /// <summary>The dialog opened last (for tests).</summary>
    internal FeedbackDialog? LastFeedbackDialog { get; private set; }

    /// <summary>The one-line crash notice currently shown (null when hidden). For tests.</summary>
    internal string? NoticeMessage => NoticeBar.IsVisible ? NoticeText.Text : null;

    private void InitFeedback()
    {
        FeedbackButton.Click += (_, _) => _commands.Get("help.feedback").Execute();
        NoticeClose.Click += (_, _) => NoticeBar.IsVisible = false;
        NoticeOpenFolder.Click += (_, _) => OpenNoticeFolder();

        // What a crash report can say about the open design, even when it happens on another thread.
        var crash = FeedbackHost.Get().Crash;
        crash.Documents = () => [new CrashDocument(_documentTitle, path =>
        {
            DsmWriter.Save(_session.Document, path);
            NoteEmergencyCopy(path);   // lets the next start offer this copy for restoring (MainWindow.Recovery.cs)
        })];
        crash.UndoHistory = () => _session.History.UndoList.Select(c => c.Description).ToList();
        crash.DocumentSummary = DescribeDocument;
        crash.SystemInfoProvider = CollectSystemInfo;

        void OnUiError() => Dispatcher.UIThread.Post(() => Flash(FeedbackHost.Get().AlwaysSendCrashes
            ? "Something went wrong. A report was saved and will be sent to the developer."
            : "Something went wrong. A report was saved; you will be asked next time whether to send it."));
        FeedbackHost.UiErrorCaught += OnUiError;
        Closed += (_, _) => FeedbackHost.UiErrorCaught -= OnUiError;

        Opened += (_, _) => OnOpenedForFeedback();
        if (_options.CrashTest is { } how) Opened += (_, _) => ThrowTestCrash(how);
    }

    /// <summary>Development only (<c>--crash-test</c>): proves the crash hooks work in the real program.</summary>
    private static void ThrowTestCrash(string how)
    {
        if (how == "ui")
            _ = Task.Delay(2000).ContinueWith(_ => Dispatcher.UIThread.Post(
                () => throw new InvalidOperationException("Deliberate test exception on the UI thread (--crash-test ui)")));
        else
            new Thread(() =>
            {
                Thread.Sleep(2000);
                throw new InvalidOperationException("Deliberate test crash on a worker thread (--crash-test thread)");
            }) { IsBackground = true }.Start();
    }

    private void OnOpenedForFeedback()
    {
        _displayLines = ScreenshotTools.DescribeDisplays(this).ToList();
        AppLog.Info("startup", CollectSystemInfo().ToText());
        if (!FeedbackHost.BackgroundWork) return;
        var service = FeedbackHost.Get();
        _ = Task.Run(async () =>
        {
            try
            {
                await RecoveryPromptDone;   // the design comes first: "Restore it?" is asked before "Send the crash report?"
                // A crash report is only sent after the person said yes (the prompt), or when they chose "always send".
                if (service.FindCrashesToAsk() is { } prompt)
                    await Dispatcher.UIThread.InvokeAsync(() => AskAboutCrashesAsync(prompt));
                else if (await service.RecoverFromCrashAsync() is { } notice)
                    await Dispatcher.UIThread.InvokeAsync(() => ShowCrashNotice(notice));
            }
            catch (Exception ex) { AppLog.Error("crash", "Checking for an earlier crash failed", ex); }
            service.StartBackground();
        });
    }

    /// <summary>The crash prompt opened last (for tests).</summary>
    internal CrashPromptDialog? LastCrashPrompt { get; private set; }

    /// <summary>
    /// "DeSign Mage closed unexpectedly last time. Send the crash report?" Send queues and sends it (and remembers "always send" when
    /// ticked), Don't send keeps it on this computer, closing the window asks again at the next start. Shows the one-line notice after.
    /// </summary>
    internal async Task AskAboutCrashesAsync(CrashPrompt prompt)
    {
        var service = FeedbackHost.Get();
        // The restore question already dealt with the design: this one is only about the report.
        if (_restorePromptShown) prompt = prompt with { RecoveredFolder = null, RecoveredFiles = [] };
        var dialog = new CrashPromptDialog(prompt, service.IncludeDesign);
        LastCrashPrompt = dialog;
        AppLog.Info("crash", $"Asking whether to send {prompt.Count} crash report(s)");
        await dialog.ShowDialog(this);
        var answer = dialog.Answer ?? new CrashAnswer(CrashChoice.Later, false);
        if (answer.Choice == CrashChoice.Later) return;
        try
        {
            var notice = await service.AnswerCrashPromptAsync(answer.Choice == CrashChoice.Send, answer.Always);
            if (notice is not null) ShowCrashNotice(_restorePromptShown ? notice with { RecoveredFolder = null, RecoveredFiles = [] } : notice);
        }
        catch (Exception ex) { AppLog.Error("crash", "Handling the crash prompt answer failed", ex); }
        Surface.Focus();
    }

    // ---------------------------------------------------------------- the dialog

    private async Task ShowFeedbackAsync()
    {
        try
        {
            AppLog.Info("feedback", "dialog opened");
            _displayLines = ScreenshotTools.DescribeDisplays(this).ToList();
            var service = FeedbackHost.Get();
            var dialog = new FeedbackDialog(service.CanSend, service.SavedName, SendFeedbackAsync, service.IncludeDesign, service.IncludeScreenshot);
            LastFeedbackDialog = dialog;
            await dialog.ShowDialog(this);
            Surface.Focus();
        }
        catch (Exception ex)
        {
            AppLog.Error("feedback", "The feedback dialog failed", ex);
        }
    }

    private async Task<SubmitResult> SendFeedbackAsync(FeedbackInput input)
    {
        var service = FeedbackHost.Get();
        service.SaveName(input.Name);
        if (input.Kind == ReportKind.Bug)
        {
            // Remembered for next time; it also decides whether a later crash report may carry the design.
            service.IncludeDesign = input.IncludeDesign;
            service.IncludeScreenshot = input.IncludeScreenshot;
        }
        var info = CollectSystemInfo();
        var request = new ReportRequest
        {
            Kind = input.Kind,
            Description = input.Description,
            Name = input.Name,
            AppVersion = info.AppVersion,
            Windows = info.WindowsShort,
        };

        if (input.Kind == ReportKind.Bug)
        {
            var notes = new List<string>();
            byte[]? screenshot = null;
            if (input.IncludeScreenshot)
            {
                screenshot = ScreenshotTools.CapturePng(this);
                if (screenshot is null) notes.Add("The window could not be captured (screenshot not included).");
            }
            else notes.Add("Screenshot not included: switched off by the sender.");

            var designs = new List<ReportFile>();
            if (input.IncludeDesign)
            {
                var bytes = SerializeDesignForReport();
                if (bytes is null) notes.Add("The design could not be saved for the report.");
                else designs.Add(new ReportFile("design.dsm", "application/octet-stream", bytes));
            }
            else notes.Add("Design not included: switched off by the sender.");

            request = request with
            {
                SystemInfoText = info.ToText(),
                DocumentSummary = DescribeDocument(),
                UndoHistory = _session.History.UndoList.Select(c => c.Description).ToList(),
                LogLines = AppLog.Tail(service.BuilderOptions.MaxLogLines),
                ScreenshotPng = screenshot,
                Designs = designs,
                Notes = notes,
            };
        }
        return await service.SubmitAsync(request);
    }

    /// <summary>
    /// The current design as .dsm bytes, written to a temp file with the normal writer. Her real file, its name, the unsaved-changes
    /// state and the recent-files list are not touched.
    /// </summary>
    private byte[]? SerializeDesignForReport()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"dsm-report-{Guid.NewGuid():N}{DsmFormat.Extension}");
        try
        {
            DsmWriter.Save(_session.Document, temp);
            return File.ReadAllBytes(temp);
        }
        catch (Exception ex)
        {
            AppLog.Error("feedback", "Saving the design for the report failed", ex);
            return null;
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception) { }
        }
    }

    // ---------------------------------------------------------------- facts for reports

    internal SystemInfo CollectSystemInfo() => SystemInfo.Collect(typeof(App).Assembly, _displayLines);

    /// <summary>"Sign 3.dsm (unsaved changes), 14 objects, 2 layers": names and counts only, never text from the design.</summary>
    private string DescribeDocument()
    {
        var doc = _session.Document;
        return $"{_documentTitle}{(_session.History.IsModified ? " (unsaved changes)" : "")}, {doc.ObjectCount} objects, {doc.Layers.Count} layers";
    }

    // ---------------------------------------------------------------- crash notice

    internal void ShowCrashNotice(CrashNotice notice)
    {
        NoticeText.Text = notice.Text;
        _noticeFolder = notice.RecoveredFiles.Count > 0 ? notice.RecoveredFolder : null;
        NoticeOpenFolder.IsVisible = _noticeFolder is not null;
        NoticeBar.IsVisible = true;
        AppLog.Info("crash", "Showed the notice: " + notice.Text);
    }

    private void OpenNoticeFolder()
    {
        if (_noticeFolder is null || !Directory.Exists(_noticeFolder)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_noticeFolder}\"") { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Warn("crash", $"Could not open the folder: {ex.GetType().Name}"); }
    }
}

/// <summary>Logs every undo step done, undone or redone (the description only: "Add Rectangle", "Group"), so a report shows what led to a problem.</summary>
internal sealed class HistoryLogger
{
    private int _count;
    private IDocumentCommand? _top;

    public HistoryLogger(CommandHistory history)
    {
        _count = history.UndoList.Count;
        _top = _count > 0 ? history.UndoList[^1] : null;
        history.Changed += (_, _) => OnChanged(history);
    }

    private void OnChanged(CommandHistory history)
    {
        var list = history.UndoList;
        int count = list.Count;
        var top = count > 0 ? list[^1] : null;
        if (count == _count && ReferenceEquals(top, _top)) return;   // a save or another notification, nothing new in the history
        if (count < _count) AppLog.Info("edit", $"undo: {_top?.Description ?? "(history cleared)"} ({count} steps)");
        else AppLog.Info("edit", $"{top?.Description ?? "?"} ({count} steps)");
        _count = count;
        _top = top;
    }
}
