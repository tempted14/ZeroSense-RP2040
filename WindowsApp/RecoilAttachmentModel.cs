using System;
using System.Collections.Generic;

namespace RainbowRecoil;

internal sealed record ResolvedAttachmentSetup(
    string Grip,
    string Barrel,
    string? Note = null);

/// <summary>
/// Recoil-related attachment effects and operator-specific availability.
/// Vertical Grip's current 20% control bonus and the 35% Compensator / 50%
/// first-shot Muzzle Brake bonuses are documented by Ubisoft. The Flash Hider
/// vertical factor comes from the supplied 2025 recoil reference and remains
/// an estimate because Ubisoft describes its effect without publishing a
/// current numeric value.
/// </summary>
internal static class RecoilAttachmentModel
{
    private const double VerticalGripMultiplier = 0.80;
    private const double FlashHiderMultiplier = 0.80;

    public const double CompensatorHorizontalMultiplier = 0.65;
    public const double MuzzleBrakeFirstShotMultiplier = 0.50;

    private static readonly IReadOnlyDictionary<string, ResolvedAttachmentSetup> OperatorOverrides =
        new Dictionary<string, ResolvedAttachmentSetup>(StringComparer.OrdinalIgnoreCase)
        {
            [Key("Aruni", "Mk 14 EBR")] = new(
                "Vertical grip",
                "No recoil-control barrel",
                "Y11S3: Muzzle Brake removed from Aruni's defensive Mk 14 EBR."),
            [Key("Dokkaebi", "Mk 14 EBR")] = new(
                "Vertical grip",
                "Muzzle brake",
                "Muzzle Brake remains available on Dokkaebi's attacking Mk 14 EBR."),
            [Key("Tubarão", "AR-15.50")] = new(
                "Vertical grip",
                "No recoil-control barrel",
                "Y11S3: Muzzle Brake removed from Tubarão's defensive AR-15.50."),
            [Key("Maverick", "AR-15.50")] = new(
                "Vertical grip",
                "Muzzle brake",
                "Muzzle Brake remains available on Maverick's attacking AR-15.50.")
        };

    public static ResolvedAttachmentSetup Resolve(WeaponProfile profile, string? operatorName)
    {
        if (!string.IsNullOrWhiteSpace(operatorName) &&
            OperatorOverrides.TryGetValue(Key(operatorName, profile.Name), out var setup))
        {
            return setup;
        }

        return new ResolvedAttachmentSetup(profile.Grip, profile.Barrel);
    }

    public static double ContinuousVerticalMultiplier(string grip, string barrel)
    {
        var multiplier = 1.0;
        if (grip.Equals("Vertical grip", StringComparison.OrdinalIgnoreCase))
        {
            multiplier *= VerticalGripMultiplier;
        }
        if (barrel.Equals("Flash hider", StringComparison.OrdinalIgnoreCase))
        {
            multiplier *= FlashHiderMultiplier;
        }
        return multiplier;
    }

    public static double SemiAutomaticVerticalMultiplier(string grip, string barrel)
    {
        var multiplier = ContinuousVerticalMultiplier(grip, barrel);
        if (barrel.Equals("Muzzle brake", StringComparison.OrdinalIgnoreCase))
        {
            multiplier *= MuzzleBrakeFirstShotMultiplier;
        }
        return multiplier;
    }

    private static string Key(string operatorName, string weaponName) =>
        $"{operatorName.Trim()}|{weaponName.Trim()}";
}
