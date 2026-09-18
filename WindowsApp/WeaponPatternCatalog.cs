using System;
using System.Collections.Generic;
using System.Globalization;

namespace RainbowRecoil;

public enum CompensationMode : byte
{
    General = 0,
    WeaponPattern = 1,
    Experimental = 2
}

public enum PatternDataQuality
{
    None,
    VideoDerivedEstimate,
    Measured
}

public readonly record struct RecoilPatternPoint(float Horizontal, float Vertical);

internal enum RecoilTier
{
    Low,
    Normal,
    High,
    Extreme
}

internal enum PatternShape
{
    Centered,
    RightDrift,
    Sway,
    Irregular,
    TwoStage
}

internal sealed record WeaponPatternDefinition(
    int RoundsPerMinute,
    int MagazineSize,
    RecoilTier Tier,
    PatternShape Shape,
    string Grip,
    string Barrel);

/// <summary>
/// Timing and qualitative recoil classifications for current automatic weapons.
/// RPM/capacity were checked against hanslhansl's Y11 weapon-statistics data.
/// Barrel choices and qualitative shapes were checked against SINOOS's current
/// all-weapon guide (YouTube video 4YhYKtgUrDY, reviewed 2026-09-15). Grip is
/// intentionally normalized to the user's requested Vertical Grip setup.
/// Ubisoft does not publish numeric per-shot vectors, so generated points are
/// deliberately identified as estimates rather than exact game data.
/// </summary>
internal static class WeaponPatternCatalog
{
    private const string PatternData = """
L85A2|669|30|Low|Sway|Vertical grip|Flash hider
AR33|748|25|Normal|Centered|Vertical grip|Flash hider
G36C|779|30|Low|Sway|Vertical grip|Flash hider
R4-C|859|25|High|RightDrift|Vertical grip|Flash hider
556XI|689|30|Low|Sway|Vertical grip|Flash hider
F2|978|25|Extreme|Centered|Vertical grip|Flash hider
AK-12|850|30|High|Centered|Vertical grip|Flash hider
552 COMMANDO|690|30|Low|Centered|Vertical grip|Extended barrel
AUG A2|719|30|Normal|Sway|Vertical grip|Flash hider
C8-SFW|837|30|High|Centered|Vertical grip|Flash hider
MK17 CQB|584|20|Low|Centered|Vertical grip|Extended barrel
PARA-308|649|30|Low|Centered|Vertical grip|Extended barrel
TYPE-89|848|20|High|Centered|Vertical grip|Flash hider
C7E|799|30|Normal|Centered|Vertical grip|Flash hider
M762|729|30|High|Centered|Vertical grip|Flash hider
V308|699|50|Low|Sway|Vertical grip|Flash hider
SPEAR.308|699|30|Low|Centered|Vertical grip|Flash hider
M4|750|30|Normal|Centered|Vertical grip|Flash hider
ARX200|700|20|Normal|Centered|Vertical grip|Flash hider
AK-74M|649|40|Low|Centered|Vertical grip|Flash hider
F90|780|30|Normal|Centered|Vertical grip|Flash hider
SC3000K|800|25|High|Centered|Vertical grip|Extended barrel
POF-9|739|50|Low|Sway|Vertical grip|Flash hider
416-C CARBINE|739|25|High|RightDrift|Vertical grip|Flash hider
AUG A3|699|31|Low|Centered|Vertical grip|Muzzle brake
6P41|680|100|Normal|Sway|Vertical grip|Flash hider
G8A1|851|50|Normal|Sway|Vertical grip|Flash hider
M249|650|100|Normal|Sway|Vertical grip|Flash hider
M249 SAW|650|60|Normal|Sway|Vertical grip|Flash hider
T-95 LSW|650|80|Normal|Centered|Vertical grip|Flash hider
LMG-E|720|150|High|Sway|Vertical grip|Flash hider
DP27|550|70|Low|Centered|Vertical grip|Flash hider
ALDA 5.56|900|80|Normal|Centered|Vertical grip|Flash hider
PDW9|799|50|Low|Sway|Vertical grip|Flash hider
FMG-9|799|30|High|Irregular|Vertical grip|Flash hider
SMG-11|1271|16|Extreme|TwoStage|Vertical grip|Muzzle brake
MP7|899|30|Normal|Centered|Vertical grip|Flash hider
MP5K|799|30|Low|Sway|Vertical grip|Flash hider
UMP45|599|25|Low|Centered|Vertical grip|Extended barrel
MP5|799|30|Low|Centered|Vertical grip|Flash hider
P90|968|50|Normal|Centered|Vertical grip|Extended barrel
9x19VSN|749|30|Low|Centered|Vertical grip|Flash hider
9mm C1|575|34|Low|Centered|Vertical grip|Extended barrel
MPX|830|30|Low|Centered|Vertical grip|Flash hider
M12|550|30|Low|Centered|Vertical grip|Suppressor
MP5SD|800|30|Low|Centered|Vertical grip|Integral suppressor
VECTOR.45 ACP|1200|25|High|Sway|Vertical grip|Flash hider
T-5 SMG|899|30|Low|Centered|Vertical grip|Flash hider
K1A|720|30|Low|Centered|Vertical grip|Flash hider
SCORPION EVO 3 A1|1079|40|High|Irregular|Vertical grip|Compensator
Mx4 Storm|948|30|Low|Centered|Vertical grip|Flash hider
Commando 9|780|25|Low|Centered|Vertical grip|Flash hider
P10 Roni|979|15|Low|Centered|Vertical grip|Flash hider
UZK50GI|700|22|Normal|Centered|Vertical grip|Flash hider
BEARING 9|1098|25|High|TwoStage|Vertical grip|Flash hider
SMG-12|1273|22|Extreme|Irregular|Vertical grip|Not available
C75 Auto|999|26|High|Irregular|Vertical grip|Not available
SPSMG9|980|20|High|Centered|Vertical grip|Flash hider
PCX-33|744|31|Low|Centered|Vertical grip|Flash hider
REAPER-MK2|764|33|Normal|Sway|Vertical grip|Flash hider
XK23|676|35|Normal|Sway|Vertical grip|Flash hider
""";

