using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RainbowRecoil;

/// <summary>
/// Keeps a stalled Windows USB-serial open off the UI thread. A timed-out
/// native open retains the gate until it returns and its port is disposed, so
/// reconnect cannot stack more opens against the same unhealthy COM device.
/// </summary>
internal sealed class SerialPortOpenGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _phase = "idle";
    private long _phaseStartedAt;

    // Called only by the worker that owns the gate. Exposing the phase makes
    // a stuck Windows driver distinguishable from an app retry loop.
    internal void UpdatePhase(string phase)
    {
        Volatile.Write(ref _phase, phase);
        Interlocked.Exchange(ref _phaseStartedAt, Stopwatch.GetTimestamp());
    }

    public async Task OpenAsync(
        Action open,
        Action disposeAbandonedPort,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(disposeAbandonedPort);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        bool acquired;
        try
        {
            acquired = await _gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // This port was never opened and has no worker that could own cleanup.
            _ = Task.Run(disposeAbandonedPort);
            throw;
        }
        if (!acquired)
        {
            // No worker owns this newly constructed port.
            _ = Task.Run(disposeAbandonedPort);
            var startedAt = Interlocked.Read(ref _phaseStartedAt);
            var elapsed = startedAt == 0
                ? ""
                : $" for {Stopwatch.GetElapsedTime(startedAt).TotalSeconds:0} s";
            throw new TimeoutException(
                $"A previous COM-port operation is still {Volatile.Read(ref _phase)}{elapsed}. " +
                "Windows has not released the old session; reconnecting in parallel is unsafe. " +
                "If it remains stuck, unplug and replug the board.");
        }

        UpdatePhase("opening or handshaking");

        Task openTask;
        try
        {
            openTask = Task.Run(open);
        }
        catch
        {
            try
            {
                await Task.Run(disposeAbandonedPort).ConfigureAwait(false);
            }
            finally
            {
                UpdatePhase("idle");
                _gate.Release();
            }
            throw;
        }

        try
        {
            await openTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            UpdatePhase("idle");
            _gate.Release();
        }
        catch
        {
            // If Open() is stuck inside the OS driver, closing this port now
            // could race it. Transfer cleanup ownership to its completion.
            _ = openTask.ContinueWith(
                completed => Task.Run(() =>
                {
                    _ = completed.Exception; // Observe faults that arrive after the timeout.
                    try { disposeAbandonedPort(); }
                    finally
                    {
                        UpdatePhase("idle");
                        _gate.Release();
                    }
                }),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).Unwrap();
            throw;
        }
    }

    public async Task CloseAsync(Action close)
    {
        ArgumentNullException.ThrowIfNull(close);
        await _gate.WaitAsync().ConfigureAwait(false);
        UpdatePhase("closing the old connection");
        try
        {
            // Acquiring the gate happens before the caller starts a reconnect;
            // the potentially stuck USB-driver close itself runs off the UI.
            await Task.Run(close).ConfigureAwait(false);
        }
        finally
        {
            UpdatePhase("idle");
            _gate.Release();
        }
    }
}
