using Velopack;
using Velopack.Sources;

namespace DeSignMage.App.Updates;

/// <summary>Where updates come from.</summary>
internal enum UpdateSourceKind { GitHub, Directory, Web }

internal sealed record UpdateSourceSpec(UpdateSourceKind Kind, string Location)
{
    public const string DefaultRepository = "https://github.com/dell90wattac/DeSignMage";

    /// <summary>
    /// The public GitHub releases of this program, unless an address is given (the <c>DESIGNMAGE_UPDATE_SOURCE</c> environment variable,
    /// or the <c>updates.source</c> setting): a GitHub repository address, another web address, or a folder (used for testing an
    /// update on one computer).
    /// </summary>
    public static UpdateSourceSpec Resolve(string? configured)
    {
        string text = configured?.Trim() ?? "";
        if (text.Length == 0) return new(UpdateSourceKind.GitHub, DefaultRepository);
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return new(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? UpdateSourceKind.GitHub : UpdateSourceKind.Web, text);
        return new(UpdateSourceKind.Directory, text);
    }

    internal IUpdateSource CreateSource() => Kind switch
    {
        UpdateSourceKind.GitHub => new GithubSource(Location, accessToken: null, prerelease: false),
        UpdateSourceKind.Web => new SimpleWebSource(Location),
        _ => new SimpleFileSource(new DirectoryInfo(Location)),
    };
}

/// <summary>The real update machinery: Velopack (MIT). All its types stay in this file.</summary>
internal sealed class VelopackUpdateClient : IUpdateClient
{
    private readonly UpdateManager _manager;

    public VelopackUpdateClient(UpdateSourceSpec source)
    {
        _manager = new UpdateManager(source.CreateSource());
    }

    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? "";

    public UpdateOffer? DownloadedUpdate() =>
        _manager.UpdatePendingRestart is { } asset ? new UpdateOffer(asset.Version.ToString(), asset, asset.NotesMarkdown) : null;

    public async Task<UpdateOffer?> CheckAsync(CancellationToken ct)
    {
        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return info is null ? null : new UpdateOffer(info.TargetFullRelease.Version.ToString(), info, info.TargetFullRelease.NotesMarkdown);
    }

    public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct) =>
        _manager.DownloadUpdatesAsync((UpdateInfo)offer.Handle!, progress, ct);

    public void ApplyAndRestart(UpdateOffer offer, IReadOnlyList<string> restartArgs)
    {
        var asset = offer.Handle switch { UpdateInfo info => info.TargetFullRelease, VelopackAsset a => a, _ => throw new InvalidOperationException("Unknown update.") };
        _manager.ApplyUpdatesAndRestart(asset, restartArgs.ToArray());
    }
}
