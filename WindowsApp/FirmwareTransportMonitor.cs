using System;
using System.Globalization;

namespace RainbowRecoil;

internal readonly record struct TransportSnapshot(
    string Summary, bool Warning, double? AcceptedReportsPerSecond = null,
    double? EmptyCallbacksPerSecond = null, double? HidReportsPerSecond = null);

/// <summary>
/// Observational only: never arms, resets or disconnects the device. Rates are
/// derived from two STATUS snapshots, not a measurement of Windows USB polling.
/// </summary>
internal sealed class FirmwareTransportMonitor
{
    private FirmwareStatusUpdate? _previous;
    private double _previousSeconds;

    public void Reset() => _previous = null;

    public TransportSnapshot Observe(FirmwareStatusUpdate current, double seconds, bool mouseProxy)
    {
        if (current.Kind != FirmwareStatusKind.TransportMetrics || !double.IsFinite(seconds))
            throw new ArgumentException("A metrics snapshot and finite monotonic time are required.");

        bool stalled = mouseProxy && current.HostTaskAgeMs > 500;
        var previous = _previous;
        double elapsed = seconds - _previousSeconds;
        // Reconnect/reboot, clock reset or long gaps start a fresh baseline.
        // Counter wrap also rebaselines instead of displaying billions of Hz.
        bool comparable = previous is { } last && elapsed >= 0.25 && elapsed <= 30 &&
            current.HostReportsReceived >= last.HostReportsReceived &&
            current.HostDecodeErrors >= last.HostDecodeErrors &&
            current.HostEmptyReports >= last.HostEmptyReports &&
            current.HidReportsSent >= last.HidReportsSent;

        if (!comparable)
        {
            if (previous is null || elapsed >= 0.25 || elapsed < 0)
            {
                _previous = current;
                _previousSeconds = seconds;
            }
            return new(stalled
                ? "Mouse USB task stalled; the board's serial connection may still respond. Copy diagnostics before reconnecting."
                : "Collecting USB rate samples. Move the mouse continuously to check input delivery.", stalled);
        }

        var prior = previous!.Value;
        _previous = current;
        _previousSeconds = seconds;
        double received = current.HostReportsReceived - prior.HostReportsReceived;
        double rejected = current.HostDecodeErrors - prior.HostDecodeErrors;
        // Counters are read separately across cores; avoid negative rates from
        // a boundary-racing snapshot. They are diagnostics, not precise traces.
        double acceptedRate = Math.Max(0, received - rejected) / elapsed;
        double emptyRate = (current.HostEmptyReports - prior.HostEmptyReports) / elapsed;
        double hidRate = (current.HidReportsSent - prior.HidReportsSent) / elapsed;
        bool faults = rejected > 0 || current.PioTxTimeouts > prior.PioTxTimeouts ||
            current.PioRxFlagTimeouts > prior.PioRxFlagTimeouts ||
            current.PioRxPacketTimeouts > prior.PioRxPacketTimeouts ||
            current.PioRxOversize > prior.PioRxOversize ||
            current.HostReceiveQueueFailures > prior.HostReceiveQueueFailures ||
            current.HostMouseUnmounts > prior.HostMouseUnmounts ||
            current.HostAccumulatorSaturations > prior.HostAccumulatorSaturations ||
            current.UpstreamDisconnectStops > prior.UpstreamDisconnectStops ||
            current.CdcDroppedMessages > prior.CdcDroppedMessages ||
            current.CurrentQueuedDelta > 127 ||
            current.GeneralIntervalClamps > prior.GeneralIntervalClamps;
        string state = stalled ? "Mouse USB task stalled" : faults ? "USB/input warning in this sample" :
            "No new USB/input faults in this sample";
        string rates = mouseProxy
            ? string.Create(CultureInfo.InvariantCulture,
                $"Approx. {acceptedRate:F0} accepted mouse reports/s; {emptyRate:F0} empty callbacks/s; {hidRate:F0} outgoing HID reports/s.")
            : string.Create(CultureInfo.InvariantCulture, $"Approx. {hidRate:F0} outgoing HID reports/s.");
        string idle = mouseProxy && received == 0 && !stalled
            ? " No input reports can mean the mouse is idle; move it to check." : "";
        return new($"{state}. {rates}{idle}", stalled || faults,
            acceptedRate, emptyRate, hidRate);
    }
}
