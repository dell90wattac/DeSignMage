using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DeSignMage.App.Recovery;
using DeSignMage.App.Reporting;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
using DeSignMage.Formats.Native;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Auto-save and restore in the real window: the copies, the cleanup, and the question at the next start.</summary>
public class RecoveryShellTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dsm-recovery-ui-" + Guid.NewGuid().ToString("N")[..8]);
    private DateTime _now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    private readonly List<RecoveryStore> _stores = [];
    private readonly List<MainWindow> _windows = [];
    private readonly FeedbackService? _oldFeedback = FeedbackHost.Current;

    public RecoveryShellTests()
    {
        Directory.CreateDirectory(_root);
        AutoSaveSettings.Reset();
        MainWindow.AutoSaveClock = () => _now;
        FeedbackHost.Current = new FeedbackService(new AppPaths(Path.Combine(_root, "data")), sender: null);
    }

    public void Dispose()
    {
        foreach (var w in _windows) { try { w.Close(); } catch (Exception) { } }
        foreach (var s in _stores) s.Dispose();
        RecoveryHost.Current = null;
        MainWindow.AutoSaveClock = null;
        AutoSaveSettings.Reset();
        FeedbackHost.Current = _oldFeedback;
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Recovery => Path.Combine(_root, "data", "recovery");

    /// <summary>A new "run of the program": its own store, like a new process.</summary>
    private RecoveryStore NewRun(string id)
    {
        var store = new RecoveryStore(Recovery, FeedbackHost.Get().Paths.Crash, () => _now, id);
        _stores.Add(store);
        RecoveryHost.Current = store;
        return store;
    }

    private MainWindow Show(StartupOptions? options = null)
    {
        var w = new MainWindow(options ?? new StartupOptions()) { Width = 1400, Height = 860 };
        _windows.Add(w);
        w.DialogOverride = (_, _) => Task.FromResult(true);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        return w;
    }

    private static void Draw(MainWindow w, double width = 60) =>
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, new RectangleObject(width, 40)
        {
            Name = "box", Style = ObjectStyle.FillOnly(new DocColor(10, 20, 30, 255, "Test Vinyl")),
        }));

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Lets the window's own work (the start-up question runs on a worker first) go on until <paramref name="done"/>.</summary>
    private static async Task PumpUntil(Func<bool> done)
    {
        for (int i = 0; i < 300 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Makes the window save a copy now (the clock moves past the interval).</summary>
    private async Task MakeCopy(MainWindow w)
    {
        Assert.Equal(AutoSaveResult.NotDue, w.AutoSave!.Tick().Result);
        _now = _now.AddMinutes(6);
        var tick = w.AutoSave.Tick();
        Assert.Equal(AutoSaveResult.Started, tick.Result);
        await tick.Write!;
    }

    /// <summary>The process "dies": the window is gone but nothing was cleaned up.</summary>
    private void Kill(MainWindow w, RecoveryStore run)
    {
        run.Dispose();      // its lock is released; no clean close
        w.Close();          // (a closed store ignores the clean-close request)
    }

    // ---------------------------------------------------------------- the copies

    [AvaloniaFact]
    public async Task A_design_with_unsaved_changes_gets_a_recovery_copy_and_Save_removes_it()
    {
        var run = NewRun("run-a");
        var w = Show();
        Assert.Equal(AutoSaveResult.NoChanges, w.AutoSave!.Tick().Result);   // a fresh design has nothing to protect
        Assert.False(run.HasCopy("design"));

        Draw(w);
        await MakeCopy(w);
        Assert.True(run.HasCopy("design"));

        w.SavePathOverride = _ => Path.Combine(_root, "sign.dsm");
        Assert.True(await w.SaveAsAsync());
        Dispatcher.UIThread.RunJobs();
        Assert.False(run.HasCopy("design"));                                // saved: the copy is gone
        _now = _now.AddMinutes(30);
        Assert.Equal(AutoSaveResult.NoChanges, w.AutoSave.Tick().Result);

        Draw(w, 90);                                                         // new changes: a new copy, after the interval
        await MakeCopy(w);
        Assert.True(run.HasCopy("design"));
    }

    [AvaloniaFact]
    public async Task A_clean_close_removes_the_copies_whether_or_not_the_design_was_saved()
    {
        var run = NewRun("run-a");
        var w = Show();
        Draw(w);
        await MakeCopy(w);
        Assert.True(Directory.Exists(run.RunFolder));

        w.SaveChangesOverride = (_, _) => Task.FromResult(MainWindow.SaveChoice.Discard);
        w.Close();
        await PumpUntil(() => !Directory.Exists(run.RunFolder));

        Assert.False(Directory.Exists(run.RunFolder));
        Assert.Empty(NewRun("run-b").FindRecoverable());                      // nothing to ask at the next start
    }

    [AvaloniaFact]
    public async Task New_removes_the_copy_of_the_design_that_was_given_up()
    {
        var run = NewRun("run-a");
        var w = Show();
        Draw(w);
        await MakeCopy(w);
        w.SaveChangesOverride = (_, _) => Task.FromResult(MainWindow.SaveChoice.Discard);

        var before = w.Session;
        w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().First(m => (m.Header as string)!.Contains("File"))
            .Items.OfType<MenuItem>().First(m => (m.Header as string)!.Contains("New")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await PumpUntil(() => !ReferenceEquals(before, w.Session));

        Assert.False(ReferenceEquals(before, w.Session));
        Assert.False(run.HasCopy("design"));
    }

    [AvaloniaFact]
    public async Task Switched_off_in_Options_nothing_is_written()
    {
        var run = NewRun("run-a");
        var old = AppSettings.Store;
        AppSettings.Store = new SettingsStore(null);
        try
        {
            AutoSaveSettings.SetEnabled(false);
            var w = Show();
            Draw(w);
            _now = _now.AddMinutes(60);
            Assert.Equal(AutoSaveResult.Disabled, w.AutoSave!.Tick().Result);
            Assert.False(run.HasCopy("design"));
        }
        finally { AppSettings.Store = old; }
        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------- the question at the next start

    /// <summary>Runs a window, edits it, makes a copy and kills it; returns the path it was saved to (or null).</summary>
    private async Task<string?> LeaveBehind(string runId, bool savedFirst, int extraBoxes = 1)
    {
        var run = NewRun(runId);
        MainWindow.AskToRestoreAtStart = false;   // this window only exists to be killed
        MainWindow w;
        try { w = Show(); }
        finally { MainWindow.AskToRestoreAtStart = true; }
        string? path = null;
        Draw(w);
        if (savedFirst)
        {
            path = Path.Combine(_root, $"job-{runId}.dsm");
            w.SavePathOverride = _ => path;
            Assert.True(await w.SaveAsAsync());
        }
        for (int i = 0; i < extraBoxes; i++) Draw(w, 70 + i);
        await MakeCopy(w);
        Kill(w, run);
        return path;
    }

    [AvaloniaFact]
    public async Task After_a_kill_the_next_start_offers_the_design_and_Restore_opens_it_unsaved_with_its_file_remembered()
    {
        var path = await LeaveBehind("run-a", savedFirst: true, extraBoxes: 2);   // 1 saved box + 2 unsaved
        Assert.Equal(1, DsmReader.Load(path!).Document.ObjectCount);

        var run = NewRun("run-b");
        var w = Show();
        await PumpUntil(() => w.LastRestorePrompt is not null);
        var prompt = Assert.IsType<RestorePromptDialog>(w.LastRestorePrompt);

        Assert.Equal("DeSign Mage closed before your design was saved. Restore it?", prompt.Heading.Text);
        var row = Assert.Single(prompt.RowTexts);
        Assert.Contains("job-run-a.dsm", row);                                // its name
        Assert.Contains("today at", row);                                     // and when it was backed up
        Assert.Contains(path!, row);

        Click(prompt.RestoreButtons[0]);
        await PumpUntil(() => w.Session.Document.ObjectCount == 3);
        await w.RecoveryPromptDone;

        Assert.Equal(3, w.Session.Document.ObjectCount);
        Assert.True(w.Session.History.IsModified);                            // opens as unsaved...
        Assert.EndsWith(" * - DeSign Mage", w.Title);
        Assert.Equal(path, w.DocumentPath);                                   // ...remembering the real file
        Assert.True(run.HasCopy("design"));                                   // and protected from the first moment
        Assert.Empty(NewRun("run-c").FindRecoverable());                      // (the old entry is gone)

        w.SavePathOverride = _ => throw new InvalidOperationException("Save must go back to the real file without asking.");
        Assert.True(await w.SaveAsync());
        Assert.Equal(3, DsmReader.Load(path!).Document.ObjectCount);
        Assert.False(w.Session.History.IsModified);
    }

    [AvaloniaFact]
    public async Task A_design_that_was_never_saved_is_restored_untitled_and_Save_asks_for_a_name()
    {
        await LeaveBehind("run-a", savedFirst: false);
        NewRun("run-b");
        var w = Show();
        await PumpUntil(() => w.LastRestorePrompt is not null);
        Assert.Contains("Not saved to a file yet", Assert.Single(w.LastRestorePrompt!.RowTexts));

        Click(w.LastRestorePrompt.RestoreButtons[0]);
        await PumpUntil(() => w.Session.Document.ObjectCount == 2);

        Assert.Null(w.DocumentPath);
        Assert.True(w.Session.History.IsModified);
        string? suggested = null;
        var target = Path.Combine(_root, "named.dsm");
        w.SavePathOverride = s => { suggested = s; return target; };
        Assert.True(await w.SaveAsync());
        Assert.Equal("Untitled.dsm", suggested);
        Assert.Equal(2, DsmReader.Load(target).Document.ObjectCount);
    }

    [AvaloniaFact]
    public async Task Discard_asks_once_more_then_removes_the_copy_and_the_design_stays_empty()
    {
        await LeaveBehind("run-a", savedFirst: false);
        var run = NewRun("run-b");
        var w = Show();
        await PumpUntil(() => w.LastRestorePrompt is not null);
        var prompt = w.LastRestorePrompt!;

        Click(prompt.DiscardButtons[0]);
        Assert.True(prompt.ConfirmDiscardButtons[0].IsVisible);              // "Throw this copy away?"
        Assert.Single(run.FindRecoverable());                                 // nothing deleted yet
        Click(prompt.KeepButtons[0]);
        Assert.False(prompt.ConfirmDiscardButtons[0].IsVisible);
        Assert.Single(run.FindRecoverable());

        Click(prompt.DiscardButtons[0]);
        Click(prompt.ConfirmDiscardButtons[0]);
        await w.RecoveryPromptDone;

        Assert.Empty(run.FindRecoverable());
        Assert.Equal(0, w.Session.Document.ObjectCount);
        Assert.False(w.Session.History.IsModified);
    }

    [AvaloniaFact]
    public async Task Closing_the_question_with_Not_now_keeps_the_copy_for_the_next_start()
    {
        await LeaveBehind("run-a", savedFirst: false);
        var run = NewRun("run-b");
        var w = Show();
        await PumpUntil(() => w.LastRestorePrompt is not null);

        Click(w.LastRestorePrompt!.NotNowButton);
        await w.RecoveryPromptDone;

        Assert.Single(run.FindRecoverable());
        Assert.Equal(0, w.Session.Document.ObjectCount);
    }

    [AvaloniaFact]
    public async Task Nothing_is_asked_when_the_last_run_closed_normally()
    {
        var run = NewRun("run-a");
        var w = Show();
        Draw(w);
        await MakeCopy(w);
        w.SaveChangesOverride = (_, _) => Task.FromResult(MainWindow.SaveChoice.Discard);
        w.Close();
        await PumpUntil(() => !Directory.Exists(run.RunFolder));

        NewRun("run-b");
        var again = Show();
        await again.RecoveryPromptDone;
        Assert.Null(again.LastRestorePrompt);
    }

    [AvaloniaFact]
    public async Task A_second_copy_of_the_program_leaves_the_first_ones_design_alone()
    {
        var first = NewRun("run-a");
        var w1 = Show();
        Draw(w1);
        await MakeCopy(w1);                                                   // the first copy is still running

        var second = NewRun("run-b");
        var w2 = Show();
        await w2.RecoveryPromptDone;

        Assert.Null(w2.LastRestorePrompt);                                    // its lock is held: not a leftover
        Assert.True(first.HasCopy("design"));
    }

    [AvaloniaFact]
    public async Task Two_leftover_designs_are_both_listed_and_restoring_one_keeps_the_other()
    {
        await LeaveBehind("run-a", savedFirst: true);
        _now = _now.AddHours(2);
        await LeaveBehind("run-b", savedFirst: true, extraBoxes: 2);

        var run = NewRun("run-c");
        var w = Show();
        await PumpUntil(() => w.LastRestorePrompt is not null);
        var prompt = w.LastRestorePrompt!;
        Assert.Equal(2, prompt.RestoreButtons.Count);
        Assert.Contains("job-run-b.dsm", prompt.RowTexts[0]);                 // newest first
        Assert.Contains("2 designs", prompt.Heading.Text);
        Assert.Contains("Only one design can be open", prompt.NoteText);

        Click(prompt.RestoreButtons[0]);
        await PumpUntil(() => w.Session.Document.ObjectCount == 3);
        await w.RecoveryPromptDone;

        var left = run.FindRecoverable();
        Assert.Equal("run-a", Assert.Single(left).RunId);                     // offered again next time
    }

    [AvaloniaFact]
    public async Task A_file_given_on_the_command_line_wins_and_the_question_waits()
    {
        await LeaveBehind("run-a", savedFirst: false);
        var file = Path.Combine(_root, "other.dsm");
        DsmWriter.Save(DemoContent.NewDocument(), file);

        var run = NewRun("run-b");
        var w = Show(new StartupOptions(OpenPath: file));
        await PumpUntil(() => w.DocumentPath == Path.GetFullPath(file));
        await w.RecoveryPromptDone;

        Assert.Null(w.LastRestorePrompt);
        Assert.Single(run.FindRecoverable());
    }

    [AvaloniaFact]
    public async Task An_unreadable_copy_says_so_once_and_is_set_aside()
    {
        var run = NewRun("run-a");
        var w1 = Show();
        Draw(w1);
        await MakeCopy(w1);
        File.WriteAllText(Path.Combine(run.RunFolder, "design.dsm"), "this is not a zip file");
        Kill(w1, run);

        var next = NewRun("run-b");
        var w = Show();
        var messages = new List<string>();
        w.DialogOverride = (title, message) => { messages.Add(title + ": " + message); return Task.FromResult(true); };
        await PumpUntil(() => w.LastRestorePrompt is not null);
        Click(w.LastRestorePrompt!.RestoreButtons[0]);
        await PumpUntil(() => w.LastRestoreError is not null);
        await w.RecoveryPromptDone;

        Assert.NotNull(w.LastRestoreError);
        Assert.Contains(messages, m => m.StartsWith("Cannot restore"));
        Assert.Empty(next.FindRecoverable());                                 // not asked again at every start
        Assert.Equal(0, w.Session.Document.ObjectCount);
    }

    // ---------------------------------------------------------------- the emergency copy of a crash

    [AvaloniaFact]
    public async Task A_crash_leaves_an_emergency_copy_that_the_restore_question_uses_instead_of_the_older_background_copy()
    {
        var run = NewRun("run-a");
        var w = Show();
        Draw(w);
        await MakeCopy(w);                                                    // 1 box in the background copy
        Draw(w, 80);                                                           // a second box, only in the emergency copy
        _now = _now.AddMinutes(1);
        FeedbackHost.Get().Crash.Report(new InvalidOperationException("test crash"), "test", fatal: true);
        Kill(w, run);

        NewRun("run-b");
        var next = Show();
        await PumpUntil(() => next.LastRestorePrompt is not null);
        Assert.Contains("stopped", Assert.Single(next.LastRestorePrompt!.RowTexts));   // "saved at the moment the program stopped"
        Click(next.LastRestorePrompt.RestoreButtons[0]);
        await PumpUntil(() => next.Session.Document.ObjectCount == 2);
        await next.RecoveryPromptDone;

        Assert.Equal(2, next.Session.Document.ObjectCount);                    // the newer copy
        Assert.Empty(RecoveryHost.Current!.FindRecoverable());                // one question, not two
        Assert.NotEmpty(Directory.GetFiles(FeedbackHost.Get().Paths.Crash, "recovered-*.dsm", SearchOption.AllDirectories));   // the report folder keeps its own file
    }

    [AvaloniaFact]
    public async Task A_survivable_error_does_not_make_the_next_start_ask_when_the_program_then_closes_normally()
    {
        var run = NewRun("run-a");
        var w = Show();
        Draw(w);
        FeedbackHost.Get().Crash.Report(new InvalidOperationException("survivable"), "Dispatcher", fatal: false);
        w.SaveChangesOverride = (_, _) => Task.FromResult(MainWindow.SaveChoice.Discard);
        w.Close();
        await PumpUntil(() => !Directory.Exists(run.RunFolder));

        Assert.Empty(NewRun("run-b").FindRecoverable());
    }

    // ---------------------------------------------------------------- Options

    [AvaloniaFact]
    public void The_Options_page_switches_auto_save_on_and_off_and_keeps_the_minutes_in_range()
    {
        var old = AppSettings.Store;
        AppSettings.Store = new SettingsStore(null);
        try
        {
            Assert.Contains(OptionsPages.All, p => p.Id == OptionsPages.AutoSaveId && p.Title == "Auto-save");
            var page = (AutoSaveOptionsPage)OptionsPages.All.First(p => p.Id == OptionsPages.AutoSaveId).Create();
            Assert.True(page.EnabledBox.IsChecked);
            Assert.Equal("5", page.MinutesBox.Text);                           // the default is 5 minutes
            Assert.True(page.Details.IsVisible);

            page.MinutesBox.Text = "10";
            page.MinutesBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal(TimeSpan.FromMinutes(10), AutoSaveSettings.Current.Interval);
            Assert.Equal(10, AppSettings.Store.Get(AutoSaveSettings.MinutesKey, 0));

            page.MinutesBox.Text = "0";                                         // too small: the smallest allowed
            page.MinutesBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal("1", page.MinutesBox.Text);
            page.MinutesBox.Text = "999";
            page.MinutesBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal("60", page.MinutesBox.Text);
            page.MinutesBox.Text = "soon";                                       // not a number: the old value stays
            page.MinutesBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal("60", page.MinutesBox.Text);

            page.LargestBox.Text = "50";
            page.LargestBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal(50L * 1024 * 1024, AutoSaveSettings.Current.LargestBytes);

            page.EnabledBox.IsChecked = false;
            Assert.False(AutoSaveSettings.Current.Enabled);
            Assert.False(AppSettings.Store.Get(AutoSaveSettings.EnabledKey, true));
            Assert.False(page.Details.IsVisible);                              // nothing to choose while it is off

            AutoSaveSettings.Reset();                                           // a new run reads what was remembered
            AutoSaveSettings.Load(AppSettings.Store);
            Assert.False(AutoSaveSettings.Enabled);
            Assert.Equal(60, AutoSaveSettings.Minutes);
            Assert.Equal(50, AutoSaveSettings.LargestMb);
        }
        finally { AppSettings.Store = old; }
    }

    [Fact]
    public void The_restore_prompt_says_when_in_plain_words()
    {
        var now = new DateTime(2026, 10, 18, 15, 0, 0, DateTimeKind.Local);
        Assert.StartsWith("today at", RecoveryText.When(now.AddMinutes(-20).ToUniversalTime(), now));
        Assert.StartsWith("yesterday at", RecoveryText.When(now.AddDays(-1).ToUniversalTime(), now));
        Assert.DoesNotContain("today", RecoveryText.When(now.AddDays(-5).ToUniversalTime(), now));
        Assert.Equal("DeSign Mage closed before your design was saved. Restore it?", RecoveryText.Heading(1));
        Assert.Contains("3 designs", RecoveryText.Heading(3));
    }
}
