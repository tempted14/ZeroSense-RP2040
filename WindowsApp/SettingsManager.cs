using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RainbowRecoil;

/// <summary>Persists application and calibration settings as JSON.</summary>
public static class SettingsManager
{
    private const string ConfigFileName = "rainbowrecoil.json";
    private static readonly object SyncRoot = new();
    private static readonly string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RainbowRecoil",
        "settings");
    private static Settings? _currentSettings;

    public static string? LastError { get; private set; }

    public static string ConfigFilePath => Path.Combine(AppDataPath, ConfigFileName);

    public static Settings LoadSettings()
    {
        lock (SyncRoot)
        {
            if (_currentSettings != null)
            {
                return _currentSettings;
            }

            try
            {
                Directory.CreateDirectory(AppDataPath);
                if (!File.Exists(ConfigFilePath))
                {
                    LastError = null;
                    return _currentSettings = new Settings();
                }

                var settings = JsonSerializer.Deserialize<Settings>(
                    File.ReadAllText(ConfigFilePath),
                    JsonOptions()) ?? new Settings();
                var loadedCalibrationVersion = settings.CalibrationVersion;
                settings.Normalize();
                _currentSettings = settings;
                if (loadedCalibrationVersion < Settings.CurrentCalibrationVersion &&
                    !TrySave(out _))
                {
                    return settings;
                }
                LastError = null;
                return settings;
            }
            catch (Exception ex)
            {
                LastError = $"Could not load {ConfigFilePath}: {ex.Message}";
                System.Diagnostics.Debug.WriteLine(LastError);
                return _currentSettings = new Settings();
            }
        }
    }

    public static void Save()
    {
        if (!TrySave(out var error))
        {
            System.Diagnostics.Debug.WriteLine($"Settings save error: {error}");
        }
    }

    public static bool TrySave(out string? error)
    {
        lock (SyncRoot)
        {
            var temporaryPath = ConfigFilePath + ".tmp";
            try
            {
                Directory.CreateDirectory(AppDataPath);
                var settings = LoadSettings();
                settings.Normalize();
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(settings, JsonOptions(true)));
                File.Move(temporaryPath, ConfigFilePath, true);
                error = null;
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                TryDeleteTemporaryFile(temporaryPath);
                error = ex.Message;
                LastError = $"Could not save {ConfigFilePath}: {error}";
                return false;
            }
        }
    }

    public static void RemoveConfig()
    {
        lock (SyncRoot)
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    File.Delete(ConfigFilePath);
                }
                TryDeleteTemporaryFile(ConfigFilePath + ".tmp");
                _currentSettings = null;
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = $"Could not remove {ConfigFilePath}: {ex.Message}";
                System.Diagnostics.Debug.WriteLine(LastError);
            }
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A stale temporary file is harmless and can be replaced next time.
        }
    }

    public static string? LastOperator
    {
        get => LoadSettings().LastOperator;
        set => LoadSettings().LastOperator = value;
    }

    public static string? LastPort
    {
        get => LoadSettings().LastPort;
        set => LoadSettings().LastPort = value;
    }

    public static string? LastWeapon
    {
        get => LoadSettings().LastWeapon;
        set => LoadSettings().LastWeapon = value;
    }

    public static float? VerticalSensitivity
    {
        get => LoadSettings().VerticalSensitivity;
        set => LoadSettings().VerticalSensitivity = value;
    }

    public static float? HorizontalSensitivity
    {
        get => LoadSettings().HorizontalSensitivity;
        set => LoadSettings().HorizontalSensitivity = value;
    }

    public static int? MouseDpi
    {
        get => LoadSettings().MouseDpi;
        set => LoadSettings().MouseDpi = value;
    }

    public static bool EnableRecoilControl
    {
        get => LoadSettings().EnableRecoilControl;
        set => LoadSettings().EnableRecoilControl = value;
    }

    public static CompensationMode CompensationMode
    {
        get => LoadSettings().CompensationMode;
        set => LoadSettings().CompensationMode = value;
    }

    /// <summary>Compatibility bridge for the unfinished v4 tracking flag.</summary>
    [Obsolete("Use Settings.OperatorDetectionMode instead.")]
    public static bool AutoOperatorTrackingEnabled
    {
        get => LoadSettings().OperatorDetectionMode != OperatorDetectionMode.Disabled;
        set
        {
            var settings = LoadSettings();
            settings.AutoOperatorTrackingEnabled = value;
            settings.OperatorDetectionMode = value
                ? OperatorDetectionMode.Keybind
                : OperatorDetectionMode.Disabled;
        }
    }

    private static JsonSerializerOptions JsonOptions(bool indented = false) => new()
    {
        WriteIndented = indented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}

