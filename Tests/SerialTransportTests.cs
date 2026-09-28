using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using RainbowRecoil;

internal static class SerialTransportTests
{
    public static void HandshakeRecoversLostReply()
    {
        using var port = new FakePort("lost-first-probe") { ReplyAfterProbe = 2 };
        var elapsed = TimeSpan.Zero;
        port.OnEmptyRead = () => elapsed += TimeSpan.FromMilliseconds(100);
        port.Open();
        FirmwareHandshake.Run(port, new SerialLineReader(), CancellationToken.None, () => elapsed);
        Check(port.Probes == 2, "lost first handshake reply must cause a second PING");
        Check(port.NonPingWrites == 0, "handshake must never replay output commands");

        using var wrong = new FakePort("wrong-device") { ReplyText = "PONG:OTHER-FIRMWARE:4\n" };
        elapsed = TimeSpan.Zero;
        wrong.OnEmptyRead = () => elapsed += TimeSpan.FromMilliseconds(100);
        wrong.Open();
        try
        {
            FirmwareHandshake.Run(wrong, new SerialLineReader(), CancellationToken.None, () => elapsed);
            throw new Exception("wrong firmware was accepted");
        }
        catch (InvalidOperationException)
        {
            Check(elapsed >= FirmwareHandshake.Deadline, "handshake has a bounded deadline");
        }
    }

    public static void LineFramingRecoversCorruption()
    {
        using var port = new FakePort("framing");
        var reader = new SerialLineReader();
        port.Input.Enqueue(Encoding.UTF8.GetBytes("PROFILE:Tubar"));
        Check(reader.ReadLine(port) is null, "partial response is retained");
        try { reader.ReadLine(port); }
        catch (TimeoutException) { }
        port.Input.Enqueue(Encoding.UTF8.GetBytes("ão\r\n"));
        Check(reader.ReadLine(port) == "PROFILE:Tubarão", "partial line survives a read timeout");
        port.Input.Enqueue(new byte[] { 0xFF, 0xFE, (byte)'\n' });
        Check(reader.ReadLine(port) is null && reader.RejectedLines == 1,
            "invalid UTF-8 is discarded as a whole line");
        reader.Feed(Encoding.ASCII.GetBytes(new string('x', SerialLineReader.MaximumLineBytes + 1) + "\n"));
        port.Input.Enqueue(Encoding.ASCII.GetBytes("PONG:RAINBOW-RECOIL:4\n"));
        Check(reader.ReadLine(port) == "PONG:RAINBOW-RECOIL:4" && reader.RejectedLines == 2,
            "oversized line does not prevent the next valid acknowledgement");
    }

    public static void FailedReaderClosesBeforeReconnect()
    {
        var name = "fault-test-" + Guid.NewGuid();
        using var oldPort = new FakePort(name);
        using var newPort = new FakePort(name);
        using var releaseClose = new ManualResetEventSlim();
        using var closing = new ManualResetEventSlim();
        using var disconnected = new ManualResetEventSlim();
        using var response = new ManualResetEventSlim();
        oldPort.OnClose = () => { closing.Set(); releaseClose.Wait(); };
        using var oldConnection = new SerialConnection(name, () => oldPort);
        using var newConnection = new SerialConnection(name, () => newPort);
        oldConnection.OnStatusChanged += (_, status) =>
        {
            if (status.StartsWith("Disconnected", StringComparison.Ordinal)) disconnected.Set();
        };
        oldConnection.OnCommandReceived += (_, line) =>
        {
            if (line == "TEST:ALIVE") response.Set();
        };
        try
        {
            oldConnection.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            oldPort.Input.Enqueue(new byte[] { 0xFF, (byte)'\n' });
            oldPort.Input.Enqueue(Encoding.ASCII.GetBytes("TEST:ALIVE\n"));
            Check(response.Wait(TimeSpan.FromSeconds(2)), "real connection continues after a malformed line");
            Check(oldConnection.IsConnected, "malformed line must not disconnect the COM session");

            oldPort.FailReads = true;
            Check(disconnected.Wait(TimeSpan.FromSeconds(2)), "read failure is reported before native Close returns");
            Check(closing.Wait(TimeSpan.FromSeconds(2)), "faulted port cleanup was reserved");
            var reconnect = newConnection.ConnectAsync(CancellationToken.None);
            Thread.Sleep(100);
            Check(newPort.OpenCalls == 0, "reconnect must not open while the old native Close is stalled");
            releaseClose.Set();
            reconnect.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Check(oldPort.Disposed && newConnection.IsConnected,
                "reconnect succeeds only after the old port is disposed");
        }
        finally
        {
            releaseClose.Set();
            // Wait for background cleanup before disposing the test's events.
            oldConnection.Disconnect();
            newConnection.Disconnect();
            SpinWait.SpinUntil(() => oldPort.Disposed && newPort.Disposed, TimeSpan.FromSeconds(2));
        }
    }

