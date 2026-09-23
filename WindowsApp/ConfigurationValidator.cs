using System;
using System.Collections.Generic;
using System.Linq;

namespace RainbowRecoil;

internal sealed record ConfigurationValidationResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
    public string Summary => IsValid
        ? Warnings.Count == 0
            ? "Configuration is valid."
            : $"Configuration is valid with warning: {string.Join(" ", Warnings)}"
        : string.Join(" ", Errors);
}

/// <summary>Validates the complete output configuration before it can reach firmware.</summary>
internal static class ConfigurationValidator
{
    public static ConfigurationValidationResult Validate(
        Settings settings,
        WeaponProfile? profile,
        CompensationMode mode,
        bool rapidFireRequested)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!Enum.IsDefined(mode))
        {
            errors.Add("Select a supported compensation mode.");
        }
        if (profile is null)
        {
            errors.Add("Select a weapon profile.");
            return new ConfigurationValidationResult(errors, warnings);
        }
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            errors.Add("The selected profile has no name.");
        }
        else if (profile.Name.Any(char.IsControl) ||
                 System.Text.Encoding.UTF8.GetByteCount(profile.Name) >
                    SerialProtocol.MaximumProfileNameLength)
        {
            errors.Add(
                $"Profile names must contain no control characters and fit within " +
                $"{SerialProtocol.MaximumProfileNameLength} UTF-8 bytes.");
        }
        if (!double.IsFinite(profile.VerticalCompensation) ||
            profile.VerticalCompensation is < FirmwareContract.MinimumVerticalCompensation or
                > FirmwareContract.MaximumVerticalCompensation)
        {
            errors.Add(
                $"Vertical compensation must be finite and between " +
                $"{FirmwareContract.MinimumVerticalCompensation:0} and " +
                $"{FirmwareContract.MaximumVerticalCompensation:0}.");
        }
        if (!double.IsFinite(profile.HorizontalCompensation) ||
            profile.HorizontalCompensation is < FirmwareContract.MinimumHorizontalCompensation or
                > FirmwareContract.MaximumHorizontalCompensation)
        {
            errors.Add(
                $"Horizontal compensation must be finite and between " +
                $"{FirmwareContract.MinimumHorizontalCompensation:0} and " +
                $"{FirmwareContract.MaximumHorizontalCompensation:0}.");
        }
        if (profile.BurstProgression is < 0 or > 100)
        {
            errors.Add("Burst progression must be between 0 and 100.");
        }
        if (profile.SupportsContinuousCompensation)
        {
            if (profile.RoundsPerMinute is
                < FirmwareContract.MinimumPatternRoundsPerMinute or
                > FirmwareContract.MaximumPatternRoundsPerMinute)
            {
                errors.Add(
                    $"Automatic weapon RPM must be between " +
                    $"{FirmwareContract.MinimumPatternRoundsPerMinute} and " +
                    $"{FirmwareContract.MaximumPatternRoundsPerMinute}.");
            }
            if (profile.MagazineSize is < 1 or > 160)
            {
                errors.Add("Automatic weapon magazine size must be between 1 and 160.");
            }
            if (SerialProtocol.UsesPattern(mode) && !profile.HasWeaponPattern)
            {
                warnings.Add("No usable weapon pattern is available; General mode will be used.");
            }
        }
        if (profile.Pattern.Length > FirmwareContract.MaximumPatternPoints)
        {
            errors.Add("The recoil pattern exceeds the firmware limit of 160 points.");
        }
        if (profile.Pattern.Any(point =>
                !float.IsFinite(point.Horizontal) || !float.IsFinite(point.Vertical) ||
                point.Horizontal is < -127 or > 127 || point.Vertical is < 0 or > 127))
        {
            errors.Add("The recoil pattern contains an invalid or unrepresentable point.");
        }

        var scale = settings.CalculateSensitivityScale(profile, mode);
        if (!float.IsFinite(scale.Horizontal) || !float.IsFinite(scale.Vertical) ||
            scale.Horizontal is < FirmwareContract.MinimumSensitivityFactor or
                > FirmwareContract.MaximumSensitivityFactor ||
            scale.Vertical is < FirmwareContract.MinimumSensitivityFactor or
                > FirmwareContract.MaximumSensitivityFactor)
        {
            errors.Add(
                $"Sensitivity calibration produced H={scale.Horizontal:0.###}, " +
                $"V={scale.Vertical:0.###}; each exact scale must be between " +
                $"{FirmwareContract.MinimumSensitivityFactor:0.##} and " +
                $"{FirmwareContract.MaximumSensitivityFactor:0.##}.");
        }
        if (rapidFireRequested && !profile.SupportsRapidFire)
        {
            warnings.Add("Rapid fire is ignored for this automatic or unsupported weapon.");
        }
        if (rapidFireRequested && profile.SupportsRapidFire &&
            profile.RapidFireRoundsPerMinute is < 60 or > 1200)
        {
            errors.Add("Rapid-fire RPM must be between 60 and 1200.");
        }

        return new ConfigurationValidationResult(errors, warnings);
    }
}