public sealed class Settings
{
    public const int CurrentCalibrationVersion = 14;

    [JsonPropertyName("calibrationVersion")]
    public int CalibrationVersion { get; set; } = CurrentCalibrationVersion;

    [JsonPropertyName("lastOperator")]
    public string? LastOperator { get; set; }

    [JsonPropertyName("lastWeapon")]
    public string? LastWeapon { get; set; }

    [JsonPropertyName("primaryWeaponsByOperator")]
    public Dictionary<string, string> PrimaryWeaponsByOperator { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("secondaryWeaponsByOperator")]
    public Dictionary<string, string> SecondaryWeaponsByOperator { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("lastPort")]
    public string? LastPort { get; set; }

    [JsonPropertyName("verticalSensitivity")]
    public float? VerticalSensitivity { get; set; } = 55.0f;

    [JsonPropertyName("horizontalSensitivity")]
    public float? HorizontalSensitivity { get; set; } = 55.0f;

    [JsonPropertyName("mouseSensitivityMultiplierUnit")]
    public float MouseSensitivityMultiplierUnit { get; set; } = 0.001f;

    [JsonPropertyName("mouseDpi")]
    public int? MouseDpi { get; set; } = 1600;

    [JsonPropertyName("adsSensitivity")]
    public Dictionary<string, float> AdsSensitivity { get; set; } = NewAdsSensitivity();

    [JsonPropertyName("activeMagnification")]
    public string ActiveMagnification { get; set; } = "1.0x";

    [JsonPropertyName("automaticMagnificationEnabled")]
    public bool AutomaticMagnificationEnabled { get; set; } = true;

    [JsonPropertyName("enableRecoilControl")]
    public bool EnableRecoilControl { get; set; }

