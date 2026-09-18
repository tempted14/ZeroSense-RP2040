using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Media.Ocr;

namespace RainbowRecoil;

internal sealed record WeaponDetectionResult(
    OperatorMatch? PrimaryMatch,
    OperatorMatch? SecondaryMatch,
    string Status,
    string PrimaryRecognizedText,
    string SecondaryRecognizedText);

/// <summary>
/// Reads the named primary and secondary cards from Siege's Loadout screen.
/// In-match weapon HUDs are intentionally not used because current layouts
/// expose icons rather than stable weapon-name text.
/// </summary>
internal sealed class WeaponDetectionService
{
    private readonly OcrEngine? _engine;

    public WeaponDetectionService()
    {
        _engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (_engine is null)
        {
            try
            {
                _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"));
            }
            catch
            {
                // The caller presents the missing OCR component to the user.
            }
        }
    }

    public async Task<WeaponDetectionResult> DetectAsync(
        Settings settings,
        IReadOnlyCollection<string> primaryWeaponNames,
        IReadOnlyCollection<string> secondaryWeaponNames,
        CancellationToken cancellationToken)
    {
        if (_engine is null)
        {
            return new WeaponDetectionResult(
                null,
                null,
                "Windows OCR is unavailable. Install an English language OCR component.",
                string.Empty,
                string.Empty);
        }

        if (primaryWeaponNames.Count == 0 && secondaryWeaponNames.Count == 0)
        {
            return new WeaponDetectionResult(
                null,
                null,
                "No weapons are available for the selected operator.",
                string.Empty,
                string.Empty);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var halfHeight = settings.WeaponDetectionRegionHeight / 2.0;
        var primaryRegion = new CaptureRegion(
            settings.WeaponDetectionRegionX,
            settings.WeaponDetectionRegionY,
            settings.WeaponDetectionRegionWidth,
            halfHeight);
        var secondaryRegion = new CaptureRegion(
            settings.WeaponDetectionRegionX,
            settings.WeaponDetectionRegionY + halfHeight,
            settings.WeaponDetectionRegionWidth,
            halfHeight);
        using var primaryBitmap = ScreenCaptureService.Capture(
            primaryRegion,
            OcrEngine.MaxImageDimension);
        using var secondaryBitmap = ScreenCaptureService.Capture(
            secondaryRegion,
            OcrEngine.MaxImageDimension);
        var primaryResult = await _engine.RecognizeAsync(primaryBitmap);
        var secondaryResult = await _engine.RecognizeAsync(secondaryBitmap);
        cancellationToken.ThrowIfCancellationRequested();

        var primaryText = primaryResult.Text?.Trim() ?? string.Empty;
        var secondaryText = secondaryResult.Text?.Trim() ?? string.Empty;
        var primaryMatch = OperatorNameMatcher.FindBest(
            primaryText,
            primaryWeaponNames,
            settings.WeaponDetectionConfidence);
        var secondaryMatch = OperatorNameMatcher.FindBest(
            secondaryText,
            secondaryWeaponNames,
            settings.WeaponDetectionConfidence);

        var status = (primaryMatch, secondaryMatch) switch
        {
            ({ } primary, { } secondary) =>
                $"Primary {primary.Name} ({primary.Confidence:P0}) · " +
                $"Secondary {secondary.Name} ({secondary.Confidence:P0})",
            ({ } primary, null) =>
                $"Primary {primary.Name} detected at {primary.Confidence:P0}; secondary was not recognized.",
            (null, { } secondary) =>
                $"Secondary {secondary.Name} detected at {secondary.Confidence:P0}; primary was not recognized.",
            _ when string.IsNullOrWhiteSpace(primaryText) && string.IsNullOrWhiteSpace(secondaryText) =>
                "No readable names were found. Open the operator Loadout screen and tune this region.",
            _ => "No compatible loadout names met the confidence threshold."
        };
        return new WeaponDetectionResult(
            primaryMatch,
            secondaryMatch,
            status,
            primaryText,
            secondaryText);
    }
}
