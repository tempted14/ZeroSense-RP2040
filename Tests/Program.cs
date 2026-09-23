using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RainbowRecoil;

var tests = new (string Name, Action Run)[]
{
    ("catalog is complete and unique", CatalogIsCompleteAndUnique),
    ("automatic profiles have bounded patterns", AutomaticProfilesHaveBoundedPatterns),
    ("current recoil source facts remain pinned", CurrentRecoilSourceFactsArePinned),
    ("custom timing controls generated pattern timing and length", CustomPatternTimingIsConsistent),
    ("pattern rate and attachment math are consistent", PatternMathIsConsistent),
    ("integer shot scheduling has no systematic RPM drift", RpmSchedulingHasNoSystematicDrift),
    ("generated pattern stages transition smoothly", GeneratedPatternStagesAreContinuous),
    ("research estimate is isolated and normalized", ResearchEstimateIsIsolatedAndNormalized),
    ("profile source switching preserves optic-specific references", ProfileSourceSwitchingIsExact),
    ("per-weapon output strength is isolated and bounded", WeaponOutputStrengthIsSafe),
    ("horizontal recoil overrides are isolated and customizable", HorizontalRecoilOverridesAreSafe),
    ("experimental tuning is isolated and stage-specific", ExperimentalTuningIsIsolated),
    ("operator attachment overrides are isolated", OperatorOverridesAreIsolated),
    ("settings normalization and scaling are safe", SettingsNormalizationIsSafe),
    ("2.5x automatic boost survives the 127-point profile limit", TwoPointFiveBoostIsScoped),
    ("original pattern output multiplier is scoped and combines safely", OriginalPatternMultiplierIsScoped),
    ("operator OCR matching tolerates realistic noise", OperatorOcrMatchingIsRobust),
    ("detection settings normalize safely", DetectionSettingsNormalizeSafely),
    ("weapon slots and defaults are deterministic", WeaponSlotsAndDefaultsAreDeterministic),
    ("automatic optic defaults respect side and DMR exceptions", OpticDefaultsAreCorrect),
    ("operator loadouts retain unassigned custom profiles", CustomProfilesRemainVisible),
    ("1 and 2 slot hotkeys are edge triggered", WeaponSlotHotkeysAreEdgeTriggered),
    ("weapon OCR matching handles loadout text", WeaponOcrMatchingIsRobust),
    ("continuous OCR changes require consecutive matches", DetectionDebounceIsSafe),
    ("semi-automatic profiles expose rapid fire and recoil", SemiAutomaticProfilesAreActive),
    ("profile persistence round-trips", ProfilePersistenceRoundTrips),
    ("profile backup recovers a corrupt primary", ProfileBackupRecoversCorruption),
    ("malformed profile files fail closed", MalformedProfileFilesFailClosed),
    ("legacy settings migrate to safe defaults", LegacySettingsMigrateSafely),
    ("attachment math is applied once", AttachmentMathIsConsistent),
    ("serial packets preserve framing and UTF-8", SerialPacketsAreValid),
    ("serial reliability tolerates isolated read and acknowledgement stalls", SerialReliabilityIsBounded),
    ("all commands and pattern chunks encode exactly", SerialProtocolCoverageIsComplete),
    ("configuration hashes and transactions are deterministic", ConfigurationTransactionsAreDeterministic),
    ("measured packs require and preserve exact loadouts", MeasuredProfilePacksAreExact),
    ("configuration validator rejects unsafe output", ConfigurationValidationFailsClosed),
    ("firmware status identifies both boards and proxy mouse state", FirmwareStatusIsParsed),
    ("RP2040 simulator exercises the complete configuration path", SimulatorExercisesConfigurationPath),
    ("release updates report assets without assuming a signature", ReleaseUpdatesAreReportedTruthfully)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {test.Name}: {exception.Message}");
        Console.Error.WriteLine(failures[^1]);
    }
}

Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} tests passed.");
return failures.Count == 0 ? 0 : 1;

