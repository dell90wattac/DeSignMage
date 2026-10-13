using System.Net;
using System.Text;

namespace DeSignMage.Feedback.Tests;

/// <summary>A made-up address in the right shape. It is not a real webhook, and no test ever sends to the network.</summary>
internal static class Fake
{
    public const string Id = "123456789012345678";
    public const string Token = "FAKEtokenFAKEtokenFAKEtoken_abc-123456";
    public const string Url = $"https://discord.com/api/webhooks/{Id}/{Token}";

    public static WebhookConfig Config(bool forum = false) =>
        WebhookConfig.Parse(Url + (forum ? "\nforumThreads: true" : "")).Config!;

    public const string BugId = "20261012-101500-abc123";

    /// <summary>The one text attachment of Bug(): header, description, notes, undo history, log.</summary>
    public const string BugFile = "DeSignMage-Bug-" + BugId + ".txt";

    public static byte[] Bytes(int count, byte fill = 7) => Enumerable.Repeat(fill, count).ToArray();

    public static ReportRequest Bug(string text = "The ruler is wrong after I zoom.") => new()
    {
        Kind = ReportKind.Bug,
        Id = BugId,
        Description = text,
        Name = "Mum",
        AppVersion = "0.1.0",
        Windows = "Windows 11 (10.0.26300)",
        SystemInfoText = "App version : 0.1.0",
        DocumentSummary = "Sign 3.dsm, 14 objects",
        UndoHistory = ["Add Rectangle", "Group"],
        LogLines = Enumerable.Range(0, 800).Select(i => $"2026-10-12 10:00:00.000 INF [cmd] line {i}").ToList(),
        ScreenshotPng = Bytes(20_000),
        Designs = [new ReportFile("design.dsm", "application/octet-stream", Bytes(30_000))],
    };
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsm-feedback-test-" + Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public AppPaths Paths => new(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (Exception) { }
    }
}

/// <summary>A scripted stand-in for the network: each call gets the next answer and the request is recorded.</summary>
internal sealed class FakeHandler(params Func<HttpResponseMessage>[] answers) : HttpMessageHandler
{
    public List<Recorded> Requests { get; } = [];

    public sealed record Recorded(string Url, string ContentType, string Body, byte[] Raw);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var raw = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Requests.Add(new Recorded(request.RequestUri!.ToString(), request.Content?.Headers.ContentType?.ToString() ?? "", Encoding.UTF8.GetString(raw), raw));
        int index = Math.Min(Requests.Count, answers.Length) - 1;
        return answers[index]();
    }

    public static Func<HttpResponseMessage> NoContent() => () => new HttpResponseMessage(HttpStatusCode.NoContent);

    public static Func<HttpResponseMessage> Status(HttpStatusCode code, string body = "{}") =>
        () => new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static Func<HttpResponseMessage> RateLimited(double retryAfter) =>
        Status(HttpStatusCode.TooManyRequests, $"{{\"message\":\"You are being rate limited.\",\"retry_after\":{retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"global\":false}}");

    public static Func<HttpResponseMessage> Offline() => () => throw new HttpRequestException("No such host is known. (discord.com:443)");
}

/// <summary>Sender used by outbox and service tests: answers from a script, remembers what it was asked to send.</summary>
internal sealed class ScriptedSender(params SendResult[] results) : IReportSender
{
    private int _calls;
    public List<string> Sent { get; } = [];
    public int Calls => _calls;

    public Task<SendResult> SendAsync(OutboxItem item, CancellationToken ct)
    {
        var result = results[Math.Min(_calls, results.Length - 1)];
        _calls++;
        if (result.IsSent) Sent.Add(item.Id);
        return Task.FromResult(result);
    }
}
