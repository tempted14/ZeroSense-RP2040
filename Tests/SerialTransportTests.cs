using System.Collections.Concurrent;
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
            else ++NonPingWrites;
        }
    }
}
