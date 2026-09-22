using System;

namespace RainbowRecoil;

/// <summary>
/// Shared limits for the user-facing per-weapon output multiplier. A neutral
/// value of 1.0 preserves every existing General, Pattern, Experimental, and
/// semi-automatic profile exactly.
/// </summary>
public static class RecoilStrengthModel
{
    // User-facing output controls span the full signed relative-HID/Q8.8
    // magnitude supported by the firmware. Zero is useful for disabling one
    // layer without deleting a profile; values above 1 are explicit gain.
    public const double Minimum = 0.0;
    public const double Maximum = 127.0;
    public const double Default = 1.0;

    // The source profiles intentionally preserve their measured/estimated shape.
    // This master calibration converts those relative values into the stronger
    // physical HID output required by the shipping RP2040/RP2350 path.
    public const double MasterMinimum = 0.0;
    public const double MasterMaximum = 127.0;
    public const double MasterDefault = 12.0;

    // A user can combine the master calibration with the existing per-weapon
    // trim. Keep the final product bounded before it reaches profile clamping.
    public const double EffectiveMinimum = 0.0;
    public const double EffectiveMaximum = 127.0;

    public static double Normalize(double value) => double.IsFinite(value)
        ? Math.Clamp(value, Minimum, Maximum)
        : Default;

    public static double NormalizeMaster(double value) => double.IsFinite(value)
        ? Math.Clamp(value, MasterMinimum, MasterMaximum)
        : MasterDefault;

    public static double Combine(double master, double perWeapon) => Math.Clamp(
        NormalizeMaster(master) * Normalize(perWeapon),
        EffectiveMinimum,
        EffectiveMaximum);
}
