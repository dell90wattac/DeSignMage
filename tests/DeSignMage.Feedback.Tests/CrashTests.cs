using System.Text;

namespace DeSignMage.Feedback.Tests;

public class CrashTests
{
    private static CrashReporter Reporter(TempDir dir, bool withDocument = true, Action<string>? save = null)
    {
        var reporter = new CrashReporter(dir.Paths)
        {
            SystemInfoProvider = () => new SystemInfo("0.1.0", "2026-10-12", "Windows 11 (10.0.26300)", ".NET 10", "X64 OS", 8, 16000, "en-US", ["1920x1080 at 125%"]),
            UndoHistory = () => ["Add Rectangle", "Group"],
            DocumentSummary = () => "Sign 3.dsm, 14 objects",
        };
        if (withDocument)
            reporter.Documents = () => [new CrashDocument("Sign 3.dsm", save ?? (path => File.WriteAllBytes(path, Fake.Bytes(500))))];
        return reporter;
    }

    private static Exception Thrown(string message = "boom")
    {
        try { throw new InvalidOperationException(message); }
        catch (Exception ex) { return ex; }
    }

    [Fact]
    public void Crash_writes_report_log_and_an_emergency_save_of_the_open_design()
    {
        using var dir = new TempDir();
        AppLog.Info("test", "something happened before the crash");

        var record = Reporter(dir).Report(Thrown(), "AppDomain", fatal: true)!;

        Assert.True(Directory.Exists(record.Directory));
        Assert.StartsWith(dir.Paths.Crash, record.Directory);
        var text = File.ReadAllText(Path.Combine(record.Directory, "crash-report.txt"));
        Assert.Contains("InvalidOperationException: boom", text);
        Assert.Contains("CrashTests", text);                 // the stack
        Assert.Contains("Windows 11 (10.0.26300)", text);    // system info
        Assert.Contains("Sign 3.dsm, 14 objects", text);
        Assert.Contains("Add Rectangle", text);              // undo history
        Assert.Contains("something happened before the crash", File.ReadAllText(Path.Combine(record.Directory, "log.txt")));
        var recovered = Assert.Single(record.Recovered);
        Assert.EndsWith(".dsm", recovered);
        Assert.Equal(500, new FileInfo(Path.Combine(record.Directory, recovered)).Length);
        Assert.Equal("new", record.Status);
        Assert.Equal(record.Id, CrashRecovery.List(dir.Paths).Single().Id);
    }

    [Fact]
    public void A_failing_emergency_save_does_not_lose_the_report_and_is_noted()
    {
        using var dir = new TempDir();
        var record = Reporter(dir, save: _ => throw new IOException("disk full")).Report(Thrown(), "AppDomain", fatal: true)!;

        Assert.Empty(record.Recovered);
        var text = File.ReadAllText(Path.Combine(record.Directory, "crash-report.txt"));
        Assert.Contains("Emergency save of Sign 3.dsm failed", text);
        Assert.Contains("No design was recovered", text);
    }

    [Fact]
    public void Fatal_crash_calls_the_notice_hook_but_a_survivable_one_does_not()
    {
        using var dir = new TempDir();
        var told = new List<CrashRecord>();
        var reporter = Reporter(dir);
        reporter.FatalNotice = told.Add;

        reporter.Report(Thrown("kept running"), "Dispatcher", fatal: false);
        Assert.Empty(told);
        reporter.Report(Thrown("died"), "AppDomain", fatal: true);
        Assert.Single(told);
    }