static void CatalogIsCompleteAndUnique()
{
    Equal(115, SiegeCatalog.Weapons.Count, "weapon count");
    Equal(
        SiegeCatalog.Weapons.Count,
        SiegeCatalog.Weapons.Select(weapon => weapon.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        "unique weapon names");
    True(SiegeCatalog.Operators.Count > 60, "operator catalog should be populated");
    True(
        SiegeCatalog.Operators.All(item => item.Weapons.Contains(item.PrimaryWeapon)),
        "every primary weapon should belong to its operator");
}

static void AutomaticProfilesHaveBoundedPatterns()
{
    var profiles = WeaponProfile.DefaultProfiles;
    Equal(115, profiles.Count, "default profile count");
    Equal(61, profiles.Count(profile => profile.HasWeaponPattern), "pattern profile count");
    True(
        profiles.Where(profile => profile.SupportsContinuousCompensation)
            .All(profile => profile.HasWeaponPattern && profile.Pattern.Length == profile.MagazineSize),
        "every continuous profile should have one point per magazine round");
    True(profiles.All(profile => profile.Pattern.Length <= 160), "pattern packet index is one byte");
    True(
        profiles.Where(profile => profile.HasWeaponPattern).All(profile =>
            profile.Pattern.All(point => float.IsFinite(point.Horizontal) &&
                                         float.IsFinite(point.Vertical) &&
                                         point is { Horizontal: >= -127 and <= 127, Vertical: >= 0 and <= 127 })),
        "all pattern points should be finite and representable");
}

static void CurrentRecoilSourceFactsArePinned()
{
    var f2 = WeaponPatternCatalog.Find("F2")!;
    Equal(978, f2.RoundsPerMinute, "measured F2 RPM");
    Equal(25, f2.MagazineSize, "F2 magazine");
    Equal("Vertical grip", f2.Grip, "F2 Y11S1 restored grip");

    var mk17 = WeaponPatternCatalog.Find("MK17 CQB")!;
    Equal(584, mk17.RoundsPerMinute, "measured MK17 RPM");
    Equal(20, mk17.MagazineSize, "Y10S3 MK17 magazine");

    var smg12 = WeaponPatternCatalog.Find("SMG-12")!;
    Equal(1273, smg12.RoundsPerMinute, "measured SMG-12 RPM");
    Equal(22, smg12.MagazineSize, "Y11S3 SMG-12 magazine");

    var reaper = WeaponPatternCatalog.Find("REAPER-MK2")!;
    Equal(764, reaper.RoundsPerMinute, "measured Reaper RPM");
    Equal(33, reaper.MagazineSize, "Reaper magazine");
    Equal(PatternShape.ReaperStages, reaper.Shape, "Y11S2.3 Reaper stage model");
}

static void CustomPatternTimingIsConsistent()
{
    var fast = WeaponPatternCatalog.CreateEstimatedPattern(
        "F2", 1.0, 0.0, "Flash hider", 1000, 10);
    var slow = WeaponPatternCatalog.CreateEstimatedPattern(
        "F2", 1.0, 0.0, "Flash hider", 500, 10);
    Equal(10, fast.Length, "custom fast magazine length");
    Equal(10, slow.Length, "custom slow magazine length");
    True(slow[0].Vertical > fast[0].Vertical,
        "slower RPM should convert the same continuous rate into more movement per shot");

    var generic = WeaponPatternCatalog.CreateEstimatedPattern(
        "Custom automatic", 1.0, 0.0, "Not applicable", 600, 12);
    Equal(12, generic.Length, "generic custom pattern length");
    True(generic.All(point => point.Horizontal == 0), "generic custom pattern is centered");

    var customProfile = new WeaponProfile
    {
        Name = "F2",
        WeaponType = "Assault Rifle",
        VerticalCompensation = 1.0,
        HorizontalCompensation = 0.0,
        Grip = "Vertical grip",
        Barrel = "Flash hider",
        RoundsPerMinute = 600,
        MagazineSize = 7
    };
    var hydrated = customProfile.WithAttachmentSetup(
        new ResolvedAttachmentSetup("Vertical grip", "Flash hider"));
    Equal(7, hydrated.Pattern.Length, "attachment hydration preserves custom magazine length");
    Equal(600, hydrated.RoundsPerMinute, "attachment hydration preserves custom RPM");
}

static void PatternMathIsConsistent()
{
    var fast = WeaponPatternCatalog.CreateEstimatedPattern(
        "F2", 1.0, 0.0, "No recoil-control barrel", 1000, 20);
    var slow = WeaponPatternCatalog.CreateEstimatedPattern(
        "F2", 1.0, 0.0, "No recoil-control barrel", 500, 20);
    var fastRate = fast.Average(point => point.Vertical) * 1000.0;
    var slowRate = slow.Average(point => point.Vertical) * 500.0;
    Near(fastRate, slowRate, 0.01, "RPM conversion preserves displacement per minute");

    var noMuzzle = WeaponPatternCatalog.CreateEstimatedPattern(
        "Custom automatic", 1.0, 0.0, "No recoil-control barrel", 600, 8);
    var muzzle = WeaponPatternCatalog.CreateEstimatedPattern(
        "Custom automatic", 1.0, 0.0, "Muzzle brake", 600, 8);
    Near(
        noMuzzle[0].Vertical * RecoilAttachmentModel.MuzzleBrakeFirstShotMultiplier,
        muzzle[0].Vertical,
        0.0001,
        "muzzle brake applies once to the first point");
    for (var index = 1; index < noMuzzle.Length; ++index)
    {
        Near(noMuzzle[index].Vertical, muzzle[index].Vertical, 0.0001,
            $"muzzle brake leaves point {index} unchanged");
    }

    var noCompensator = WeaponPatternCatalog.CreateEstimatedPattern(
        "R4-C", 1.0, 0.0, "No recoil-control barrel", 859, 25);
    var compensator = WeaponPatternCatalog.CreateEstimatedPattern(
        "R4-C", 1.0, 0.0, "Compensator", 859, 25);
    for (var index = 0; index < noCompensator.Length; ++index)
    {
        Near(
            Math.Abs(noCompensator[index].Horizontal) *
                RecoilAttachmentModel.CompensatorHorizontalMultiplier,
            Math.Abs(compensator[index].Horizontal),
            0.0001,
            $"compensator horizontal factor at point {index}");
    }
}

static void RpmSchedulingHasNoSystematicDrift()
{
    foreach (var rpm in new[] { 584, 978, 1273, 2000 })
    {
        uint remainder = 0;
        long elapsedMicroseconds = 0;
        for (var shot = 0; shot < rpm; ++shot)
        {
            var interval = 60_000_000U / (uint)rpm;
            remainder += 60_000_000U % (uint)rpm;
            if (remainder >= rpm)
            {
                remainder -= (uint)rpm;
                ++interval;
            }
            elapsedMicroseconds += interval;
        }

        Equal(60_000_000L, elapsedMicroseconds, $"{rpm} RPM one-minute phase sum");
        Equal(0U, remainder, $"{rpm} RPM remainder closes after one cycle");
    }
}

static void GeneratedPatternStagesAreContinuous()
{
    var largeMagazine = WeaponPatternCatalog.CreateEstimatedPattern(
        "M249", 1.0, 0.0, "Flash hider", 650, 100);
    var twoStage = WeaponPatternCatalog.CreateEstimatedPattern(
        "BEARING 9", 1.0, 0.0, "Flash hider", 1098, 25);
    var repeat = WeaponPatternCatalog.CreateEstimatedPattern(
        "M249", 1.0, 0.0, "Flash hider", 650, 100);
    var reaper = WeaponPatternCatalog.CreateEstimatedPattern(
        "REAPER-MK2", 1.0, 0.0, "Flash hider", 764, 33);

    True(largeMagazine.SequenceEqual(repeat),
        "pattern generation must remain deterministic");
    True(MaxRelativeVerticalStep(largeMagazine) < 0.08,
        "large-magazine stage boundaries should not introduce abrupt jumps");
    True(MaxRelativeVerticalStep(twoStage) < 0.20,
        "two-stage transitions should be blended across adjacent shots");
    True(MaxHorizontalStepRelativeToVertical(twoStage) < 0.15,
        "two-stage horizontal transitions should not introduce a boundary jump");
    True(MaxRelativeVerticalStep(reaper) < 0.08,
        "published Reaper stage boundaries should remain smoothly blended");
    True(reaper[2].Vertical < reaper[3].Vertical &&
         reaper[9].Vertical < reaper[10].Vertical &&
         reaper[24].Vertical < reaper[25].Vertical,
        "Reaper stages should begin at published bullet indices 3, 10, and 25");
}

static double MaxRelativeVerticalStep(IReadOnlyList<RecoilPatternPoint> pattern)
{
    var maximum = 0.0;
    for (var index = 1; index < pattern.Count; ++index)
    {
        var scale = Math.Max(Math.Abs(pattern[index - 1].Vertical), 0.001f);
        maximum = Math.Max(
            maximum,
            Math.Abs(pattern[index].Vertical - pattern[index - 1].Vertical) / scale);
    }
    return maximum;
}

static double MaxHorizontalStepRelativeToVertical(IReadOnlyList<RecoilPatternPoint> pattern)
{
    var maximum = 0.0;
    for (var index = 1; index < pattern.Count; ++index)
    {
        var scale = Math.Max(Math.Abs(pattern[index].Vertical), 0.001f);
        maximum = Math.Max(
            maximum,
            Math.Abs(pattern[index].Horizontal - pattern[index - 1].Horizontal) / scale);
    }
    return maximum;
}

static void ResearchEstimateIsIsolatedAndNormalized()
{
    Equal(60, ResearchRecoilModel.DefinitionCount, "research definition count");
    var coveredProfiles = WeaponProfile.DefaultProfiles
        .Where(profile => profile.HasWeaponPattern)
        .Where(profile => ResearchRecoilModel.TryGetDefinition(profile.Name, out _))
        .ToArray();
    Equal(60, coveredProfiles.Length, "current automatic profiles covered by research data");
    Equal(
        "XK23",
        WeaponProfile.DefaultProfiles.Single(profile =>
            profile.HasWeaponPattern &&
            !ResearchRecoilModel.TryGetDefinition(profile.Name, out _)).Name,
        "only post-dataset automatic weapon should use fallback");

    var source = WeaponProfile.FindByName("M762")?.Pattern.ToArray() ?? [];
    var originalSnapshot = source.ToArray();
    True(
        ResearchRecoilModel.TryCreatePattern("M762", source, out var research),
        "M762 should have a research-stage model");
    True(source.SequenceEqual(originalSnapshot), "research mode must not mutate original points");
    Equal(source.Length, research.Length, "research pattern length");
    Near(
        source.Sum(point => point.Vertical),
        research.Sum(point => point.Vertical),
        0.001,
        "research profile preserves total vertical output");
    True(
        source.Select(point => point.Horizontal).SequenceEqual(
            research.Select(point => point.Horizontal)),
        "research profile preserves the original horizontal trace");

    True(ResearchRecoilModel.TryGetDefinition("M762", out var definition),
        "M762 definition lookup");
    Near(
        definition.FirstShotKick / definition.StageOneStep,
        research[0].Vertical / research[1].Vertical,
        0.0001,
        "first-shot ratio comes from research data");
    Near(
        definition.StageTwoStep / definition.StageOneStep,
        research[7].Vertical / research[6].Vertical,
        0.0001,
        "M762 long-burst transition starts on shot 8");

    var xk23 = WeaponProfile.FindByName("XK23")?.Pattern.ToArray() ?? [];
    True(
        !ResearchRecoilModel.TryCreatePattern("XK23", xk23, out var fallback),
        "post-Y11S1.3 weapon should report a fallback");
    True(xk23.SequenceEqual(fallback), "fallback must preserve the original estimate");
}

static void ProfileSourceSwitchingIsExact()
{
    var measuredPoints = new[]
    {
        new RecoilPatternPoint(0.5f, 10.0f),
        new RecoilPatternPoint(-0.25f, 20.0f)
    };
    var profile = new WeaponProfile(
        "F2",
        "Assault Rifle",
        ["Twitch"],
        1.0,
        0.0,
        0,
        "Vertical grip",
        "Flash hider",
        roundsPerMinute: 980,
        magazineSize: 26)
    {
        Pattern = [new RecoilPatternPoint(0.0f, 1.0f)]
    };
    profile.MeasuredPatterns =
    [
        new MeasuredPatternVariant(
            "Vertical grip",
            "Flash hider",
            "1.0x",
            "Twitch",
            "test-build",
            980,
            DateTimeOffset.Parse("2026-09-20T00:00:00Z"),
            measuredPoints,
            "controlled optic capture")
    ];

    var originalSettings = new Settings
    {
        ActiveMagnification = "1.0x",
        CompensationMode = CompensationMode.WeaponPattern,
        MasterRecoilGain = 1.0
    };
    var original = RecoilProfileResolver.Build(profile, "Twitch", originalSettings);
    Equal(PatternDataQuality.Measured, original.PatternDataQuality,
        "original source selects exact measured optic");
    True(original.Pattern.SequenceEqual(measuredPoints),
        "original source preserves measured points");
    Equal(1.0f, originalSettings.CalculateSensitivityScale(
        original, CompensationMode.WeaponPattern).Vertical,
        "measured optic source does not receive the estimated-pattern multiplier");

    var researchSettings = new Settings
    {
        ActiveMagnification = "1.0x",
        CompensationMode = CompensationMode.ResearchEstimate,
        MasterRecoilGain = 1.0
    };
    var research = RecoilProfileResolver.Build(profile, "Twitch", researchSettings);
    Equal(PatternDataQuality.VideoDerivedEstimate, research.PatternDataQuality,
        "research source remains labeled estimated");
    Equal("1.0x", research.PatternOptic,
        "research source retains its exact optic reference");
    Near(measuredPoints.Sum(point => point.Vertical),
        research.Pattern.Sum(point => point.Vertical),
        0.001,
        "research source normalizes to the measured optic total");
    True(research.PatternSource.Contains("controlled optic capture", StringComparison.Ordinal),
        "research source discloses its selected reference");
    True(profile.Pattern.Length == 1 && profile.Pattern[0].Vertical == 1.0f,
        "source switching never mutates the original profile");
}

static void WeaponOutputStrengthIsSafe()
{
    var source = new WeaponProfile
    {
        Name = "Strength test",
        WeaponType = "SMG",
        VerticalCompensation = 2.0,
        HorizontalCompensation = -1.0,
        Pattern = [new RecoilPatternPoint(-2.0f, 4.0f)]
    };
    var adjusted = source.WithOutputStrength(1.5);
    var neutral = source.WithOutputStrength(RecoilStrengthModel.Default);
    var combined = source.WithCombinedOutputStrength(2.5, 1.2);
    var highOutput = source.WithCombinedOutputStrength(12.0, 4.0);
    Equal(source.VerticalCompensation, neutral.VerticalCompensation,
        "neutral strength preserves vertical output");
    True(source.Pattern.SequenceEqual(neutral.Pattern),
        "neutral strength preserves every pattern point");
    Equal(2.0, source.VerticalCompensation, "source vertical remains unchanged");
    Equal(new RecoilPatternPoint(-2.0f, 4.0f), source.Pattern[0],
        "source pattern remains unchanged");
    Equal(3.0, adjusted.VerticalCompensation, "vertical output strength");
    Equal(-1.5, adjusted.HorizontalCompensation, "horizontal output strength");
    Equal(new RecoilPatternPoint(-3.0f, 6.0f), adjusted.Pattern[0],
        "pattern output strength");
    Equal(6.0, combined.VerticalCompensation, "combined master and weapon vertical output");
    Equal(-3.0, combined.HorizontalCompensation, "combined master and weapon horizontal output");
    Equal(new RecoilPatternPoint(-6.0f, 12.0f), combined.Pattern[0],
        "combined master and weapon pattern output");
    Equal(96.0, highOutput.VerticalCompensation,
        "high custom gain reaches the expanded firmware range");
    Equal(-48.0, highOutput.HorizontalCompensation,
        "high custom horizontal gain remains proportional");
    Equal(new RecoilPatternPoint(-96.0f, 127.0f), highOutput.Pattern[0],
        "pattern output is bounded only at the representable device limit");

    Equal(RecoilStrengthModel.Minimum, RecoilStrengthModel.Normalize(-10),
        "strength lower clamp");
    Equal(10.0, RecoilStrengthModel.Normalize(10),
        "strength accepts user-selected values inside the HID range");
    Equal(RecoilStrengthModel.Maximum, RecoilStrengthModel.Normalize(1000),
        "strength upper clamp");
    Equal(RecoilStrengthModel.Default, RecoilStrengthModel.Normalize(double.NaN),
        "strength non-finite fallback");
    Equal(RecoilStrengthModel.MasterMinimum, RecoilStrengthModel.NormalizeMaster(-10),
        "master gain lower clamp");
    Equal(10.0, RecoilStrengthModel.NormalizeMaster(10),
        "master gain accepts user-selected values inside the HID range");
    Equal(RecoilStrengthModel.MasterMaximum, RecoilStrengthModel.NormalizeMaster(1000),
        "master gain upper clamp");
    Equal(RecoilStrengthModel.MasterDefault, RecoilStrengthModel.NormalizeMaster(double.NaN),
        "master gain non-finite fallback");
    Equal(3.0, RecoilStrengthModel.Combine(2.5, 1.2),
        "master and weapon gains combine proportionally");
    Equal(RecoilStrengthModel.EffectiveMaximum, RecoilStrengthModel.Combine(100, 2),
        "combined custom gain caps at the representable device limit");
}

static void ExperimentalTuningIsIsolated()
{
    var source = Enumerable.Range(0, 10)
        .Select(index => new RecoilPatternPoint(index + 1, index + 2))
        .ToArray();
    var original = source.ToArray();
    var tuning = new ExperimentalRecoilTuning
    {
        FirstShotGain = 1.5,
        EarlyGain = 1.25,
        MidGain = 0.75,
        LateGain = 1.1,
        HorizontalGain = 0.5
    };

    var result = ExperimentalRecoilModel.Apply(source, tuning);
    var neutral = ExperimentalRecoilModel.Apply(source, new ExperimentalRecoilTuning());

    True(neutral.SequenceEqual(source), "neutral tuning must exactly reproduce the standard pattern");
    Equal(original[0], source[0], "source first point remains unchanged");
    Equal(original[7], source[7], "source late point remains unchanged");
    Equal(0.5f, result[0].Horizontal, "horizontal gain");
    Equal(3.0f, result[0].Vertical, "first-shot gain");
    Equal(5.0f, result[2].Vertical, "early gain");
    Equal(3.75f, result[3].Vertical, "mid gain");
    Equal(9.9f, result[7].Vertical, "late gain");

    var normalized = ExperimentalRecoilModel.Normalize(new ExperimentalRecoilTuning
    {
        FirstShotGain = double.NaN,
        EarlyGain = -10,
        MidGain = 50,
        LateGain = double.PositiveInfinity,
        HorizontalGain = -1
    });
    Equal(1.0, normalized.FirstShotGain, "non-finite first-shot fallback");
    Equal(ExperimentalRecoilModel.MinimumVerticalGain, normalized.EarlyGain, "early lower clamp");
    Equal(ExperimentalRecoilModel.MaximumVerticalGain, normalized.MidGain, "mid upper clamp");
    Equal(1.0, normalized.LateGain, "non-finite late fallback");
    Equal(ExperimentalRecoilModel.MinimumHorizontalGain, normalized.HorizontalGain,
        "horizontal lower clamp");
}

static void HorizontalRecoilOverridesAreSafe()
{
    var source = WeaponProfile.FindByName("R4-C")
        ?? throw new InvalidOperationException("R4-C profile is missing.");
    var originalPattern = source.Pattern.ToArray();
    var settings = new Settings
    {
        ActiveMagnification = "1.0x",
        CompensationMode = CompensationMode.WeaponPattern,
        MasterRecoilGain = 1.0
    };
    settings.SetWeaponHorizontalTuning("R4-C", new HorizontalRecoilTuning
    {
        Mode = HorizontalPatternMode.WeaponPullRight,
        Strength = 2.0
    });

    var rightPull = RecoilProfileResolver.Build(source, "Ash", settings);
    True(rightPull.Pattern.All(point => point.Horizontal <= 0),
        "right weapon pull produces leftward compensation");
    True(rightPull.Pattern.Any(point => point.Horizontal < 0),
        "right pull override produces horizontal output");
    True(source.Pattern.SequenceEqual(originalPattern),
        "custom horizontal tuning never mutates the source profile");

    settings.SetWeaponHorizontalTuning("R4-C", new HorizontalRecoilTuning
    {
        Mode = HorizontalPatternMode.WeaponPullLeft,
        Strength = 2.0
    });
    var leftPull = RecoilProfileResolver.Build(source, "Ash", settings);
    True(leftPull.Pattern.All(point => point.Horizontal >= 0),
        "left weapon pull produces rightward compensation");

    settings.SetWeaponHorizontalTuning("R4-C", new HorizontalRecoilTuning
    {
        Mode = HorizontalPatternMode.Alternating,
        Strength = 1.0
    });
    var alternating = RecoilProfileResolver.Build(source, "Ash", settings);
    True(alternating.Pattern.Any(point => point.Horizontal < 0) &&
         alternating.Pattern.Any(point => point.Horizontal > 0),
        "alternating override moves in both horizontal directions");

    settings.SetWeaponHorizontalTuning("R4-C", new HorizontalRecoilTuning
    {
        Enabled = false,
        Mode = HorizontalPatternMode.Profile,
        Strength = 1.0
    });
    var verticalOnly = RecoilProfileResolver.Build(source, "Ash", settings);
    True(verticalOnly.Pattern.All(point => point.Horizontal == 0),
        "disabled horizontal output preserves vertical-only compensation");
    True(verticalOnly.Pattern.Select(point => point.Vertical)
            .SequenceEqual(rightPull.Pattern.Select(point => point.Vertical)),
        "horizontal controls do not change vertical output");

    var normalized = HorizontalRecoilModel.Normalize(new HorizontalRecoilTuning
    {
        Mode = (HorizontalPatternMode)999,
        Strength = double.PositiveInfinity
    });
    Equal(HorizontalPatternMode.Profile, normalized.Mode, "invalid horizontal mode fallback");
    Equal(HorizontalRecoilModel.DefaultStrength, normalized.Strength,
        "non-finite horizontal strength fallback");
    True(HorizontalRecoilModel.DescribeCatalogDirection("R4-C")
            .Contains("right", StringComparison.OrdinalIgnoreCase),
        "catalog direction is exposed for the UI");
}

static void OperatorOverridesAreIsolated()
{
    var profile = WeaponProfile.FindByName("Mk 14 EBR")
        ?? throw new InvalidOperationException("Mk 14 EBR profile is missing.");
    var aruni = RecoilAttachmentModel.Resolve(profile, "Aruni");
    var dokkaebi = RecoilAttachmentModel.Resolve(profile, "Dokkaebi");
    True(aruni.Barrel.Contains("No recoil", StringComparison.OrdinalIgnoreCase), "Aruni override");
    Equal("Muzzle brake", dokkaebi.Barrel, "Dokkaebi override");
}

static void SettingsNormalizationIsSafe()
{
    var settings = new Settings
    {
        HorizontalSensitivity = -50,
        VerticalSensitivity = 900,
        MouseDpi = -1,
        MouseSensitivityMultiplierUnit = float.NaN,
        AdsSensitivity = new Dictionary<string, float> { ["1.0x"] = -10 },
        ExperimentalRecoilTunings = new Dictionary<string, ExperimentalRecoilTuning>
        {
            [" F2 "] = new()
            {
                FirstShotGain = double.NaN,
                EarlyGain = -1,
                MidGain = 99,
                LateGain = 1.1,
                HorizontalGain = 99
            }
        },
        WeaponOutputStrengths = new Dictionary<string, double>
        {
            [" MP7 "] = 999,
            [" F2 "] = double.NaN
        },
        WeaponHorizontalTunings = new Dictionary<string, HorizontalRecoilTuning>
        {
            [" R4-C "] = new()
            {
                Enabled = true,
                Mode = HorizontalPatternMode.WeaponPullRight,
                Strength = 99
            }
        },
        MasterRecoilGain = 999,
        GeneralTimingVarianceEnabled = true,
        DeltaNoiseEnabled = true
    };
    settings.Normalize();
    var scale = settings.CalculateSensitivityScale();

    Equal(0.5f, settings.HorizontalSensitivity, "horizontal clamp");
    Equal(180.0f, settings.VerticalSensitivity, "vertical clamp");
    Equal(100, settings.MouseDpi, "DPI clamp");
    True(float.IsFinite(scale.Horizontal) && float.IsFinite(scale.Vertical), "finite scales");
    var experimental = settings.GetExperimentalRecoilTuning("F2");
    Equal(1.0, experimental.FirstShotGain, "experimental finite fallback");
    Equal(ExperimentalRecoilModel.MinimumVerticalGain, experimental.EarlyGain,
        "experimental vertical lower clamp");
    Equal(ExperimentalRecoilModel.MaximumVerticalGain, experimental.MidGain,
        "experimental vertical upper clamp");
    Equal(ExperimentalRecoilModel.MaximumHorizontalGain, experimental.HorizontalGain,
        "experimental horizontal clamp");
    Equal(RecoilStrengthModel.Maximum, settings.GetWeaponOutputStrength("MP7"),
        "per-weapon strength upper clamp");
    Equal(RecoilStrengthModel.Default, settings.GetWeaponOutputStrength("F2"),
        "non-finite per-weapon strength fallback");
    Equal(RecoilStrengthModel.MasterMaximum, settings.MasterRecoilGain,
        "master recoil gain upper clamp");
    Equal(RecoilStrengthModel.EffectiveMaximum, settings.GetEffectiveOutputGain("MP7"),
        "master and per-weapon output are bounded together");
    True(!settings.WeaponOutputStrengths.ContainsKey("F2"),
        "neutral per-weapon strengths should not be persisted");
    var horizontal = settings.GetWeaponHorizontalTuning("R4-C");
    Equal(HorizontalPatternMode.WeaponPullRight, horizontal.Mode,
        "horizontal pattern mode normalization");
    Equal(HorizontalRecoilModel.MaximumStrength, horizontal.Strength,
        "horizontal strength upper clamp");

    var roundTrip = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))
        ?? throw new InvalidOperationException("settings round-trip returned null");
    roundTrip.Normalize();
    Equal(1.1, roundTrip.GetExperimentalRecoilTuning("F2").LateGain,
        "experimental per-weapon persistence");
    Equal(RecoilStrengthModel.Maximum, roundTrip.GetWeaponOutputStrength("MP7"),
        "output strength persistence");
    Equal(RecoilStrengthModel.MasterMaximum, roundTrip.MasterRecoilGain,
        "master recoil gain persistence");
    Equal(HorizontalPatternMode.WeaponPullRight,
        roundTrip.GetWeaponHorizontalTuning("R4-C").Mode,
        "horizontal pattern mode persistence");
    Equal(HorizontalRecoilModel.MaximumStrength,
        roundTrip.GetWeaponHorizontalTuning("R4-C").Strength,
        "horizontal strength persistence");
    True(roundTrip.GeneralTimingVarianceEnabled, "timing variance persistence");
    True(roundTrip.DeltaNoiseEnabled, "delta noise persistence");

    foreach (var magnification in new[] { "1.0x", "2.5x", "3.5x", "8.0x" })
    {
        var defaults = new Settings { ActiveMagnification = magnification };
        defaults.Normalize();
        var defaultScale = defaults.CalculateSensitivityScale();
        Equal(1.0f, defaultScale.Horizontal,
            $"{magnification} default horizontal ADS calibration");
        Equal(1.0f, defaultScale.Vertical,
            $"{magnification} default vertical ADS calibration");
    }
}

