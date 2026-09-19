using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RainbowRecoil;

/// <summary>
/// Runtime profile sent to the connected board. Values represent HID counts per firmware
/// movement tick at the reference calibration. Full accuracy still requires a
/// controlled shooting-range pass because the published roster does not provide
/// authoritative recoil vectors.
/// </summary>
public sealed class WeaponProfile
{
    public static string? LastLoadError { get; private set; }

    public string Name { get; set; } = "";
    public string WeaponType { get; set; } = "Unknown";
    public string[] Operators { get; set; } = Array.Empty<string>();
    public double VerticalCompensation { get; set; }
    public double HorizontalCompensation { get; set; }
    public int BurstProgression { get; set; }
    public string Grip { get; set; } = "Not applicable";
    public string Barrel { get; set; } = "Not applicable";
    public bool IsBaseline { get; set; } = true;
    public int RoundsPerMinute { get; set; }
    public int MagazineSize { get; set; }

    [JsonIgnore]
    public RecoilPatternPoint[] Pattern { get; set; } = Array.Empty<RecoilPatternPoint>();

    [JsonIgnore]
    public PatternDataQuality PatternDataQuality { get; set; } = PatternDataQuality.None;

    [JsonIgnore]
    public string PatternSource { get; set; } = "No pattern data";

    [JsonIgnore]
    public bool SupportsContinuousCompensation => WeaponType is
        "Assault Rifle" or "SMG" or "LMG" or "Machine Pistol";

    [JsonIgnore]
    public bool HasWeaponPattern => SupportsContinuousCompensation &&
        RoundsPerMinute > 0 && Pattern.Length > 0;

    [JsonIgnore]
    public bool SupportsRapidFire => RapidFireCatalog.TryGetRoundsPerMinute(this, out _);

    [JsonIgnore]
    public int RapidFireRoundsPerMinute => RapidFireCatalog.TryGetRoundsPerMinute(this, out var rate)
        ? rate
        : 0;

    public WeaponProfile()
    {
    }

    public WeaponProfile(
        string name,
        string weaponType,
        string[] operators,
        double verticalCompensation,
        double horizontalCompensation,
        int burstProgression,
        string grip,
        string barrel,
        bool isBaseline = true,
        int roundsPerMinute = 0,
        int magazineSize = 0)
    {
        Name = name;
        WeaponType = weaponType;
        Operators = operators;
        VerticalCompensation = verticalCompensation;
        HorizontalCompensation = horizontalCompensation;
        BurstProgression = burstProgression;
        Grip = grip;
        Barrel = barrel;
        IsBaseline = isBaseline;
        RoundsPerMinute = roundsPerMinute;
        MagazineSize = magazineSize;
    }

