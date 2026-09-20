using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
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
    private readonly Uri _latestReleaseApi;

    public ReleaseUpdateService(HttpClient? client = null, Uri? latestReleaseApi = null)
    {
        _client = client ?? SharedClient;
        _latestReleaseApi = latestReleaseApi ?? LatestReleaseApi;
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
            _client.DefaultRequestHeaders.UserAgent.ParseAdd($"ZeroSense-Updater/{version}");
        }
        if (!_client.DefaultRequestHeaders.Accept.Any())
        {
            _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }
    }

    public async Task<ReleaseUpdateResult> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        using var response = await _client.GetAsync(_latestReleaseApi, cancellationToken)
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
        var assetNames = root.GetProperty("assets")
            .EnumerateArray()
            .Select(asset => asset.GetProperty("name").GetString() ?? string.Empty)
            .ToArray();
        var hasInstaller = assetNames.Any(name =>
            name.StartsWith("ZeroSense-Setup-", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        var hasStarterBundle = assetNames.Any(name =>
            name.StartsWith("ZeroSense-", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith("-Starter-Bundle.zip", StringComparison.OrdinalIgnoreCase));
        var hasChecksumManifest = assetNames.Any(name =>
            name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
        return new ReleaseUpdateResult(
            availableVersion > currentVersion,
            currentVersion,
            availableVersion,
            page,
            hasInstaller,
            hasStarterBundle,
            hasChecksumManifest);
    }
}

internal sealed record ReleaseUpdateResult(
    bool IsUpdateAvailable,
    Version CurrentVersion,
    Version AvailableVersion,
    Uri? ReleasePage,
    bool HasInstaller,
    bool HasStarterBundle,
    bool HasChecksumManifest)
{
    public static ReleaseUpdateResult Current(Version version) =>
        new(false, version, version, null, false, false, false);
}
