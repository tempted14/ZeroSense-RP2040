using System;
using System.Globalization;

namespace RainbowRecoil;

/// <summary>
/// Values that are part of the desktop/firmware wire contract. Firmware rejects
/// values outside these ranges; the desktop must never assume silent clamping.
/// </summary>
internal static class FirmwareContract
{
    public const float MinimumVerticalCompensation = 0.0f;
    public const float MaximumVerticalCompensation = 127.0f;
    public const float MinimumHorizontalCompensation = -127.0f;
    public const float MaximumHorizontalCompensation = 127.0f;
    // Broad wire-safety bounds, not tuning clamps. Values are transferred
    // exactly and acknowledged; the UI calibration determines the actual scale.
    public const float MinimumSensitivityFactor = 0.05f;
    public const float MaximumSensitivityFactor = 8.0f;
    public const int MaximumPatternPoints = 160;
    public const int MinimumPatternRoundsPerMinute = 1;
    public const int MaximumPatternRoundsPerMinute = 2000;

    public static bool ConfigurationBeginAcknowledgementMatches(
        string line,
        uint transactionId,
        uint configurationHash) => line.Equals(
            $"CONFIG:BEGIN:TX={transactionId:X8}:HASH={configurationHash:X8}",
            StringComparison.Ordinal);

    public static bool ConfigurationCommitAcknowledgementMatches(
        string line,
        uint transactionId,
        uint configurationHash) => line.Equals(
            $"CONFIG:COMMIT:TX={transactionId:X8}:HASH={configurationHash:X8}",
            StringComparison.Ordinal);

    public static bool CommittedStatusMatches(string line, uint configurationHash) =>
        line.Equals(
            $"STATUS:HASH={configurationHash:X8}:TX=IDLE:FIRE=OFF",
            StringComparison.Ordinal);

    public static bool ProfileAcknowledgementMatches(
        string line,
        WeaponProfile profile,
        CompensationMode mode)
    {
        var usesPattern = SerialProtocol.UsesPattern(mode) && profile.HasWeaponPattern;
        var expectedMode = usesPattern ? "PATTERN" : "GENERAL";
        var expectedRpm = usesPattern ? profile.RoundsPerMinute : 0;
        var expectedPoints = usesPattern ? Math.Min(profile.Pattern.Length, MaximumPatternPoints) : 0;
        var expectedSuffix = string.Create(
            CultureInfo.InvariantCulture,
            $":MODE={expectedMode}:V={(float)profile.VerticalCompensation:F3}" +
            $":H={(float)profile.HorizontalCompensation:F3}:RPM={expectedRpm}:POINTS={expectedPoints}");
        var expectedName = string.IsNullOrEmpty(profile.Name) ? "Unnamed" : profile.Name;
        return line.Equals($"PROFILE:{expectedName}{expectedSuffix}", StringComparison.Ordinal);
    }

    public static bool SensitivityAcknowledgementMatches(string line, SensitivityScale scale) =>
        line.Equals(
            string.Create(
                CultureInfo.InvariantCulture,
                $"SENSITIVITY:H={scale.Horizontal:F3}:V={scale.Vertical:F3}"),
            StringComparison.Ordinal);

    public static bool RapidFireAcknowledgementMatches(
        string line,
        bool enabled,
        int roundsPerMinute) =>
        line.Equals(
            $"RAPID_FIRE:{(enabled ? "ON" : "OFF")}:RPM={(enabled ? roundsPerMinute : 0)}",
            StringComparison.Ordinal);

    public static bool GeneralSettingsAcknowledgementMatches(
        string line,
        bool timingVarianceEnabled,
        bool deltaNoiseEnabled = false) =>
        line.Equals(
            $"GENERAL_SETTINGS:TIMING_VARIANCE={(timingVarianceEnabled ? "ON" : "OFF")}" +
            $":DELTA_NOISE={(deltaNoiseEnabled ? "ON" : "OFF")}",
            StringComparison.Ordinal);

    public static bool PatternAcknowledgementMatches(string line, WeaponProfile profile) =>
        line.Equals(
            $"PATTERN:READY:{Math.Min(profile.Pattern.Length, MaximumPatternPoints)}",
            StringComparison.Ordinal);
}