static void TwoPointFiveBoostIsScoped()
{
    var automatic = new WeaponProfile
    {
        Name = "Boost test",
        WeaponType = "Assault Rifle",
        VerticalCompensation = 2.0,
        Pattern = [new RecoilPatternPoint(0.0f, 2.0f)]
    };
    var saturated = automatic.WithCombinedOutputStrength(127.0, 1.0);
    Equal(127.0, saturated.VerticalCompensation, "master gain reaches profile wire limit");
    var settings = new Settings
    {
        ActiveMagnification = "2.5x",
        TwoPointFiveAutoVerticalBoost = 4.0
    };
    settings.Normalize();
    var boosted = settings.CalculateSensitivityScale(saturated);
    Equal(1.0f, boosted.Horizontal, "boost does not change horizontal output");
    Equal(4.0f, boosted.Vertical, "boost applies after saturated profile");
    var sensitivityPacket = SerialProtocol.BuildSensitivityCommand(boosted);
    Equal(4.0f, BinaryPrimitives.ReadSingleLittleEndian(sensitivityPacket.AsSpan(11, 4)),
        "boosted vertical scale reaches the firmware command exactly");
    Equal(1.0f, settings.CalculateSensitivityScale().Vertical,
        "unscoped calibration remains unchanged");
    Equal(1.0f, settings.CalculateSensitivityScale(new WeaponProfile
    {
        WeaponType = "Marksman Rifle"
    }).Vertical, "semi-automatic weapons are unchanged");
    settings.ActiveMagnification = "1.0x";
    Equal(1.0f, settings.CalculateSensitivityScale(saturated).Vertical,
        "other optics are unchanged");

    settings.TwoPointFiveAutoVerticalBoost = 999.0;
    settings.Normalize();
    Equal(4.0, settings.TwoPointFiveAutoVerticalBoost, "boost upper clamp");
    var roundTrip = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))
        ?? throw new InvalidOperationException("boost settings round-trip returned null");
    roundTrip.Normalize();
    Equal(4.0, roundTrip.TwoPointFiveAutoVerticalBoost, "boost persists");
    settings.TwoPointFiveAutoVerticalBoost = double.NaN;
    settings.Normalize();
    Equal(1.0, settings.TwoPointFiveAutoVerticalBoost, "invalid boost defaults to neutral");
}

