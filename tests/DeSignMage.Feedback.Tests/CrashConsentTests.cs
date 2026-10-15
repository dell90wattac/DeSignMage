namespace DeSignMage.Feedback.Tests;

/// <summary>A crash report is only sent after the person said yes: in the prompt, or by "always send crash reports".</summary>
public class CrashConsentTests
{
    private static CrashReporter Reporter(TempDir dir) => new(dir.Paths)
    {
        SystemInfoProvider = () => new SystemInfo("0.1.0", "2026-10-12", "Windows 11 (10.0.26300)", ".NET 10", "X64 OS", 8, 16000, "en-US", ["1920x1080 at 125%"]),
        UndoHistory = () => ["Add Rectangle"],
        DocumentSummary = () => "Sign 3.dsm, 14 objects",
        Documents = () => [new CrashDocument("Sign 3.dsm", path => File.WriteAllBytes(path, Fake.Bytes(500)))],
    };

    private static CrashRecord Crash(TempDir dir, string message = "boom")
    {
        Exception ex;
        try { throw new InvalidOperationException(message); }
        catch (Exception caught) { ex = caught; }
        return Reporter(dir).Report(ex, "AppDomain", fatal: true)!;
    }

    [Fact]
    public async Task Without_an_answer_nothing_is_queued_and_nothing_is_sent()
    {
        using var dir = new TempDir();
        Crash(dir);
        var sender = new ScriptedSender(new SendResult(SendStatus.Sent));
        using var service = new FeedbackService(dir.Paths, sender);

        Assert.False(service.AlwaysSendCrashes);
        Assert.Null(await service.RecoverFromCrashAsync());   // the automatic path does nothing without "always send"
        await service.RunBackgroundOnceAsync();                // neither does the background pass

        Assert.Equal(0, sender.Calls);
        Assert.Empty(service.Outbox.List());
        Assert.Equal("new", Assert.Single(CrashRecovery.List(dir.Paths)).Status);
    }

    [Fact]
    public void The_prompt_says_how_many_crashes_and_where_the_design_copy_is()
    {
        using var dir = new TempDir();
        var first = Crash(dir, "one");
        using var service = new FeedbackService(dir.Paths, null);

        var prompt = service.FindCrashesToAsk();

        Assert.NotNull(prompt);
        Assert.Equal(1, prompt!.Count);
        Assert.Equal(first.Directory, prompt.RecoveredFolder);
        Assert.Single(prompt.RecoveredFiles);
        Assert.Null(new FeedbackService(new TempDir().Paths, null).FindCrashesToAsk());   // nothing waiting: no prompt
    }

