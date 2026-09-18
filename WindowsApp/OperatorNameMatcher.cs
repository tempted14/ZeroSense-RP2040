using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RainbowRecoil;

public sealed record OperatorMatch(string Name, double Confidence, string RecognizedText);

/// <summary>Matches noisy OCR output to the known operator catalog.</summary>
public static class OperatorNameMatcher
{
    public static OperatorMatch? FindBest(
        string? recognizedText,
        IEnumerable<string> operatorNames,
        double minimumConfidence)
    {
        if (string.IsNullOrWhiteSpace(recognizedText))
        {
            return null;
        }

        var names = operatorNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedText = NormalizeWords(recognizedText);
        var paddedText = $" {normalizedText} ";

        foreach (var name in names.OrderByDescending(item => NormalizeCompact(item).Length))
        {
            var candidateWords = NormalizeWords(name);
            if (paddedText.Contains($" {candidateWords} ", StringComparison.Ordinal))
            {
                return new OperatorMatch(name, 1.0, recognizedText.Trim());
            }
        }

        var fragments = CreateFragments(recognizedText).Distinct(StringComparer.Ordinal).ToArray();
        OperatorMatch? best = null;
        foreach (var name in names)
        {
            var candidate = NormalizeCompact(name);
            if (candidate.Length < 4)
            {
                continue;
            }

            foreach (var fragment in fragments)
            {
                if (Math.Abs(fragment.Length - candidate.Length) > Math.Max(2, candidate.Length / 3))
                {
                    continue;
                }

                var distance = LevenshteinDistance(candidate, fragment);
                var confidence = 1.0 - (double)distance / Math.Max(candidate.Length, fragment.Length);
                if (confidence >= minimumConfidence && (best is null || confidence > best.Confidence))
                {
                    best = new OperatorMatch(name, confidence, recognizedText.Trim());
                }
            }
        }

        return best;
    }

    internal static string NormalizeCompact(string value) =>
        NormalizeWords(value).Replace(" ", string.Empty, StringComparison.Ordinal);

    private static IEnumerable<string> CreateFragments(string text)
    {
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var words = NormalizeWords(rawLine)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var index = 0; index < words.Length; index++)
            {
                for (var length = 1; length <= 3 && index + length <= words.Length; length++)
                {
                    yield return string.Concat(words.Skip(index).Take(length));
                }
            }
        }
    }

    private static string NormalizeWords(string value)
    {
        var decomposed = value
            .Replace("ø", "o", StringComparison.OrdinalIgnoreCase)
            .Replace("æ", "ae", StringComparison.OrdinalIgnoreCase)
            .Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var previousWasSpace = true;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static int LevenshteinDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitution);
            }
            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
