using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Media.Ocr;

namespace RainbowRecoil;

internal sealed record OperatorDetectionResult(
    OperatorMatch? Match,
    string Status,
    string RecognizedText);

internal sealed class OperatorDetectionService
{
    private readonly OcrEngine? _engine;

    public OperatorDetectionService()
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
                // The UI reports the missing Windows OCR language pack on use.
            }
        }
    }

    public async Task<OperatorDetectionResult> DetectAsync(
        Settings settings,
        IReadOnlyCollection<string> operatorNames,
        CancellationToken cancellationToken)
    {
        if (_engine is null)
        {
            return new OperatorDetectionResult(
                null,
                "Windows OCR is unavailable. Install an English language OCR component.",
                string.Empty);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var region = new CaptureRegion(
            settings.DetectionRegionX,
            settings.DetectionRegionY,
            settings.DetectionRegionWidth,
            settings.DetectionRegionHeight);
        using var bitmap = ScreenCaptureService.Capture(region, OcrEngine.MaxImageDimension);
        var result = await _engine.RecognizeAsync(bitmap);
        cancellationToken.ThrowIfCancellationRequested();

        var text = result.Text?.Trim() ?? string.Empty;
        var match = OperatorNameMatcher.FindBest(
            text,
            operatorNames,
            settings.OperatorDetectionConfidence);
        return match is null
            ? new OperatorDetectionResult(
                null,
                string.IsNullOrWhiteSpace(text)
                    ? "No readable text was found in the capture region."
                    : "No operator name met the confidence threshold.",
                text)
            : new OperatorDetectionResult(
                match,
                $"Detected {match.Name} · {match.Confidence:P0} confidence",
                text);
    }
}
