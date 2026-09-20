using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RainbowRecoil;

internal sealed class DeviceConfigurationSynchronizer
{
    public async Task<ConfigurationSyncResult> SynchronizeAsync(
        IRecoilDeviceConnection connection,
        DeviceConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request.Profile);
        var hash = SerialProtocol.ComputeConfigurationHash(
            request.Profile,
            request.Mode,
            request.Sensitivity,
            request.RapidFireEnabled,
            request.RapidFireRoundsPerMinute);
        var started = Stopwatch.GetTimestamp();
        await connection.SynchronizeConfigurationAsync(
            request.Profile,
            request.Mode,
            request.Sensitivity,
            request.RapidFireEnabled,
            request.RapidFireRoundsPerMinute,
            cancellationToken).ConfigureAwait(false);
        return new ConfigurationSyncResult(
            hash,
            Stopwatch.GetElapsedTime(started));
    }
}

internal sealed record DeviceConfigurationRequest(
    WeaponProfile Profile,
    CompensationMode Mode,
    SensitivityScale Sensitivity,
    bool RapidFireEnabled,
    int RapidFireRoundsPerMinute);

internal readonly record struct ConfigurationSyncResult(uint Hash, TimeSpan Elapsed);
