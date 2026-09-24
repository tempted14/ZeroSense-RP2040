using System;
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

        if (!await _gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            // No worker owns this newly constructed port.
            await Task.Run(disposeAbandonedPort).ConfigureAwait(false);
            throw new TimeoutException("A previous COM-port open is still stalled.");
        }

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
                _gate.Release();
            }
            throw;
        }

        try
        {
            await openTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            _gate.Release();
        }
        catch
        {
            // If Open() is stuck inside the OS driver, closing this port now
            // could race it. Transfer cleanup ownership to its completion.
            _ = openTask.ContinueWith(
                _ => Task.Run(() =>
                {
                    try { disposeAbandonedPort(); }
                    finally { _gate.Release(); }
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
        try
        {
            // Acquiring the gate happens before the caller starts a reconnect;
            // the potentially stuck USB-driver close itself runs off the UI.
            await Task.Run(close).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