    [Fact]
    public async Task Send_queues_and_sends_but_does_not_turn_on_always_send()
    {
        using var dir = new TempDir();
        Crash(dir);
        var sender = new ScriptedSender(new SendResult(SendStatus.Sent));
        using var service = new FeedbackService(dir.Paths, sender);

        var notice = await service.AnswerCrashPromptAsync(send: true, always: false);

        Assert.True(notice!.Sent);
        Assert.Equal(1, sender.Calls);
        Assert.Empty(service.Outbox.List());
        Assert.False(service.AlwaysSendCrashes);

        Crash(dir, "second crash");                       // a later crash asks again
        Assert.NotNull(service.FindCrashesToAsk());
        Assert.Null(await service.RecoverFromCrashAsync());
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public async Task Always_send_is_remembered_and_later_crashes_go_out_without_asking()
    {
        using var dir = new TempDir();
        Crash(dir);
        var first = new ScriptedSender(new SendResult(SendStatus.Sent));
        using (var service = new FeedbackService(dir.Paths, first))
        {
            await service.AnswerCrashPromptAsync(send: true, always: true);
            Assert.True(service.AlwaysSendCrashes);
        }
        Assert.Equal(1, first.Calls);

        // a new run of the program: the setting is read back and there is no prompt
        Crash(dir, "another");
        var second = new ScriptedSender(new SendResult(SendStatus.Sent));
        using var again = new FeedbackService(dir.Paths, second);
        Assert.True(again.AlwaysSendCrashes);
        Assert.Null(again.FindCrashesToAsk());
        var notice = await again.RecoverFromCrashAsync();
        Assert.True(notice!.Sent);
        Assert.Equal(1, second.Calls);

        // and a crash in the middle of a run is picked up by the background pass
        Crash(dir, "third");
        await again.RunBackgroundOnceAsync();
        Assert.Equal(2, second.Calls);
    }

    [Fact]
    public async Task Dont_send_keeps_everything_on_the_computer_and_does_not_ask_again()
    {
        using var dir = new TempDir();
        var record = Crash(dir);
        var sender = new ScriptedSender(new SendResult(SendStatus.Sent));
        using var service = new FeedbackService(dir.Paths, sender);

        var notice = await service.AnswerCrashPromptAsync(send: false, always: true);   // "always" counts only together with Send

        Assert.True(notice!.Declined);
        Assert.False(notice.Sent);
        Assert.StartsWith("A crash report from last time was not sent (as you chose).", notice.Text);
        Assert.Contains(record.Directory, notice.Text);   // where the design copy is
        Assert.Equal(0, sender.Calls);
        Assert.Empty(service.Outbox.List());
        Assert.False(service.AlwaysSendCrashes);
        Assert.True(File.Exists(Path.Combine(record.Directory, record.Recovered[0])));
        Assert.Equal("declined", Assert.Single(CrashRecovery.List(dir.Paths)).Status);

        Assert.Null(service.FindCrashesToAsk());
        await service.RunBackgroundOnceAsync();
        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task Offline_after_Send_waits_in_the_outbox_and_goes_out_later_without_asking_again()
    {
        using var dir = new TempDir();
        Crash(dir);
        var sender = new ScriptedSender(new SendResult(SendStatus.Offline, "no network"), new SendResult(SendStatus.Sent));
        using var service = new FeedbackService(dir.Paths, sender);

        var notice = await service.AnswerCrashPromptAsync(send: true);

        Assert.False(notice!.Sent);
        Assert.Contains("will be sent when online", notice.Text);
        Assert.Single(service.Outbox.List());
        Assert.False(service.AlwaysSendCrashes);

        await service.RunBackgroundOnceAsync();           // the person already agreed to this one
        Assert.Empty(service.Outbox.List());
        Assert.Equal(2, sender.Calls);
    }

    [Fact]
    public async Task Closing_the_prompt_without_an_answer_asks_again_next_start()
    {
        using var dir = new TempDir();
        Crash(dir);
        using (var run1 = new FeedbackService(dir.Paths, new ScriptedSender(new SendResult(SendStatus.Sent))))
            Assert.NotNull(run1.FindCrashesToAsk());        // the window is closed: no call to Answer...

        var sender = new ScriptedSender(new SendResult(SendStatus.Sent));
        using var run2 = new FeedbackService(dir.Paths, sender);
        Assert.NotNull(run2.FindCrashesToAsk());            // ... so the question comes back
        await run2.RunBackgroundOnceAsync();
        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task The_design_copy_goes_along_only_when_designs_are_allowed()
    {
        using var dir = new TempDir();
        Crash(dir);
        using var service = new FeedbackService(dir.Paths, null);
        await service.AnswerCrashPromptAsync(send: true);
        Assert.DoesNotContain(service.Outbox.List().Single().Report.Files, f => f.FileName.EndsWith(".dsm"));

        using var dir2 = new TempDir();
        Crash(dir2);
        using var allowed = new FeedbackService(dir2.Paths, null) { IncludeDesign = true };
        await allowed.AnswerCrashPromptAsync(send: true);
        Assert.Contains(allowed.Outbox.List().Single().Report.Files, f => f.FileName.EndsWith(".dsm"));
    }

    [Fact]
    public void Old_declined_crash_folders_are_pruned_like_sent_ones()
    {
        using var dir = new TempDir();
        var reporter = new CrashReporter(dir.Paths, () => DateTime.UtcNow.AddDays(-90));
        reporter.Documents = () => [];
        try { throw new InvalidOperationException("old"); }
        catch (Exception ex) { reporter.Report(ex, "AppDomain", fatal: false); }
        CrashRecovery.Decline(dir.Paths);

        CrashRecovery.Prune(dir.Paths, TimeSpan.FromDays(60), DateTime.UtcNow);

        Assert.Empty(CrashRecovery.List(dir.Paths));
    }
}
