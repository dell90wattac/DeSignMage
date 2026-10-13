namespace DeSignMage.Feedback.Tests;

public class OutboxTests
{
    private static readonly SendResult Ok = new(SendStatus.Sent);
    private static readonly SendResult Offline = new(SendStatus.Offline, "no network");

    private static string AddReport(Outbox outbox, string text, DateTime? created = null) =>
        outbox.Add(ReportBuilder.Build(Fake.Bug(text) with { Id = null, CreatedUtc = created }));

    [Fact]
    public async Task Report_is_deleted_only_after_a_successful_send()
    {
        using var dir = new TempDir();
        var outbox = new Outbox(dir.Paths.Outbox);
        var id = AddReport(outbox, "one");
        Assert.Single(outbox.List());
        Assert.True(File.Exists(Path.Combine(dir.Paths.Outbox, id, "report.json")));
        Assert.True(File.Exists(Path.Combine(dir.Paths.Outbox, id, "design.dsm")));

        var run = await outbox.ProcessAsync(new ScriptedSender(Ok), default);

        Assert.Equal(1, run.Sent);
        Assert.Empty(outbox.List());
        Assert.False(Directory.Exists(Path.Combine(dir.Paths.Outbox, id)));
    }

    [Fact]
    public async Task Offline_keeps_the_report_and_a_later_pass_delivers_it()
    {
        using var dir = new TempDir();
        var outbox = new Outbox(dir.Paths.Outbox);
        var id = AddReport(outbox, "offline");

        var sender = new ScriptedSender(Offline, Offline, Ok);
        var first = await outbox.ProcessAsync(sender, default);
        Assert.Equal(1, first.Failed);
        var kept = Assert.Single(outbox.List());
        Assert.Equal(1, kept.Report.Attempts);
        Assert.Contains("Offline", kept.Report.LastError);
        Assert.Null(kept.Report.NotBeforeUtc);   // offline: simply try again on the next pass

        await outbox.ProcessAsync(sender, default);
        Assert.Single(outbox.List());
        var third = await outbox.ProcessAsync(sender, default);
        Assert.Equal(1, third.Sent);
        Assert.Empty(outbox.List());
        Assert.Equal([id], sender.Sent);
    }

    [Fact]
    public async Task Rate_limited_report_waits_for_its_retry_time_then_goes()
    {
        using var dir = new TempDir();
        var clock = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var outbox = new Outbox(dir.Paths.Outbox, utcNow: () => clock);
        AddReport(outbox, "rate", clock);
        var sender = new ScriptedSender(new SendResult(SendStatus.RateLimited, RetryAfter: TimeSpan.FromSeconds(90), HttpStatus: 429), Ok);

        await outbox.ProcessAsync(sender, default);
        Assert.Equal(1, sender.Calls);
        var waiting = Assert.Single(outbox.List());
        Assert.True(waiting.Report.NotBeforeUtc >= clock.AddSeconds(90));

        clock = clock.AddSeconds(30);   // too early: not even tried
        var early = await outbox.ProcessAsync(sender, default);
        Assert.Equal(1, early.Skipped);
        Assert.Equal(1, sender.Calls);

        clock = clock.AddSeconds(120);
        var late = await outbox.ProcessAsync(sender, default);
        Assert.Equal(1, late.Sent);
        Assert.Empty(outbox.List());
    }

    [Fact]
    public async Task Server_errors_back_off_with_growing_delays()
    {
        using var dir = new TempDir();
        var clock = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var outbox = new Outbox(dir.Paths.Outbox, utcNow: () => clock);
        AddReport(outbox, "5xx", clock);
        var sender = new ScriptedSender(new SendResult(SendStatus.ServerError, HttpStatus: 502));

        await outbox.ProcessAsync(sender, default);
        var first = outbox.List()[0].Report.NotBeforeUtc!.Value;
        clock = first.AddSeconds(1);
        await outbox.ProcessAsync(sender, default);
        var second = outbox.List()[0].Report.NotBeforeUtc!.Value;

        Assert.Equal(TimeSpan.FromMinutes(5), first - new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(TimeSpan.FromMinutes(10), second - clock);
    }

    [Fact]
    public async Task A_down_network_stops_the_pass_but_a_rejected_report_does_not_block_the_next()
    {
        using var dir = new TempDir();
        var t = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var outbox = new Outbox(dir.Paths.Outbox);
        AddReport(outbox, "first", t);
        AddReport(outbox, "second", t.AddMinutes(1));

        var offline = new ScriptedSender(Offline);
        await outbox.ProcessAsync(offline, default);
        Assert.Equal(1, offline.Calls);   // did not hammer the second one

        var mixed = new ScriptedSender(new SendResult(SendStatus.Rejected, HttpStatus: 400), Ok);
        var run = await outbox.ProcessAsync(mixed, default, ignoreBackoff: true);
        Assert.Equal(1, run.Sent);
        Assert.Equal(1, run.Failed);
        Assert.Single(outbox.List());   // the rejected one is kept, never deleted without a 2xx
    }

    [Fact]
    public async Task Reports_go_out_oldest_first()
    {
        using var dir = new TempDir();
        var t = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var outbox = new Outbox(dir.Paths.Outbox);
        var newer = AddReport(outbox, "newer", t.AddHours(1));
        var older = AddReport(outbox, "older", t);
        var sender = new ScriptedSender(Ok);

        await outbox.ProcessAsync(sender, default);

        Assert.Equal([older, newer], sender.Sent);
    }

    [Fact]
    public async Task A_report_stuck_for_a_month_moves_to_the_expired_folder_and_is_not_deleted()
    {
        using var dir = new TempDir();
        var clock = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var outbox = new Outbox(dir.Paths.Outbox, dir.Paths.Expired, () => clock);
        var id = AddReport(outbox, "old", clock.AddDays(-31));
        var sender = new ScriptedSender(Ok);

        await outbox.ProcessAsync(sender, default);

        Assert.Equal(0, sender.Calls);
        Assert.Empty(outbox.List());
        Assert.True(File.Exists(Path.Combine(dir.Paths.Expired, id, "report.json")));
    }

    [Fact]
    public void Half_written_and_damaged_entries_are_ignored()
    {
        using var dir = new TempDir();
        var outbox = new Outbox(dir.Paths.Outbox);
        var good = AddReport(outbox, "fine");
        Directory.CreateDirectory(Path.Combine(dir.Paths.Outbox, "20260101-000000-abc.partial"));
        Directory.CreateDirectory(Path.Combine(dir.Paths.Outbox, "broken"));
        File.WriteAllText(Path.Combine(dir.Paths.Outbox, "broken", "report.json"), "{ not json");

        Assert.Equal([good], outbox.List().Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task A_sender_that_throws_keeps_the_report()
    {
        using var dir = new TempDir();
        var outbox = new Outbox(dir.Paths.Outbox);
        AddReport(outbox, "boom");

        await outbox.ProcessAsync(new ThrowingSender(), default);

        Assert.Single(outbox.List());
    }

    private sealed class ThrowingSender : IReportSender
    {
        public Task<SendResult> SendAsync(OutboxItem item, CancellationToken ct) => throw new InvalidOperationException("boom");
    }
}
