using System;
using System.IO;

namespace RainbowRecoil;

/// <summary>
/// Central bounds for tolerating short USB CDC stalls without hiding a real
/// unplug or repeatedly blocking the application on a failed device.
/// </summary>
internal static class SerialReliabilityPolicy
{
    public const int MaximumResponseAttempts = 3;
    public const int MaximumConsecutiveReadFailures = 3;
    public const int ReadFailureBackoffMilliseconds = 40;
    public static TimeSpan ReconnectDelay(int attempt) =>
        TimeSpan.FromMilliseconds(attempt <= 3 ? 500 * Math.Max(1, attempt) : 15000);

    public static bool IsTransientReadFailure(Exception exception) =>
        exception is IOException or InvalidOperationException;

    public static bool ShouldDisconnectAfterReadFailure(int consecutiveFailures) =>
        consecutiveFailures >= MaximumConsecutiveReadFailures;
}
