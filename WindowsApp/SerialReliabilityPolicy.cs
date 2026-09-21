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
    public const int MaximumAutomaticReconnectAttempts = 3;

    public static bool IsTransientReadFailure(Exception exception) =>
        exception is IOException or InvalidOperationException;

    public static bool ShouldDisconnectAfterReadFailure(int consecutiveFailures) =>
        consecutiveFailures >= MaximumConsecutiveReadFailures;
}
