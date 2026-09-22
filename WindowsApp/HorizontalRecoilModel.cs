using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace RainbowRecoil;

public enum HorizontalPatternMode
{
    Profile,
    MirrorProfile,
    WeaponPullLeft,
    WeaponPullRight,
    Alternating
}

public sealed class HorizontalRecoilTuning
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("mode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HorizontalPatternMode Mode { get; set; } = HorizontalPatternMode.Profile;

    [JsonPropertyName("strength")]
    public double Strength { get; set; } = HorizontalRecoilModel.DefaultStrength;
}

/// <summary>
/// Applies a user-owned horizontal override to an already isolated effective
/// profile. Profile mode retains the source X trace; the other modes derive a
/// deterministic lateral trace from each shot's vertical displacement.
/// </summary>
internal static class HorizontalRecoilModel
{
    public const double MinimumStrength = 0.0;
    public const double MaximumStrength = 10.0;
    public const double DefaultStrength = 1.0;
    private const double OverrideHorizontalRatio = 0.08;

    public static HorizontalRecoilTuning Normalize(HorizontalRecoilTuning? tuning)
    {
        tuning ??= new HorizontalRecoilTuning();
        if (!Enum.IsDefined(tuning.Mode))
        {
            tuning.Mode = HorizontalPatternMode.Profile;
        }
        tuning.Strength = double.IsFinite(tuning.Strength)
            ? Math.Clamp(tuning.Strength, MinimumStrength, MaximumStrength)
            : DefaultStrength;
        return tuning;
    }

    public static bool IsDefault(HorizontalRecoilTuning tuning)
    {
        tuning = Normalize(tuning);
        return tuning.Enabled &&
               tuning.Mode == HorizontalPatternMode.Profile &&
               Math.Abs(tuning.Strength - DefaultStrength) <= 0.0001;
    }

    public static WeaponProfile Apply(WeaponProfile effective, HorizontalRecoilTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(effective);
        tuning = Normalize(tuning);

        if (!tuning.Enabled || tuning.Strength <= 0)
        {
            effective.HorizontalCompensation = 0;
            effective.Pattern = effective.Pattern
                .Select(point => new RecoilPatternPoint(0, point.Vertical))
                .ToArray();
            effective.PatternSource = $"{effective.PatternSource}; horizontal output disabled";
            return effective;
        }

        effective.HorizontalCompensation = ApplyGeneralHorizontal(
            effective.HorizontalCompensation,
            effective.VerticalCompensation,
            tuning);
        effective.Pattern = effective.Pattern
            .Select((point, index) => new RecoilPatternPoint(
                ApplyPatternHorizontal(point, index, tuning),
                point.Vertical))
            .ToArray();

        if (!IsDefault(tuning))
        {
            effective.PatternSource =
                $"{effective.PatternSource}; horizontal {DescribeMode(tuning.Mode).ToLowerInvariant()} " +
                $"at {tuning.Strength:0.00}x";
        }
        return effective;
    }

    public static string DescribeMode(HorizontalPatternMode mode) => mode switch
    {
        HorizontalPatternMode.Profile => "Profile pattern",
        HorizontalPatternMode.MirrorProfile => "Mirrored profile pattern",
        HorizontalPatternMode.WeaponPullLeft => "Gun pulls left",
        HorizontalPatternMode.WeaponPullRight => "Gun pulls right",
        HorizontalPatternMode.Alternating => "Alternating sway",
        _ => "Profile pattern"
    };

    public static string DescribeCatalogDirection(string weaponName)
    {
        var definition = WeaponPatternCatalog.Find(weaponName);
        return definition?.Shape switch
        {
            PatternShape.RightDrift => "Catalog estimate: gun drifts right; compensation moves left.",
            PatternShape.Sway => "Catalog estimate: alternating left/right sway.",
            PatternShape.Irregular => "Catalog estimate: irregular alternating drift.",
            PatternShape.TwoStage => "Catalog estimate: early drift followed by alternating sway.",
            PatternShape.ReaperStages => "Catalog estimate: stage-shaped alternating sway.",
            PatternShape.Centered => "Catalog estimate: centered, with no stable lateral direction.",
            _ => "No catalog horizontal pattern is available for this weapon."
        };
    }

    private static double ApplyGeneralHorizontal(
        double profileHorizontal,
        double vertical,
        HorizontalRecoilTuning tuning)
    {
        var horizontal = tuning.Mode switch
        {
            HorizontalPatternMode.Profile => profileHorizontal,
            HorizontalPatternMode.MirrorProfile => -profileHorizontal,
            HorizontalPatternMode.WeaponPullLeft => Math.Abs(vertical) * OverrideHorizontalRatio,
            HorizontalPatternMode.WeaponPullRight => -Math.Abs(vertical) * OverrideHorizontalRatio,
            HorizontalPatternMode.Alternating => profileHorizontal,
            _ => profileHorizontal
        };
        return Math.Clamp(
            horizontal * tuning.Strength,
            FirmwareContract.MinimumHorizontalCompensation,
            FirmwareContract.MaximumHorizontalCompensation);
    }

    private static float ApplyPatternHorizontal(
        RecoilPatternPoint point,
        int index,
        HorizontalRecoilTuning tuning)
    {
        var amplitude = Math.Abs(point.Vertical) * OverrideHorizontalRatio;
        var horizontal = tuning.Mode switch
        {
            HorizontalPatternMode.Profile => point.Horizontal,
            HorizontalPatternMode.MirrorProfile => -point.Horizontal,
            // These labels describe weapon movement, so compensation is opposite.
            HorizontalPatternMode.WeaponPullLeft => amplitude,
            HorizontalPatternMode.WeaponPullRight => -amplitude,
            HorizontalPatternMode.Alternating => -Math.Sin(index * 0.72) * amplitude,
            _ => point.Horizontal
        };
        return (float)Math.Clamp(horizontal * tuning.Strength, -127.0, 127.0);
    }
}
