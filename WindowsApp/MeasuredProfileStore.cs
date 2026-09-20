using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RainbowRecoil;

/// <summary>
/// Loads versioned, provenance-bearing measurements without ever labeling the
/// built-in estimates as measured data. All valid candidates are retained so
/// the exact loadout can be resolved only after attachments and optic are known.
/// </summary>
internal static class MeasuredProfileStore
{
    public const int CurrentSchemaVersion = 1;
    public static string DirectoryPath => Path.Combine(
        Path.GetDirectoryName(SettingsManager.ConfigFilePath)!,
        "measured-profile-packs");
    public static string? LastWarning { get; private set; }

    public static void Apply(IList<WeaponProfile> profiles, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        directory ??= DirectoryPath;
        LastWarning = null;
        if (!Directory.Exists(directory))
        {
            return;
        }

        var candidates = new List<MeasuredCandidate>();
        var errors = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order())
        {
            try
            {
                var pack = JsonSerializer.Deserialize<MeasuredProfilePack>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip
                    }) ?? throw new InvalidDataException("Pack is empty.");
                ValidatePack(pack);
                candidates.AddRange(pack.Profiles.Select(profile => new MeasuredCandidate(
                    profile,
                    pack.GameBuild.Trim(),
                    pack.Source.Trim(),
                    path)));
            }
            catch (Exception exception)
            {
                errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        foreach (var profile in profiles)
        {
            profile.MeasuredPatterns = candidates
                .Where(value => value.Profile.Weapon.Equals(
                    profile.Name,
                    StringComparison.OrdinalIgnoreCase))
                .Select(value => new MeasuredPatternVariant(
                    value.Profile.Grip.Trim(),
                    value.Profile.Barrel.Trim(),
                    value.Profile.Optic.Trim(),
                    value.Profile.Operator?.Trim() ?? string.Empty,
                    value.GameBuild,
                    value.Profile.RoundsPerMinute,
                    value.Profile.MeasuredAtUtc,
                    value.Profile.Points
                        .Select(point => new RecoilPatternPoint(
                            point.Horizontal,
                            point.Vertical))
                        .ToArray(),
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Measured · {value.GameBuild} · " +
                        $"{value.Profile.MeasuredAtUtc:yyyy-MM-dd} · " +
                        $"{value.PackSource} · {Path.GetFileName(value.Path)}")))
                .ToArray();
        }

        if (errors.Count > 0)
        {
            LastWarning = string.Join(Environment.NewLine, errors);
            DiagnosticLog.Record("measured-profile-warning", LastWarning);
        }
    }

    private static void ValidatePack(MeasuredProfilePack pack)
    {
        if (pack.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported schema {pack.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(pack.GameBuild))
            throw new InvalidDataException("gameBuild is required.");
        if (string.IsNullOrWhiteSpace(pack.Source))
            throw new InvalidDataException("source is required.");
        if (pack.Profiles is null || pack.Profiles.Count == 0)
            throw new InvalidDataException("At least one measured profile is required.");

        foreach (var profile in pack.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Weapon) ||
                string.IsNullOrWhiteSpace(profile.Grip) ||
                string.IsNullOrWhiteSpace(profile.Barrel) ||
                string.IsNullOrWhiteSpace(profile.Optic))
                throw new InvalidDataException("Weapon, grip, barrel and optic are required.");
            if (profile.Operator is not null && string.IsNullOrWhiteSpace(profile.Operator))
                throw new InvalidDataException($"{profile.Weapon}: operator cannot be blank.");
            if (profile.MeasuredAtUtc == default)
                throw new InvalidDataException($"{profile.Weapon}: measuredAtUtc is required.");
            if (profile.RoundsPerMinute is < 1 or > 2000)
                throw new InvalidDataException($"{profile.Weapon}: RPM is outside 1..2000.");
            if (profile.Points is null || profile.Points.Count is < 1 or > FirmwareContract.MaximumPatternPoints)
                throw new InvalidDataException($"{profile.Weapon}: point count is outside 1..160.");
            if (profile.Points.Any(point =>
                !float.IsFinite(point.Horizontal) || !float.IsFinite(point.Vertical) ||
                point.Horizontal is < -127 or > 127 || point.Vertical is < 0 or > 127))
                throw new InvalidDataException($"{profile.Weapon}: a point is outside the HID contract.");
        }
    }

    private sealed record MeasuredCandidate(
        MeasuredWeaponProfile Profile,
        string GameBuild,
        string PackSource,
        string Path);
}

internal sealed record MeasuredPatternVariant(
    string Grip,
    string Barrel,
    string Optic,
    string Operator,
    string GameBuild,
    int RoundsPerMinute,
    DateTimeOffset MeasuredAtUtc,
    RecoilPatternPoint[] Points,
    string Source);

internal sealed class MeasuredProfilePack
{
    public int SchemaVersion { get; set; }
    public string GameBuild { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public List<MeasuredWeaponProfile> Profiles { get; set; } = [];
}

internal sealed class MeasuredWeaponProfile
{
    public string Weapon { get; set; } = string.Empty;
    public string Grip { get; set; } = string.Empty;
    public string Barrel { get; set; } = string.Empty;
    public string Optic { get; set; } = string.Empty;
    public string? Operator { get; set; }
    public int RoundsPerMinute { get; set; }
    public DateTimeOffset MeasuredAtUtc { get; set; }
    public List<MeasuredPatternPoint> Points { get; set; } = [];
}

internal sealed class MeasuredPatternPoint
{
    public float Horizontal { get; set; }
    public float Vertical { get; set; }
}
