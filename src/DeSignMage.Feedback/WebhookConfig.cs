using System.Text.RegularExpressions;

namespace DeSignMage.Feedback;

/// <summary>
/// Where reports go: a Discord webhook address, plus options. The address is a secret (the user creates it and pastes it into a file);
/// nothing here ever prints it, and <see cref="ToString"/> hides it.
/// </summary>
public sealed partial class WebhookConfig
{
    public const string FileName = "feedback-webhook.txt";

    [GeneratedRegex(@"^https://(?:(?:ptb|canary)\.)?(?:discord|discordapp)\.com/api/(?:v\d+/)?webhooks/(?<id>\d{5,25})/(?<token>[A-Za-z0-9_\-]{20,})/?(?:\?[A-Za-z0-9_=&\-]*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    private WebhookConfig(string url, bool forumThreads)
    {
        Url = url;
        ForumThreads = forumThreads;
    }

    public string Url { get; }

    /// <summary>Send <c>thread_name</c> so every report becomes its own post in a Discord Forum channel.</summary>
    public bool ForumThreads { get; }

    public override string ToString() => "WebhookConfig(configured)";

    public static bool LooksValid(string? url) => url is not null && Pattern().IsMatch(url.Trim());

    /// <summary>The webhook's id and token, for <see cref="Redactor.RegisterSecrets"/>.</summary>
    public IEnumerable<string> Secrets()
    {
        var m = Pattern().Match(Url);
        if (m.Success) { yield return m.Groups["id"].Value; yield return m.Groups["token"].Value; }
    }

    /// <summary>
    /// Reads the file text: the address on its own line (lines starting with # are comments), optionally <c>forumThreads: true</c>.
    /// </summary>
    public static WebhookConfigResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return WebhookConfigResult.Missing;
        string? url = null;
        bool forum = false;
        bool sawAddress = false;
        foreach (var raw in text.Replace("﻿", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;
            var option = Regex.Match(line, @"^forumThreads\s*[:=]\s*(true|false|yes|no|on|off|1|0)\s*$", RegexOptions.IgnoreCase);
            if (option.Success)
            {
                forum = option.Groups[1].Value.ToLowerInvariant() is "true" or "yes" or "on" or "1";
                continue;
            }
            if (!sawAddress)
            {
                sawAddress = true;
                if (LooksValid(line)) url = line;
            }
        }
        if (!sawAddress) return WebhookConfigResult.Missing;
        return url is null
            ? new WebhookConfigResult(null, "The feedback address does not look like https://discord.com/api/webhooks/<id>/<token>.")
            : new WebhookConfigResult(new WebhookConfig(url, forum), null);
    }

    /// <summary>
    /// Finds the configuration: first the address built into the program at build time (<paramref name="embedded"/>), then
    /// <c>feedback-webhook.txt</c> next to the program. The first valid one wins.
    /// </summary>
    public static WebhookConfigResult Load(Func<string?>? embedded, string? programDirectory)
    {
        WebhookConfigResult? invalid = null;
        foreach (var source in Sources())
        {
            string? text;
            try { text = source(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            var r = Parse(text);
            if (r.Config is not null) return r;
            if (r.Problem is not null) invalid ??= r;
        }
        return invalid ?? WebhookConfigResult.Missing;

        IEnumerable<Func<string?>> Sources()
        {
            if (embedded is not null) yield return embedded;
            if (programDirectory is not null)
                yield return () => File.Exists(Path.Combine(programDirectory, FileName)) ? File.ReadAllText(Path.Combine(programDirectory, FileName)) : null;
        }
    }
}

/// <summary>A configuration, "missing" (nothing set up), or a problem description that never contains the address.</summary>
public sealed record WebhookConfigResult(WebhookConfig? Config, string? Problem)
{
    public static WebhookConfigResult Missing { get; } = new(null, null);
    public bool IsMissing => Config is null && Problem is null;
}
