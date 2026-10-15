using System.Text;

namespace DeSignMage.Feedback.Tests;

public class RelayConfigTests
{
    private static SettingsStore Store(TempDir dir, string? json)
    {
        var path = Path.Combine(dir.Path, "settings.json");
        if (json is not null) File.WriteAllText(path, json);
        return new SettingsStore(path);
    }

    [Fact]
    public void The_public_relay_is_the_default()
    {
        using var dir = new TempDir();
        var config = RelayConfig.FromSettings(Store(dir, null));
        Assert.Equal(new Uri(RelayConfig.DefaultUrl), config.Url);
        Assert.Equal("https", config.Url.Scheme);
        Assert.Equal(config.Url, RelayConfig.FromSettings(null).Url);
    }

    [Fact]
    public void A_setting_can_point_at_another_relay()
    {
        using var dir = new TempDir();
        var config = RelayConfig.FromSettings(Store(dir, "{ \"feedback.relayUrl\": \"https://relay.example.org/feedback\" }"));
        Assert.Equal("https://relay.example.org/feedback", config.Url.ToString());
        Assert.Equal("http://localhost:8787/", RelayConfig.FromSettings(Store(dir, "{ \"feedback.relayUrl\": \"http://localhost:8787/\" }")).Url.ToString());
    }