    private static readonly IReadOnlyDictionary<string, WeaponPatternDefinition> Definitions =
        ParseDefinitions();

    public static WeaponPatternDefinition? Find(string weaponName) =>
        Definitions.TryGetValue(weaponName, out var definition) ? definition : null;

    public static RecoilPatternPoint[] CreateEstimatedPattern(
        string weaponName,
        double generalVertical,
        double generalHorizontal,
        string? selectedBarrel = null,
        int roundsPerMinute = 0,
        int magazineSize = 0)
    {
        var definition = Find(weaponName);
        var effectiveRoundsPerMinute = roundsPerMinute > 0
            ? Math.Clamp(roundsPerMinute, 1, ushort.MaxValue)
            : definition?.RoundsPerMinute ?? 0;
        var effectiveMagazineSize = magazineSize > 0
            ? Math.Clamp(magazineSize, 1, 160)
            : definition?.MagazineSize ?? 0;
        if (effectiveRoundsPerMinute <= 0 || effectiveMagazineSize <= 0)
        {
            return Array.Empty<RecoilPatternPoint>();
        }

        var tier = definition?.Tier ?? RecoilTier.Normal;
        var shape = definition?.Shape ?? PatternShape.Centered;
        var pointCount = effectiveMagazineSize;
        var points = new RecoilPatternPoint[pointCount];
        var barrel = selectedBarrel ?? definition?.Barrel ?? "Not applicable";
        var shotIntervalMs = 60000.0 / effectiveRoundsPerMinute;
        var tierScale = tier switch
        {
            RecoilTier.Low => 0.90,
            RecoilTier.Normal => 1.00,
            RecoilTier.High => 1.12,
            RecoilTier.Extreme => 1.25,
            _ => 1.00
        };

        // Existing profile values are HID counts per 8 ms general-mode tick.
        // Convert that rate to a per-shot displacement, then apply deterministic
        // staged evolution. These curves are intentionally smooth and contain no
        // randomization or detection-evasion variance.
        var baseVerticalPerShot = generalVertical * shotIntervalMs / 8.0 * tierScale;
        var baseHorizontalPerShot = generalHorizontal * shotIntervalMs / 8.0;

        for (var index = 0; index < pointCount; ++index)
        {
            var progress = pointCount <= 1 ? 0.0 : index / (double)(pointCount - 1);
            var stageScale = StageScale(progress, shape, effectiveMagazineSize >= 60);
            var vertical = baseVerticalPerShot * stageScale;
            if (index == 0 && barrel.Equals("Muzzle brake", StringComparison.OrdinalIgnoreCase))
            {
                vertical *= RecoilAttachmentModel.MuzzleBrakeFirstShotMultiplier;
            }
            var horizontal = baseHorizontalPerShot + HorizontalEstimate(
                index,
                progress,
                vertical,
                shape,
                barrel);

            points[index] = new RecoilPatternPoint(
                (float)Math.Clamp(horizontal, -127.0, 127.0),
                (float)Math.Clamp(vertical, 0.0, 127.0));
        }

        return points;
    }

