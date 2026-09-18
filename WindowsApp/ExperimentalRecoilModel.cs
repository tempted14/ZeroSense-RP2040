using System;

namespace RainbowRecoil;

public sealed class ExperimentalRecoilTuning
{
    public double FirstShotGain { get; set; } = 1.0;
    public double EarlyGain { get; set; } = 1.0;
    public double MidGain { get; set; } = 1.0;
    public double LateGain { get; set; } = 1.0;
    public double HorizontalGain { get; set; } = 1.0;
}

/// <summary>
/// Applies safe, deterministic, per-weapon calibration gains to the existing
/// estimated pattern. The source pattern is never modified, so General and the
/// standard Weapon Pattern mode remain unchanged.
/// </summary>
public static class ExperimentalRecoilModel
{
    public const double MinimumVerticalGain = 0.25;
    public const double MaximumVerticalGain = 2.0;
    public const double MinimumHorizontalGain = 0.0;
    public const double MaximumHorizontalGain = 2.0;

    public static ExperimentalRecoilTuning Normalize(ExperimentalRecoilTuning? tuning)
    {
        tuning ??= new ExperimentalRecoilTuning();
        tuning.FirstShotGain = ClampVertical(tuning.FirstShotGain);
        tuning.EarlyGain = ClampVertical(tuning.EarlyGain);
        tuning.MidGain = ClampVertical(tuning.MidGain);
        tuning.LateGain = ClampVertical(tuning.LateGain);
        tuning.HorizontalGain = double.IsFinite(tuning.HorizontalGain)
            ? Math.Clamp(tuning.HorizontalGain, MinimumHorizontalGain, MaximumHorizontalGain)
            : 1.0;
        return tuning;
    }

    public static RecoilPatternPoint[] Apply(
        ReadOnlySpan<RecoilPatternPoint> source,
        ExperimentalRecoilTuning? tuning)
    {
        var normalized = Normalize(tuning);
        var result = new RecoilPatternPoint[source.Length];
        for (var index = 0; index < source.Length; ++index)
        {
            var progress = source.Length <= 1 ? 0.0 : index / (double)(source.Length - 1);
            var verticalGain = index == 0
                ? normalized.FirstShotGain
                : progress < 0.25
                    ? normalized.EarlyGain
                    : progress < 0.70
                        ? normalized.MidGain
                        : normalized.LateGain;
            result[index] = new RecoilPatternPoint(
                (float)Math.Clamp(
                    source[index].Horizontal * normalized.HorizontalGain,
                    -127.0,
                    127.0),
                (float)Math.Clamp(source[index].Vertical * verticalGain, 0.0, 127.0));
        }
        return result;
    }

    private static double ClampVertical(double value) => double.IsFinite(value)
        ? Math.Clamp(value, MinimumVerticalGain, MaximumVerticalGain)
        : 1.0;
}
