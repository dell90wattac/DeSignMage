using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DeSignMage.Feedback;

/// <summary>
/// Posts a report to a Discord webhook as <c>multipart/form-data</c> (<c>payload_json</c> + <c>files[n]</c>). Handles 429 rate limits
/// (waits <c>retry_after</c> when it is short), timeouts and being offline; never writes the address anywhere.
/// </summary>
public sealed class DiscordSender : IReportSender
{
    public const string BotName = "DeSign Mage feedback";

    private readonly WebhookConfig _config;
    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="delay">How to wait out a rate limit (tests pass an instant one).</param>
    public DiscordSender(WebhookConfig config, HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _config = config;
        _http = http;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>A rate limit shorter than this is waited out inside the call; a longer one is left to the outbox.</summary>
    public TimeSpan MaxInlineWait { get; init; } = TimeSpan.FromSeconds(20);

    public int MaxAttempts { get; init; } = 4;

    /// <summary>The client the program uses: no overall timeout (callers pass a cancellation token), so slow uploads are not cut short.</summary>
    public static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DeSignMage", "1.0"));
        return client;
    }

    public async Task<SendResult> SendAsync(OutboxItem item, CancellationToken ct)
    {
        bool withFiles = true;
        for (int attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var content = BuildContent(item, withFiles);
                response = await _http.PostAsync(_config.Url, content, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new SendResult(SendStatus.Offline, ct.IsCancellationRequested ? "cancelled or timed out" : "timed out");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
            {
                // The message names the host at most, but scrub anyway: the address is a secret.
                return new SendResult(SendStatus.Offline, Redactor.Scrub(ex.Message));
            }

            using (response)
            {
                int code = (int)response.StatusCode;
                if (response.IsSuccessStatusCode) return new SendResult(SendStatus.Sent, HttpStatus: code, AttachmentsDropped: !withFiles && item.Report.Files.Count > 0);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var wait = await ReadRetryAfterAsync(response, ct).ConfigureAwait(false);
                    if (attempt >= MaxAttempts || wait > MaxInlineWait)
                        return new SendResult(SendStatus.RateLimited, "rate limited", wait, code);
                    try { await _delay(wait + TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return new SendResult(SendStatus.RateLimited, "cancelled while waiting", wait, code); }
                    continue;
                }

                // Too big or malformed with attachments: say what we can, without them, rather than lose the report.
                if (withFiles && item.Report.Files.Count > 0 && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge)
                {
                    AppLog.Warn("discord", $"HTTP {code} with attachments; retrying without them");
                    withFiles = false;
                    continue;
                }

                if (code >= 500) return new SendResult(SendStatus.ServerError, null, null, code);
                return new SendResult(SendStatus.Rejected, await ReadMessageAsync(response, ct).ConfigureAwait(false), null, code);
            }
        }
    }

    // ---------------------------------------------------------------- request

    internal MultipartFormDataContent BuildContent(OutboxItem item, bool withFiles)
    {
        var report = item.Report;
        var files = withFiles ? report.Files : [];
        var multipart = new MultipartFormDataContent("dsm-" + Guid.NewGuid().ToString("N"));
        multipart.Add(new StringContent(BuildPayloadJson(report, files, _config.ForumThreads, withFiles), Encoding.UTF8, "application/json"), "payload_json");
        for (int i = 0; i < files.Count; i++)
        {
            var bytes = new ByteArrayContent(item.ReadFile(files[i].FileName));
            bytes.Headers.ContentType = MediaTypeHeaderValue.TryParse(files[i].ContentType, out var type) ? type : new MediaTypeHeaderValue("application/octet-stream");
            multipart.Add(bytes, $"files[{i}]", files[i].FileName);
        }
        return multipart;
    }

    /// <summary>The <c>payload_json</c> part. Public for tests: it is where the embed limits and the thread name show.</summary>
    public static string BuildPayloadJson(OutgoingReport report, IReadOnlyList<OutgoingFile> files, bool forumThreads, bool filesIncluded = true)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("username", BotName);
            w.WriteStartObject("allowed_mentions");   // report text can never ping anyone
            w.WriteStartArray("parse");
            w.WriteEndArray();
            w.WriteEndObject();
            if (forumThreads) w.WriteString("thread_name", TextUtil.Truncate(report.Title, 100));

            w.WriteStartArray("embeds");
            w.WriteStartObject();
            w.WriteString("title", TextUtil.Truncate(report.Title, ReportBuilder.TitleLimit));
            var description = report.Description;
            if (!filesIncluded && report.Files.Count > 0) description += "\n\n(Attachments were left out: Discord refused the upload.)";
            w.WriteString("description", TextUtil.Truncate(description, 4096));
            w.WriteNumber("color", report.Color);
            if (report.Fields.Count > 0)
            {
                w.WriteStartArray("fields");
                foreach (var f in report.Fields.Take(25))
                {
                    w.WriteStartObject();
                    w.WriteString("name", TextUtil.Truncate(f.Name, 256));
                    w.WriteString("value", f.Value.Length == 0 ? "-" : TextUtil.Truncate(f.Value, ReportBuilder.FieldValueLimit));
                    w.WriteBoolean("inline", f.Inline);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteStartObject("footer");
            w.WriteString("text", TextUtil.Truncate(report.Footer, 2048));
            w.WriteEndObject();
            w.WriteString("timestamp", report.CreatedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
            if (filesIncluded && report.ImageFile is { } image && files.Any(f => f.FileName == image))
            {
                w.WriteStartObject("image");
                w.WriteString("url", "attachment://" + image);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndArray();

            if (files.Count > 0)
            {
                w.WriteStartArray("attachments");
                for (int i = 0; i < files.Count; i++)
                {
                    w.WriteStartObject();
                    w.WriteNumber("id", i);
                    w.WriteString("filename", files[i].FileName);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ---------------------------------------------------------------- responses

    private static async Task<TimeSpan> ReadRetryAfterAsync(HttpResponseMessage response, CancellationToken ct)
    {
        double seconds = -1;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("retry_after", out var value) && value.TryGetDouble(out var d)) seconds = d;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException or InvalidOperationException) { }
        if (seconds < 0 && response.Headers.RetryAfter?.Delta is { } delta) seconds = delta.TotalSeconds;
        if (seconds < 0) seconds = 5;
        return TimeSpan.FromSeconds(Math.Min(seconds, 3600));
    }

    private static async Task<string?> ReadMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("message", out var m) ? Redactor.Scrub(TextUtil.Truncate(m.GetString(), 200)) : null;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException or InvalidOperationException) { return null; }
    }
}
