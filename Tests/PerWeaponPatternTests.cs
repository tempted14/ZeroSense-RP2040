using System.Buffers.Binary;
using System.Text.Json;
using RainbowRecoil;

internal static class PerWeaponPatternTests
{
    public static void LegacySettingsAndPersistence()
    {
        var settings = JsonSerializer.Deserialize<Settings>("""
            {"calibrationVersion":17,"originalPatternOutputMultiplier":3.25,
             "masterRecoilGain":12,"generalTimingVarianceEnabled":true,"deltaNoiseEnabled":true}
            """)!;
        settings.Normalize();
        foreach (var gun in WeaponProfile.DefaultProfiles.Where(gun => gun.HasWeaponPattern))
            Equal(3.25, settings.GetWeaponOriginalPatternOutputMultiplier(gun.Name), "legacy value retained for " + gun.Name);
        settings.SetWeaponOriginalPatternOutputMultiplier("SMG-12", 1.25);
        settings.SetWeaponOriginalPatternOutputMultiplier(WeaponPatternCatalog.ScorpionWeaponName, 3.75);
        Equal(3.25, settings.OriginalPatternOutputMultiplier, "editing a gun must not change inherited default");
        Equal(3.25, settings.GetWeaponOriginalPatternOutputMultiplier("F2"), "F2 must not follow SMG-12 edits");

        var directory = Path.Combine(Path.GetTempPath(), "zerosense-per-gun-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(settings));
            var restored = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path))!;
            restored.Normalize();
            Equal(1.25, restored.GetWeaponOriginalPatternOutputMultiplier("smg-12"), "per-gun value survives restart");
            Equal(3.75, restored.GetWeaponOriginalPatternOutputMultiplier(WeaponPatternCatalog.ScorpionWeaponName), "second gun survives restart");
            Equal(3.25, restored.GetWeaponOriginalPatternOutputMultiplier("F2"), "inherited default survives restart");
            Equal(true, restored.GeneralTimingVarianceEnabled, "variance retained on upgrade");
            Equal(true, restored.DeltaNoiseEnabled, "noise retained on upgrade");
            Equal(12.0, restored.MasterRecoilGain, "master gain retained on upgrade");
            restored.ResetWeaponOriginalPatternOutputMultiplier("smg-12");
            Equal(3.25, restored.GetWeaponOriginalPatternOutputMultiplier("SMG-12"), "reset uses saved default");
            Equal(3.75, restored.GetWeaponOriginalPatternOutputMultiplier(WeaponPatternCatalog.ScorpionWeaponName), "reset cannot affect another gun");
        }
        finally { Directory.Delete(directory, true); }
    }

    public static void MalformedOverridesAreNormalized()
    {
        var settings = new Settings
        {
            OriginalPatternOutputMultiplier = 3,
            WeaponOriginalPatternOutputMultipliers = new Dictionary<string, double>
            {
                [" SMG-12 "] = 2,
                ["smg-12"] = 4,
                ["F2"] = double.NaN,
                ["MP7"] = double.PositiveInfinity,
                [" "] = 4,
                ["L85A2"] = 999,
                ["AR33"] = -1
            }
        };
        settings.Normalize();
        Equal(3, settings.WeaponOriginalPatternOutputMultipliers.Count, "invalid entries removed and duplicates merged");
        Equal(4.0, settings.GetWeaponOriginalPatternOutputMultiplier("SMG-12"), "case-insensitive last override wins");
        Equal(3.0, settings.GetWeaponOriginalPatternOutputMultiplier("F2"), "nonfinite entry inherits legacy default");
        Equal(4.0, settings.GetWeaponOriginalPatternOutputMultiplier("L85A2"), "upper bound retained");
        Equal(1.0, settings.GetWeaponOriginalPatternOutputMultiplier("AR33"), "lower bound retained");
        // F2 and MP7 inherit; only three distinct finite non-default overrides remain.
        Equal(false, settings.WeaponOriginalPatternOutputMultipliers.ContainsKey("MP7"), "infinite override removed");
        settings.WeaponOriginalPatternOutputMultipliers = null!;
        settings.Normalize();
        Equal(0, settings.WeaponOriginalPatternOutputMultipliers.Count, "null map recovered");
    }

    public static void WeaponSwitchingReachesProtocol()
    {
        using var simulator = new SimulatedRecoilDeviceConnection();
        simulator.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        var settings = new Settings
        {
            CompensationMode = CompensationMode.WeaponPattern,
            MasterRecoilGain = 1,
            OriginalPatternOutputMultiplier = 2,
            GeneralTimingVarianceEnabled = true,
            DeltaNoiseEnabled = true
        };
        settings.SetWeaponOriginalPatternOutputMultiplier("SMG-12", 1);
        settings.SetWeaponOriginalPatternOutputMultiplier("F2", 3.5);
        var hashes = new Dictionary<string, uint>();
        foreach (var name in new[] { "SMG-12", "F2", WeaponPatternCatalog.ScorpionWeaponName, "SMG-12" })
        {
            var original = WeaponProfile.FindByName(name)!;
            var sourcePoints = original.Pattern.ToArray();
            var effective = RecoilProfileResolver.Build(original, null, settings);
            var scale = settings.CalculateSensitivityScale(effective, settings.CompensationMode);
            var expected = (float)settings.GetWeaponOriginalPatternOutputMultiplier(name);
            Equal(expected, scale.Horizontal, "selected gun's X multiplier");
            Equal(expected, scale.Vertical, "selected gun's Y multiplier");
            var packet = SerialProtocol.BuildSensitivityCommand(scale);
            Equal(expected, BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(11, 4)), "per-gun output in wire packet");
            simulator.SynchronizeConfigurationAsync(effective, settings.CompensationMode, scale,
                false, 0, true, true, 1, CancellationToken.None).GetAwaiter().GetResult();
            Equal(scale, simulator.LastSensitivity, "simulated board readback follows gun selection");
            Equal(true, simulator.LastDeltaNoiseEnabled, "noise remains enabled");
            Equal(true, simulator.LastGeneralTimingVarianceEnabled, "variance remains enabled");
            if (hashes.TryGetValue(name, out var previous))
                Equal(previous, simulator.LastConfigurationHash, "switching back restores exact configuration hash");
            hashes[name] = simulator.LastConfigurationHash;
            Check(sourcePoints.SequenceEqual(original.Pattern), "configuration building mutated source points");
        }
        var smg = WeaponProfile.FindByName("SMG-12")!;
        settings.SetWeaponOriginalPatternOutputMultiplier("SMG-12", 4);
        foreach (var mode in new[] { CompensationMode.General, CompensationMode.Experimental, CompensationMode.ResearchEstimate })
            Equal(1.0f, settings.CalculateSensitivityScale(smg, mode).Vertical, "override must not affect " + mode);
    }

    public static void ScorpionCorrectionIsSustained()
    {
        var source = WeaponProfile.FindByName(WeaponPatternCatalog.ScorpionWeaponName)!;
        var original = source.Pattern.ToArray();
        var settings = new Settings { CompensationMode = CompensationMode.WeaponPattern, MasterRecoilGain = 1 };
        var profile = RecoilProfileResolver.Build(source, "Ela", settings);
        Equal(source.MagazineSize, profile.Pattern.Length, "full magazine length retained");
        Equal(source.RoundsPerMinute, profile.RoundsPerMinute, "RPM retained");
        Check(profile.PatternSource.Contains("Screenshot-guided", StringComparison.Ordinal), "estimate provenance missing");
        Equal(PatternDataQuality.VideoDerivedEstimate, profile.PatternDataQuality, "screenshot is not measured data");
        foreach (var point in profile.Pattern)
            Check(float.IsFinite(point.Horizontal) && float.IsFinite(point.Vertical) &&
                point.Horizontal >= 0 && point.Vertical > 0 && point.Vertical <= 127, "invalid or reversed Scorpion correction");
        for (var index = 0; index < 4; ++index)
            Equal(0.0f, profile.Pattern[index].Horizontal, "short vertical onset retained");
        for (var index = 12; index < profile.Pattern.Length; ++index)
        {
            Equal(profile.Pattern[12], profile.Pattern[index], "sustained correction must not fade or alternate late in magazine");
            Check(profile.Pattern[index].Horizontal > 0, "leftward gun pull needs positive HID X");
        }
        for (var index = 1; index < profile.Pattern.Length; ++index)
            Check(Math.Abs(profile.Pattern[index].Vertical - profile.Pattern[index - 1].Vertical) <
                profile.Pattern[index].Vertical * 0.20, "vertical transition is discontinuous");
        Check(original.SequenceEqual(source.Pattern), "original reference was mutated");
        var extended = WeaponPatternCatalog.CreateEstimatedPattern(source.Name, source.VerticalCompensation,
            0, source.Barrel, source.RoundsPerMinute, 80, useScorpionReference: true);
        Equal(extended[12], extended[^1], "tail correction continues to a custom magazine end");
    }

    public static void ScorpionRefinementIsIsolated()
    {
        var source = WeaponProfile.FindByName(WeaponPatternCatalog.ScorpionWeaponName)!;
        var settings = new Settings { MasterRecoilGain = 1, CompensationMode = CompensationMode.Experimental };
        var experimental = RecoilProfileResolver.Build(source, "Ela", settings);
        Check(source.Pattern.SequenceEqual(experimental.Pattern), "experimental reference changed");
        settings.CompensationMode = CompensationMode.ResearchEstimate;
        ResearchRecoilModel.TryCreatePattern(source.Name, source.Pattern, out var reference);
        Check(reference.SequenceEqual(RecoilProfileResolver.Build(source, "Ela", settings).Pattern), "research data changed");
        settings.CompensationMode = CompensationMode.WeaponPattern;
        foreach (var gun in WeaponProfile.DefaultProfiles.Where(gun => gun.HasWeaponPattern && gun.Name != source.Name))
            Check(gun.Pattern.SequenceEqual(RecoilProfileResolver.Build(gun, null, settings).Pattern), "unrelated pattern changed: " + gun.Name);
        var custom = source.WithOutputStrength(1);
        custom.IsBaseline = false;
        Check(source.Pattern.SequenceEqual(RecoilProfileResolver.Build(custom, "Ela", settings).Pattern), "custom reference changed");
        var measured = source.WithOutputStrength(1);
        RecoilPatternPoint[] points = [new(-2, 3), new(1, 4)];
        measured.MeasuredPatterns = [new MeasuredPatternVariant(source.Grip, source.Barrel, "1.0x", "Ela",
            "test-build", source.RoundsPerMinute, DateTimeOffset.UtcNow, points, "test-only measured fixture")];
        var resolved = RecoilProfileResolver.Build(measured, "Ela", settings);
        Check(points.SequenceEqual(resolved.Pattern), "measured trace was replaced by screenshot estimate");
        Equal(PatternDataQuality.Measured, resolved.PatternDataQuality, "measured quality retained");
        settings.SetWeaponOriginalPatternOutputMultiplier(source.Name, 4);
        Equal(1.0f, settings.CalculateSensitivityScale(resolved, settings.CompensationMode).Vertical, "measured profile must not inherit Original multiplier");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Equal<T>(T expected, T actual, string message) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, got {actual}");
}
