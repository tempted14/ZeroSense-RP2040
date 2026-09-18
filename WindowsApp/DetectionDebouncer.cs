using System;

namespace RainbowRecoil;

/// <summary>Requires the same non-empty detection on consecutive observations.</summary>
internal sealed class DetectionDebouncer
{
    private string? _candidate;
    private int _observations;

    public int Observations => _observations;

    public bool Observe(string? candidate, int requiredObservations = 2)
    {
        if (requiredObservations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredObservations));
        }
        if (string.IsNullOrWhiteSpace(candidate))
        {
            Reset();
            return false;
        }

        var normalized = candidate.Trim();
        if (!normalized.Equals(_candidate, StringComparison.OrdinalIgnoreCase))
        {
            _candidate = normalized;
            _observations = 1;
        }
        else
        {
            ++_observations;
        }
        return _observations >= requiredObservations;
    }

    public void Reset()
    {
        _candidate = null;
        _observations = 0;
    }
}