    [Theory]
    [InlineData("http://relay.example.org/")]                       // plain http only to this computer
    [InlineData("https://user:pass@relay.example.org/")]            // no credentials in the address
    [InlineData("ftp://relay.example.org/")]
    [InlineData("not a url")]
    [InlineData("")]
    public void An_unusable_setting_falls_back_to_the_default(string value)
    {
        using var dir = new TempDir();
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["feedback.relayUrl"] = value });
        Assert.Equal(new Uri(RelayConfig.DefaultUrl), RelayConfig.FromSettings(Store(dir, json)).Url);
    }

    [Fact]
    public void The_program_holds_no_chat_webhook_address()
    {
        // The default address is the relay, never a chat webhook.
        Assert.DoesNotContain("discord", RelayConfig.DefaultUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("webhooks", RelayConfig.DefaultUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_webhook_address_is_never_printed_by_scrubbing()
    {
        Assert.DoesNotContain(Fake.Token, Redactor.Scrub($"failed: {Fake.Url} and more"));
        Assert.DoesNotContain(Fake.Token, Redactor.Scrub("https://discordapp.com/api/v10/webhooks/123456789012345678/" + Fake.Token));
        Assert.Equal("plain text", Redactor.Scrub("plain text"));
        Assert.DoesNotContain(Fake.Token, new RelayConfig(new Uri("https://relay.test/")).ToString());
    }
}

public class FeedbackServiceTests
{
    [Fact]
    public async Task Sent_report_leaves_the_outbox_empty_and_says_thanks()
    {
        using var dir = new TempDir();
        var sender = new ScriptedSender(new SendResult(SendStatus.Sent));
        using var service = new FeedbackService(dir.Paths, sender);

        var result = await service.SubmitAsync(Fake.Bug());

        Assert.Equal(SubmitStatus.Sent, result.Status);
        Assert.Equal("Thanks, sent.", result.Message);
        Assert.Empty(service.Outbox.List());
        Assert.Equal([result.Id], sender.Sent);
    }

    [Fact]
    public async Task Offline_report_is_saved_and_goes_out_on_a_later_background_pass()
    {
        using var dir = new TempDir();
        var sender = new ScriptedSender(new SendResult(SendStatus.Offline, "no network"), new SendResult(SendStatus.Sent));
        using var service = new FeedbackService(dir.Paths, sender);

        var result = await service.SubmitAsync(Fake.Bug());

        Assert.Equal(SubmitStatus.Queued, result.Status);
        Assert.Equal("Saved, will send when online.", result.Message);
        Assert.Single(service.Outbox.List());

        await service.RunBackgroundOnceAsync();
        Assert.Empty(service.Outbox.List());
    }

    [Fact]
    public async Task Without_a_sender_the_report_is_still_saved()
    {
        using var dir = new TempDir();
        using var service = new FeedbackService(dir.Paths, sender: null);

        var result = await service.SubmitAsync(Fake.Bug());

        Assert.False(service.CanSend);
        Assert.Equal(SubmitStatus.NotConfigured, result.Status);
        Assert.Contains("Sending isn't set up yet", result.Message);
        Assert.Single(service.Outbox.List());
        await service.RunBackgroundOnceAsync();   // harmless
        Assert.Single(service.Outbox.List());
    }

    [Theory]
    [InlineData(SendStatus.Offline, "Saved, will send when online.")]
    [InlineData(SendStatus.RateLimited, "Saved. A lot of reports were sent from this computer lately, so this one will be sent a little later.")]
    [InlineData(SendStatus.ServerError, "Saved. The feedback service isn't taking reports right now; this one will be sent later.")]
    [InlineData(SendStatus.Rejected, "Saved. The feedback service isn't taking reports right now; this one will be sent later.")]
    public async Task A_report_the_relay_did_not_take_says_why_in_plain_words(SendStatus status, string message)
    {
        using var dir = new TempDir();
        using var service = new FeedbackService(dir.Paths, new ScriptedSender(new SendResult(status, "x")));

        var result = await service.SubmitAsync(Fake.Bug());

        Assert.Equal(SubmitStatus.Queued, result.Status);
        Assert.Equal(message, result.Message);
        Assert.Single(service.Outbox.List());
    }

    [Fact]
    public void Design_and_screenshot_choices_start_off_and_are_remembered()
    {
        using var dir = new TempDir();
        using (var first = new FeedbackService(dir.Paths, null))
        {
            Assert.False(first.IncludeDesign);
            Assert.False(first.IncludeScreenshot);
            first.IncludeDesign = true;
        }
        using var second = new FeedbackService(dir.Paths, null);
        Assert.True(second.IncludeDesign);
        Assert.False(second.IncludeScreenshot);
    }

    [Fact]
    public async Task A_send_that_takes_too_long_falls_back_to_queued()
    {
        using var dir = new TempDir();
        using var service = new FeedbackService(dir.Paths, new HangingSender()) { SendTimeout = TimeSpan.FromMilliseconds(100) };

        var result = await service.SubmitAsync(Fake.Bug());

        Assert.Equal(SubmitStatus.Queued, result.Status);
        Assert.Single(service.Outbox.List());
    }

    [Fact]
    public async Task The_real_sender_with_a_fake_network_delivers_end_to_end()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(FakeHandler.RateLimited(0.01), FakeHandler.NoContent());
        var sender = new RelaySender(Fake.Relay, new HttpClient(handler), (_, _) => Task.CompletedTask);
        using var service = new FeedbackService(dir.Paths, sender);

        var result = await service.SubmitAsync(Fake.Bug());

        Assert.Equal(SubmitStatus.Sent, result.Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Empty(service.Outbox.List());
    }

    [Fact]
    public async Task Feature_report_sends_only_text_and_version()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(FakeHandler.NoContent());
        using var service = new FeedbackService(dir.Paths, new RelaySender(Fake.Relay, new HttpClient(handler)));

        var result = await service.SubmitAsync(new ReportRequest { Kind = ReportKind.Feature, Description = "A polygon tool please", AppVersion = "0.1.0" });

        Assert.Equal(SubmitStatus.Sent, result.Status);
        var body = handler.Requests[0].Body;
        Assert.Contains("A polygon tool please", body);
        Assert.DoesNotContain("files[0]", body);
    }

    [Fact]
    public void The_name_is_remembered_between_sessions()
    {
        using var dir = new TempDir();
        using (var first = new FeedbackService(dir.Paths, null))
        {
            Assert.Null(first.SavedName);
            first.SaveName("  Mum ");
        }
        using var second = new FeedbackService(dir.Paths, null);
        Assert.Equal("Mum", second.SavedName);
    }

    private sealed class HangingSender : IReportSender
    {
        public async Task<SendResult> SendAsync(OutboxItem item, CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            return new SendResult(SendStatus.Offline, "timed out");
        }
    }
}

public class LogTests
{
    [Fact]
    public void Ring_keeps_the_latest_lines_in_order()
    {
        using var log = new LogService(ringCapacity: 10);
        for (int i = 0; i < 25; i++) log.Write(LogLevel.Info, "t", $"line {i}");

        var tail = log.Tail(100);
        Assert.Equal(10, tail.Count);
        Assert.EndsWith("line 24", tail[^1]);
        Assert.EndsWith("line 15", tail[0]);
        Assert.Equal(3, log.Tail(3).Count);
        Assert.EndsWith("line 22", log.Tail(3)[0]);
    }

    [Fact]
    public void Lines_have_time_level_and_category_and_multi_line_messages_are_split()
    {
        using var log = new LogService();
        log.Write(LogLevel.Warn, "open", "first\r\nsecond\nthird");

        var lines = log.Tail(10);
        Assert.Equal(3, lines.Count);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} WRN \[open\] first$", lines[0]);
        Assert.Equal("    second", lines[1]);
    }

    [Fact]
    public void File_log_rolls_over_and_keeps_a_few_files()
    {
        using var dir = new TempDir();
        using (var log = new LogService(directory: Path.Combine(dir.Path, "logs"), maxFileBytes: 2000, keepFiles: 3))
        {
            for (int i = 0; i < 200; i++) log.Write(LogLevel.Info, "t", $"line number {i} {new string('x', 30)}");
            log.Flush();
        }
        var files = Directory.GetFiles(Path.Combine(dir.Path, "logs")).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Contains("designmage.log", files);
        Assert.Contains("designmage.1.log", files);
        Assert.True(files.Length <= 3, string.Join(",", files));
        Assert.Contains("line number 199", File.ReadAllText(Path.Combine(dir.Path, "logs", "designmage.log")));
    }

    [Fact]
    public void The_webhook_address_is_scrubbed_from_log_lines()
    {
        using var log = new LogService();
        log.Write(LogLevel.Error, "discord", $"request to {Fake.Url} failed");
        Assert.DoesNotContain(Fake.Token, string.Concat(log.Tail(5)));
    }

    [Fact]
    public void A_log_folder_that_cannot_be_used_does_not_break_anything()
    {
        using var dir = new TempDir();
        var blocker = Path.Combine(dir.Path, "file");
        File.WriteAllText(blocker, "x");   // a file where the log folder should be
        using var log = new LogService(directory: Path.Combine(blocker, "logs"));
        log.Write(LogLevel.Info, "t", "still works in memory");
        Assert.Single(log.Tail(5));
    }

    [Fact]
    public void Exceptions_are_logged_with_their_stack()
    {
        using var log = new LogService();
        AppLog.Use(log);
        try
        {
            try { throw new InvalidOperationException("kaput"); }
            catch (Exception ex) { AppLog.Error("save", "Saving failed", ex); }
        }
        finally { AppLog.Use(new LogService()); }

        var text = string.Join("\n", log.Tail(50));
        Assert.Contains("ERR [save] Saving failed: System.InvalidOperationException: kaput", text);
        Assert.Contains("LogTests", text);
    }
}