static void OriginalPatternMultiplierIsScoped()
{
    var source = new WeaponProfile
    {
        Name = "Original test",
        WeaponType = "Assault Rifle",
        VerticalCompensation = 2.0,
        HorizontalCompensation = -2.0,
        RoundsPerMinute = 800,
        MagazineSize = 30,
        PatternDataQuality = PatternDataQuality.VideoDerivedEstimate,
        Pattern = [new RecoilPatternPoint(-2.0f, 2.0f)]
    };
    var saturated = source.WithCombinedOutputStrength(127.0, 1.0);
    Equal(new RecoilPatternPoint(-127.0f, 127.0f), saturated.Pattern[0],
        "the original source saturates at the Q8.8 profile limit");
    Equal(new RecoilPatternPoint(-2.0f, 2.0f), source.Pattern[0],
        "the source pattern remains unchanged");

    var settings = new Settings { ActiveMagnification = "1.0x" };
    settings.Normalize();
    Equal(2.0, settings.OriginalPatternOutputMultiplier,
        "new and migrated settings default to double output");
    var originalScale = settings.CalculateSensitivityScale(
        saturated, CompensationMode.WeaponPattern);
    Equal(2.0f, originalScale.Horizontal, "original pattern doubles horizontal output");
    Equal(2.0f, originalScale.Vertical, "original pattern doubles vertical output");
    var packet = SerialProtocol.BuildSensitivityCommand(originalScale);
    Equal(2.0f, BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(7, 4)),
        "horizontal multiplier reaches the firmware command");
    Equal(2.0f, BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(11, 4)),
        "vertical multiplier reaches the firmware command");

    foreach (var mode in new[]
    {
        CompensationMode.General,
        CompensationMode.Experimental,
        CompensationMode.ResearchEstimate
    })
    {
        var neutral = settings.CalculateSensitivityScale(saturated, mode);
        Equal(1.0f, neutral.Horizontal, $"{mode} horizontal remains neutral");
        Equal(1.0f, neutral.Vertical, $"{mode} vertical remains neutral");
    }
    Equal(1.0f, settings.CalculateSensitivityScale(new WeaponProfile
    {
        WeaponType = "Marksman Rifle"
    }, CompensationMode.WeaponPattern).Vertical,
        "semi-automatic profiles do not receive original-pattern gain");
    saturated.PatternDataQuality = PatternDataQuality.Measured;
    Equal(1.0f, settings.CalculateSensitivityScale(
        saturated, CompensationMode.WeaponPattern).Vertical,
        "measured profiles remain at their calibrated output");
    saturated.PatternDataQuality = PatternDataQuality.VideoDerivedEstimate;
    saturated.IsBaseline = false;
    Equal(1.0f, settings.CalculateSensitivityScale(
        saturated, CompensationMode.WeaponPattern).Vertical,
        "user-modified estimates remain at their chosen output");
    saturated.IsBaseline = true;

    settings.ActiveMagnification = "2.5x";
    settings.TwoPointFiveAutoVerticalBoost = 4.0;
    settings.OriginalPatternOutputMultiplier = 4.0;
    settings.Normalize();
    var combined = settings.CalculateSensitivityScale(
        saturated, CompensationMode.WeaponPattern);
    Equal(4.0f, combined.Horizontal, "independent optic boost leaves pattern X alone");
    Equal(16.0f, combined.Vertical, "pattern and optic multipliers combine");
    True(ConfigurationValidator.Validate(
        settings, saturated, CompensationMode.WeaponPattern, false).IsValid,
        "maximum combined scale fits the updated firmware contract");
    _ = SerialProtocol.BuildSensitivityCommand(combined);

    settings.OriginalPatternOutputMultiplier = 999.0;
    settings.Normalize();
    Equal(4.0, settings.OriginalPatternOutputMultiplier, "pattern multiplier upper clamp");
    var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))
        ?? throw new InvalidOperationException("pattern settings round-trip returned null");
    restored.Normalize();
    Equal(4.0, restored.OriginalPatternOutputMultiplier, "pattern multiplier persists");
    settings.OriginalPatternOutputMultiplier = double.NaN;
    settings.Normalize();
    Equal(2.0, settings.OriginalPatternOutputMultiplier,
        "invalid pattern multiplier returns to the default");
}

