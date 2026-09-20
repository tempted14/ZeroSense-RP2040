using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RainbowRecoil;

internal sealed class ReleaseUpdateService
{
    private static readonly Uri LatestReleaseApi = new(
        "https://api.github.com/repos/tempted14/ZeroSense-RP2040/releases/latest");
    private static readonly HttpClient SharedClient = new();
    private readonly HttpClient _client;

    public ReleaseUpdateService(HttpClient? client = null)
    {
        _client = client ?? SharedClient;
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("ZeroSense-Updater/1.3");
        }
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<ReleaseUpdateResult> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        using var response = await _client.GetAsync(LatestReleaseApi, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() ||
            root.GetProperty("prerelease").GetBoolean())
        {
            return ReleaseUpdateResult.Current(currentVersion);
        }

        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        var normalized = tag.Trim().TrimStart('v', 'V').Split('-', 2)[0];
        if (!Version.TryParse(normalized, out var availableVersion))
        {
            throw new InvalidOperationException($"Release tag '{tag}' is not a valid version.");
        }
        var page = new Uri(root.GetProperty("html_url").GetString()
            ?? throw new InvalidOperationException("Release URL is missing."));
        var hasSignedInstaller = root.GetProperty("assets")
            .EnumerateArray()
            .Any(asset =>
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                return name.StartsWith("ZeroSense-Setup-", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            });
        return new ReleaseUpdateResult(
            availableVersion > currentVersion,
            currentVersion,
            availableVersion,
            page,
            hasSignedInstaller);
    }
}

internal sealed record ReleaseUpdateResult(
    bool IsUpdateAvailable,
    Version CurrentVersion,
    Version AvailableVersion,
    Uri? ReleasePage,
    bool HasSignedInstaller)
{
    public static ReleaseUpdateResult Current(Version version) =>
        new(false, version, version, null, false);
}