public class SystemInfoTests
{
    [Fact]
    public void Collects_version_windows_dotnet_and_displays()
    {
        var info = SystemInfo.Collect(typeof(SystemInfoTests).Assembly, ["1920x1080 at 125% (primary)"]);
        var text = info.ToText();
        Assert.Contains("Windows", info.Windows);
        Assert.Contains(".NET", info.DotNet);
        Assert.Contains("1920x1080 at 125% (primary)", text);
        Assert.Contains("App version", text);
        Assert.False(string.IsNullOrWhiteSpace(info.AppVersion));
    }
}

public class TextUtilTests
{
    [Fact]
    public void Truncate_never_splits_a_surrogate_pair()
    {
        var s = new string('a', 9) + "😀" + "bbb";
        var t = TextUtil.Truncate(s, 11, "…");
        Assert.True(t.Length <= 11);
        Assert.DoesNotContain('\ufffd', t);
        Assert.False(char.IsHighSurrogate(t[^2]));
    }

    [Fact]
    public void Safe_file_names_only_use_plain_characters()
    {
        Assert.Equal("Sign_3.dsm", TextUtil.SafeFileName("Sign 3.dsm"));
        Assert.Equal("file", TextUtil.SafeFileName("..\\.."));
        Assert.Equal(".", new string(TextUtil.SafeFileName("a/b\\c:d.dsm").Where(c => c == '.').ToArray()));
    }
}
