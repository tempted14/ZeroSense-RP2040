using System;
using System.Diagnostics;
using System.Threading;

namespace RainbowRecoil;

internal static class FirmwareHandshake
{
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(400);

    public static void Run(
        ISerialPortTransport port,
        SerialLineReader reader,
        CancellationToken cancellationToken,
        Func<TimeSpan>? elapsed = null)
    {
        var started = Stopwatch.GetTimestamp();
        elapsed ??= () => Stopwatch.GetElapsedTime(started);
        var nextProbe = TimeSpan.Zero;
        var probes = 0;
        port.DiscardInBuffer();

        while (elapsed() < Deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (elapsed() >= nextProbe)
            {
                // PING is read-only and safe to repeat after a lost reply or
                // the board's CDC/DTR startup interval. Never retry START here.
                var ping = SerialProtocol.BuildCommand("PING");
                port.Write(ping, 0, ping.Length);
                ++probes;
                nextProbe = elapsed() + ProbeInterval;
            }
            try
            {
                if (reader.ReadLine(port) == "PONG:RAINBOW-RECOIL:4")
                {
                    DiagnosticLog.Record("serial", $"{port.PortName}: handshake accepted after {probes} probe(s).");
                    return;
                }
            }
            catch (TimeoutException)
            {
                // No complete response yet. The next probe has its own deadline.
            }
        }

        throw new InvalidOperationException(
            $"{port.PortName} opened, but no matching firmware reply arrived after {probes} probes " +
            $"in {Deadline.TotalSeconds:0} seconds. The COM port can remain listed while its CDC interface is stalled.");
    }
}
