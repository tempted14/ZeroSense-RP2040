using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace RainbowRecoil;

/// <summary>Thread-safe USB CDC transport for the RP2040 firmware protocol.</summary>
public sealed class SerialConnection : IRecoilDeviceConnection
{
    private readonly string _portName;
    private readonly object _stateLock = new();
    private readonly object _writeLock = new();
    private readonly object _responseLock = new();
    private readonly SemaphoreSlim _configurationLock = new(1, 1);
    private SerialPort? _serialPort;
    private CancellationTokenSource? _readCancellation;
    private Task? _readTask;
    private bool _disposed;
    private PendingResponse? _pendingResponse;
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
                return _serialPort?.IsOpen == true;
            }
        }
    }

    public string PortName => _portName;
    public bool IsSimulator => false;

    public SerialConnection(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new ArgumentException("A serial port name is required.", nameof(portName));
        }

        _portName = portName.Trim();
    }

    public Task Connect() => ConnectAsync(CancellationToken.None);

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_serialPort?.IsOpen == true)
            {
                return;
            }
        }

        var port = new SerialPort(_portName, 115200)
        {
            ReadTimeout = 100,
            WriteTimeout = 500,
            DtrEnable = true,
            RtsEnable = false,
            NewLine = "\n"
        };

        try
        {
            port.Open();
            await Task.Delay(75, cancellationToken).ConfigureAwait(false);
            await Task.Run(() => PerformHandshake(port, cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            var readCancellation = new CancellationTokenSource();
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _serialPort = port;
                _readCancellation = readCancellation;
                _readTask = Task.Run(() => ReadLoop(port, readCancellation.Token));
                _connectedAt = DateTimeOffset.UtcNow;
            }
            RaiseStatusChanged("Connected");
        }
        catch (UnauthorizedAccessException ex)
        {
            CloseAndDispose(port);
            throw new InvalidOperationException(
                $"Cannot open {_portName}; another program may already be using it.",
                ex);
        }
        catch
        {
            CloseAndDispose(port);
            throw;
        }
    }

    private static void PerformHandshake(SerialPort port, CancellationToken cancellationToken)
    {
        port.DiscardInBuffer();
        var ping = SerialProtocol.BuildCommand("PING");
        port.Write(ping, 0, ping.Length);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var line = port.ReadLine().TrimEnd('\r');
                if (line.Equals("PONG:RAINBOW-RECOIL:3", StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (TimeoutException)
            {
                // A short timeout keeps cancellation and the overall deadline responsive.
            }
        }

        throw new InvalidOperationException(
            $"{port.PortName} did not identify as Rainbow Recoil firmware.");
    }

    private void ReadLoop(SerialPort port, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && port.IsOpen)
            {
                try
                {
                    var line = port.ReadLine().TrimEnd('\r');
                    if (line.Length > 0)
                    {
                        CompletePendingResponse(line);
                        RaiseCommandReceived(line);
                    }
                }
                catch (TimeoutException)
                {
                    // A quiet CDC connection is normal.
                }
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            MarkConnectionFailed(port);
            RaiseStatusChanged($"Disconnected: {ex.Message}");
            RaiseCommandReceived(null);
        }
    }

    private void MarkConnectionFailed(SerialPort failedPort)
    {
        lock (_stateLock)
        {
            if (!ReferenceEquals(_serialPort, failedPort))
            {
                return;
            }

            _serialPort = null;
            _readCancellation?.Dispose();
            _readCancellation = null;
            _readTask = null;
        }

        CloseAndDispose(failedPort);
        FailPendingResponse(new IOException("The RP2040 CDC port was disconnected."));
    }

    public byte[] BuildCommand(string commandType, string payload) =>
        SerialProtocol.BuildCommand(commandType, payload);

    public byte[] BuildProfileCommand(WeaponProfile profile) =>
        SerialProtocol.BuildProfileCommand(profile, CompensationMode.General);

    public byte[] BuildProfileCommand(WeaponProfile profile, CompensationMode mode) =>
        SerialProtocol.BuildProfileCommand(profile, mode);

    public IReadOnlyList<byte[]> BuildPatternCommands(WeaponProfile profile) =>
        SerialProtocol.BuildPatternCommands(profile);

    public byte[] BuildSensitivityCommand(SensitivityScale scale) =>
        SerialProtocol.BuildSensitivityCommand(scale);

    public byte[] BuildRapidFireCommand(bool enabled, int roundsPerMinute) =>
        SerialProtocol.BuildRapidFireCommand(enabled, roundsPerMinute);

    public void SendProfile(WeaponProfile profile) =>
        SendProfile(profile, CompensationMode.General);

    public int SendProfile(WeaponProfile profile, CompensationMode mode)
    {
        Write(SerialProtocol.BuildProfileCommand(profile, mode));
        var packetsSent = 1;
        if (SerialProtocol.UsesPattern(mode) && profile.HasWeaponPattern)
        {
            foreach (var command in SerialProtocol.BuildPatternCommands(profile))
            {
                Write(command);
                ++packetsSent;
            }
        }
        return packetsSent;
    }

    public void SendSensitivity(SensitivityScale scale) =>
        Write(SerialProtocol.BuildSensitivityCommand(scale));

    /// <summary>
    /// Sends a complete configuration and waits for the firmware to acknowledge
    /// each stage. A successful write alone is not treated as device acceptance.
    /// </summary>
    public async Task SynchronizeConfigurationAsync(
        WeaponProfile profile,
        CompensationMode mode,
        SensitivityScale scale,
        bool rapidFireEnabled,
        int rapidFireRoundsPerMinute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _configurationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendAndAwaitAsync(
                () => Write(SerialProtocol.BuildProfileCommand(profile, mode)),
                line => line.StartsWith("PROFILE:", StringComparison.Ordinal),
                line => line.StartsWith("ERROR:PROFILE", StringComparison.Ordinal),
                "profile",
                cancellationToken).ConfigureAwait(false);

            if (SerialProtocol.UsesPattern(mode) && profile.HasWeaponPattern)
            {
                await SendAndAwaitAsync(
                    () =>
                    {
                        foreach (var command in SerialProtocol.BuildPatternCommands(profile))
                        {
                            Write(command);
                        }
                    },
                    line => line.StartsWith("PATTERN:READY:", StringComparison.Ordinal),
                    line => line.StartsWith("ERROR:PATTERN", StringComparison.Ordinal),
                    "pattern",
                    cancellationToken).ConfigureAwait(false);
            }

            await SendAndAwaitAsync(
                () => Write(SerialProtocol.BuildSensitivityCommand(scale)),
                line => line.StartsWith("SENSITIVITY:", StringComparison.Ordinal),
                line => line.StartsWith("ERROR:SENSITIVITY", StringComparison.Ordinal),
                "sensitivity",
                cancellationToken).ConfigureAwait(false);

            await SendAndAwaitAsync(
                () => Write(SerialProtocol.BuildRapidFireCommand(
                    rapidFireEnabled,
                    rapidFireRoundsPerMinute)),
                line => line.StartsWith("RAPID_FIRE:", StringComparison.Ordinal),
                line => line.StartsWith("ERROR:RAPID_FIRE", StringComparison.Ordinal),
                "rapid-fire",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _configurationLock.Release();
        }
    }

    public void SendCommand(string commandType) =>
        Write(SerialProtocol.BuildCommand(commandType));

    public void Write(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        SerialPort port;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            port = _serialPort ?? throw new InvalidOperationException(
                "The RP2040 CDC port is not connected.");
        }

        try
        {
            lock (_writeLock)
            {
                if (!port.IsOpen)
                {
                    throw new IOException("The RP2040 CDC port was disconnected.");
                }
                port.Write(data, 0, data.Length);
            }
            Interlocked.Increment(ref _commandsSent);
        }
        catch
        {
            Interlocked.Increment(ref _failedCommands);
            throw;
        }
    }

    public void Disconnect()
    {
        SerialPort? port;
        CancellationTokenSource? cancellation;
        lock (_stateLock)
        {
            port = _serialPort;
            cancellation = _readCancellation;
            _serialPort = null;
            _readCancellation = null;
            _readTask = null;
        }

        if (port is null)
        {
            cancellation?.Dispose();
            return;
        }

        try
        {
            if (port.IsOpen)
            {
                lock (_writeLock)
                {
                    var stop = SerialProtocol.BuildCommand("STOP");
                    port.Write(stop, 0, stop.Length);
                }
            }
        }
        catch
        {
            // The device may already have been unplugged.
        }
        finally
        {
            cancellation?.Cancel();
            CloseAndDispose(port);
            cancellation?.Dispose();
            FailPendingResponse(new IOException("The RP2040 CDC port was disconnected."));
            RaiseStatusChanged("Disconnected");
        }
    }

    private async Task SendAndAwaitAsync(
        Action send,
        Func<string, bool> isSuccess,
        Func<string, bool> isFailure,
        string stage,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingResponse(isSuccess, isFailure, completion);
        lock (_responseLock)
        {
            if (_pendingResponse is not null)
            {
                throw new InvalidOperationException("Another firmware response is already pending.");
            }
            _pendingResponse = pending;
        }

        try
        {
            send();
            string response;
            try
            {
                response = await completion.Task
                    .WaitAsync(TimeSpan.FromSeconds(2.5), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                Interlocked.Increment(ref _failedCommands);
                throw new InvalidOperationException(
                    $"The firmware did not acknowledge the {stage} configuration.", ex);
            }

            if (isFailure(response))
            {
                Interlocked.Increment(ref _failedCommands);
                throw new InvalidOperationException(
                    $"The firmware rejected the {stage} configuration: {response}");
            }
            RecordAcknowledgement(Stopwatch.GetTimestamp() - started);
        }
        finally
        {
            lock (_responseLock)
            {
                if (ReferenceEquals(_pendingResponse, pending))
                {
                    _pendingResponse = null;
                }
            }
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

    private void RecordAcknowledgement(long ticks)
    {
        Interlocked.Exchange(ref _lastAcknowledgementTicks, ticks);
        Interlocked.Add(ref _totalAcknowledgementTicks, ticks);
        Interlocked.Increment(ref _acknowledgements);
    }

    private static double ToMilliseconds(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;

    private void CompletePendingResponse(string line)
    {
        PendingResponse? pending;
        lock (_responseLock)
        {
            pending = _pendingResponse;
        }

        if (pending is not null && (pending.IsSuccess(line) || pending.IsFailure(line)))
        {
            pending.Completion.TrySetResult(line);
        }
    }

    private void FailPendingResponse(Exception exception)
    {
        PendingResponse? pending;
        lock (_responseLock)
        {
            pending = _pendingResponse;
            _pendingResponse = null;
        }
        pending?.Completion.TrySetException(exception);
    }

    private void RaiseCommandReceived(string? command)
    {
        try
        {
            OnCommandReceived?.Invoke(this, command);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Command event handler failed: {ex.Message}");
        }
    }

    private void RaiseStatusChanged(string status)
    {
        try
        {
            OnStatusChanged?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Status event handler failed: {ex.Message}");
        }
    }

    private static void CloseAndDispose(SerialPort port)
    {
        try
        {
            if (port.IsOpen)
            {
                port.Close();
            }
        }
        catch
        {
        }
        finally
        {
            port.Dispose();
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
            _disposed = true;
        }

        Disconnect();
    }

    private sealed record PendingResponse(
        Func<string, bool> IsSuccess,
        Func<string, bool> IsFailure,
        TaskCompletionSource<string> Completion);
}