    public static void FailedHandshakeCleanupIsBounded()
    {
        var name = "failed-handshake-" + Guid.NewGuid();
        using var oldPort = new FakePort(name) { FailPing = true };
        using var newPort = new FakePort(name);
        using var releaseClose = new ManualResetEventSlim();
        using var closing = new ManualResetEventSlim();
        oldPort.OnClose = () => { closing.Set(); releaseClose.Wait(); };
        using var failed = new SerialConnection(name, () => oldPort);
        using var replacement = new SerialConnection(name, () => newPort);
        try
        {
            try
            {
                failed.ConnectAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                throw new Exception("injected handshake failure was ignored");
            }
            catch (IOException)
            {
                // The failure is returned without waiting on the native Close.
            }
            Check(closing.Wait(TimeSpan.FromSeconds(2)), "failed handshake owns cleanup");
            var reconnect = replacement.ConnectAsync(CancellationToken.None);
            Thread.Sleep(100);
            Check(newPort.OpenCalls == 0, "failed handshake cleanup blocks overlapping opens");
            releaseClose.Set();
            reconnect.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Check(oldPort.Disposed && replacement.IsConnected, "replacement owns a clean session");
        }
        finally
        {
            releaseClose.Set();
            replacement.Disconnect();
            SpinWait.SpinUntil(() => oldPort.Disposed && newPort.Disposed, TimeSpan.FromSeconds(2));
        }
    }