    [JsonPropertyName("compensationMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CompensationMode CompensationMode { get; set; } = RainbowRecoil.CompensationMode.General;

    [JsonPropertyName("experimentalRecoilTunings")]
    public Dictionary<string, ExperimentalRecoilTuning> ExperimentalRecoilTunings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("weaponOutputStrengths")]
    public Dictionary<string, double> WeaponOutputStrengths { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("masterRecoilGain")]
    public double MasterRecoilGain { get; set; } = RecoilStrengthModel.MasterDefault;

    // Kept for backward-compatible deserialization of the unfinished v4 setting.
    [JsonPropertyName("autoOperatorTrackingEnabled")]
    public bool AutoOperatorTrackingEnabled { get; set; }

    [JsonPropertyName("operatorDetectionMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OperatorDetectionMode OperatorDetectionMode { get; set; } = OperatorDetectionMode.Disabled;

    [JsonPropertyName("autoApplyDetectedOperator")]
    public bool AutoApplyDetectedOperator { get; set; } = true;

    [JsonPropertyName("operatorDetectionConfidence")]
    public double OperatorDetectionConfidence { get; set; } = 0.74;

    [JsonPropertyName("detectionRegionX")]
    public double DetectionRegionX { get; set; }

    [JsonPropertyName("detectionRegionY")]
    public double DetectionRegionY { get; set; }

    [JsonPropertyName("detectionRegionWidth")]
    public double DetectionRegionWidth { get; set; } = 100;

    [JsonPropertyName("detectionRegionHeight")]
    public double DetectionRegionHeight { get; set; } = 55;

    [JsonPropertyName("weaponDetectionEnabled")]
    public bool WeaponDetectionEnabled { get; set; }

    [JsonPropertyName("autoApplyDetectedWeapon")]
    public bool AutoApplyDetectedWeapon { get; set; } = true;

    [JsonPropertyName("weaponDetectionConfidence")]
    public double WeaponDetectionConfidence { get; set; } = 0.72;

    [JsonPropertyName("weaponDetectionRegionX")]
    public double WeaponDetectionRegionX { get; set; } = 2;

    [JsonPropertyName("weaponDetectionRegionY")]
    public double WeaponDetectionRegionY { get; set; } = 38;

    [JsonPropertyName("weaponDetectionRegionWidth")]
    public double WeaponDetectionRegionWidth { get; set; } = 20;

    [JsonPropertyName("weaponDetectionRegionHeight")]
    public double WeaponDetectionRegionHeight { get; set; } = 22;

    [JsonPropertyName("weaponSlotHotkeysEnabled")]
    public bool WeaponSlotHotkeysEnabled { get; set; } = true;

    [JsonPropertyName("rapidFireEnabled")]
    public bool RapidFireEnabled { get; set; } = true;

    [JsonPropertyName("generalTimingVarianceEnabled")]
    public bool GeneralTimingVarianceEnabled { get; set; }

    [JsonPropertyName("deltaNoiseEnabled")]
    public bool DeltaNoiseEnabled { get; set; }

    [JsonPropertyName("detectionHotkeyModifier")]
    public string DetectionHotkeyModifier { get; set; } = "Shift";

    [JsonPropertyName("detectionHotkeyKey")]
    public string DetectionHotkeyKey { get; set; } = "M1";

    [JsonPropertyName("overlayEnabled")]
    public bool OverlayEnabled { get; set; } = true;

    [JsonPropertyName("overlayHotkeyModifier")]
    public string OverlayHotkeyModifier { get; set; } = "None";

    [JsonPropertyName("overlayHotkeyKey")]
    public string OverlayHotkeyKey { get; set; } = "F8";

    [JsonPropertyName("sessionStats")]
    public SessionStats? SessionStats { get; set; }

    public void Normalize()
    {
        if (CalibrationVersion < 5)
        {
            // v4 exposed an unfinished Shift+click setting. The real screen reader is
            // privacy-sensitive, so migration keeps it explicitly off until enabled.
            OperatorDetectionMode = OperatorDetectionMode.Disabled;
            AutoOperatorTrackingEnabled = false;
        }

        if (CalibrationVersion < 7)
        {
            // v6 attempted to read an in-match, icon-only HUD region. Current
            // detection reads the named primary/secondary cards on Loadout.
            WeaponDetectionRegionX = 2;
            WeaponDetectionRegionY = 38;
            WeaponDetectionRegionWidth = 20;
            WeaponDetectionRegionHeight = 22;
        }

        if (CalibrationVersion < 8)
        {
            AutomaticMagnificationEnabled = true;
        }

        if (CalibrationVersion < 11)
        {
            // Deterministic cadence remains the safe, repeatable default. Users
            // must explicitly opt into the ±8% General-mode timing variation.
            GeneralTimingVarianceEnabled = false;
        }

        if (CalibrationVersion < 12)
        {
            // Delta noise changes the emitted pointer path and therefore
            // requires a deliberate opt-in after upgrading.
            DeltaNoiseEnabled = false;
        }


        if (CalibrationVersion < 13)
        {
            // v1.5 profile vectors were validated for shape but shipped roughly
            // 2-3x below the physical output observed on RP2350 hardware.
            MasterRecoilGain = RecoilStrengthModel.MasterDefault;
        }

        if (CalibrationVersion < 14)
        {
            // First physical RP2350 calibration established that the preserved
            // profile shapes need about 3-4x output on the reference setup.
            MasterRecoilGain = RecoilStrengthModel.MasterDefault;
        }

        if (CalibrationVersion < 2)
        {
            // Migrate the old 0.5-3.0 placeholder sliders to the supplied Siege setup.
            HorizontalSensitivity = 55.0f;
            VerticalSensitivity = 55.0f;
            MouseSensitivityMultiplierUnit = 0.001f;
            MouseDpi = 1600;
            AdsSensitivity = NewAdsSensitivity();
            ActiveMagnification = "1.0x";
        }

        if (!Enum.IsDefined(CompensationMode))
        {
            CompensationMode = RainbowRecoil.CompensationMode.General;
        }
        if (!Enum.IsDefined(OperatorDetectionMode))
        {
            OperatorDetectionMode = OperatorDetectionMode.Disabled;
        }
        CalibrationVersion = CurrentCalibrationVersion;
        // Arming is deliberately session-only. Never restore an armed state
        // after a crash, disconnect, update, or ordinary application restart.
        EnableRecoilControl = false;

        HorizontalSensitivity ??= 55.0f;
        VerticalSensitivity ??= 55.0f;
        MouseDpi ??= 1600;
        HorizontalSensitivity = float.IsFinite(HorizontalSensitivity.Value)
            ? Math.Clamp(HorizontalSensitivity.Value, 0.5f, 180.0f)
            : 55.0f;
        VerticalSensitivity = float.IsFinite(VerticalSensitivity.Value)
            ? Math.Clamp(VerticalSensitivity.Value, 0.5f, 180.0f)
            : 55.0f;
        MouseDpi = Math.Clamp(MouseDpi.Value, 100, 32000);
        MouseSensitivityMultiplierUnit = float.IsFinite(MouseSensitivityMultiplierUnit) &&
                                         MouseSensitivityMultiplierUnit > 0
            ? Math.Clamp(MouseSensitivityMultiplierUnit, 0.0001f, 0.1f)
            : 0.001f;
        if (AdsSensitivity == null || AdsSensitivity.Count == 0)
        {
            AdsSensitivity = NewAdsSensitivity();
        }
        else
        {
            AdsSensitivity = AdsSensitivity
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .GroupBy(pair => pair.Key.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last().Value,
                    StringComparer.OrdinalIgnoreCase);
        }
        foreach (var pair in NewAdsSensitivity())
        {
            AdsSensitivity.TryAdd(pair.Key, pair.Value);
        }
        foreach (var key in AdsSensitivity.Keys.ToArray())
        {
            AdsSensitivity[key] = float.IsFinite(AdsSensitivity[key])
                ? Math.Clamp(AdsSensitivity[key], 1.0f, 200.0f)
                : NewAdsSensitivity().GetValueOrDefault(key, 38.0f);
        }
        if (!AdsSensitivity.ContainsKey(ActiveMagnification))
        {
            ActiveMagnification = "1.0x";
        }

        PrimaryWeaponsByOperator = NormalizeLoadoutMap(PrimaryWeaponsByOperator);
        SecondaryWeaponsByOperator = NormalizeLoadoutMap(SecondaryWeaponsByOperator);
        ExperimentalRecoilTunings ??= new Dictionary<string, ExperimentalRecoilTuning>(
            StringComparer.OrdinalIgnoreCase);
        ExperimentalRecoilTunings = ExperimentalRecoilTunings
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .GroupBy(pair => pair.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => ExperimentalRecoilModel.Normalize(group.Last().Value),
                StringComparer.OrdinalIgnoreCase);
        WeaponOutputStrengths ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        WeaponOutputStrengths = WeaponOutputStrengths
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .GroupBy(pair => pair.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new KeyValuePair<string, double>(
                group.Key,
                RecoilStrengthModel.Normalize(group.Last().Value)))
            .Where(pair => Math.Abs(pair.Value - RecoilStrengthModel.Default) > 0.0001)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        MasterRecoilGain = RecoilStrengthModel.NormalizeMaster(MasterRecoilGain);

        OperatorDetectionConfidence = double.IsFinite(OperatorDetectionConfidence)
            ? Math.Clamp(OperatorDetectionConfidence, 0.55, 1.0)
            : 0.74;
        DetectionRegionX = Math.Min(95, ClampPercent(DetectionRegionX, 0));
        DetectionRegionY = Math.Min(95, ClampPercent(DetectionRegionY, 0));
        DetectionRegionWidth = ClampPercent(DetectionRegionWidth, 100, 5);
        DetectionRegionHeight = ClampPercent(DetectionRegionHeight, 55, 5);
        DetectionRegionWidth = Math.Min(DetectionRegionWidth, 100 - DetectionRegionX);
        DetectionRegionHeight = Math.Min(DetectionRegionHeight, 100 - DetectionRegionY);
        WeaponDetectionConfidence = double.IsFinite(WeaponDetectionConfidence)
            ? Math.Clamp(WeaponDetectionConfidence, 0.55, 1.0)
            : 0.72;
        WeaponDetectionRegionX = Math.Min(95, ClampPercent(WeaponDetectionRegionX, 2));
        WeaponDetectionRegionY = Math.Min(95, ClampPercent(WeaponDetectionRegionY, 38));
        WeaponDetectionRegionWidth = ClampPercent(WeaponDetectionRegionWidth, 20, 5);
        WeaponDetectionRegionHeight = ClampPercent(WeaponDetectionRegionHeight, 22, 10);
        WeaponDetectionRegionWidth = Math.Min(WeaponDetectionRegionWidth, 100 - WeaponDetectionRegionX);
        WeaponDetectionRegionHeight = Math.Min(WeaponDetectionRegionHeight, 100 - WeaponDetectionRegionY);
        DetectionHotkeyModifier = NormalizeModifier(DetectionHotkeyModifier, "Shift");
        DetectionHotkeyKey = HotkeyChord.NormalizeKey(DetectionHotkeyKey, "M1");
        OverlayHotkeyModifier = NormalizeModifier(OverlayHotkeyModifier, "None");
        OverlayHotkeyKey = HotkeyChord.NormalizeKey(OverlayHotkeyKey, "F8");
    }

    public float GetActiveAdsSensitivity() =>
        AdsSensitivity.TryGetValue(ActiveMagnification, out var value) ? value : 38.0f;

    public ExperimentalRecoilTuning GetExperimentalRecoilTuning(string weaponName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(weaponName);
        if (!ExperimentalRecoilTunings.TryGetValue(weaponName, out var tuning))
        {
            tuning = new ExperimentalRecoilTuning();
            ExperimentalRecoilTunings[weaponName] = tuning;
        }
        return ExperimentalRecoilModel.Normalize(tuning);
    }

    public double GetWeaponOutputStrength(string weaponName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(weaponName);
        return WeaponOutputStrengths.TryGetValue(weaponName, out var strength)
            ? RecoilStrengthModel.Normalize(strength)
            : RecoilStrengthModel.Default;
    }

    public void SetWeaponOutputStrength(string weaponName, double requestedStrength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(weaponName);
        var strength = RecoilStrengthModel.Normalize(requestedStrength);
        if (Math.Abs(strength - RecoilStrengthModel.Default) <= 0.0001)
        {
            WeaponOutputStrengths.Remove(weaponName);
        }
        else
        {
            WeaponOutputStrengths[weaponName] = strength;
        }
    }

    public double GetEffectiveOutputGain(string weaponName) =>
        RecoilStrengthModel.Combine(MasterRecoilGain, GetWeaponOutputStrength(weaponName));

    public SensitivityScale CalculateSensitivityScale()
    {
        const float referenceHipGain = 55.0f * 0.001f;

        var horizontalGain = Math.Max(0.000001f,
            (HorizontalSensitivity ?? 55.0f) * MouseSensitivityMultiplierUnit);
        var verticalGain = Math.Max(0.000001f,
            (VerticalSensitivity ?? 55.0f) * MouseSensitivityMultiplierUnit);
        var ads = Math.Max(1.0f, GetActiveAdsSensitivity());
        var referenceAds = GetReferenceAdsSensitivity(ActiveMagnification);
        var adsScale = referenceAds / ads;

        return new SensitivityScale(
            referenceHipGain / horizontalGain * adsScale,
            referenceHipGain / verticalGain * adsScale);
    }

    [JsonIgnore]
    public float DefaultMultiplierEquivalent =>
        (HorizontalSensitivity ?? 55.0f) * MouseSensitivityMultiplierUnit / 0.02f;

    private static Dictionary<string, float> NewAdsSensitivity() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["1.0x"] = 38.0f,
        ["2.5x"] = 67.0f,
        ["3.5x"] = 72.0f,
        ["8.0x"] = 74.0f
    };

    private static float GetReferenceAdsSensitivity(string magnification) => magnification switch
    {
        "2.5x" => 67.0f,
        "3.5x" => 72.0f,
        "8.0x" => 74.0f,
        _ => 38.0f
    };

    private static double ClampPercent(double value, double fallback, double minimum = 0) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, 100) : fallback;

    private static string NormalizeModifier(string? value, string fallback)
    {
        var match = HotkeyChord.Modifiers.FirstOrDefault(option =>
            option.Equals(value, StringComparison.OrdinalIgnoreCase));
        return match ?? fallback;
    }

    private static Dictionary<string, string> NormalizeLoadoutMap(
        Dictionary<string, string>? source) => (source ?? new Dictionary<string, string>())
        .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
        .GroupBy(pair => pair.Key.Trim(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group => group.Key,
            group => group.Last().Value.Trim(),
            StringComparer.OrdinalIgnoreCase);
}

public enum OperatorDetectionMode
{
    Disabled,
    Continuous,
    Keybind
}

public readonly record struct SensitivityScale(float Horizontal, float Vertical);

public sealed class SessionStats
{
    [JsonPropertyName("shotsFired")]
    public int ShotsFired { get; set; }

    [JsonPropertyName("totalCompensationApplied")]
    public double TotalCompensationApplied { get; set; }

    [JsonPropertyName("sessionsCompleted")]
    public int SessionsCompleted { get; set; }
}
