using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RainbowRecoil;

/// <summary>
/// One ordered, bounded writer per session. Enqueue never enters the USB driver.
/// Completion includes any abandoned native write, not merely its timeout, so
/// a reconnect cannot overlap a driver operation from the previous session.
/// </summary>
internal sealed class SerialWritePump
{
    internal const int Capacity = 32;
    internal static readonly TimeSpan WriteDeadline = TimeSpan.FromMilliseconds(750);
    internal static readonly TimeSpan MaximumQueueAge = TimeSpan.FromMilliseconds(500);
    private readonly Channel<Packet> _queue = Channel.CreateBounded<Packet>(
        new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    private readonly CancellationTokenSource _stop = new();
    private readonly object _stopLock = new();
    private readonly Action<byte[]> _write;
    private readonly Action _written;
    private readonly Action<Exception> _failed;
    private readonly Func<long> _timestamp;
    private int _stopped;

    public SerialWritePump(Action<byte[]> write, Action written, Action<Exception> failed,
        Func<long>? timestamp = null)
    {
        _write = write;
        _written = written;
        _failed = failed;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        Completion = Task.Run(RunAsync);
    }

    public Task Completion { get; }

    public void Enqueue(byte[] bytes)
    {
        if (Volatile.Read(ref _stopped) != 0 ||
            !_queue.Writer.TryWrite(new Packet((byte[])bytes.Clone(), _timestamp())))
        {
            throw new IOException("The CDC write queue is full or closed; reconnect before sending more commands.");
        }
    }

    public void Stop()
    {
        lock (_stopLock)
        {
            if (_stopped == 0)
            {
                Volatile.Write(ref _stopped, 1);
                _queue.Writer.TryComplete();
                _stop.Cancel();
            }
        }
    }

    private async Task RunAsync()
    {
        Task? nativeWrite = null;
        var token = _stop.Token;
        try
        {
            while (await _queue.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var packet))
                {
                    token.ThrowIfCancellationRequested();
                    nativeWrite = Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        // Check immediately before driver entry, including time
                        // spent waiting for a thread-pool worker under load.
                        if (Stopwatch.GetElapsedTime(packet.EnqueuedAt, _timestamp()) >= MaximumQueueAge)
                        {
                            throw new IOException("CDC commands became stale in the write queue; pending output was discarded.");
                        }
                        _write(packet.Bytes);
                    });
                    await nativeWrite.WaitAsync(WriteDeadline, token).ConfigureAwait(false);
                    nativeWrite = null;
                    _written();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Disconnect discards queued START/KEEPALIVE/lease packets.
        }
        catch (Exception error)
        {
            Stop();
            _failed(error is TimeoutException
                ? new IOException("The CDC write stalled for 750 ms; the session was stopped.", error)
                : error);
        }
        finally
        {
            Stop();
            while (_queue.Reader.TryRead(out _)) { }
            // WaitAsync does NOT cancel a synchronous driver call. Cleanup may
            // close the port to interrupt it, but must retain ownership until
            // the real call returns. Never start a second writer after timeout.
            if (nativeWrite is not null)
            {
                try { await nativeWrite.ConfigureAwait(false); }
                catch { /* The failure was already reported or close aborted it. */ }
            }
            lock (_stopLock) { _stop.Dispose(); }
        }
    }

    private sealed record Packet(byte[] Bytes, long EnqueuedAt);
}
