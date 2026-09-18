using System;
using System.Collections.Generic;

namespace RainbowRecoil;

public enum OperatorSide
{
    Unknown,
    Attacker,
    Defender
}

/// <summary>
/// Chooses the sensitivity profile associated with the optic normally used by
/// each side. Defender DMR exceptions are operator-specific so shared attacker
/// weapons do not change the defender policy, or vice versa.
/// </summary>
public static class OpticMagnificationPolicy
{
    private static readonly HashSet<string> Attackers = new(
        new[]
        {
            "Sledge", "Thatcher", "Ash", "Thermite", "Twitch", "Montagne", "Glaz",
            "Fuze", "Blitz", "IQ", "Buck", "Blackbeard", "Capitão", "Hibana",
            "Jackal", "Ying", "Zofia", "Dokkaebi", "Lion", "Finka", "Maverick",
            "Nomad", "Gridlock", "Nøkk", "Amaru", "Kali", "Iana", "Ace", "Zero",
            "Flores", "Osa", "Sens", "Grim", "Brava", "Ram", "Deimos", "Striker",
            "Rauora", "Solid Snake"
        },
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Defenders = new(
        new[]
        {
            "Smoke", "Mute", "Castle", "Pulse", "Doc", "Rook", "Kapkan", "Tachanka",
            "Jäger", "Bandit", "Frost", "Valkyrie", "Caveira", "Echo", "Mira",
            "Lesion", "Ela", "Vigil", "Maestro", "Alibi", "Clash", "Kaid", "Mozzie",
            "Warden", "Goyo", "Wamai", "Oryx", "Melusi", "Aruni", "Thunderbird",
            "Thorn", "Azami", "Solis", "Fenrir", "Tubarão", "Sentry", "Skopós",
            "Denari", "Noor"
        },
        StringComparer.OrdinalIgnoreCase);

    public static OperatorSide GetSide(string? operatorName)
    {
        if (string.IsNullOrWhiteSpace(operatorName))
        {
            return OperatorSide.Unknown;
        }

        if (Attackers.Contains(operatorName))
        {
            return OperatorSide.Attacker;
        }

        return Defenders.Contains(operatorName)
            ? OperatorSide.Defender
            : OperatorSide.Unknown;
    }

    public static string Recommend(string? operatorName, string? weaponName)
    {
        var side = GetSide(operatorName);
        if (side == OperatorSide.Attacker)
        {
            return "2.5x";
        }

        if (side == OperatorSide.Defender && IsDefenderMagnifiedException(operatorName, weaponName))
        {
            return "2.5x";
        }

        return "1.0x";
    }

    private static bool IsDefenderMagnifiedException(string? operatorName, string? weaponName) =>
        weaponName?.Equals("TCSG12", StringComparison.OrdinalIgnoreCase) == true ||
        (operatorName?.Equals("Tubarão", StringComparison.OrdinalIgnoreCase) == true &&
         weaponName?.Equals("AR-15.50", StringComparison.OrdinalIgnoreCase) == true) ||
        (operatorName?.Equals("Aruni", StringComparison.OrdinalIgnoreCase) == true &&
         weaponName?.Equals("Mk 14 EBR", StringComparison.OrdinalIgnoreCase) == true);
}
