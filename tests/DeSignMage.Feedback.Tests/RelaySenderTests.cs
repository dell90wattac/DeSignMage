using System.Net;
using System.Text.Json;

namespace DeSignMage.Feedback.Tests;

public class RelaySenderTests
{
    private static OutboxItem MakeItem(TempDir dir, ReportRequest? request = null)
    {
        var outbox = new Outbox(dir.Paths.Outbox);
        var id = outbox.Add(ReportBuilder.Build(request ?? Fake.Bug()));
        return outbox.Get(id)!;
    }

    private static RelaySender Sender(FakeHandler handler, List<TimeSpan>? waits = null) =>
        new(Fake.Relay, new HttpClient(handler), (t, _) => { waits?.Add(t); return Task.CompletedTask; });

    private static JsonElement Payload(FakeHandler.Recorded request)
    {
        // The multipart body is text for the parts that matter: pull payload_json out of it.
        int start = request.Body.IndexOf('{');
        int end = request.Body.IndexOf("\r\n--", start, StringComparison.Ordinal);
        return JsonDocument.Parse(request.Body[start..end]).RootElement.Clone();
    }

    [Fact]
    public async Task Posts_multipart_with_payload_json_and_files()
    {
        using var dir = new TempDir();
        var item = MakeItem(dir);
        var handler = new FakeHandler(FakeHandler.NoContent());

        var result = await Sender(handler).SendAsync(item, default);

        Assert.True(result.IsSent);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://relay.test/", request.Url);
        Assert.Equal(RelayConfig.ClientValue, request.Client);       // the public app identifier travels with every report
        Assert.StartsWith("multipart/form-data", request.ContentType);
        Assert.Contains("payload_json", request.Body);
        for (int i = 0; i < item.Report.Files.Count; i++) Assert.Contains($"files[{i}]", request.Body);
        Assert.Contains(Fake.BugFile, request.Body);
        Assert.Contains("design.dsm", request.Body);

        var payload = Payload(request);
        var embed = payload.GetProperty("embeds")[0];
        Assert.StartsWith("Bug: ", embed.GetProperty("title").GetString());
        Assert.Equal("attachment://screenshot.png", embed.GetProperty("image").GetProperty("url").GetString());
        Assert.False(payload.TryGetProperty("thread_name", out _));
        Assert.Empty(payload.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
        var attachments = payload.GetProperty("attachments");
        Assert.Equal(item.Report.Files.Count, attachments.GetArrayLength());
        Assert.Equal(0, attachments[0].GetProperty("id").GetInt32());
        Assert.Equal(Fake.BugFile, attachments[0].GetProperty("filename").GetString());
    }

    [Fact]
    public async Task Multipart_framing_is_what_the_relay_expects()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(FakeHandler.NoContent());
        await Sender(handler).SendAsync(MakeItem(dir), default);

        var request = handler.Requests[0];
        var boundary = request.ContentType.Split("boundary=")[1].Trim('"');
        var parts = request.Body.Split("--" + boundary).Skip(1).Where(p => !p.StartsWith("--")).ToList();   // last piece is the closing "--"
        var dispositions = parts.Select(p => p.Split("\r\n").First(l => l.StartsWith("Content-Disposition:"))).ToList();
        // Names and file names are quoted (the relay's host cannot read bare ones), with no second filename* form.
        Assert.Contains(dispositions, d => d == "Content-Disposition: form-data; name=\"payload_json\"");
        Assert.Contains(dispositions, d => d == $"Content-Disposition: form-data; name=\"files[0]\"; filename=\"{Fake.BugFile}\"");
        Assert.Contains(dispositions, d => d.StartsWith("Content-Disposition: form-data; name=\"files[1]\"; filename=\""));
        Assert.DoesNotContain(dispositions, d => d.Contains("filename*"));
        Assert.Contains(parts, p => p.Contains("Content-Type: application/json"));
        Assert.EndsWith("--" + boundary + "--\r\n", request.Body);
    }

    [Fact]
    public async Task The_program_never_chooses_the_thread_the_relay_does()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(FakeHandler.NoContent());
        await Sender(handler).SendAsync(MakeItem(dir), default);