    [Fact]
    public void The_same_survivable_error_is_reported_once_and_the_total_is_capped()
    {
        using var dir = new TempDir();
        var reporter = Reporter(dir);
        Exception SameSpot() { try { throw new InvalidOperationException("again"); } catch (Exception ex) { return ex; } }

        Assert.NotNull(reporter.Report(SameSpot(), "Dispatcher", fatal: false));
        Assert.Null(reporter.Report(SameSpot(), "Dispatcher", fatal: false));

        for (int i = 0; i < 20; i++)
            reporter.Report(new Exception("different " + i, new Exception(i.ToString())) { Data = { ["i"] = i } }, "Dispatcher", fatal: false);
        Assert.True(reporter.Written <= 6);
        // a fatal crash is always written, whatever came before
        Assert.NotNull(reporter.Report(Thrown("last"), "AppDomain", fatal: true));
    }

    [Fact]
    public void An_error_inside_the_crash_hooks_never_escapes()
    {
        using var dir = new TempDir();
        var reporter = Reporter(dir);
        reporter.SystemInfoProvider = () => throw new InvalidOperationException("provider broke");
        reporter.Documents = () => throw new InvalidOperationException("documents broke");
        reporter.UndoHistory = () => throw new InvalidOperationException("undo broke");

        var record = reporter.Report(Thrown(), "AppDomain", fatal: true);

        Assert.NotNull(record);
        Assert.Contains("boom", File.ReadAllText(Path.Combine(record!.Directory, "crash-report.txt")));
    }

    [Fact]
    public void Queueing_builds_a_crash_report_with_the_stack_log_and_recovered_design_once()
    {
        using var dir = new TempDir();
        var record = Reporter(dir).Report(Thrown("kaboom"), "AppDomain", fatal: true)!;
        var outbox = new Outbox(dir.Paths.Outbox);

        var queued = CrashRecovery.QueuePending(dir.Paths, outbox);

        var q = Assert.Single(queued);
        Assert.Equal("queued", q.Status);
        var item = Assert.Single(outbox.List());
        Assert.Equal(ReportKind.Crash, item.Report.Kind);
        Assert.StartsWith("Crash: InvalidOperationException: kaboom", item.Report.Title);
        Assert.Contains("```", item.Report.Description);
        Assert.Equal(1, item.Report.Files.Count(f => f.FileName.EndsWith(".txt")));   // one text file: header, exception, ..., log
        var text = Encoding.UTF8.GetString(item.ReadFile(item.Report.Files.Single(f => f.FileName.EndsWith(".txt")).FileName));
        Assert.StartsWith("DeSign Mage Crash report", text);
        Assert.True(text.IndexOf("==== EXCEPTION ====", StringComparison.Ordinal) < text.IndexOf("==== DESCRIPTION ====", StringComparison.Ordinal));
        Assert.Contains("InvalidOperationException: kaboom", text);
        Assert.Contains("Windows 11 (10.0.26300)", text);          // header: system block
        Assert.Contains("1920x1080 at 125%", text);
        Assert.Contains("Add Rectangle", text);                    // undo history
        Assert.True(text.IndexOf("==== LOG ====", StringComparison.Ordinal) > text.IndexOf("==== UNDO HISTORY", StringComparison.Ordinal));
        Assert.Contains(item.Report.Files, f => f.FileName.EndsWith(".dsm"));
        Assert.Equal(record.Id, item.Id);
        // queued only once; the recovered design stays in the crash folder for her
        Assert.Empty(CrashRecovery.QueuePending(dir.Paths, outbox));
        Assert.True(File.Exists(Path.Combine(record.Directory, record.Recovered[0])));
    }

    [Fact]
    public async Task Next_launch_sends_the_crash_report_and_tells_her_where_the_design_is()
    {
        using var dir = new TempDir();
        var record = Reporter(dir).Report(Thrown(), "AppDomain", fatal: true)!;
        var sender = new ScriptedSender(new SendResult(SendStatus.Sent));

        // a new run of the program
        using var service = new FeedbackService(dir.Paths, sender);
        var notice = await service.RecoverFromCrashAsync();

        Assert.NotNull(notice);
        Assert.True(notice!.Sent);
        Assert.Equal(1, sender.Calls);
        Assert.Empty(service.Outbox.List());
        Assert.StartsWith("A crash report from last time was sent.", notice.Text);
        Assert.Contains(record.Directory, notice.Text);
        Assert.DoesNotContain('\n', notice.Text);
    }