    private static double StageScale(double progress, PatternShape shape, bool largeMagazine)
    {
        progress = Math.Clamp(progress, 0.0, 1.0);
        if (shape == PatternShape.TwoStage)
        {
            // Preserve the two-stage character without an instantaneous jump
            // between adjacent shots around the transition.
            return SmoothInterpolate(0.90, 1.28, Normalize(progress, 0.14, 0.38));
        }

        if (progress < 0.12)
        {
            return SmoothInterpolate(1.08, 0.94, Normalize(progress, 0.0, 0.12));
        }
        if (progress < 0.42)
        {
            return SmoothInterpolate(0.94, 1.05, Normalize(progress, 0.12, 0.42));
        }
        if (progress < 0.76)
        {
            return SmoothInterpolate(
                1.05,
                largeMagazine ? 1.24 : 1.18,
                Normalize(progress, 0.42, 0.76));
        }

        return SmoothInterpolate(
            largeMagazine ? 1.24 : 1.18,
            largeMagazine ? 1.372 : 1.252,
            Normalize(progress, 0.76, 1.0));
    }

    private static double Normalize(double value, double start, double end) =>
        Math.Clamp((value - start) / (end - start), 0.0, 1.0);

    private static double SmoothInterpolate(double start, double end, double amount)
    {
        var smoothAmount = amount * amount * (3.0 - 2.0 * amount);
        return start + (end - start) * smoothAmount;
    }

    private static double HorizontalEstimate(
        int shotIndex,
        double progress,
        double vertical,
        PatternShape shape,
        string barrel)
    {
        if (shape == PatternShape.Centered)
        {
            return 0.0;
        }

        var attachmentScale = barrel.Equals("Compensator", StringComparison.OrdinalIgnoreCase)
            ? RecoilAttachmentModel.CompensatorHorizontalMultiplier
            : 1.0;
        var amplitude = vertical * (0.035 + 0.07 * progress) * attachmentScale;

        return shape switch
        {
            // A rightward weapon pull is countered with negative HID X.
            PatternShape.RightDrift => -amplitude,
            PatternShape.Sway => -Math.Sin(shotIndex * 0.72) * amplitude,
            PatternShape.Irregular => -IrregularDirection(shotIndex) * amplitude * 1.45,
            PatternShape.TwoStage => SmoothInterpolate(
                -0.25 * amplitude,
                -Math.Sin(shotIndex * 0.93) * amplitude,
                Normalize(progress, 0.14, 0.38)),
            _ => 0.0
        };
    }

    private static double IrregularDirection(int shotIndex)
    {
        ReadOnlySpan<double> sequence =
        [0.25, -0.65, 0.90, -0.30, 0.55, -1.00, 0.40, 0.75, -0.50, 0.15];
        return sequence[shotIndex % sequence.Length];
    }

    private static IReadOnlyDictionary<string, WeaponPatternDefinition> ParseDefinitions()
    {
        var definitions = new Dictionary<string, WeaponPatternDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in PatternData.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.TrimEnd('\r').Split('|');
            if (fields.Length != 7 ||
                !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rpm) ||
                !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var magazine) ||
                !Enum.TryParse<RecoilTier>(fields[3], out var tier) ||
                !Enum.TryParse<PatternShape>(fields[4], out var shape))
            {
                continue;
            }

            definitions[fields[0]] = new WeaponPatternDefinition(
                rpm,
                magazine,
                tier,
                shape,
                fields[5],
                fields[6]);
        }

        return definitions;
    }
}
