using System;
using System.Collections.Generic;
using System.Globalization;

namespace RainbowRecoil;

internal readonly record struct ResearchRecoilDefinition(
    int SourceRoundsPerMinute,
    int SourceMagazineSize,
    double FirstShotKick,
    double StageOneStep,
    double StageTwoStep,
    double StageThreeStep,
    int LongBurstStartsOnShot);

/// <summary>
/// Optional vertical-stage model derived from the user-supplied Y11S1.3 research
/// table. The table's plot pixels are not HID counts, so this model uses only its
/// within-weapon ratios and normalizes every result to the original pattern's
/// total vertical output. Horizontal output is preserved because the source has
/// spread magnitudes, not deterministic per-shot directions.
/// </summary>
internal static class ResearchRecoilModel
{
    public const string DataVersion = "Y11S1.3 research estimate";

    private const string DefinitionData = """
556XI|690|31|56.63|39.06|46.87|58.59|12
ARX200|700|21|52.66|36.32|43.58|54.47|12
L85A2|670|31|58.33|40.22|48.27|60.34|12
PARA-308|650|31|60.12|41.46|49.75|62.19|12
M762|730|31|52.15|35.97|43.16|53.95|8
SC3000K|800|26|46.35|31.97|38.36|47.95|12
AK-74M|650|41|60.26|41.56|49.87|62.34|12
M4|750|31|50.08|34.54|41.45|51.81|12
MK17 CQB|585|21|60.56|41.77|50.12|62.65|12
V308|700|51|57.82|39.88|47.85|59.81|12
552 COMMANDO|690|31|53.69|37.03|44.43|55.54|12
AUG A2|720|31|50.73|34.99|41.99|52.48|12
C7E|800|31|45.66|31.49|37.79|47.23|12
SPEAR.308|700|31|52.18|35.99|43.19|53.98|12
AR33|749|26|46.82|32.29|38.75|48.43|12
AK-12|850|31|41.73|28.78|34.54|43.17|12
C8-SFW|837|31|42.38|29.23|35.07|43.84|12
TYPE-89|850|21|39.37|27.15|32.58|40.72|12
R4-C|860|26|39.57|27.29|32.75|40.93|8
416-C CARBINE|740|26|45.27|31.22|37.47|46.84|12
F90|780|31|44.10|30.41|36.50|45.62|12
G36C|780|31|44.10|30.41|36.50|45.62|12
F2|980|26|33.64|23.20|27.84|34.80|12
POF-9|740|51|49.29|34.00|40.80|50.99|12
Commando 9|780|26|41.58|28.68|34.41|43.02|12
PCX-33|745|32|44.91|30.97|37.17|46.46|12
M12|550|31|60.38|41.64|49.97|62.46|12
UMP45|600|26|53.90|37.18|44.61|55.76|12
9mm C1|575|35|53.62|36.98|44.37|55.47|12
AUG A3|700|32|43.45|29.97|35.96|44.95|12
K1A|720|31|42.05|29.00|34.80|43.50|6
UZK50GI|700|23|41.35|28.52|34.22|42.78|12
9x19VSN|750|31|39.00|26.90|32.28|40.35|12
FMG-9|800|31|36.57|25.22|30.26|37.83|12
PDW9|800|51|39.40|27.17|32.61|40.76|12
MP7|900|31|31.34|21.62|25.94|32.42|12
MP5K|800|31|33.92|23.39|28.07|35.09|12
MP5SD|800|31|33.92|23.39|28.07|35.09|12
T-5 SMG|900|31|28.93|19.95|23.94|29.93|12
MP5|800|31|31.84|21.96|26.35|32.94|12
MPX|830|31|30.00|20.69|24.83|31.04|12
Mx4 Storm|950|31|26.21|18.08|21.69|27.12|12
P10 Roni|980|16|23.01|15.87|19.04|23.81|12
SCORPION EVO 3 A1|1080|41|22.34|15.41|18.49|23.11|12
VECTOR.45 ACP|1200|26|18.78|12.95|15.54|19.43|12
P90|970|51|25.03|17.26|20.71|25.89|12
DP27|550|70|88.73|61.19|73.43|91.79|12
M249|650|100|69.28|47.78|57.33|71.67|12
M249 SAW|650|61|64.33|44.36|53.24|66.55|12
6P41|680|100|64.55|44.52|53.42|66.78|12
T-95 LSW|650|81|65.43|45.12|54.15|67.69|12
LMG-E|720|150|60.47|41.70|50.04|62.55|12
G8A1|850|51|40.96|28.25|33.90|42.38|12
ALDA 5.56|900|80|40.03|27.61|33.13|41.41|12
C75 Auto|1000|27|33.53|23.12|27.75|34.69|12
BEARING 9|1100|26|29.26|20.18|24.21|30.27|12
SPSMG9|980|21|31.80|21.93|26.32|32.90|12
SMG-11|1270|17|23.34|16.10|19.32|24.15|12
REAPER-MK2|765|34|42.18|29.09|34.91|43.64|12
SMG-12|1270|33|23.80|16.41|19.69|24.62|12
""";