    public static IReadOnlyList<WeaponProfile> DefaultProfiles { get; } = SiegeCatalog.Weapons
        .Select(CreateBaseline)
        .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    public static WeaponProfile? FindByName(string? name) => string.IsNullOrWhiteSpace(name)
        ? null
        : DefaultProfiles.FirstOrDefault(
            profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static List<WeaponProfile> LoadAll()
    {
        var customPath = Path.Combine(
            Path.GetDirectoryName(SettingsManager.ConfigFilePath)!,
            "weapon-profiles.json");

        var custom = LoadFromFile(customPath);
        if (custom.Count == 0)
        {
            return DefaultProfiles.Select(Clone).ToList();
        }

        var customByName = custom
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Name))
            .GroupBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        return DefaultProfiles
            .Select(defaultProfile => customByName.TryGetValue(defaultProfile.Name, out var replacement)
                ? MarkModified(Hydrate(replacement, defaultProfile))
                : Clone(defaultProfile))
            .Concat(custom
                .Where(profile => !string.IsNullOrWhiteSpace(profile.Name) && FindByName(profile.Name) == null)
                .Select(profile => MarkModified(Hydrate(profile, null))))
            .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static List<WeaponProfile> LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            LastLoadError = null;
            return new List<WeaponProfile>();
        }

        try
        {
            var profiles = ReadProfileFile(path);
            LastLoadError = null;
            return profiles;
        }
        catch (Exception ex)
        {
            var backupPath = path + ".bak";
            if (File.Exists(backupPath))
            {
                try
                {
                    var recovered = ReadProfileFile(backupPath);
                    try
                    {
                        var corruptPath = path + ".corrupt";
                        File.Move(path, corruptPath, true);
                        File.Copy(backupPath, path, true);
                        LastLoadError =
                            $"The profile file was corrupt and was restored from its backup. " +
                            $"The damaged copy is preserved at {corruptPath}.";
                    }
                    catch (Exception restoreException)
                    {
                        LastLoadError =
                            "The profile file was corrupt. The backup was loaded in memory, " +
                            $"but the primary file could not be repaired: {restoreException.Message}";
                    }
                    DiagnosticLog.Record("profiles", "Recovered a corrupt profile file from backup.");
                    return recovered;
                }
                catch (Exception recoveryException)
                {
                    LastLoadError =
                        $"Could not load {path}: {ex.Message} Backup recovery also failed: " +
                        recoveryException.Message;
                }
            }
            else
            {
                LastLoadError = $"Could not load {path}: {ex.Message}";
            }
            System.Diagnostics.Debug.WriteLine($"Profile load error: {LastLoadError}");
            return new List<WeaponProfile>();
        }
    }

    public static void SaveToFile(string path, IEnumerable<WeaponProfile> profiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(profiles);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        var temporaryPath = path + ".tmp";
        try
        {
            if (File.Exists(path))
            {
                try
                {
                    _ = ReadProfileFile(path);
                    File.Copy(path, path + ".bak", true);
                }
                catch
                {
                    // Never replace a known-good backup with a corrupt primary.
                }
            }
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(profiles, options));
            File.Move(temporaryPath, path, true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
            }
            throw;
        }
    }

    private static List<WeaponProfile> ReadProfileFile(string path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        return JsonSerializer.Deserialize<List<WeaponProfile>>(
            File.ReadAllText(path), options) ?? new List<WeaponProfile>();
    }

    private static WeaponProfile MarkModified(WeaponProfile profile)
    {
        profile.IsBaseline = false;
        return profile;
    }

    internal WeaponProfile WithAttachmentSetup(ResolvedAttachmentSetup setup)
    {
        var effective = Clone(this);
        var originalMultiplier = SupportsRapidFire
            ? RecoilAttachmentModel.SemiAutomaticVerticalMultiplier(Grip, Barrel)
            : RecoilAttachmentModel.ContinuousVerticalMultiplier(Grip, Barrel);
        var effectiveMultiplier = SupportsRapidFire
            ? RecoilAttachmentModel.SemiAutomaticVerticalMultiplier(setup.Grip, setup.Barrel)
            : RecoilAttachmentModel.ContinuousVerticalMultiplier(setup.Grip, setup.Barrel);
        if (originalMultiplier > 0)
        {
            effective.VerticalCompensation = Math.Round(
                VerticalCompensation / originalMultiplier * effectiveMultiplier,
                3);
        }

        effective.Grip = setup.Grip;
        effective.Barrel = setup.Barrel;
        effective.Pattern = WeaponPatternCatalog.CreateEstimatedPattern(
            effective.Name,
            effective.VerticalCompensation,
            effective.HorizontalCompensation,
            effective.Barrel,
            effective.RoundsPerMinute,
            effective.MagazineSize);
        return effective;
    }

    internal WeaponProfile WithOutputStrength(double requestedStrength)
    {
        var strength = RecoilStrengthModel.Normalize(requestedStrength);
        var effective = Clone(this);
        effective.VerticalCompensation = Math.Clamp(
            effective.VerticalCompensation * strength,
            0.0,
            20.0);
        effective.HorizontalCompensation = Math.Clamp(
            effective.HorizontalCompensation * strength,
            -20.0,
            20.0);
        effective.Pattern = effective.Pattern
            .Select(point => new RecoilPatternPoint(
                (float)Math.Clamp(point.Horizontal * strength, -127.0, 127.0),
                (float)Math.Clamp(point.Vertical * strength, 0.0, 127.0)))
            .ToArray();
        if (Math.Abs(strength - RecoilStrengthModel.Default) > 0.0001)
        {
            effective.PatternSource = $"{effective.PatternSource}; per-weapon output {strength:0.00}x";
        }
        return effective;
    }

    private static WeaponProfile CreateBaseline(SiegeWeaponDefinition weapon)
    {
        var continuous = weapon.Type is "Assault Rifle" or "SMG" or "LMG" or "Machine Pistol";
        var vertical = weapon.Type switch
        {
            "Assault Rifle" => 1.00,
            "SMG" => 0.82,
            "LMG" => 1.05,
            "Machine Pistol" => 1.15,
            "Marksman Rifle" => 1.10,
            "Handgun" => 0.55,
            "Revolver" => 0.80,
            "Sniper Rifle" when weapon.Name == "OTs-03" => 1.10,
            "Shotgun" when RapidFireCatalog.TryGetRoundsPerMinute(
                weapon.Name,
                weapon.Type,
                out _) => 1.00,
            _ => 0.0
        };

        // Coarse starting strengths only. These make the first calibration pass safer
        // than the old 20-158 count placeholders and are not presented as measured data.
        vertical *= weapon.Name switch
        {
            "F2" => 1.25,
            "C8-SFW" => 1.15,
            "R4-C" => 1.10,
            "M762" => 1.10,
            "POF-9" => 1.10,
            "416-C CARBINE" => 1.05,
            "MPX" => 0.75,
            "UMP45" => 0.75,
            "T-5 SMG" => 0.85,
            "AUG A3" => 0.85,
            "ALDA 5.56" => 0.90,
            "SMG-11" => 1.25,
            "SMG-12" => 1.35,
            "BEARING 9" => 1.20,
            "SCORPION EVO 3 A1" => 1.30,
            _ => 1.0
        };

        var patternDefinition = continuous ? WeaponPatternCatalog.Find(weapon.Name) : null;
        var grip = patternDefinition?.Grip ?? (continuous ? "Vertical grip" : "Not applicable");
        var barrel = patternDefinition?.Barrel ?? (continuous ? "Flash hider" : "Not applicable");

        // Attachment effects are applied from one central formula so a newly gained
        // or removed attachment automatically changes both general and pattern modes.
        // Horizontal compensation remains neutral until a direction is measured.
        if (continuous)
        {
            vertical *= RecoilAttachmentModel.ContinuousVerticalMultiplier(grip, barrel);
        }

        var profile = new WeaponProfile(
            weapon.Name,
            weapon.Type,
            weapon.Operators,
            Math.Round(vertical, 3),
            0.0,
            0,
            grip,
            barrel,
            roundsPerMinute: patternDefinition?.RoundsPerMinute ?? 0,
            magazineSize: patternDefinition?.MagazineSize ?? 0);

        return Hydrate(profile, null);
    }

    private static WeaponProfile Hydrate(WeaponProfile profile, WeaponProfile? fallback)
    {
        profile.Name = profile.Name?.Trim() ?? string.Empty;
        profile.WeaponType = profile.WeaponType?.Trim() ?? "Unknown";
        profile.Operators ??= Array.Empty<string>();
        profile.Grip = profile.Grip?.Trim() ?? "Not applicable";
        profile.Barrel = profile.Barrel?.Trim() ?? "Not applicable";
        profile.VerticalCompensation = double.IsFinite(profile.VerticalCompensation)
            ? Math.Clamp(profile.VerticalCompensation, 0.0, 20.0)
            : 0.0;
        profile.HorizontalCompensation = double.IsFinite(profile.HorizontalCompensation)
            ? Math.Clamp(profile.HorizontalCompensation, -20.0, 20.0)
            : 0.0;
        profile.BurstProgression = Math.Clamp(profile.BurstProgression, 0, 100);
        profile.RoundsPerMinute = Math.Clamp(profile.RoundsPerMinute, 0, ushort.MaxValue);
        profile.MagazineSize = Math.Clamp(profile.MagazineSize, 0, 160);

        if (fallback != null)
        {
            profile.WeaponType = string.IsNullOrWhiteSpace(profile.WeaponType) ||
                                 profile.WeaponType.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                ? fallback.WeaponType
                : profile.WeaponType;
            if (profile.Operators.Length == 0)
            {
                profile.Operators = fallback.Operators.ToArray();
            }
            if (string.IsNullOrWhiteSpace(profile.Grip) || profile.Grip == "Not applicable")
            {
                profile.Grip = fallback.Grip;
            }
            if (string.IsNullOrWhiteSpace(profile.Barrel) || profile.Barrel == "Not applicable")
            {
                profile.Barrel = fallback.Barrel;
            }
            profile.RoundsPerMinute = profile.RoundsPerMinute > 0
                ? profile.RoundsPerMinute
                : fallback.RoundsPerMinute;
            profile.MagazineSize = profile.MagazineSize > 0
                ? profile.MagazineSize
                : fallback.MagazineSize;
        }

        // Normalize old saved profiles to the selected attachment policy: Vertical
        // Grip on every automatic profile, and Flash Hider in place of Compensator
        // except on Ela's Scorpion. Scale once from the previous attachment setup.
        if (profile.SupportsContinuousCompensation)
        {
            var previousMultiplier = RecoilAttachmentModel.ContinuousVerticalMultiplier(
                profile.Grip,
                profile.Barrel);
            profile.Grip = "Vertical grip";
            if (profile.Barrel.Equals("Compensator", StringComparison.OrdinalIgnoreCase) &&
                !profile.Name.Equals("SCORPION EVO 3 A1", StringComparison.OrdinalIgnoreCase))
            {
                profile.Barrel = "Flash hider";
            }
            var preferredMultiplier = RecoilAttachmentModel.ContinuousVerticalMultiplier(
                profile.Grip,
                profile.Barrel);
            if (previousMultiplier > 0)
            {
                profile.VerticalCompensation = Math.Round(
                    profile.VerticalCompensation / previousMultiplier * preferredMultiplier,
                    3);
            }
        }

        var definition = WeaponPatternCatalog.Find(profile.Name);
        profile.RoundsPerMinute = profile.RoundsPerMinute > 0
            ? profile.RoundsPerMinute
            : definition?.RoundsPerMinute ?? 0;
        profile.MagazineSize = profile.MagazineSize > 0
            ? profile.MagazineSize
            : definition?.MagazineSize ?? 0;
        profile.Pattern = WeaponPatternCatalog.CreateEstimatedPattern(
            profile.Name,
            profile.VerticalCompensation,
            profile.HorizontalCompensation,
            profile.Barrel,
            profile.RoundsPerMinute,
            profile.MagazineSize);
        profile.PatternDataQuality = profile.Pattern.Length > 0
            ? PatternDataQuality.VideoDerivedEstimate
            : PatternDataQuality.None;
        profile.PatternSource = profile.Pattern.Length > 0
            ? definition is null
                ? "Custom timing plus a generic centered estimate with grip/barrel modifiers"
                : "Current timing plus a deterministic estimate with grip/barrel modifiers"
            : "No automatic-weapon pattern available";
        return profile;
    }

    private static WeaponProfile Clone(WeaponProfile profile)
    {
        var clone = new WeaponProfile(
            profile.Name,
            profile.WeaponType,
            profile.Operators.ToArray(),
            profile.VerticalCompensation,
            profile.HorizontalCompensation,
            profile.BurstProgression,
            profile.Grip,
            profile.Barrel,
            profile.IsBaseline,
            profile.RoundsPerMinute,
            profile.MagazineSize);
        clone.Pattern = profile.Pattern.ToArray();
        clone.PatternDataQuality = profile.PatternDataQuality;
        clone.PatternSource = profile.PatternSource;
        return clone;
    }
}

