using System;
using System.Collections.Generic;

namespace RainbowRecoil;

/// <summary>
/// USB click-repeat requests for supported semi-automatic weapons. These are
/// not claims about a game's accepted fire rate or a weapon's engine cap.
/// </summary>
public static class RapidFireCatalog
{
    public const int DefaultRoundsPerMinute = 960; // 16 CPS nominal.

    private static readonly HashSet<string> SupportedWeapons =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "417", "CAMRS", "SR-25", "Mk 14 EBR", "AR-15.50", "PMR90A2",
            "OTs-03", "LUISON", "M1014", "SIX12", "SIX12 SD", "SASG-12",
            "FO-12", "SPAS-15", "TCSG12", "Super 90", "GLAIVE-12"
        };

    public static bool TryGetRoundsPerMinute(WeaponProfile profile, out int roundsPerMinute)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return TryGetRoundsPerMinute(profile.Name, profile.WeaponType, out roundsPerMinute);
    }

    public static bool TryGetRoundsPerMinute(
        string weaponName,
        string weaponType,
        out int roundsPerMinute)
    {
        if (SupportedWeapons.Contains(weaponName) ||
            weaponType is "Handgun" or "Revolver" or "Marksman Rifle")
        {
            roundsPerMinute = DefaultRoundsPerMinute;
            return true;
        }

        roundsPerMinute = 0;
        return false;
    }
}