    private static readonly IReadOnlyDictionary<string, ResearchRecoilDefinition> Definitions =
        ParseDefinitions();

    public static int DefinitionCount => Definitions.Count;

    public static bool TryGetDefinition(
        string weaponName,
        out ResearchRecoilDefinition definition) =>
        Definitions.TryGetValue(weaponName, out definition);

    public static bool TryCreatePattern(
        string weaponName,
        IReadOnlyList<RecoilPatternPoint> originalPattern,
        out RecoilPatternPoint[] pattern)
    {
        ArgumentNullException.ThrowIfNull(originalPattern);
        pattern = Copy(originalPattern);
        if (pattern.Length == 0 || !Definitions.TryGetValue(weaponName, out var definition))
        {
            return false;
        }

        var originalTotal = 0.0;
        var researchTotal = 0.0;
        var researchSteps = new double[pattern.Length];
        for (var index = 0; index < pattern.Length; ++index)
        {
            originalTotal += Math.Max(originalPattern[index].Vertical, 0.0f);
            var shotNumber = index + 1;
            var step = shotNumber switch
            {
                1 => definition.FirstShotKick,
                _ when shotNumber < definition.LongBurstStartsOnShot => definition.StageOneStep,
                _ when shotNumber < definition.LongBurstStartsOnShot + 8 => definition.StageTwoStep,
                _ => definition.StageThreeStep
            };
            researchSteps[index] = step;
            researchTotal += step;
        }

        if (originalTotal <= 0.0 || researchTotal <= 0.0)
        {
            return false;
        }

        var scale = originalTotal / researchTotal;
        for (var index = 0; index < pattern.Length; ++index)
        {
            pattern[index] = new RecoilPatternPoint(
                originalPattern[index].Horizontal,
                (float)Math.Clamp(researchSteps[index] * scale, 0.0, 127.0));
        }
        return true;
    }

    private static RecoilPatternPoint[] Copy(IReadOnlyList<RecoilPatternPoint> source)
    {
        var copy = new RecoilPatternPoint[source.Count];
        for (var index = 0; index < source.Count; ++index)
        {
            copy[index] = source[index];
        }
        return copy;
    }

    private static IReadOnlyDictionary<string, ResearchRecoilDefinition> ParseDefinitions()
    {
        var definitions = new Dictionary<string, ResearchRecoilDefinition>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var line in DefinitionData.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.TrimEnd('\r').Split('|');
            if (fields.Length != 8 ||
                !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rpm) ||
                !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var magazine) ||
                !double.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var kick) ||
                !double.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var stageOne) ||
                !double.TryParse(fields[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var stageTwo) ||
                !double.TryParse(fields[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var stageThree) ||
                !int.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var longBurst) ||
                rpm <= 0 || magazine <= 0 || kick <= 0.0 || stageOne <= 0.0 ||
                stageTwo <= 0.0 || stageThree <= 0.0 || longBurst < 2)
            {
                continue;
            }

            definitions[fields[0]] = new ResearchRecoilDefinition(
                rpm,
                magazine,
                kick,
                stageOne,
                stageTwo,
                stageThree,
                longBurst);
        }
        return definitions;
    }
}
