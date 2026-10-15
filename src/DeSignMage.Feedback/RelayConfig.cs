namespace DeSignMage.Feedback;

/// <summary>
/// Where reports are posted: the public address of the feedback relay (a small web service that checks a report and passes it on to
/// the project's chat channel). The address is not a secret, so it is part of the source; <see cref="SettingsKey"/> in the settings
/// file overrides it (for a test relay). The program never holds anything that could post to the channel directly.
/// </summary>
public sealed class RelayConfig
{
    /// <summary>The public relay (see <c>tools/feedback-relay</c>).</summary>
    public const string DefaultUrl = "https://designmage-feedback.dell100wattac.workers.dev/";

    /// <summary>A fixed identifier sent with every report (with a <c>DeSignMage/</c> user agent). Public, not a secret: it only lets the relay ignore stray traffic.</summary>
    public const string ClientHeader = "X-DeSignMage-Client";
    public const string ClientValue = "feedback-v1";

    /// <summary>Settings-file key that overrides <see cref="DefaultUrl"/>.</summary>
    public const string SettingsKey = "feedback.relayUrl";

    public RelayConfig(Uri url) => Url = url;

    public Uri Url { get; }

    /// <summary>The default relay, or the address in the settings when it is usable (https, or http to this computer for testing).</summary>
    public static RelayConfig FromSettings(SettingsStore? settings)
    {
        var text = settings?.Get<string?>(SettingsKey, null);
        if (TryParse(text, out var custom)) return new RelayConfig(custom);
        if (!string.IsNullOrWhiteSpace(text)) AppLog.Warn("feedback", $"Ignoring {SettingsKey}: it is not an https address");
        return new RelayConfig(new Uri(DefaultUrl));
    }

    public static bool TryParse(string? text, out Uri url)
    {
        url = null!;
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var parsed) || !string.IsNullOrEmpty(parsed.UserInfo)) return false;
        bool ok = parsed.Scheme == Uri.UriSchemeHttps || (parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback);
        if (ok) url = parsed;
        return ok;
    }

    public override string ToString() => $"RelayConfig({Url.Host})";
}
