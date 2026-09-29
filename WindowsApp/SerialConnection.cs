using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace RainbowRecoil;

/// <summary>Thread-safe USB CDC transport for the ZeroSense firmware protocol.</summary>
public sealed class SerialConnection : IRecoilDeviceConnection
{
    private static readonly ConcurrentDictionary<string, SerialPortOpenGate> OpenGates =
        new(StringComparer.OrdinalIgnoreCase);
    // Bounds the native open plus handshake. A stalled operation keeps the
    // per-port gate until cleanup finishes, while the UI-facing await expires.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);

    private readonly string _portName;
    private readonly Func<ISerialPortTransport> _portFactory;
    private readonly object _stateLock = new();
    private readonly object _responseLock = new();
    private readonly SemaphoreSlim _configurationLock = new(1, 1);
    private ISerialPortTransport? _serialPort;
    private CancellationTokenSource? _readCancellation;
    private Task? _readTask;
    private SerialWritePump? _writer;
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

    public SerialConnection(string portName) : this(portName, () => new SerialPortTransport(portName.Trim()))
    {
    }

    internal SerialConnection(string portName, Func<ISerialPortTransport> portFactory)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new ArgumentException("A serial port name is required.", nameof(portName));
        }

        _portName = portName.Trim();
        _portFactory = portFactory ?? throw new ArgumentNullException(nameof(portFactory));
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

        var port = _portFactory();
        var reader = new SerialLineReader();
        var gate = OpenGates.GetOrAdd(_portName, _ => new SerialPortOpenGate());

        var portOwnershipReturned = false;
        try
        {
            await gate.OpenAsync(() =>
                {
                    port.Open();
                    if (cancellationToken.WaitHandle.WaitOne(75))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    FirmwareHandshake.Run(port, reader, cancellationToken);
                }, () => CloseAndDispose(port), ConnectTimeout, cancellationToken)
                .ConfigureAwait(false);
            portOwnershipReturned = true;

            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                var readCancellation = new CancellationTokenSource();
                _serialPort = port;
                _readCancellation = readCancellation;
                _writer = new SerialWritePump(
                    data => port.Write(data, 0, data.Length),
                    () => Interlocked.Increment(ref _commandsSent),
                    error => HandleWriteFailure(port, error));
                _readTask = Task.Run(() => ReadLoop(port, reader, readCancellation.Token));
                _connectedAt = DateTimeOffset.UtcNow;
            }
            RaiseStatusChanged("Connected");
        }
        catch (UnauthorizedAccessException ex)
        {
            if (portOwnershipReturned)
            {
                _ = gate.CloseAsync(() => CloseAndDispose(port));
            }
            throw new InvalidOperationException(
                $"Cannot open {_portName}; another program may already be using it.",
                ex);
        }
        catch
        {
            if (portOwnershipReturned)
            {
                _ = gate.CloseAsync(() => CloseAndDispose(port));
            }
            throw;
        }
    }

    private void ReadLoop(ISerialPortTransport port, SerialLineReader reader, CancellationToken cancellationToken)
    {
        var consecutiveReadFailures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested && port.IsOpen)
            {
                try
                {
                    var rejectedBefore = reader.RejectedLines;
                    var line = reader.ReadLine(port);
                    consecutiveReadFailures = 0;
                    if (reader.RejectedLines != rejectedBefore)
                    {
                        DiagnosticLog.Record("serial", $"{_portName}: discarded a malformed/oversized response line; retaining the connection.");
                    }
                    if (line is not null)
                    {
                        CompletePendingResponse(line);
                        RaiseCommandReceived(line);
                    }
                }
                catch (TimeoutException)
                {
                    // A quiet CDC connection is normal.
                    consecutiveReadFailures = 0;
                }
                catch (Exception ex) when (
                    !cancellationToken.IsCancellationRequested &&
                    SerialReliabilityPolicy.IsTransientReadFailure(ex))
                {
                    ++consecutiveReadFailures;
                    DiagnosticLog.Record("serial-error",
                        $"{_portName}: read failure {consecutiveReadFailures}, {ex.GetType().Name} " +
                        $"(0x{ex.HResult:X8}): {ex.Message}");
                    if (!port.IsOpen ||
                        SerialReliabilityPolicy.ShouldDisconnectAfterReadFailure(
                            consecutiveReadFailures))
                    {
                        throw new IOException(
                            $"{_portName}: CDC read failed {consecutiveReadFailures} times: " +
                            $"{ex.Message} (0x{ex.HResult:X8}).",
                            ex);
                    }

                    if (cancellationToken.WaitHandle.WaitOne(
                        SerialReliabilityPolicy.ReadFailureBackoffMilliseconds))
                    {
                        break;
                    }
                }
            }
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("The device CDC port closed unexpectedly.");
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (MarkConnectionFailed(port))
            {
                RaiseStatusChanged($"Disconnected: {ex.Message}");
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Closing the native port is allowed to interrupt a pending read.
        }
    }

    private bool MarkConnectionFailed(ISerialPortTransport failedPort)
    {
        CancellationTokenSource? cancellation;
        Task? readTask;
        lock (_stateLock)
        {
            if (!ReferenceEquals(_serialPort, failedPort))
            {
                return false;
            }

            _serialPort = null;
            cancellation = _readCancellation;
            readTask = _readTask;
            _readCancellation = null;
            _readTask = null;
            cancellation?.Cancel();
            var writer = _writer;
            _writer = null;
            writer?.Stop();
            QueuePortClose(failedPort, cancellation, readTask, writer, sendStop: false);
        }

        FailPendingResponse(new IOException("The device CDC port was disconnected."));
        return true;
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
        bool generalTimingVarianceEnabled,
        bool deltaNoiseEnabled,
        float firstBulletKickMultiplier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _configurationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var transactionId = unchecked((uint)RandomNumberGenerator.GetInt32(1, int.MaxValue));
        var configurationHash = SerialProtocol.ComputeConfigurationHash(
            profile,
            mode,
            scale,
            rapidFireEnabled,
            rapidFireRoundsPerMinute,
            generalTimingVarianceEnabled,
            deltaNoiseEnabled,
            firstBulletKickMultiplier);
        var transactionStarted = false;
        try
        {
            await SendAndAwaitAsync(
                () => Write(SerialProtocol.BuildConfigurationBeginCommand(
                    transactionId,
                    configurationHash)),
                line => FirmwareContract.ConfigurationBeginAcknowledgementMatches(
                    line,
                    transactionId,
                    configurationHash),
                line => line.StartsWith("ERROR:CONFIG", StringComparison.Ordinal) ||
                    (line.StartsWith("CONFIG:BEGIN:", StringComparison.Ordinal) &&
                     !FirmwareContract.ConfigurationBeginAcknowledgementMatches(
                         line,
                         transactionId,
                         configurationHash)),
                "configuration transaction begin",
                cancellationToken).ConfigureAwait(false);
            transactionStarted = true;

            await SendAndAwaitAsync(
                () => Write(SerialProtocol.BuildProfileCommand(profile, mode)),
                line => FirmwareContract.ProfileAcknowledgementMatches(line, profile, mode),
                line => line.StartsWith("ERROR:PROFILE", StringComparison.Ordinal) ||
                    (line.StartsWith("PROFILE:", StringComparison.Ordinal) &&
                     !FirmwareContract.ProfileAcknowledgementMatches(line, profile, mode)),
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
                    line => FirmwareContract.PatternAcknowledgementMatches(line, profile),
                    line => line.StartsWith("ERROR:PATTERN", StringComparison.Ordinal) ||
                        (line.StartsWith("PATTERN:READY:", StringComparison.Ordinal) &&
                         !FirmwareContract.PatternAcknowledgementMatches(line, profile)),
                    "pattern",
                    cancellationToken).ConfigureAwait(false);
            }

            await SendAndAwaitAsync(
                () => Write(SerialProtocol.BuildSensitivityCommand(scale)),
                line => FirmwareContract.SensitivityAcknowledgementMatches(line, scale),
                line => line.StartsWith("ERROR:SENSITIVITY", StringComparison.Ordinal) ||
                    (line.StartsWith("SENSITIVITY:", StringComparison.Ordinal) &&
                     !FirmwareContract.SensitivityAcknowledgementMatches(line, scale)),
                "sensitivity",
                cancellationToken).ConfigureAwait(false);

            await SendAndAwaitAsync(
                () => Write(SerialProtocol.BuildRapidFireCommand(
                    rapidFireEnabled,
                    rapidFireRoundsPerMinute)),
                line => FirmwareContract.RapidFireAcknowledgementMatches(
                    line,
                    rapidFireEnabled,
                    rapidFireRoundsPerMinute),
                line => line.StartsWith("ERROR:RAPID_FIRE", StringComparison.Ordinal) ||
                    (line.StartsWith("RAPID_FIRE:", StringComparison.Ordinal) &&
                     !FirmwareContract.RapidFireAcknowledgementMatches(
                         line,
                         rapidFireEnabled,
                         rapidFireRoundsPerMinute)),
                "rapid-fire",
                cancellationToken).ConfigureAwait(false);

            await SendAndAwaitAsync(
                () => Write(SerialProtocol.BuildGeneralSettingsCommand(
                    generalTimingVarianceEnabled,
                    deltaNoiseEnabled)),
                line => FirmwareContract.GeneralSettingsAcknowledgementMatches(
                    line,
                    generalTimingVarianceEnabled,
                    deltaNoiseEnabled),
                line => line.StartsWith("ERROR:GENERAL_SETTINGS", StringComparison.Ordinal) ||
                    (line.StartsWith("GENERAL_SETTINGS:", StringComparison.Ordinal) &&
                     !FirmwareContract.GeneralSettingsAcknowledgementMatches(
                         line,
                         generalTimingVarianceEnabled,
                         deltaNoiseEnabled)),
                "general movement settings",
                cancellationToken).ConfigureAwait(false);

            await SendAndAwaitAsync(
                () => Write(SerialProtocol.BuildFirstBulletKickCommand(firstBulletKickMultiplier)),
                line => FirmwareContract.FirstBulletKickAcknowledgementMatches(
                    line, firstBulletKickMultiplier),
                line => line.StartsWith("ERROR:FIRST_BULLET_KICK", StringComparison.Ordinal) ||
                    (line.StartsWith("FIRST_BULLET_KICK:", StringComparison.Ordinal) &&
                     !FirmwareContract.FirstBulletKickAcknowledgementMatches(
                         line, firstBulletKickMultiplier)),
                "first bullet kick",
                cancellationToken).ConfigureAwait(false);

            try
            {
                await SendAndAwaitAsync(
                    () => Write(SerialProtocol.BuildConfigurationCommitCommand(
                        transactionId,
                        configurationHash)),
                    line => FirmwareContract.ConfigurationCommitAcknowledgementMatches(
                        line,
                        transactionId,
                        configurationHash),
                    line => line.StartsWith("ERROR:CONFIG", StringComparison.Ordinal) ||
                        (line.StartsWith("CONFIG:COMMIT:", StringComparison.Ordinal) &&
                         !FirmwareContract.ConfigurationCommitAcknowledgementMatches(
                             line,
                             transactionId,
                             configurationHash)),
                    "configuration transaction commit",
                    cancellationToken).ConfigureAwait(false);
                transactionStarted = false;
            }
            catch (FirmwareResponseTimeoutException commitTimeout)
            {
                // The device may have committed successfully while its acknowledgement
                // was lost. Query the canonical hash before attempting a rollback.
                try
                {
                    await SendAndAwaitAsync(
                        () => Write(SerialProtocol.BuildStatusCommand()),
                        line => FirmwareContract.CommittedStatusMatches(line, configurationHash),
                        line => line.StartsWith("ERROR:STATUS", StringComparison.Ordinal) ||
                            (line.StartsWith("STATUS:", StringComparison.Ordinal) &&
                             !FirmwareContract.CommittedStatusMatches(line, configurationHash)),
                        "configuration commit recovery",
                        cancellationToken).ConfigureAwait(false);
                    transactionStarted = false;
                }
                catch (Exception recoveryException)
                {
                    throw new InvalidOperationException(
                        "The commit acknowledgement was lost and firmware status could not " +
                        "prove that the exact configuration was committed.",
                        new AggregateException(commitTimeout, recoveryException));
                }
            }
        }
        catch
        {
            if (transactionStarted)
            {
                try
                {
                    Write(SerialProtocol.BuildConfigurationAbortCommand(transactionId));
                }
                catch
                {
                    // The firmware also rolls the transaction back on timeout/disconnect.
                }
            }
            throw;
        }
        finally
        {
            _configurationLock.Release();
        }
    }

    public void SendCommand(string commandType) =>
        Write(SerialProtocol.BuildCommand(commandType));

    public void SendArmLease(bool enabled) =>
        Write(SerialProtocol.BuildArmLeaseCommand(enabled));

    public void Write(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        ISerialPortTransport port;
        SerialWritePump writer;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            port = _serialPort ?? throw new InvalidOperationException(
                "The device CDC port is not connected.");
            writer = _writer ?? throw new IOException("The CDC writer is not connected.");
        }

        try
        {
            // This may run on the WinUI dispatcher. Only enqueue here; never
            // enter a native Write or wait behind a writer blocked in Windows.
            // Configuration success still requires exact firmware readback.
            writer.Enqueue(data);
        }
        catch (Exception ex)
        {
            HandleWriteFailure(port, ex);
            throw;
        }
    }

    private void HandleWriteFailure(ISerialPortTransport port, Exception error)
    {
        if (!MarkConnectionFailed(port)) return;
        Interlocked.Increment(ref _failedCommands);
        DiagnosticLog.Record("serial-error",
            $"{_portName}: write failed, {error.GetType().Name} (0x{error.HResult:X8}): {error.Message}");
        RaiseStatusChanged($"Disconnected: {error.Message}");
    }

    public void Disconnect()
    {
        ISerialPortTransport? port;
        CancellationTokenSource? cancellation;
        Task? readTask;
        lock (_stateLock)
        {
            port = _serialPort;
            cancellation = _readCancellation;
            readTask = _readTask;
            _serialPort = null;
            _readCancellation = null;
            _readTask = null;
            cancellation?.Cancel();
            var writer = _writer;
            _writer = null;
            writer?.Stop();
            if (port is not null)
            {
                // Reserve cleanup before exposing a disconnected session.
                QueuePortClose(port, cancellation, readTask, writer, sendStop: true);
            }
        }

        if (port is null)
        {
            cancellation?.Dispose();
            return;
        }

        FailPendingResponse(new IOException("The device CDC port was disconnected."));
        RaiseStatusChanged("Disconnected");
    }

    private void QueuePortClose(
        ISerialPortTransport port, CancellationTokenSource? cancellation,
        Task? readTask, SerialWritePump? writer, bool sendStop)
    {
        // Windows can stall both Write() and Close() after a USB/CDC fault.
        // Neither may block the WinUI dispatcher during disconnect/reconnect.
        var gate = OpenGates.GetOrAdd(_portName, _ => new SerialPortOpenGate());
        _ = gate.CloseAsync(() =>
        {
            Task? stopWrite = null;
            try
            {
                if (!sendStop && port.IsOpen)
                {
                    gate.UpdatePhase("aborting the old transmit queue");
                    // A failed CDC write may leave a pending operation in the
                    // Windows transmit queue. Abort it before Close, which can
                    // otherwise wait for that operation indefinitely.
                    try { port.DiscardOutBuffer(); }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Record("serial-cleanup",
                            $"{_portName}: transmit purge failed: {ex.Message}");
                    }
                }
                // Only send a best-effort STOP after the single writer exits.
                // If it is stuck, close now to interrupt it; DTR and the device
                // watchdog stop output without queueing more driver writes.
                if (sendStop && (writer is null || writer.Completion.Wait(100)) && port.IsOpen)
                {
                    gate.UpdatePhase("sending the final STOP");
                    var stop = SerialProtocol.BuildCommand("STOP");
                    stopWrite = Task.Run(() => port.Write(stop, 0, stop.Length));
                    // Even the final safety packet can stall in the driver.
                    // Still attempt Close instead of waiting forever to do so.
                    stopWrite.Wait(SerialWritePump.WriteDeadline);
                }
            }
            catch
            {
                // The device may already have been unplugged; its own output
                // watchdog also expires if the STOP packet cannot be delivered.
            }
            finally
            {
                try
                {
                    gate.UpdatePhase("closing the old CDC handle");
                    DiagnosticLog.Record("serial-cleanup", $"{_portName}: closing old CDC handle.");
                    CloseAndDispose(port);
                    DiagnosticLog.Record("serial-cleanup", $"{_portName}: old CDC handle disposed.");
                }
                finally
                {
                    try
                    {
                        gate.UpdatePhase("waiting for the old reader");
                        // The old reader must exit before a new session on this
                        // COM port can consume responses. This runs off the UI.
                        readTask?.GetAwaiter().GetResult();
                    }
                    catch
                    {
                        // Native Close can abort the old reader with an I/O error.
                    }
                    finally
                    {
                        try
                        {
                            gate.UpdatePhase("waiting for the old writer");
                            writer?.Completion.GetAwaiter().GetResult();
                        }
                        catch { /* Write failures already raised the disconnect. */ }
                        finally
                        {
                            try
                            {
                                gate.UpdatePhase("waiting for the final STOP");
                                stopWrite?.GetAwaiter().GetResult();
                            }
                            catch { /* Close may interrupt the best-effort STOP. */ }
                            finally { cancellation?.Dispose(); }
                        }
                    }
                }
            }
        });
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
            string? response = null;
            TimeoutException? lastTimeout = null;
            for (var attempt = 1;
                 attempt <= SerialReliabilityPolicy.MaximumResponseAttempts;
                 ++attempt)
            {
                send();
                try
                {
                    response = await completion.Task
                        .WaitAsync(TimeSpan.FromSeconds(2.5), cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }
                catch (TimeoutException ex)
                {
                    lastTimeout = ex;
                    Interlocked.Increment(ref _failedCommands);
                }
            }

            if (response is null)
            {
                throw new FirmwareResponseTimeoutException(
                    $"The firmware did not acknowledge the {stage} configuration after " +
                    $"{SerialReliabilityPolicy.MaximumResponseAttempts} attempts.",
                    lastTimeout ?? new TimeoutException());
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

    private static void CloseAndDispose(ISerialPortTransport port)
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
            try
            {
                port.Dispose();
            }
            catch
            {
                // USB removal can invalidate the native handle mid-dispose.
            }
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

    private sealed class FirmwareResponseTimeoutException : InvalidOperationException
    {
        public FirmwareResponseTimeoutException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
