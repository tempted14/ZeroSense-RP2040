using System;
using System.Collections.Generic;

namespace RainbowRecoil;

/// <summary>
/// Conservative trigger-repeat limits for weapons whose normal fire mode is
/// semi-automatic. Values are starting points, not claims about engine caps.
/// </summary>
public static class RapidFireCatalog
{
    private static readonly IReadOnlyDictionary<string, int> WeaponRates =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["417"] = 430,
            ["CAMRS"] = 420,
            ["SR-25"] = 440,
            ["Mk 14 EBR"] = 440,
            ["AR-15.50"] = 430,
            ["PMR90A2"] = 430,
            ["OTs-03"] = 380,
            ["LUISON"] = 440,
            ["M1014"] = 215,
            ["SIX12"] = 220,
            ["SIX12 SD"] = 220,
            ["SASG-12"] = 340,
            ["FO-12"] = 400,
            ["SPAS-15"] = 300,
            ["TCSG12"] = 490,
            ["Super 90"] = 220,
            ["GLAIVE-12"] = 450
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
        if (WeaponRates.TryGetValue(weaponName, out roundsPerMinute))
        {
            return true;
        }

        if (weaponType is "Handgun" or "Revolver")
        {
            roundsPerMinute = 480;
            return true;
        }

        if (weaponType == "Marksman Rifle")
        {
            roundsPerMinute = 430;
            return true;
        }

        roundsPerMinute = 0;
        return false;
    }
}