        Assert.False(Payload(handler.Requests[0]).TryGetProperty("thread_name", out _));
    }

    [Fact]
    public async Task Rate_limit_is_waited_out_then_retried()
    {
        using var dir = new TempDir();
        var item = MakeItem(dir);
        var handler = new FakeHandler(FakeHandler.RateLimited(1.5), FakeHandler.RateLimited(0.5), FakeHandler.NoContent());
        var waits = new List<TimeSpan>();

        var result = await Sender(handler, waits: waits).SendAsync(item, default);

        Assert.True(result.IsSent);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, waits.Count);
        Assert.InRange(waits[0].TotalSeconds, 1.5, 2.0);
        Assert.InRange(waits[1].TotalSeconds, 0.5, 1.0);
    }

    [Fact]
    public async Task Long_rate_limit_is_returned_not_slept_through()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(FakeHandler.RateLimited(600));
        var waits = new List<TimeSpan>();

        var result = await Sender(handler, waits: waits).SendAsync(MakeItem(dir), default);

        Assert.Equal(SendStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(600), result.RetryAfter);
        Assert.Empty(waits);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Endless_short_rate_limits_give_up_after_a_few_attempts()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(FakeHandler.RateLimited(0.2));
        var result = await Sender(handler).SendAsync(MakeItem(dir), default);
        Assert.Equal(SendStatus.RateLimited, result.Status);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Offline_is_reported_without_the_address()
    {
        using var dir = new TempDir();
        var result = await Sender(new FakeHandler(FakeHandler.Offline())).SendAsync(MakeItem(dir), default);

        Assert.Equal(SendStatus.Offline, result.Status);
        Assert.DoesNotContain(Fake.Token, result.Detail ?? "");
    }

    [Fact]
    public async Task A_cancelled_send_counts_as_offline_not_as_an_error()
    {
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler(() => { cts.Cancel(); throw new TaskCanceledException(); });

        var result = await Sender(handler).SendAsync(MakeItem(dir), cts.Token);

        Assert.Equal(SendStatus.Offline, result.Status);
    }

    [Fact]
    public async Task Rejected_attachments_are_dropped_and_the_text_still_goes()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(FakeHandler.Status(HttpStatusCode.RequestEntityTooLarge), FakeHandler.NoContent());

        var result = await Sender(handler).SendAsync(MakeItem(dir), default);

        Assert.True(result.IsSent);
        Assert.True(result.AttachmentsDropped);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain("files[0]", handler.Requests[1].Body);
        var second = Payload(handler.Requests[1]);
        Assert.False(second.TryGetProperty("attachments", out _));
        Assert.Contains("Attachments were left out", second.GetProperty("embeds")[0].GetProperty("description").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, SendStatus.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, SendStatus.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, SendStatus.Rejected)]               // an unknown app identifier
    [InlineData(HttpStatusCode.UnsupportedMediaType, SendStatus.Rejected)]    // an attachment type the relay does not take
    [InlineData(HttpStatusCode.ServiceUnavailable, SendStatus.ServerError)]   // the relay is switched off
    [InlineData(HttpStatusCode.BadGateway, SendStatus.ServerError)]
    [InlineData(HttpStatusCode.InternalServerError, SendStatus.ServerError)]
    public async Task Other_failures_are_classified(HttpStatusCode code, SendStatus expected)
    {
        using var dir = new TempDir();
        var result = await Sender(new FakeHandler(FakeHandler.Status(code, "{\"message\":\"Unknown Webhook\",\"code\":10015}"))).SendAsync(MakeItem(dir), default);
        Assert.Equal(expected, result.Status);
        Assert.Equal((int)code, result.HttpStatus);
    }

    [Fact]
    public async Task A_refusal_from_the_relay_carries_its_message_for_the_log()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(FakeHandler.Status(HttpStatusCode.UnsupportedMediaType, "{\"ok\":false,\"error\":\"bad_file_type\",\"message\":\"An attachment is not a kind the feedback service accepts.\"}"));

        var result = await Sender(handler).SendAsync(MakeItem(dir), default);

        Assert.Equal(SendStatus.Rejected, result.Status);
        Assert.Equal("An attachment is not a kind the feedback service accepts.", result.Detail);
    }

    [Fact]
    public async Task The_relays_own_rate_limit_answer_is_understood()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(FakeHandler.Status((HttpStatusCode)429, "{\"ok\":false,\"error\":\"rate_limited\",\"message\":\"Too many reports\",\"retry_after\":829}"));

        var result = await Sender(handler).SendAsync(MakeItem(dir), default);

        Assert.Equal(SendStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(829), result.RetryAfter);
        Assert.Single(handler.Requests);   // too long to wait here: the outbox tries again later
    }

    [Fact]
    public void Payload_text_is_clamped_to_chat_limits_even_if_the_manifest_is_not()
    {
        var report = new OutgoingReport
        {
            Id = "x", Kind = ReportKind.Bug, Title = new string('t', 900), Description = new string('d', 9000), Footer = new string('f', 5000),
            Fields = [new EmbedField { Name = new string('n', 500), Value = new string('v', 3000) }],
        };
        var json = JsonDocument.Parse(RelaySender.BuildPayloadJson(report, [])).RootElement;
        var embed = json.GetProperty("embeds")[0];
        Assert.True(embed.GetProperty("title").GetString()!.Length <= 256);
        Assert.True(embed.GetProperty("description").GetString()!.Length <= 4096);
        Assert.True(embed.GetProperty("fields")[0].GetProperty("value").GetString()!.Length <= 1024);
        Assert.True(embed.GetProperty("footer").GetProperty("text").GetString()!.Length <= 2048);
    }
}
