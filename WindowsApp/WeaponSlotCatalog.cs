using System;
using System.Collections.Generic;
using System.Linq;

namespace RainbowRecoil;

public enum WeaponSlot
{
    Primary,
    Secondary
}

/// <summary>
/// Siege loadout-slot rules shared by automatic selection, OCR, and the 1/2
/// shortcuts. Compact/utility shotguns can be secondaries despite sharing the
/// Shotgun catalog type with primary weapons.
/// </summary>
public static class WeaponSlotCatalog
{
    private static readonly HashSet<string> SecondaryShotguns = new(
        new[] { "ITA12S", "SUPER SHORTY", "GLAIVE-12" },
        StringComparer.OrdinalIgnoreCase);

    public static WeaponSlot GetSlot(WeaponProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (SecondaryShotguns.Contains(profile.Name) || profile.WeaponType is
            "Handgun" or "Revolver" or "Machine Pistol" or "Hand Cannon")
        {
            return WeaponSlot.Secondary;
        }

        return WeaponSlot.Primary;
    }

    public static IReadOnlyList<WeaponProfileViewModel> ForOperator(
        IEnumerable<WeaponProfileViewModel> profiles,
        string? operatorName)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return string.IsNullOrWhiteSpace(operatorName)
            ? profiles.ToArray()
            : profiles.Where(profile =>
                profile.Profile.Operators.Length == 0 ||
                profile.Profile.Operators.Contains(operatorName, StringComparer.OrdinalIgnoreCase))
                .ToArray();
    }

    public static WeaponProfileViewModel? SelectDefault(
        IReadOnlyCollection<WeaponProfileViewModel> profiles,
        WeaponSlot slot,
        string? rememberedWeapon,
        string? preferredWeapon,
        string? catalogPrimaryWeapon = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var candidates = profiles.Where(profile => GetSlot(profile.Profile) == slot).ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        return Find(candidates, rememberedWeapon)
            ?? Find(candidates, preferredWeapon)
            ?? (slot == WeaponSlot.Primary ? Find(candidates, catalogPrimaryWeapon) : null)
            ?? candidates[0];
    }

    private static WeaponProfileViewModel? Find(
        IEnumerable<WeaponProfileViewModel> profiles,
        string? name) => string.IsNullOrWhiteSpace(name)
            ? null
            : profiles.FirstOrDefault(profile =>
                profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
