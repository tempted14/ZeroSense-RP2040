using System;

namespace RainbowRecoil;

/// <summary>
/// Builds the exact immutable profile that will be validated and transferred.
/// Keeping this outside the page makes source/loadout ordering independently
/// testable and prevents one UI mode from accidentally bypassing another.
/// </summary>
internal static class RecoilProfileResolver
{
    public static WeaponProfile Build(
        WeaponProfile selectedProfile,
        string? operatorName,
        Settings settings)
    {
        ArgumentNullException.ThrowIfNull(selectedProfile);
        ArgumentNullException.ThrowIfNull(settings);

        var setup = RecoilAttachmentModel.Resolve(selectedProfile, operatorName);
        var effective = selectedProfile
            .WithAttachmentSetup(setup)
            .WithOpticSetup(settings.ActiveMagnification, operatorName);

        if (settings.CompensationMode == CompensationMode.ResearchEstimate)
        {
            var referenceSource = effective.PatternSource;
            var hasResearchData = ResearchRecoilModel.TryCreatePattern(
                effective.Name,
                effective.Pattern,
                out var researchPattern);
            effective.Pattern = researchPattern;
            effective.PatternDataQuality = researchPattern.Length > 0
                ? PatternDataQuality.VideoDerivedEstimate
                : PatternDataQuality.None;
            effective.PatternSource = hasResearchData
                ? $"{ResearchRecoilModel.DataVersion}; normalized to {referenceSource}; " +
                  "reference horizontal trace retained"
                : $"Reference profile fallback; {ResearchRecoilModel.DataVersion} " +
                  "has no row for this weapon";
        }

        effective = effective.WithCombinedOutputStrength(
            settings.MasterRecoilGain,
            settings.GetWeaponOutputStrength(selectedProfile.Name));
        if (settings.CompensationMode == CompensationMode.Experimental &&
            effective.HasWeaponPattern)
        {
            effective.Pattern = ExperimentalRecoilModel.Apply(
                effective.Pattern,
                settings.GetExperimentalRecoilTuning(effective.Name));
            effective.PatternDataQuality = PatternDataQuality.VideoDerivedEstimate;
            effective.PatternSource =
                "Standard estimated pattern with local per-weapon experimental calibration";
        }

        effective = HorizontalRecoilModel.Apply(
            effective,
            settings.GetWeaponHorizontalTuning(effective.Name));

        return effective;
    }
}
