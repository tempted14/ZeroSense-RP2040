using System;
using System.Threading;
using System.Threading.Tasks;

namespace RainbowRecoil;

/// <summary>
/// Small device boundary used by the UI. Keeping transport details behind this
/// interface lets the complete configuration and safety flow run against either
/// real firmware or the deterministic simulator.
/// </summary>
public interface IRecoilDeviceConnection : IDisposable
{
    event EventHandler<string?>? OnCommandReceived;
    event EventHandler<string>? OnStatusChanged;

    bool IsConnected { get; }
    bool IsSimulator { get; }
    string PortName { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    Task SynchronizeConfigurationAsync(
        WeaponProfile profile,
        CompensationMode mode,
        SensitivityScale scale,
        bool rapidFireEnabled,
        int rapidFireRoundsPerMinute,
        CancellationToken cancellationToken);

    void SendCommand(string commandType);
    DeviceConnectionMetrics GetMetrics();
}

public readonly record struct DeviceConnectionMetrics(
    long CommandsSent,
    long FailedCommands,
    long Acknowledgements,
    double LastAcknowledgementMilliseconds,
    double AverageAcknowledgementMilliseconds,
    DateTimeOffset? ConnectedAt);