static void ProfilePersistenceRoundTrips()
{
    var directory = Path.Combine(Path.GetTempPath(), $"rainbow-recoil-tests-{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "profiles.json");
    try
    {
        var source = new[] { WeaponProfile.FindByName("F2")! };
        WeaponProfile.SaveToFile(path, source);
        var loaded = WeaponProfile.LoadFromFile(path);
        Equal(1, loaded.Count, "round-trip count");
        Equal("F2", loaded[0].Name, "round-trip name");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

static void MalformedProfileFilesFailClosed()
{
    var directory = Path.Combine(Path.GetTempPath(), $"rainbow-recoil-tests-{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "profiles.json");
    try
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, "{ this is not valid JSON");
        Equal(0, WeaponProfile.LoadFromFile(path).Count, "malformed profile count");
        True(!string.IsNullOrWhiteSpace(WeaponProfile.LastLoadError), "malformed profile error is exposed");
        File.WriteAllText(path, "[{\"name\":\"F2\",\"verticalCompensation\":NaN}]");
        Equal(0, WeaponProfile.LoadFromFile(path).Count, "named floating-point profile count");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

static void LegacySettingsMigrateSafely()
{
    var settings = new Settings
    {
        CalibrationVersion = 1,
        HorizontalSensitivity = 2.5f,
        VerticalSensitivity = 2.5f,
        MouseDpi = 400,
        EnableRecoilControl = true,
        GeneralTimingVarianceEnabled = true,
        DeltaNoiseEnabled = true,
        OperatorDetectionMode = OperatorDetectionMode.Continuous,
        AutoOperatorTrackingEnabled = true
    };

    settings.Normalize();
    Equal(Settings.CurrentCalibrationVersion, settings.CalibrationVersion, "calibration version");
    Equal(55.0f, settings.HorizontalSensitivity, "legacy horizontal migration");
    Equal(55.0f, settings.VerticalSensitivity, "legacy vertical migration");
    Equal(1600, settings.MouseDpi, "legacy DPI migration");
    Equal(OperatorDetectionMode.Disabled, settings.OperatorDetectionMode, "privacy-safe detection migration");
    True(!settings.AutoOperatorTrackingEnabled, "unfinished tracking flag should be cleared");
    True(settings.AutomaticMagnificationEnabled, "automatic optic migration defaults on");
    True(!settings.GeneralTimingVarianceEnabled, "timing variance migration defaults off");
    True(!settings.DeltaNoiseEnabled, "delta noise migration defaults off");
    Equal(RecoilStrengthModel.MasterDefault, settings.MasterRecoilGain,
        "legacy output migration uses corrected hardware gain");
    True(!settings.EnableRecoilControl, "saved armed state must always migrate to safe");

    var v12Settings = new Settings
    {
        CalibrationVersion = 12,
        MasterRecoilGain = RecoilStrengthModel.MasterMinimum,
        GeneralTimingVarianceEnabled = true,
        DeltaNoiseEnabled = true
    };
    v12Settings.Normalize();
    Equal(RecoilStrengthModel.MasterDefault, v12Settings.MasterRecoilGain,
        "v1.5 output migration receives corrected hardware gain");
    True(v12Settings.GeneralTimingVarianceEnabled,
        "v1.5 timing variance preference survives gain migration");
    True(v12Settings.DeltaNoiseEnabled,
        "v1.5 delta noise preference survives gain migration");

    var iconHudSettings = new Settings
    {
        CalibrationVersion = 6,
        WeaponDetectionRegionX = 75,
        WeaponDetectionRegionY = 75,
        WeaponDetectionRegionWidth = 20,
        WeaponDetectionRegionHeight = 20
    };
    iconHudSettings.Normalize();
    Equal(2.0, iconHudSettings.WeaponDetectionRegionX, "icon-HUD region migration X");
    Equal(38.0, iconHudSettings.WeaponDetectionRegionY, "loadout region migration Y");
    Equal(20.0, iconHudSettings.WeaponDetectionRegionWidth, "loadout region width");
    Equal(22.0, iconHudSettings.WeaponDetectionRegionHeight, "two-card loadout region height");

    var serialized = JsonSerializer.Serialize(iconHudSettings);
    True(!serialized.Contains("fieldOfView", StringComparison.OrdinalIgnoreCase),
        "unused FOV setting must not be serialized");
    True(!serialized.Contains("resolutionWidth", StringComparison.OrdinalIgnoreCase),
        "unused resolution setting must not be serialized");
    True(!serialized.Contains("aspectRatio", StringComparison.OrdinalIgnoreCase),
        "unused aspect-ratio setting must not be serialized");
}

static void AttachmentMathIsConsistent()
{
    Equal(0.80 * 0.80, RecoilAttachmentModel.ContinuousVerticalMultiplier("Vertical grip", "Flash hider"),
        "combined vertical multiplier");
    Equal(0.80, RecoilAttachmentModel.ContinuousVerticalMultiplier("Vertical grip", "Compensator"),
        "grip-only vertical multiplier");
    Equal(0.40, RecoilAttachmentModel.SemiAutomaticVerticalMultiplier("Vertical grip", "Muzzle brake"),
        "semi-auto grip and muzzle multiplier");

    var profile = WeaponProfile.FindByName("F2")!;
    var unchanged = profile.WithAttachmentSetup(new ResolvedAttachmentSetup(profile.Grip, profile.Barrel));
    Equal(profile.VerticalCompensation, unchanged.VerticalCompensation,
        "same attachment setup must not rescale twice");
    Equal(profile.Pattern.Length, unchanged.Pattern.Length, "pattern length after attachment hydration");
}

static void OperatorOcrMatchingIsRobust()
{
    var names = SiegeCatalog.Operators.Select(item => item.Name).ToArray();
    var exact = OperatorNameMatcher.FindBest("LOADOUT\nJÄGER\nREADY", names, 0.74);
    Equal("Jäger", exact?.Name, "accent-insensitive exact match");

    var noisy = OperatorNameMatcher.FindBest("OPERAT0R  D0KKAEBI", names, 0.74);
    Equal("Dokkaebi", noisy?.Name, "single-character OCR substitutions");

    var shortFalsePositive = OperatorNameMatcher.FindBest("EQUIPPED PRIMARY", names, 0.74);
    True(shortFalsePositive?.Name != "IQ", "short operator names require word matches");
}

static void DetectionSettingsNormalizeSafely()
{
    var settings = new Settings
    {
        CalibrationVersion = Settings.CurrentCalibrationVersion,
        OperatorDetectionMode = (OperatorDetectionMode)99,
        OperatorDetectionConfidence = double.NaN,
        DetectionRegionX = 98,
        DetectionRegionY = -10,
        DetectionRegionWidth = 80,
        DetectionRegionHeight = 500,
        DetectionHotkeyModifier = "invalid",
        DetectionHotkeyKey = "Q",
        WeaponDetectionConfidence = double.PositiveInfinity,
        WeaponDetectionRegionX = 99,
        WeaponDetectionRegionY = 97,
        WeaponDetectionRegionWidth = 80,
        WeaponDetectionRegionHeight = 80,
        PrimaryWeaponsByOperator = new Dictionary<string, string>
        {
            [" Twitch "] = " F2 ",
            [""] = "invalid"
        },
        OverlayHotkeyModifier = "ctrl",
        OverlayHotkeyKey = "f9"
    };

    settings.Normalize();
    Equal(OperatorDetectionMode.Disabled, settings.OperatorDetectionMode, "detection mode fallback");
    Equal(95.0, settings.DetectionRegionX, "region origin clamp");
    Equal(5.0, settings.DetectionRegionWidth, "region fits desktop width");
    Equal(100.0, settings.DetectionRegionHeight, "height clamp");
    Equal("Shift", settings.DetectionHotkeyModifier, "modifier fallback");
    Equal("M1", settings.DetectionHotkeyKey, "key fallback");
    Equal(0.72, settings.WeaponDetectionConfidence, "weapon confidence fallback");
    Equal(5.0, settings.WeaponDetectionRegionWidth, "weapon region fits desktop width");
    Equal(5.0, settings.WeaponDetectionRegionHeight, "weapon region fits desktop height");
    Equal("F2", settings.PrimaryWeaponsByOperator["Twitch"], "loadout map trimming");
    Equal("Ctrl", settings.OverlayHotkeyModifier, "modifier canonicalization");
    Equal("F9", settings.OverlayHotkeyKey, "key canonicalization");
    True(settings.RapidFireEnabled, "rapid fire defaults on");
}

static void WeaponSlotsAndDefaultsAreDeterministic()
{
    var f2 = new WeaponProfileViewModel(WeaponProfile.FindByName("F2")!);
    var p226 = new WeaponProfileViewModel(WeaponProfile.FindByName("P226 MK 25")!);
    var ita12s = new WeaponProfileViewModel(WeaponProfile.FindByName("ITA12S")!);
    var shorty = new WeaponProfileViewModel(WeaponProfile.FindByName("SUPER SHORTY")!);
    var glaive = new WeaponProfileViewModel(WeaponProfile.FindByName("GLAIVE-12")!);
    var shield = new WeaponProfileViewModel(WeaponProfile.FindByName("BALLISTIC SHIELD")!);
    Equal(WeaponSlot.Primary, WeaponSlotCatalog.GetSlot(f2.Profile), "F2 slot");
    Equal(WeaponSlot.Secondary, WeaponSlotCatalog.GetSlot(p226.Profile), "P226 slot");
    Equal(WeaponSlot.Secondary, WeaponSlotCatalog.GetSlot(ita12s.Profile), "ITA12S slot");
    Equal(WeaponSlot.Secondary, WeaponSlotCatalog.GetSlot(shorty.Profile), "Super Shorty slot");
    Equal(WeaponSlot.Secondary, WeaponSlotCatalog.GetSlot(glaive.Profile), "GLAIVE-12 slot");
    Equal(WeaponSlot.Primary, WeaponSlotCatalog.GetSlot(shield.Profile), "shield slot");

    var profiles = new[] { p226, f2 };
    Equal("F2", WeaponSlotCatalog.SelectDefault(
        profiles, WeaponSlot.Primary, null, "P226 MK 25", "F2")?.Name,
        "secondary preference must not replace primary default");
    Equal("P226 MK 25", WeaponSlotCatalog.SelectDefault(
        profiles, WeaponSlot.Secondary, "P226 MK 25", null)?.Name,
        "remembered secondary");
}

static void OpticDefaultsAreCorrect()
{
    foreach (var operatorDefinition in SiegeCatalog.Operators)
    {
        True(OpticMagnificationPolicy.GetSide(operatorDefinition.Name) != OperatorSide.Unknown,
            $"{operatorDefinition.Name} side classification");
    }

    Equal("2.5x", OpticMagnificationPolicy.Recommend("Ash", "R4-C"),
        "attacker rifle default");
    Equal("2.5x", OpticMagnificationPolicy.Recommend("Ash", "5.7 USG"),
        "attacker secondary follows requested side default");
    Equal("1.0x", OpticMagnificationPolicy.Recommend("Bandit", "MP7"),
        "ordinary defender default");
    Equal("2.5x", OpticMagnificationPolicy.Recommend("Kaid", "TCSG12"),
        "Kaid TCSG12 exception");
    Equal("2.5x", OpticMagnificationPolicy.Recommend("Goyo", "TCSG12"),
        "Goyo TCSG12 exception");
    Equal("2.5x", OpticMagnificationPolicy.Recommend("Sentry", "TCSG12"),
        "Sentry TCSG12 exception");
    Equal("2.5x", OpticMagnificationPolicy.Recommend("Tubarão", "AR-15.50"),
        "Tubarão DMR exception");
    Equal("1.0x", OpticMagnificationPolicy.Recommend("Tubarão", "MPX"),
        "Tubarão non-DMR default");
    Equal("2.5x", OpticMagnificationPolicy.Recommend("Aruni", "Mk 14 EBR"),
        "Aruni Mk 14 EBR exception");
    Equal("1.0x", OpticMagnificationPolicy.Recommend("Aruni", "P10 Roni"),
        "Aruni SMG default");
}

static void WeaponOcrMatchingIsRobust()
{
    var names = SiegeCatalog.Weapons.Select(item => item.Name).ToArray();
    Equal("F2", OperatorNameMatcher.FindBest("AMMO 31 / 150  F2", names, 0.72)?.Name,
        "short exact weapon name");
    Equal("SCORPION EVO 3 A1", OperatorNameMatcher.FindBest(
        "SC0RPION EVO 3 A1  40/160", names, 0.72)?.Name,
        "noisy long weapon name");
    Equal("R4-C", OperatorNameMatcher.FindBest(
        "R4—C", new[] { "R4-C", "G36C" }, 0.72)?.Name,
        "supplied loadout primary OCR");
    Equal("5.7 USG", OperatorNameMatcher.FindBest(
        "-tCUNUA 5.7 USG", new[] { "5.7 USG", "M45 MEUSOC" }, 0.72)?.Name,
        "supplied loadout secondary OCR");
}

static void WeaponSlotHotkeysAreEdgeTriggered()
{
    var state = new WeaponSlotHotkeyState();
    Equal(new WeaponSlotKeyEdges(true, false), state.Update(true, false), "1 key down edge");
    Equal(new WeaponSlotKeyEdges(false, false), state.Update(true, false), "held 1 key");
    Equal(new WeaponSlotKeyEdges(false, false), state.Update(false, false), "keys released");
    Equal(new WeaponSlotKeyEdges(false, true), state.Update(false, true), "2 key down edge");
    state.Reset();
    Equal(new WeaponSlotKeyEdges(true, true), state.Update(true, true), "simultaneous fresh edges");
}

static void SemiAutomaticProfilesAreActive()
{
    foreach (var name in new[] { "417", "P226 MK 25", "LFP586", "M1014", "OTs-03", "GLAIVE-12" })
    {
        var profile = WeaponProfile.FindByName(name)
            ?? throw new InvalidOperationException($"{name} profile is missing.");
        True(profile.SupportsRapidFire, $"{name} rapid-fire support");
        True(profile.RapidFireRoundsPerMinute is >= 60 and <= 1200, $"{name} rapid-fire rate");
        True(profile.VerticalCompensation > 0, $"{name} per-shot recoil compensation");
    }

    True(!WeaponProfile.FindByName("F2")!.SupportsRapidFire, "automatic rifle exclusion");
    True(!WeaponProfile.FindByName("CSRX 300")!.SupportsRapidFire, "bolt-action exclusion");
    True(!WeaponProfile.FindByName("ACS12")!.SupportsRapidFire, "automatic shotgun exclusion");
}

static void CustomProfilesRemainVisible()
{
    var custom = new WeaponProfileViewModel(new WeaponProfile
    {
        Name = "Custom test profile",
        WeaponType = "Unknown",
        Operators = Array.Empty<string>(),
        VerticalCompensation = 1.0,
        IsBaseline = false
    });
    var f2 = new WeaponProfileViewModel(WeaponProfile.FindByName("F2")!);
    var p226 = new WeaponProfileViewModel(WeaponProfile.FindByName("P226 MK 25")!);
    var filtered = WeaponSlotCatalog.ForOperator(new[] { custom, f2, p226 }, "Twitch");
    True(filtered.Contains(custom), "unassigned custom profile should remain selectable");
    True(filtered.Contains(f2), "operator primary should remain selectable");
    True(!filtered.Contains(p226), "another operator's weapon should remain filtered");
    Equal("Modified", custom.ProfileStatus, "custom profile status label");
    Equal("Stock", f2.ProfileStatus, "stock profile status label");
    True(custom.ToString().Contains("Modified", StringComparison.Ordinal),
        "modified profile should be visible in selector text");
}

static void SerialPacketsAreValid()
{
    var stop = SerialProtocol.BuildCommand("STOP");
    Equal(9, stop.Length, "empty command packet length");
    Equal(SerialProtocol.FrameMagicFirst, stop[0], "first sync byte");
    Equal(SerialProtocol.FrameMagicSecond, stop[1], "second sync byte");
    Equal(SerialProtocol.FrameVersion, stop[2], "frame version");
    Equal((byte)0xF2, stop[5], "STOP command id");
    Equal((byte)0, stop[6], "STOP payload length");
    Equal(
        SerialProtocol.ComputeCrc16(stop.AsSpan(2, stop.Length - 4)),
        BinaryPrimitives.ReadUInt16LittleEndian(stop.AsSpan(stop.Length - 2)),
        "STOP CRC");

    var profile = new WeaponProfile
    {
        Name = "武器🎯 profile",
        WeaponType = "SMG",
        VerticalCompensation = 1.25,
        RoundsPerMinute = 900,
        Pattern = [new RecoilPatternPoint(0.5f, 1.0f)]
    };
    var packet = SerialProtocol.BuildProfileCommand(profile, CompensationMode.WeaponPattern);
    var payloadLength = packet[6];
    Equal(packet.Length - 9, payloadLength, "declared payload length");
    True(payloadLength <= SerialProtocol.MaximumPayloadLength, "payload bound");
    var decodedName = new UTF8Encoding(false, true).GetString(packet.AsSpan(21, payloadLength - 14));
    Equal(profile.Name, decodedName, "profile name remains exact valid UTF-8");
    True(FirmwareContract.ProfileAcknowledgementMatches(
        "PROFILE:武器🎯 profile:MODE=PATTERN:V=1.250:H=0.000:RPM=900:POINTS=1",
        profile,
        CompensationMode.WeaponPattern),
        "UTF-8 profile acknowledgement matches the complete exact line");
    True(!FirmwareContract.ProfileAcknowledgementMatches(
        "PROFILE:other:MODE=PATTERN:V=1.250:H=0.000:RPM=900:POINTS=1",
        profile,
        CompensationMode.WeaponPattern),
        "a different profile name cannot satisfy exact readback");

    profile.Name = string.Concat(Enumerable.Repeat("武器🎯", 30));
    Throws<ArgumentOutOfRangeException>(() =>
        SerialProtocol.BuildProfileCommand(profile, CompensationMode.WeaponPattern));
    profile.Name = "unsafe\nname";
    Throws<ArgumentException>(() =>
        SerialProtocol.BuildProfileCommand(profile, CompensationMode.WeaponPattern));

    Throws<ArgumentOutOfRangeException>(() =>
        SerialProtocol.BuildCommand("PROFILE", new string('x', 64)));
}

static void SerialProtocolCoverageIsComplete()
{
    var commands = new Dictionary<string, byte>
    {
        ["PING"] = 0xF0,
        ["START"] = 0xF1,
        ["STOP"] = 0xF2,
        ["PROFILE"] = 0xF3,
        ["SENSITIVITY"] = 0xF4,
        ["PATTERN"] = 0xF5,
        ["RAPID_FIRE"] = 0xF6,
        ["KEEPALIVE"] = 0xF7,
        ["ARM_LEASE"] = 0xF8,
        ["CONFIG_BEGIN"] = 0xF9,
        ["CONFIG_COMMIT"] = 0xFA,
        ["CONFIG_ABORT"] = 0xFB,
        ["STATUS"] = 0xFC,
        ["GENERAL_SETTINGS"] = 0xFD,
        ["RESET"] = 0xFF
    };
    foreach (var (name, identifier) in commands)
    {
        var packet = SerialProtocol.BuildCommand(name);
        Equal(9, packet.Length, $"{name} packet length");
        Equal((byte)0, packet[6], $"{name} payload length");
        Equal(identifier, packet[5], $"{name} identifier");
    }
    Throws<ArgumentException>(() => SerialProtocol.BuildCommand("UNKNOWN"));
    Throws<ArgumentOutOfRangeException>(() =>
        SerialProtocol.BuildSensitivityCommand(new SensitivityScale(float.NaN, 1.0f)));
    var rapid = SerialProtocol.BuildRapidFireCommand(true, 480);
    Equal((byte)0xF6, rapid[5], "rapid-fire command identifier");
    Equal((byte)1, rapid[7], "rapid-fire enabled flag");
    Equal((ushort)480, BinaryPrimitives.ReadUInt16LittleEndian(rapid.AsSpan(8, 2)),
        "rapid-fire RPM");
    Throws<ArgumentOutOfRangeException>(() => SerialProtocol.BuildRapidFireCommand(true, 2000));
    var generalSettings = SerialProtocol.BuildGeneralSettingsCommand(true, true);
    Equal((byte)0xFD, generalSettings[5], "general settings command identifier");
    Equal((byte)2, generalSettings[6], "general settings payload length");
    Equal((byte)1, generalSettings[7], "timing variance enabled flag");
    Equal((byte)1, generalSettings[8], "delta noise enabled flag");
    True(FirmwareContract.GeneralSettingsAcknowledgementMatches(
        "GENERAL_SETTINGS:TIMING_VARIANCE=ON:DELTA_NOISE=ON", true, true),
        "general movement readback matches exactly");
    True(!FirmwareContract.GeneralSettingsAcknowledgementMatches(
        "GENERAL_SETTINGS:TIMING_VARIANCE=OFF:DELTA_NOISE=ON", true, true),
        "opposite general movement readback is rejected");

    var points = Enumerable.Range(0, 31)
        .Select(index => new RecoilPatternPoint(index == 0 ? 127.0f : index / 10.0f, index / 5.0f))
        .ToArray();
    var profile = new WeaponProfile
    {
        Name = "Audit pattern",
        WeaponType = "SMG",
        VerticalCompensation = 1.0,
        RoundsPerMinute = 900,
        Pattern = points
    };
    var profilePacket = SerialProtocol.BuildProfileCommand(profile, CompensationMode.WeaponPattern);
    Equal((byte)CompensationMode.WeaponPattern, profilePacket[8], "profile mode");
    Equal((byte)31, profilePacket[20], "declared pattern count");
    var experimentalPacket = SerialProtocol.BuildProfileCommand(profile, CompensationMode.Experimental);
    Equal((byte)CompensationMode.WeaponPattern, experimentalPacket[8],
        "experimental mode uses firmware-compatible pattern mode");
    Equal((byte)31, experimentalPacket[20], "experimental declared pattern count");
    var researchPacket = SerialProtocol.BuildProfileCommand(
        profile,
        CompensationMode.ResearchEstimate);
    Equal((byte)CompensationMode.WeaponPattern, researchPacket[8],
        "research estimate uses firmware-compatible pattern mode");
    Equal((byte)31, researchPacket[20], "research estimate declared pattern count");

    var chunks = SerialProtocol.BuildPatternCommands(profile);
    Equal(3, chunks.Count, "pattern chunk count");
    var expectedOffsets = new byte[] { 0, 15, 30 };
    var expectedCounts = new byte[] { 15, 15, 1 };
    for (var index = 0; index < chunks.Count; index++)
    {
        var packet = chunks[index];
        var payloadLength = packet[6];
        Equal(packet.Length - 9, (int)payloadLength, $"chunk {index} payload framing");
        True(payloadLength <= SerialProtocol.MaximumPayloadLength, $"chunk {index} payload bound");
        Equal((byte)0xF5, packet[5], $"chunk {index} identifier");
        Equal((byte)1, packet[7], $"chunk {index} version");
        Equal(expectedOffsets[index], packet[8], $"chunk {index} offset");
        Equal(expectedCounts[index], packet[9], $"chunk {index} count");
    }
    Equal(short.MaxValue - 255, BinaryPrimitives.ReadInt16LittleEndian(chunks[0].AsSpan(10, 2)),
        "positive Q8.8 boundary is transferred exactly");

    profile.Pattern[0] = new RecoilPatternPoint(200.0f, 1.0f);
    Throws<ArgumentOutOfRangeException>(() => SerialProtocol.BuildPatternCommands(profile));
    profile.Pattern[0] = new RecoilPatternPoint(1.0f, 1.0f);
    profile.RoundsPerMinute = FirmwareContract.MaximumPatternRoundsPerMinute + 1;
    Throws<ArgumentOutOfRangeException>(() =>
        SerialProtocol.BuildProfileCommand(profile, CompensationMode.WeaponPattern));
}

static void ConfigurationTransactionsAreDeterministic()
{
    var profile = new WeaponProfile
    {
        Name = "A",
        VerticalCompensation = 1.25,
        HorizontalCompensation = -0.5,
        BurstProgression = 7
    };
    var scale = new SensitivityScale(0.5f, 2.0f);
    var hash = SerialProtocol.ComputeConfigurationHash(
        profile,
        CompensationMode.General,
        scale,
        true,
        600);
    Equal(0xCD470D73u, hash, "independent FNV-1a vector");
    Equal(hash, SerialProtocol.ComputeConfigurationHash(
        profile, CompensationMode.General, scale, true, 600), "stable hash");

    var begin = SerialProtocol.BuildConfigurationBeginCommand(0x12345678, hash);
    Equal((byte)0xF9, begin[5], "begin command");
    Equal((byte)9, begin[6], "begin payload length");
    Equal(SerialProtocol.ConfigurationSchemaVersion, begin[7], "configuration schema");
    Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(begin.AsSpan(8, 4)),
        "begin transaction ID");
    Equal(hash, BinaryPrimitives.ReadUInt32LittleEndian(begin.AsSpan(12, 4)),
        "begin expected hash");
    True(FirmwareContract.CommittedStatusMatches(
        $"STATUS:HASH={hash:X8}:TX=IDLE:FIRE=OFF", hash),
        "status recovers a lost commit acknowledgement only for the exact idle hash");
    True(!FirmwareContract.CommittedStatusMatches(
        $"STATUS:HASH={hash:X8}:TX=ACTIVE:FIRE=OFF", hash),
        "active transaction cannot be mistaken for a recovered commit");

    profile.HorizontalCompensation = -0.25;
    True(hash != SerialProtocol.ComputeConfigurationHash(
        profile, CompensationMode.General, scale, true, 600),
        "material configuration change must alter hash");
    True(hash != SerialProtocol.ComputeConfigurationHash(
        new WeaponProfile
        {
            Name = "A",
            VerticalCompensation = 1.25,
            HorizontalCompensation = -0.5,
            BurstProgression = 7
        },
        CompensationMode.General,
        scale,
        true,
        600,
        true),
        "timing variance toggle must alter the configuration hash");
    True(hash != SerialProtocol.ComputeConfigurationHash(
        new WeaponProfile
        {
            Name = "A",
            VerticalCompensation = 1.25,
            HorizontalCompensation = -0.5,
            BurstProgression = 7
        },
        CompensationMode.General,
        scale,
        true,
        600,
        false,
        true),
        "delta noise toggle must alter the configuration hash");
}

static void SerialReliabilityIsBounded()
{
    True(SerialReliabilityPolicy.MaximumResponseAttempts >= 2,
        "an isolated acknowledgement loss must be retried");
    True(SerialReliabilityPolicy.MaximumResponseAttempts <= 3,
        "response retries must remain bounded");
    True(SerialReliabilityPolicy.MaximumConsecutiveReadFailures >= 2,
        "an isolated CDC read failure must not disconnect");
    True(!SerialReliabilityPolicy.ShouldDisconnectAfterReadFailure(1),
        "first transient read failure is tolerated");
    True(SerialReliabilityPolicy.ShouldDisconnectAfterReadFailure(
            SerialReliabilityPolicy.MaximumConsecutiveReadFailures),
        "repeated read failure eventually disconnects");
    True(SerialReliabilityPolicy.IsTransientReadFailure(new IOException()),
        "I/O read failures are transient candidates");
    True(SerialReliabilityPolicy.IsTransientReadFailure(new InvalidOperationException()),
        "temporary closed-state reads are transient candidates");
    True(!SerialReliabilityPolicy.IsTransientReadFailure(new UnauthorizedAccessException()),
        "access failures remain terminal");
}

static void MeasuredProfilePacksAreExact()
{
    var directory = Path.Combine(Path.GetTempPath(), $"zerosense-measured-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        File.WriteAllText(Path.Combine(directory, "f2.json"), """
        {
          "schemaVersion": 1,
          "gameBuild": "test-build",
          "source": "controlled range capture",
          "profiles": [
            {
              "weapon": "F2",
              "grip": "Vertical grip",
              "barrel": "Flash hider",
              "optic": "1.0x",
              "roundsPerMinute": 980,
              "measuredAtUtc": "2026-09-18T00:00:00Z",
              "points": [
                { "horizontal": 0.25, "vertical": 1.5 },
                { "horizontal": -0.5, "vertical": 1.75 }
              ]
            },
            {
              "weapon": "F2",
              "grip": "Vertical grip",
              "barrel": "Flash hider",
              "optic": "2.5x",
              "roundsPerMinute": 981,
              "measuredAtUtc": "2026-09-19T00:00:00Z",
              "points": [{ "horizontal": 2.5, "vertical": 3.5 }]
            },
            {
              "weapon": "F2",
              "grip": "Vertical grip",
              "barrel": "Flash hider",
              "optic": "1.0x",
              "operator": "Twitch",
              "roundsPerMinute": 979,
              "measuredAtUtc": "2026-09-17T00:00:00Z",
              "points": [{ "horizontal": 0.75, "vertical": 2.25 }]
            }
          ]
        }
        """);
        var profiles = new List<WeaponProfile>
        {
            new("F2", "Assault Rifle", ["Twitch"], 1.0, 0, 0,
                "Vertical grip", "Flash hider", roundsPerMinute: 980, magazineSize: 30)
        };
        MeasuredProfileStore.Apply(profiles, directory);
        Equal(PatternDataQuality.None, profiles[0].PatternDataQuality,
            "measurement must wait for complete loadout selection");

        var onePower = profiles[0]
            .WithAttachmentSetup(new ResolvedAttachmentSetup("Vertical grip", "Flash hider"))
            .WithOpticSetup("1.0x");
        Equal(PatternDataQuality.Measured, onePower.PatternDataQuality,
            "exact optic loadout quality");
        Equal(2, onePower.Pattern.Length, "older exact optic must beat newer other optic");
        Equal(980, onePower.RoundsPerMinute, "exact optic RPM");
        Equal("1.0x", onePower.PatternOptic, "measured optic metadata");
        True(onePower.PatternSource.Contains("controlled range capture", StringComparison.Ordinal),
            "measured provenance");

        var twoPointFive = profiles[0]
            .WithAttachmentSetup(new ResolvedAttachmentSetup("Vertical grip", "Flash hider"))
            .WithOpticSetup("2.5x");
        Equal(1, twoPointFive.Pattern.Length, "newer exact 2.5x pattern");
        Equal(981, twoPointFive.RoundsPerMinute, "2.5x RPM remains isolated");

        var twitch = profiles[0]
            .WithAttachmentSetup(new ResolvedAttachmentSetup("Vertical grip", "Flash hider"))
            .WithOpticSetup("1.0x", "Twitch");
        Equal(1, twitch.Pattern.Length, "operator-specific exact pattern takes priority");
        Near(2.25, twitch.Pattern[0].Vertical, 0.0001, "operator-specific pattern point");

        var mismatchedOptic = profiles[0]
            .WithAttachmentSetup(new ResolvedAttachmentSetup("Vertical grip", "Flash hider"))
            .WithOpticSetup("3.0x");
        Equal(PatternDataQuality.VideoDerivedEstimate, mismatchedOptic.PatternDataQuality,
            "missing optic must use estimate");
        True(mismatchedOptic.Pattern.Length > 2, "mismatch falls back to full estimate");

        File.WriteAllText(Path.Combine(directory, "f2-new-build.json"), """
        {
          "schemaVersion": 1,
          "gameBuild": "test-build-2",
          "source": "second controlled range capture",
          "profiles": [
            {
              "weapon": "F2",
              "grip": "Vertical grip",
              "barrel": "Flash hider",
              "optic": "1.0x",
              "operator": "Twitch",
              "roundsPerMinute": 982,
              "measuredAtUtc": "2026-09-20T00:00:00Z",
              "points": [{ "horizontal": 1.0, "vertical": 4.0 }]
            }
          ]
        }
        """);
        MeasuredProfileStore.Apply(profiles, directory);

        var ambiguousBuild = profiles[0]
            .WithAttachmentSetup(new ResolvedAttachmentSetup("Vertical grip", "Flash hider"))
            .WithOpticSetup("1.0x", "Twitch");
        Equal(PatternDataQuality.VideoDerivedEstimate, ambiguousBuild.PatternDataQuality,
            "multiple game builds must fail closed without an exact build selection");

        var exactBuild = profiles[0]
            .WithAttachmentSetup(new ResolvedAttachmentSetup("Vertical grip", "Flash hider"))
            .WithOpticSetup("1.0x", "Twitch", "test-build");
        Equal(PatternDataQuality.Measured, exactBuild.PatternDataQuality,
            "explicit game build selects the matching measurement");
        Equal("test-build", exactBuild.PatternGameBuild, "measured game build metadata");
        Near(2.25, exactBuild.Pattern[0].Vertical, 0.0001,
            "explicit game build keeps its exact trace");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static void DetectionDebounceIsSafe()
{
    var debouncer = new DetectionDebouncer();
    True(!debouncer.Observe("F2"), "first observation must not apply");
    True(debouncer.Observe("f2"), "second equivalent observation should apply");
    Equal(2, debouncer.Observations, "consecutive observation count");
    True(!debouncer.Observe("R4-C"), "changed candidate must restart confirmation");
    True(!debouncer.Observe(null), "empty detection must clear confirmation");
    Equal(0, debouncer.Observations, "empty detection reset");
    Throws<ArgumentOutOfRangeException>(() => debouncer.Observe("F2", 0));
}

static void ProfileBackupRecoversCorruption()
{
    var directory = Path.Combine(Path.GetTempPath(), $"rainbow-recoil-backup-{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "profiles.json");
    try
    {
        WeaponProfile.SaveToFile(path, [WeaponProfile.FindByName("F2")!]);
        WeaponProfile.SaveToFile(path, [WeaponProfile.FindByName("MP7")!]);
        True(File.Exists(path + ".bak"), "rolling backup should exist after replacement");
        File.WriteAllText(path, "{ definitely corrupt");

        var recovered = WeaponProfile.LoadFromFile(path);
        Equal(1, recovered.Count, "recovered profile count");
        Equal("F2", recovered[0].Name, "backup contents");
        True(File.Exists(path + ".corrupt"), "damaged primary should be preserved");
        Equal("F2", WeaponProfile.LoadFromFile(path)[0].Name, "primary should be restored");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

static void ConfigurationValidationFailsClosed()
{
    var settings = new Settings();
    settings.Normalize();
    var invalid = new WeaponProfile
    {
        Name = "Unsafe",
        WeaponType = "SMG",
        VerticalCompensation = double.NaN,
        HorizontalCompensation = 999,
        BurstProgression = 101,
        RoundsPerMinute = 0,
        MagazineSize = 500,
        Pattern = [new RecoilPatternPoint(float.NaN, -1)]
    };
    var rejected = ConfigurationValidator.Validate(
        settings,
        invalid,
        CompensationMode.WeaponPattern,
        false);
    True(!rejected.IsValid, "invalid output must be rejected");
    True(rejected.Errors.Count >= 5, "validator should explain each unsafe field");

    var valid = ConfigurationValidator.Validate(
        settings,
        WeaponProfile.FindByName("F2"),
        CompensationMode.WeaponPattern,
        false);
    True(valid.IsValid, $"stock F2 should validate: {valid.Summary}");
}

static void FirmwareStatusIsParsed()
{
    True(
        FirmwareStatusParser.TryParse(
            "DEVICE:RP2040-ZERO:CDC+HID",
            out var rp2040),
        "RP2040 identity should parse");
    Equal(FirmwareStatusKind.Rp2040Device, rp2040.Kind, "RP2040 identity");

    True(
        FirmwareStatusParser.TryParse(
            "DEVICE:RP2350-USB-C:MOUSE-PROXY",
            out var rp2350),
        "RP2350 identity should parse");
    Equal(FirmwareStatusKind.Rp2350MouseProxy, rp2350.Kind, "RP2350 identity");

    True(
        FirmwareStatusParser.TryParse(
            "MOUSE:CONNECTED:VID=046D:PID=C539",
            out var mouse),
        "mouse identity should parse");
    Equal(FirmwareStatusKind.MouseConnected, mouse.Kind, "mouse state");
    Equal((ushort)0x046d, mouse.VendorId, "mouse VID");
    Equal((ushort)0xc539, mouse.ProductId, "mouse PID");

    True(
        FirmwareStatusParser.TryParse("MOUSE:DISCONNECTED", out var disconnected),
        "mouse disconnect should parse");
    Equal(FirmwareStatusKind.MouseDisconnected, disconnected.Kind, "disconnect state");
    True(
        FirmwareStatusParser.TryParse(
            "MOUSE:UNSUPPORTED:HID_REPORT_DESCRIPTOR",
            out var unsupported),
        "unsupported mouse should parse");
    Equal(FirmwareStatusKind.MouseUnsupported, unsupported.Kind, "unsupported state");
    True(
        !FirmwareStatusParser.TryParse("MOUSE:CONNECTED:VID=NOPE:PID=0001", out _),
        "invalid hexadecimal identity should fail closed");
    True(
        FirmwareStatusParser.TryParse(
            "METRICS:HID_SENT=120:HID_BUSY=3:MAX_QUEUE=18:" +
            "MAX_ACTIVE_GAP_US=1320:HOST_REPORTS=875:HOST_DECODE_ERRORS=2:" +
            "HOST_SATURATIONS=1:USB_STOPS=4:CORRECTION_LATE=7:" +
            "MAX_CORRECTION_LATE_US=2400:QUEUE=5:REPORT_INTERVAL_US=1000:" +
            "GENERAL_DT_CLAMPS=2:HOST_RECOVERIES=6",
            out var metrics),
        "transport metrics should parse");
    Equal(FirmwareStatusKind.TransportMetrics, metrics.Kind, "metrics kind");
    Equal(120u, metrics.HidReportsSent, "sent reports");
    Equal(3u, metrics.HidBusyDeferrals, "busy deferrals");
    Equal(18u, metrics.MaximumQueuedDelta, "maximum queue");
    Equal(1320u, metrics.MaximumActiveReportGapUs, "maximum active gap");
    Equal(875u, metrics.HostReportsReceived, "host reports");
    Equal(2u, metrics.HostDecodeErrors, "host decode errors");
    Equal(1u, metrics.HostAccumulatorSaturations, "host saturations");
    Equal(4u, metrics.UpstreamDisconnectStops, "upstream safety stops");
    Equal(7u, metrics.CorrectionDelayedFrames, "delayed correction frames");
    Equal(2400u, metrics.MaximumCorrectionLatenessUs, "maximum correction lateness");
    Equal(5u, metrics.CurrentQueuedDelta, "current queue");
    Equal(1000u, metrics.CurrentReportIntervalUs, "current report interval");
    Equal(2u, metrics.GeneralIntervalClamps, "general dt clamps");
    Equal(6u, metrics.HostReceiveRecoveries, "host receive recoveries");
    const string recoveryMetrics =
        "METRICS:HID_SENT=120:HID_BUSY=3:MAX_QUEUE=18:" +
        "MAX_ACTIVE_GAP_US=1320:HOST_REPORTS=875:HOST_DECODE_ERRORS=2:" +
        "HOST_SATURATIONS=1:USB_STOPS=4:CORRECTION_LATE=7:" +
        "MAX_CORRECTION_LATE_US=2400:QUEUE=5:REPORT_INTERVAL_US=1000:" +
        "GENERAL_DT_CLAMPS=2:HOST_RECOVERIES=6:HOST_QUEUE_FAILURES=9:" +
        "HOST_UNMOUNTS=2:HOST_TASK_AGE_MS=150:CDC_DROPPED=3";
    True(FirmwareStatusParser.TryParse(recoveryMetrics, out var recovery),
        "expanded recovery telemetry parses");
    Equal(9u, recovery.HostReceiveQueueFailures, "host queue failures");
    Equal(2u, recovery.HostMouseUnmounts, "host unmounts");
    Equal(150u, recovery.HostTaskAgeMs, "host task age");
    Equal(3u, recovery.CdcDroppedMessages, "dropped CDC replies");
    const string pioMetrics = recoveryMetrics +
        ":PIO_TX_TIMEOUTS=1:PIO_RX_FLAG_TIMEOUTS=30:" +
        "PIO_RX_PACKET_TIMEOUTS=2:PIO_SE0_GLITCHES=4";
    True(FirmwareStatusParser.TryParse(pioMetrics, out var pioStatus),
        "host-stall test telemetry parses as metrics rather than a raw UI status line");
    Equal(1u, pioStatus.PioTxTimeouts, "PIO TX timeouts");
    Equal(30u, pioStatus.PioRxFlagTimeouts, "PIO RX flag timeouts");
    Equal(2u, pioStatus.PioRxPacketTimeouts, "PIO RX packet timeouts");
    Equal(4u, pioStatus.PioSe0Glitches, "filtered PIO SE0 glitches");
    True(!FirmwareStatusParser.TryParse(
        pioMetrics.Replace("PIO_RX_FLAG_TIMEOUTS=30", "PIO_RX_FLAG_TIMEOUTS=bad"), out _),
        "malformed PIO telemetry is rejected");
    True(!FirmwareStatusParser.TryParse(
        pioMetrics.Replace(":PIO_SE0_GLITCHES=4", ""), out _),
        "incomplete PIO telemetry is rejected");
    True(!FirmwareStatusParser.TryParse(
        recoveryMetrics.Replace("HOST_TASK_AGE_MS=150", "HOST_TASK_AGE_MS=-1"), out _),
        "negative host task age rejected");
    True(!FirmwareStatusParser.TryParse(
        recoveryMetrics.Replace(":CDC_DROPPED=3", ""), out _),
        "incomplete recovery telemetry rejected");
    DiagnosticLog.Record("firmware", recoveryMetrics);
    True(DiagnosticLog.BuildReport(new Settings(), null, null, null, false)
        .Contains(recoveryMetrics, StringComparison.Ordinal),
        "diagnostic export preserves complete recovery telemetry");
    True(
        FirmwareStatusParser.TryParse(
            "METRICS:HID_SENT=1:HID_BUSY=0:MAX_QUEUE=0:" +
            "MAX_ACTIVE_GAP_US=0:HOST_REPORTS=0:HOST_DECODE_ERRORS=0:" +
            "HOST_SATURATIONS=0",
            out var legacyMetrics) && legacyMetrics.UpstreamDisconnectStops == 0,
        "pre-USB-stop telemetry should remain parseable");
    True(
        !FirmwareStatusParser.TryParse(
            "METRICS:HID_SENT=120:HID_BUSY=bad:MAX_QUEUE=18:" +
            "MAX_ACTIVE_GAP_US=1320:HOST_REPORTS=875:HOST_DECODE_ERRORS=2:" +
            "HOST_SATURATIONS=1:USB_STOPS=4",
            out _),
        "malformed metrics must fail closed");
}

static void SimulatorExercisesConfigurationPath()
{
    using var simulator = new SimulatedRecoilDeviceConnection();
    simulator.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
    True(simulator.IsConnected, "simulator connection");
    var profile = WeaponProfile.FindByName("F2")!;
    simulator.SynchronizeConfigurationAsync(
        profile,
        CompensationMode.WeaponPattern,
        new SensitivityScale(1.0f, 1.0f),
        false,
        0,
        true,
        true,
        CancellationToken.None).GetAwaiter().GetResult();
    simulator.SendCommand("START");
    simulator.SendCommand("STOP");

    Equal("F2", simulator.LastProfile?.Name, "simulated profile");
    Equal(CompensationMode.WeaponPattern, simulator.LastMode, "simulated mode");
    True(simulator.LastGeneralTimingVarianceEnabled, "simulated timing variance setting");
    True(simulator.LastDeltaNoiseEnabled, "simulated delta noise setting");
    Equal("STOP", simulator.LastCommand, "simulated safety command");
    var metrics = simulator.GetMetrics();
    True(metrics.CommandsSent >= 4, "simulator should count encoded commands");
    Equal(1L, metrics.Acknowledgements, "simulated configuration acknowledgement");
    Equal(0L, metrics.FailedCommands, "simulated command failures");
    DiagnosticLog.Record("test", "Simulator report event");
    var report = DiagnosticLog.BuildReport(
        new Settings(), simulator, "Twitch", profile, false);
    True(report.Contains("Connection: simulator", StringComparison.Ordinal),
        "diagnostic connection type");
    True(report.Contains("Commands:", StringComparison.Ordinal),
        "diagnostic command metrics");
    True(report.Contains("Simulator report event", StringComparison.Ordinal),
        "diagnostic recent event");
}

static void ReleaseUpdatesAreReportedTruthfully()
{
    const string json = """
        {
          "tag_name": "v1.5.0",
          "html_url": "https://github.com/tempted14/ZeroSense-RP2040/releases/tag/v1.5.0",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "ZeroSense-Setup-1.5.0-win-x64.exe" },
            { "name": "ZeroSense-1.5.0-Starter-Bundle.zip" },
            { "name": "SHA256SUMS.txt" }
          ]
        }
        """;
    using var client = new HttpClient(new StaticJsonHandler(json));
    var service = new ReleaseUpdateService(
        client,
        new Uri("https://updates.invalid/releases/latest"));
    var result = service.CheckAsync(new Version(1, 4, 0), CancellationToken.None)
        .GetAwaiter().GetResult();

    True(result.IsUpdateAvailable, "newer version should be reported");
    True(result.HasInstaller, "installer filename should be detected");
    True(result.HasStarterBundle, "starter bundle filename should be detected");
    True(result.HasChecksumManifest, "checksum manifest should be detected");
    Equal(new Version(1, 5, 0), result.AvailableVersion, "available version");

    const string portableOnlyJson = """
        {
          "tag_name": "v1.4.0",
          "html_url": "https://github.com/tempted14/ZeroSense-RP2040/releases/tag/v1.4.0",
          "draft": false,
          "prerelease": false,
          "assets": [{ "name": "ZeroSense-1.4.0-Windows-x64.zip" }]
        }
        """;
    using var portableClient = new HttpClient(new StaticJsonHandler(portableOnlyJson));
    var portableResult = new ReleaseUpdateService(
            portableClient,
            new Uri("https://updates.invalid/releases/latest"))
        .CheckAsync(new Version(1, 4, 0), CancellationToken.None)
        .GetAwaiter().GetResult();
    True(!portableResult.IsUpdateAvailable, "equal version should remain current");
    True(!portableResult.HasInstaller, "portable archive is not an installer");
    True(!portableResult.HasStarterBundle, "ordinary portable archive is not the starter bundle");
    True(!portableResult.HasChecksumManifest, "missing checksum must be explicit");
}

static void True(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }
}

static void Near(double expected, double actual, double tolerance, string message)
{
    if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
    {
        throw new InvalidOperationException(
            $"{message}: expected {expected} +/- {tolerance}, got {actual}");
    }
}

static void Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

file sealed class StaticJsonHandler(string json) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
            RequestMessage = request
        };
        return Task.FromResult(response);
    }
}
