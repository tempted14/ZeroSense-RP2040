using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RainbowRecoil;

/// <summary>
/// In-process RP2040 simulator. It exercises the same protocol encoders and
/// configuration sequencing as hardware but never emits HID input.
/// </summary>
public sealed class SimulatedRecoilDeviceConnection : IRecoilDeviceConnection
{
    private readonly object _stateLock = new();
    private bool _connected;
    private bool _disposed;
    private long _commandsSent;
    private long _failedCommands;
    private long _acknowledgements;
    private long _totalAcknowledgementTicks;
    private long _lastAcknowledgementTicks;
    private DateTimeOffset? _connectedAt;

    public event EventHandler<string?>? OnCommandReceived;
    public event EventHandler<string>? OnStatusChanged;

    public bool IsConnected
    {
        get
        {
            lock (_stateLock)
            {
                return _connected;
            }
        }
    }

    public bool IsSimulator => true;
    public string PortName => "SIMULATOR";

    public WeaponProfile? LastProfile { get; private set; }
    public CompensationMode LastMode { get; private set; }
    public SensitivityScale LastSensitivity { get; private set; }
    public bool LastRapidFireEnabled { get; private set; }
    public int LastRapidFireRoundsPerMinute { get; private set; }
    public string? LastCommand { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connected)
            {
                return;
            }
        }

        await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _connected = true;
            _connectedAt = DateTimeOffset.UtcNow;
        }
        RaiseStatusChanged("Connected (simulator)");
    }

    public async Task SynchronizeConfigurationAsync(
        WeaponProfile profile,
        CompensationMode mode,
        SensitivityScale scale,
        bool rapidFireEnabled,
        int rapidFireRoundsPerMinute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        EnsureConnected();
        var started = Stopwatch.GetTimestamp();
        try
        {
            // Build every packet the real transport would send. This catches
            // protocol bounds and serialization regressions without a board.
            _ = SerialProtocol.BuildProfileCommand(profile, mode);
            Interlocked.Increment(ref _commandsSent);
            if (SerialProtocol.UsesPattern(mode) && profile.HasWeaponPattern)
            {
                foreach (var _ in SerialProtocol.BuildPatternCommands(profile))
                {
                    Interlocked.Increment(ref _commandsSent);
                }
            }
            _ = SerialProtocol.BuildSensitivityCommand(scale);
            Interlocked.Increment(ref _commandsSent);
            _ = SerialProtocol.BuildRapidFireCommand(rapidFireEnabled, rapidFireRoundsPerMinute);
            Interlocked.Increment(ref _commandsSent);

            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            LastProfile = profile;
            LastMode = mode;
            LastSensitivity = scale;
            LastRapidFireEnabled = rapidFireEnabled;
            LastRapidFireRoundsPerMinute = rapidFireRoundsPerMinute;
            RecordAcknowledgement(Stopwatch.GetTimestamp() - started);
            RaiseCommandReceived("SIMULATOR:CONFIGURATION_ACCEPTED");
        }
        catch
        {
            Interlocked.Increment(ref _failedCommands);
            throw;
        }
    }

    public void SendCommand(string commandType)
    {
        EnsureConnected();
        try
        {
            _ = SerialProtocol.BuildCommand(commandType);
            LastCommand = commandType.Trim().ToUpperInvariant();
            Interlocked.Increment(ref _commandsSent);
        }
        catch
        {
            Interlocked.Increment(ref _failedCommands);
            throw;
        }
    }

    public DeviceConnectionMetrics GetMetrics()
    {
        var acknowledgements = Interlocked.Read(ref _acknowledgements);
        var totalTicks = Interlocked.Read(ref _totalAcknowledgementTicks);
        return new DeviceConnectionMetrics(
            Interlocked.Read(ref _commandsSent),
            Interlocked.Read(ref _failedCommands),
            acknowledgements,
            ToMilliseconds(Interlocked.Read(ref _lastAcknowledgementTicks)),
            acknowledgements == 0 ? 0 : ToMilliseconds(totalTicks / acknowledgements),
            _connectedAt);
    }

    private void EnsureConnected()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_connected)
            {
                throw new InvalidOperationException("The simulated RP2040 is not connected.");
            }
        }
    }

    private void RecordAcknowledgement(long ticks)
    {
        Interlocked.Exchange(ref _lastAcknowledgementTicks, ticks);
        Interlocked.Add(ref _totalAcknowledgementTicks, ticks);
        Interlocked.Increment(ref _acknowledgements);
    }

    private static double ToMilliseconds(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;

    private void RaiseCommandReceived(string message)
    {
        try
        {
            OnCommandReceived?.Invoke(this, message);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Simulator command event failed: {exception.Message}");
        }
    }

    private void RaiseStatusChanged(string status)
    {
        try
        {
            OnStatusChanged?.Invoke(this, status);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Simulator status event failed: {exception.Message}");
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }
            _connected = false;
            _disposed = true;
        }
        RaiseStatusChanged("Disconnected (simulator)");
    }
}
