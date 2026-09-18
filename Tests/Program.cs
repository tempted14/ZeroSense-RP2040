using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using RainbowRecoil;

var tests = new (string Name, Action Run)[]
{
    ("catalog is complete and unique", CatalogIsCompleteAndUnique),
    ("automatic profiles have bounded patterns", AutomaticProfilesHaveBoundedPatterns),
    ("custom timing controls generated pattern timing and length", CustomPatternTimingIsConsistent),
    ("generated pattern stages transition smoothly", GeneratedPatternStagesAreContinuous),
    ("per-weapon output strength is isolated and bounded", WeaponOutputStrengthIsSafe),
    ("experimental tuning is isolated and stage-specific", ExperimentalTuningIsIsolated),
    ("operator attachment overrides are isolated", OperatorOverridesAreIsolated),
    ("settings normalization and scaling are safe", SettingsNormalizationIsSafe),
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
    ("all commands and pattern chunks encode exactly", SerialProtocolCoverageIsComplete),
    ("configuration validator rejects unsafe output", ConfigurationValidationFailsClosed),
    ("RP2040 simulator exercises the complete configuration path", SimulatorExercisesConfigurationPath)
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

static void GeneratedPatternStagesAreContinuous()
{
    var largeMagazine = WeaponPatternCatalog.CreateEstimatedPattern(
        "M249", 1.0, 0.0, "Flash hider", 650, 100);
    var twoStage = WeaponPatternCatalog.CreateEstimatedPattern(
        "BEARING 9", 1.0, 0.0, "Flash hider", 1098, 25);
    var repeat = WeaponPatternCatalog.CreateEstimatedPattern(
        "M249", 1.0, 0.0, "Flash hider", 650, 100);

    True(largeMagazine.SequenceEqual(repeat),
        "pattern generation must remain deterministic");
    True(MaxRelativeVerticalStep(largeMagazine) < 0.08,
        "large-magazine stage boundaries should not introduce abrupt jumps");
    True(MaxRelativeVerticalStep(twoStage) < 0.20,
        "two-stage transitions should be blended across adjacent shots");
    True(MaxHorizontalStepRelativeToVertical(twoStage) < 0.15,
        "two-stage horizontal transitions should not introduce a boundary jump");
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

    Equal(RecoilStrengthModel.Minimum, RecoilStrengthModel.Normalize(-10),
        "strength lower clamp");
    Equal(RecoilStrengthModel.Maximum, RecoilStrengthModel.Normalize(10),
        "strength upper clamp");
    Equal(RecoilStrengthModel.Default, RecoilStrengthModel.Normalize(double.NaN),
        "strength non-finite fallback");
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
            [" MP7 "] = 99,
            [" F2 "] = double.NaN
        }
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
    True(!settings.WeaponOutputStrengths.ContainsKey("F2"),
        "neutral per-weapon strengths should not be persisted");

    var roundTrip = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))
        ?? throw new InvalidOperationException("settings round-trip returned null");
    roundTrip.Normalize();
    Equal(1.1, roundTrip.GetExperimentalRecoilTuning("F2").LateGain,
        "experimental per-weapon persistence");
    Equal(RecoilStrengthModel.Maximum, roundTrip.GetWeaponOutputStrength("MP7"),
        "output strength persistence");
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
    True(!settings.EnableRecoilControl, "saved armed state must always migrate to safe");

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
    Equal(3, stop.Length, "empty command packet length");
    Equal((byte)0xF2, stop[2], "STOP command id");

    var profile = new WeaponProfile
    {
        Name = string.Concat(Enumerable.Repeat("武器🎯", 30)),
        WeaponType = "SMG",
        VerticalCompensation = 1.25,
        RoundsPerMinute = 900,
        Pattern = [new RecoilPatternPoint(0.5f, 1.0f)]
    };
    var packet = SerialProtocol.BuildProfileCommand(profile, CompensationMode.WeaponPattern);
    var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(0, 2));
    Equal(packet.Length - 3, payloadLength, "declared payload length");
    True(payloadLength <= SerialProtocol.MaximumPayloadLength, "payload bound");
    var decodedName = new UTF8Encoding(false, true).GetString(packet.AsSpan(17));
    True(decodedName.Length > 0, "truncated name remains valid UTF-8");

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
        ["RESET"] = 0xFF
    };
    foreach (var (name, identifier) in commands)
    {
        var packet = SerialProtocol.BuildCommand(name);
        Equal(3, packet.Length, $"{name} packet length");
        Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(packet), $"{name} payload length");
        Equal(identifier, packet[2], $"{name} identifier");
    }
    Throws<ArgumentException>(() => SerialProtocol.BuildCommand("UNKNOWN"));
    Throws<ArgumentOutOfRangeException>(() =>
        SerialProtocol.BuildSensitivityCommand(new SensitivityScale(float.NaN, 1.0f)));
    var rapid = SerialProtocol.BuildRapidFireCommand(true, 480);
    Equal((byte)0xF6, rapid[2], "rapid-fire command identifier");
    Equal((byte)1, rapid[3], "rapid-fire enabled flag");
    Equal((ushort)480, BinaryPrimitives.ReadUInt16LittleEndian(rapid.AsSpan(4, 2)),
        "rapid-fire RPM");
    Throws<ArgumentOutOfRangeException>(() => SerialProtocol.BuildRapidFireCommand(true, 2000));

    var points = Enumerable.Range(0, 31)
        .Select(index => new RecoilPatternPoint(index == 0 ? 200.0f : index / 10.0f, index / 5.0f))
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
    Equal((byte)CompensationMode.WeaponPattern, profilePacket[4], "profile mode");
    Equal((byte)31, profilePacket[16], "declared pattern count");
    var experimentalPacket = SerialProtocol.BuildProfileCommand(profile, CompensationMode.Experimental);
    Equal((byte)CompensationMode.WeaponPattern, experimentalPacket[4],
        "experimental mode uses firmware-compatible pattern mode");
    Equal((byte)31, experimentalPacket[16], "experimental declared pattern count");

    var chunks = SerialProtocol.BuildPatternCommands(profile);
    Equal(3, chunks.Count, "pattern chunk count");
    var expectedOffsets = new byte[] { 0, 15, 30 };
    var expectedCounts = new byte[] { 15, 15, 1 };
    for (var index = 0; index < chunks.Count; index++)
    {
        var packet = chunks[index];
        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(packet);
        Equal(packet.Length - 3, (int)payloadLength, $"chunk {index} payload framing");
        True(payloadLength <= SerialProtocol.MaximumPayloadLength, $"chunk {index} payload bound");
        Equal((byte)0xF5, packet[2], $"chunk {index} identifier");
        Equal((byte)1, packet[3], $"chunk {index} version");
        Equal(expectedOffsets[index], packet[4], $"chunk {index} offset");
        Equal(expectedCounts[index], packet[5], $"chunk {index} count");
    }
    Equal(short.MaxValue - 255, BinaryPrimitives.ReadInt16LittleEndian(chunks[0].AsSpan(6, 2)),
        "positive Q8.8 values clamp below Int16 overflow");
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
        CancellationToken.None).GetAwaiter().GetResult();
    simulator.SendCommand("START");
    simulator.SendCommand("STOP");

    Equal("F2", simulator.LastProfile?.Name, "simulated profile");
    Equal(CompensationMode.WeaponPattern, simulator.LastMode, "simulated mode");
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
