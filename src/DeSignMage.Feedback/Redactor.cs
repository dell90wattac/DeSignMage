using System.Text.RegularExpressions;

namespace DeSignMage.Feedback;

/// <summary>
/// Keeps the webhook address out of everything we write (logs, crash reports, report text, error messages). The address is a secret:
/// anyone who has it can post to the feedback channel.
/// </summary>
public static partial class Redactor
{
    [GeneratedRegex(@"https?://[^\s""'<>]*discord(?:app)?\.com/api/[^\s""'<>]*webhooks/[^\s""'<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex WebhookUrl();

    private static readonly object Gate = new();
    private static string[] _secrets = [];

    /// <summary>Also blanks these exact strings (the id and token inside the configured address) wherever they appear.</summary>
    public static void RegisterSecrets(IEnumerable<string> secrets)
    {
        lock (Gate) _secrets = [.. _secrets, .. secrets.Where(s => s.Length >= 8)];
    }

    public static void ClearSecrets()
    {
        lock (Gate) _secrets = [];
    }

    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var result = WebhookUrl().Replace(text, "[webhook address]");
        string[] secrets;
        lock (Gate) secrets = _secrets;
        foreach (var s in secrets) result = result.Replace(s, "[secret]", StringComparison.Ordinal);
        return result;
    }
}

/// <summary>Small text helpers shared by the report builder and the logs.</summary>
public static class TextUtil
{
    /// <summary>At most <paramref name="max"/> UTF-16 units; never splits a surrogate pair; adds the ellipsis when it cut something.</summary>
    public static string Truncate(string? text, int max, string ellipsis = "…")
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length <= max) return text;
        int cut = Math.Max(0, max - ellipsis.Length);
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + ellipsis;
    }

    /// <summary>The first line, whitespace collapsed, cut at a word boundary: for titles.</summary>
    public static string FirstWords(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var line = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        line = Regex.Replace(line, @"\s+", " ");
        if (line.Length <= max) return line;
        int cut = line.LastIndexOf(' ', max - 1);
        if (cut < max / 2) cut = max - 1;
        if (cut > 0 && char.IsHighSurrogate(line[cut - 1])) cut--;
        return line[..cut].TrimEnd(' ', ',', ';', ':', '.') + "…";
    }

    /// <summary>Drops control characters except tab and line breaks, and normalizes line breaks to \n.</summary>
    public static string CleanControl(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var chars = text.Replace("\r\n", "\n").Replace('\r', '\n').Where(c => c == '\n' || c == '\t' || !char.IsControl(c));
        return new string(chars.ToArray());
    }

    /// <summary>A file name made only of letters, digits, dot, dash and underscore.</summary>
    public static string SafeFileName(string? name, string fallback = "file")
    {
        var cleaned = new string((name ?? "").Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray()).Trim('.', '_');
        return cleaned.Length == 0 ? fallback : TextUtil.Truncate(cleaned, 60, "");
    }

    public static string FormatBytes(long bytes) =>
        bytes >= 1_048_576 ? $"{bytes / 1_048_576.0:0.0} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0} KB" : $"{bytes} bytes";
}