    [Fact]
    public async Task Next_launch_offline_keeps_the_crash_report_queued_and_says_so()
    {
        using var dir = new TempDir();
        Reporter(dir).Report(Thrown(), "AppDomain", fatal: true);
        var sender = new ScriptedSender(new SendResult(SendStatus.Offline, "no network"), new SendResult(SendStatus.Sent));
        using var service = new FeedbackService(dir.Paths, sender);

        var notice = await service.RecoverFromCrashAsync();

        Assert.False(notice!.Sent);
        Assert.Contains("will be sent when online", notice.Text);
        Assert.Single(service.Outbox.List());

        // the background pass delivers it later
        await service.RunBackgroundOnceAsync();
        Assert.Empty(service.Outbox.List());
        Assert.Null(await service.RecoverFromCrashAsync());   // nothing new to announce
    }

    [Fact]
    public async Task Without_a_webhook_the_crash_report_is_saved_and_the_notice_says_sending_is_not_set_up()
    {
        using var dir = new TempDir();
        Reporter(dir).Report(Thrown(), "AppDomain", fatal: true);
        using var service = new FeedbackService(dir.Paths, sender: null);

        var notice = await service.RecoverFromCrashAsync();

        Assert.False(notice!.Sent);
        Assert.False(notice.Configured);
        Assert.Contains("sending isn't set up yet", notice.Text);
        Assert.Single(service.Outbox.List());
    }

    [Fact]
    public async Task No_crash_folder_means_no_notice()
    {
        using var dir = new TempDir();
        using var service = new FeedbackService(dir.Paths, new ScriptedSender(new SendResult(SendStatus.Sent)));
        Assert.Null(await service.RecoverFromCrashAsync());
    }

    [Fact]
    public void Crash_without_a_recovered_design_says_no_design_in_the_report()
    {
        using var dir = new TempDir();
        Reporter(dir, withDocument: false).Report(Thrown(), "AppDomain", fatal: true);
        var outbox = new Outbox(dir.Paths.Outbox);
        CrashRecovery.QueuePending(dir.Paths, outbox);
        var item = outbox.List().Single();
        Assert.DoesNotContain(item.Report.Files, f => f.FileName.EndsWith(".dsm"));
        Assert.Contains("No open design could be saved.", item.Report.Notes);
        Assert.Contains("No open design could be saved.", Encoding.UTF8.GetString(item.ReadFile(item.Report.Files.Single(f => f.FileName.EndsWith(".txt")).FileName)));
    }

    [Fact]
    public void Old_sent_crash_folders_are_pruned_but_unsent_ones_never_are()
    {
        using var dir = new TempDir();
        var reporter = new CrashReporter(dir.Paths, () => DateTime.UtcNow.AddDays(-90));
        var oldSent = reporter.Report(Thrown("a"), "AppDomain", fatal: true)!;
        var oldUnsent = reporter.Report(Thrown("b"), "AppDomain", fatal: true)!;
        var outbox = new Outbox(dir.Paths.Outbox);
        // queue only the first
        File.WriteAllText(Path.Combine(oldUnsent.Directory, "crash.json"), File.ReadAllText(Path.Combine(oldUnsent.Directory, "crash.json")));
        oldSent.Status = "queued";
        File.WriteAllText(Path.Combine(oldSent.Directory, "crash.json"), System.Text.Json.JsonSerializer.Serialize(oldSent));

        CrashRecovery.Prune(dir.Paths, TimeSpan.FromDays(60), DateTime.UtcNow, id => outbox.Get(id) is not null);

        Assert.False(Directory.Exists(oldSent.Directory));
        Assert.True(Directory.Exists(oldUnsent.Directory));
    }
}