    public static void StalledWriteDoesNotBlockOrReplay()
    {
        var name = "stalled-write-" + Guid.NewGuid();
        using var oldPort = new FakePort(name);
        using var newPort = new FakePort(name);
        using var writing = new ManualResetEventSlim();
        using var releaseWrite = new ManualResetEventSlim();
        using var disconnected = new ManualResetEventSlim();
        oldPort.OnCommandWrite = _ => { writing.Set(); releaseWrite.Wait(); };
        using var connection = new SerialConnection(name, () => oldPort);
        using var replacement = new SerialConnection(name, () => newPort);
        connection.OnStatusChanged += (_, status) =>
        {
            if (status.StartsWith("Disconnected", StringComparison.Ordinal)) disconnected.Set();
        };
        try
        {
            connection.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            Task.Run(() => connection.SendCommand("STATUS"))
                .WaitAsync(TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult();
            Check(writing.Wait(TimeSpan.FromSeconds(2)), "native write fault was reached");
            connection.SendCommand("START");
            connection.SendCommand("KEEPALIVE");
            connection.SendCommand("STOP");
            Check(connection.GetMetrics().CommandsSent == 0, "enqueue is not counted as a completed write");
            Check(disconnected.Wait(TimeSpan.FromSeconds(3)), "stalled write reports failure without waiting for the driver");
            Check(!connection.IsConnected, "stalled writer invalidates the session even if COM remains listed");

            var reconnect = replacement.ConnectAsync(CancellationToken.None);
            Thread.Sleep(100);
            Check(newPort.OpenCalls == 0, "timeout alone cannot release native write ownership");
            releaseWrite.Set();
            reconnect.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Check(oldPort.NonPingWrites == 1, "queued START/KEEPALIVE/STOP must not replay after a stall");
            Check(replacement.IsConnected, "reconnect succeeds after the old writer actually finishes");
        }
        finally
        {
            releaseWrite.Set();
            connection.Disconnect();
            replacement.Disconnect();
            SpinWait.SpinUntil(() => oldPort.Disposed && newPort.Disposed, TimeSpan.FromSeconds(2));
        }
    }

    public static void WriteQueueIsBoundedAndOrdered()
    {
        using var writing = new ManualResetEventSlim();
        using var releaseWrite = new ManualResetEventSlim();
        var sent = new ConcurrentQueue<byte>();
        Exception? failure = null;
        var writer = new SerialWritePump(bytes =>
        {
            if (bytes[0] == 0) { writing.Set(); releaseWrite.Wait(); }
            sent.Enqueue(bytes[0]);
        }, () => { }, error => failure = error);
        try
        {
            writer.Enqueue([0]);
            Check(writing.Wait(TimeSpan.FromSeconds(2)), "writer starts off the caller thread");
            // More than the maximum 11 pattern chunks; preserve every frame.
            for (byte value = 1; value <= SerialWritePump.Capacity; ++value)
            {
                byte[] packet = [value];
                writer.Enqueue(packet);
                packet[0] = 255; // Enqueue must snapshot caller-owned bytes.
            }
            try { writer.Enqueue([200]); throw new Exception("full queue silently accepted a frame"); }
            catch (IOException) { }
            releaseWrite.Set();
            Check(SpinWait.SpinUntil(() => sent.Count == SerialWritePump.Capacity + 1,
                TimeSpan.FromSeconds(2)), "all accepted frames are written");
            Check(sent.SequenceEqual(Enumerable.Range(0, SerialWritePump.Capacity + 1).Select(x => (byte)x)),
                "packet order and copied bytes survive background writes");
            Check(failure is null, "healthy writes do not report a fault");
        }
        finally
        {
            releaseWrite.Set();
            writer.Stop();
            writer.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
    }

    public static void StaleWriteQueueFailsClosed()
    {
        using var writing = new ManualResetEventSlim();
        using var releaseWrite = new ManualResetEventSlim();
        using var failed = new ManualResetEventSlim();
        long now = 0;
        var sent = 0;
        var writer = new SerialWritePump(_ =>
        {
            Interlocked.Increment(ref sent);
            writing.Set();
            releaseWrite.Wait();
        }, () => { }, _ => failed.Set(), () => Interlocked.Read(ref now));
        try
        {
            writer.Enqueue([1]);
            Check(writing.Wait(TimeSpan.FromSeconds(2)), "first command entered the driver");
            writer.Enqueue([2]);
            Interlocked.Exchange(ref now, Stopwatch.Frequency); // One second in the fake clock.
            releaseWrite.Set();
            Check(failed.Wait(TimeSpan.FromSeconds(2)), "stale queued command faults the transport");
            writer.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            Check(sent == 1, "stale command never enters the driver");
        }
        finally
        {
            releaseWrite.Set();
            writer.Stop();
            writer.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
    }

    public static void DisconnectDiscardsPendingOutput()
    {
        var name = "cancel-write-" + Guid.NewGuid();
        using var port = new FakePort(name);
        using var writing = new ManualResetEventSlim();
        using var releaseWrite = new ManualResetEventSlim();
        port.OnCommandWrite = _ => { writing.Set(); releaseWrite.Wait(); };
        using var connection = new SerialConnection(name, () => port);
        try
        {
            connection.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            connection.SendCommand("STATUS");
            Check(writing.Wait(TimeSpan.FromSeconds(2)), "first command entered the driver");
            connection.SendCommand("START");
            Task.Run(connection.Disconnect).WaitAsync(TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult();
            Check(!connection.IsConnected, "disconnect returns while a native write is blocked");
            Check(SpinWait.SpinUntil(() => port.Disposed, TimeSpan.FromSeconds(2)), "close tries to interrupt the hung write");
            releaseWrite.Set();
            Thread.Sleep(100);
            Check(port.NonPingWrites == 1, "disconnect discarded queued START without a competing STOP write");
        }
        finally { releaseWrite.Set(); connection.Disconnect(); }
    }

    public static void StalledFinalStopStillClosesPort()
    {
        var name = "stalled-stop-" + Guid.NewGuid();
        using var port = new FakePort(name);
        using var writing = new ManualResetEventSlim();
        using var releaseWrite = new ManualResetEventSlim();
        port.OnCommandWrite = _ => { writing.Set(); releaseWrite.Wait(); };
        using var connection = new SerialConnection(name, () => port);
        try
        {
            connection.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            Task.Run(connection.Disconnect).WaitAsync(TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult();
            Check(writing.Wait(TimeSpan.FromSeconds(2)), "best-effort final STOP was sent");
            Check(SpinWait.SpinUntil(() => port.Disposed, TimeSpan.FromSeconds(3)),
                "stalled final STOP must not prevent attempting native close");
        }
        finally { releaseWrite.Set(); connection.Disconnect(); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class FakePort(string name) : ISerialPortTransport
    {
        public readonly ConcurrentQueue<byte[]> Input = new();
        private byte[]? _pending;
        private int _offset;
        public int OpenCalls;
        public int Probes;
        public int NonPingWrites;
        public int ReplyAfterProbe = 1;
        public string ReplyText = "PONG:RAINBOW-RECOIL:4\n";
        public Action? OnClose;
        public Action? OnEmptyRead;
        public Action<byte>? OnCommandWrite;
        public volatile bool FailReads;
        public bool FailPing;
        public volatile bool Disposed;
        private volatile bool _open;
        public string PortName => name;
        public bool IsOpen => _open;
        public void Open() { Interlocked.Increment(ref OpenCalls); _open = true; }
        public void Close() { OnClose?.Invoke(); _open = false; }
        public void Dispose() { Disposed = true; _open = false; }
        public void DiscardInBuffer() { }
        public int Read(byte[] buffer, int offset, int count)
        {
            if (FailReads) throw new IOException("Injected CDC read failure");
            if (_pending is null && !Input.TryDequeue(out _pending))
            {
                if (OnEmptyRead is not null) OnEmptyRead();
                else Thread.Sleep(5);
                throw new TimeoutException();
            }
            var take = Math.Min(count, _pending.Length - _offset);
            Array.Copy(_pending, _offset, buffer, offset, take);
            _offset += take;
            if (_offset == _pending.Length) { _pending = null; _offset = 0; }
            return take;
        }
        public void Write(byte[] buffer, int offset, int count)
        {
            if (buffer[offset + 5] == 0xF0)
            {
                if (FailPing) throw new IOException("Injected handshake write failure");
                if (++Probes >= ReplyAfterProbe) Input.Enqueue(Encoding.UTF8.GetBytes(ReplyText));
            }
            else
            {
                Interlocked.Increment(ref NonPingWrites);
                OnCommandWrite?.Invoke(buffer[offset + 5]);
            }
        }
    }
}