public sealed class WeaponProfileViewModel
{
    public WeaponProfile Profile { get; }
    public string Name => Profile.Name;
    public string WeaponType => Profile.WeaponType;
    public string Attachments => $"{Profile.Grip} / {Profile.Barrel}";
    public string ProfileStatus => Profile.IsBaseline ? "Stock" : "Modified";
    public string Description => $"{ProfileStatus} | " + (Profile.SupportsContinuousCompensation
        ? $"{WeaponType} | {Profile.RoundsPerMinute} RPM, {Profile.MagazineSize} rounds | baseline V {Profile.VerticalCompensation:0.000}, H {Profile.HorizontalCompensation:0.000} | {Attachments}"
        : Profile.SupportsRapidFire
            ? $"{WeaponType} | semi-auto rapid fire {Profile.RapidFireRoundsPerMinute} RPM | per-shot recoil V {Profile.VerticalCompensation:0.000}, H {Profile.HorizontalCompensation:0.000}"
            : $"{WeaponType} | listed for complete loadout selection; automatic output disabled");

    public WeaponProfileViewModel(WeaponProfile profile)
    {
        Profile = profile;
    }

    public override string ToString() => Profile.IsBaseline
        ? $"{Name} ({WeaponType})"
        : $"{Name} ({WeaponType}, Modified)";
}
