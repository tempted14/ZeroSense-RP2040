using System;

namespace RainbowRecoil;

/// <summary>
/// Shared limits for the user-facing per-weapon output multiplier. A neutral
/// value of 1.0 preserves every existing General, Pattern, Experimental, and
/// semi-automatic profile exactly.
/// </summary>
public static class RecoilStrengthModel
{
    public const double Minimum = 0.25;
    public const double Maximum = 2.0;
    public const double Default = 1.0;

    public static double Normalize(double value) => double.IsFinite(value)
        ? Math.Clamp(value, Minimum, Maximum)
        : Default;
}
