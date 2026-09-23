using System;
using System.Globalization;

namespace RainbowRecoil;

public enum FirmwareStatusKind
{
    Rp2040Device,
    Rp2350MouseProxy,
    MouseConnected,
    MouseDisconnected,
    MouseUnsupported,
    MouseHostError,
    TransportMetrics
}

public readonly record struct FirmwareStatusUpdate(
    FirmwareStatusKind Kind,
    ushort VendorId = 0,
    ushort ProductId = 0,
    uint HidReportsSent = 0,
    uint HidBusyDeferrals = 0,
    uint MaximumQueuedDelta = 0,
    uint MaximumActiveReportGapUs = 0,
    uint HostReportsReceived = 0,
    uint HostDecodeErrors = 0,
    uint HostAccumulatorSaturations = 0,
    uint UpstreamDisconnectStops = 0,
    uint CorrectionDelayedFrames = 0,
    uint MaximumCorrectionLatenessUs = 0,
    uint CurrentQueuedDelta = 0,
    uint CurrentReportIntervalUs = 0,
    uint GeneralIntervalClamps = 0,
    uint HostReceiveRecoveries = 0,
    uint HostReceiveQueueFailures = 0,
    uint HostMouseUnmounts = 0,
    uint HostTaskAgeMs = 0,
    uint CdcDroppedMessages = 0,
    uint PioTxTimeouts = 0,
    uint PioRxFlagTimeouts = 0,
    uint PioRxPacketTimeouts = 0,
    uint PioSe0Glitches = 0);

/// <summary>Parses asynchronous hardware identity and RP2350 mouse-host status lines.</summary>
public static class FirmwareStatusParser
{
    public static bool TryParse(string? message, out FirmwareStatusUpdate update)
    {
        update = default;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var value = message.Trim();
        if (TryParseMetrics(value, out update))
        {
            return true;
        }
        if (value.Equals("DEVICE:RP2040-ZERO:CDC+HID", StringComparison.Ordinal))
        {
            update = new(FirmwareStatusKind.Rp2040Device);
            return true;
        }
        if (value.Equals("DEVICE:RP2350-USB-C:MOUSE-PROXY", StringComparison.Ordinal))
        {
            update = new(FirmwareStatusKind.Rp2350MouseProxy);
            return true;
        }
        if (value.Equals("MOUSE:DISCONNECTED", StringComparison.Ordinal))
        {
            update = new(FirmwareStatusKind.MouseDisconnected);
            return true;
        }
        if (value.StartsWith("MOUSE:UNSUPPORTED:", StringComparison.Ordinal))
        {
            update = new(FirmwareStatusKind.MouseUnsupported);
            return true;
        }
        if (value.Equals("MOUSE:HOST_ERROR", StringComparison.Ordinal))
        {
            update = new(FirmwareStatusKind.MouseHostError);
            return true;
        }

        const string connectedPrefix = "MOUSE:CONNECTED:VID=";
        const string productMarker = ":PID=";
        if (!value.StartsWith(connectedPrefix, StringComparison.Ordinal))
        {
            return false;
        }
        var productMarkerIndex = value.IndexOf(productMarker, connectedPrefix.Length, StringComparison.Ordinal);
        if (productMarkerIndex < 0 ||
            !ushort.TryParse(
                value.AsSpan(connectedPrefix.Length, productMarkerIndex - connectedPrefix.Length),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var vendorId) ||
            !ushort.TryParse(
                value.AsSpan(productMarkerIndex + productMarker.Length),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var productId))
        {
            return false;
        }

        update = new(FirmwareStatusKind.MouseConnected, vendorId, productId);
        return true;
    }

    private static bool TryParseMetrics(string value, out FirmwareStatusUpdate update)
    {
        update = default;
        var fields = value.Split(':', StringSplitOptions.None);
        var upstreamDisconnectStops = 0u;
        var correctionDelayedFrames = 0u;
        var maximumCorrectionLatenessUs = 0u;
        var currentQueuedDelta = 0u;
        var currentReportIntervalUs = 0u;
        var generalIntervalClamps = 0u;
        var hostReceiveRecoveries = 0u;
        var hostReceiveQueueFailures = 0u;
        var hostMouseUnmounts = 0u;
        var hostTaskAgeMs = 0u;
        var cdcDroppedMessages = 0u;
        var pioTxTimeouts = 0u;
        var pioRxFlagTimeouts = 0u;
        var pioRxPacketTimeouts = 0u;
        var pioSe0Glitches = 0u;
        if (fields.Length is not (8 or 9 or 14 or 15 or 19 or 23) || fields[0] != "METRICS" ||
            !TryParseMetric(fields[1], "HID_SENT", out var hidReportsSent) ||
            !TryParseMetric(fields[2], "HID_BUSY", out var hidBusyDeferrals) ||
            !TryParseMetric(fields[3], "MAX_QUEUE", out var maximumQueuedDelta) ||
            !TryParseMetric(fields[4], "MAX_ACTIVE_GAP_US", out var maximumActiveReportGapUs) ||
            !TryParseMetric(fields[5], "HOST_REPORTS", out var hostReportsReceived) ||
            !TryParseMetric(fields[6], "HOST_DECODE_ERRORS", out var hostDecodeErrors) ||
            !TryParseMetric(fields[7], "HOST_SATURATIONS", out var hostAccumulatorSaturations) ||
            (fields.Length >= 9 &&
             !TryParseMetric(fields[8], "USB_STOPS", out upstreamDisconnectStops)) ||
            (fields.Length >= 14 &&
             (!TryParseMetric(fields[9], "CORRECTION_LATE", out correctionDelayedFrames) ||
              !TryParseMetric(
                  fields[10],
                  "MAX_CORRECTION_LATE_US",
                  out maximumCorrectionLatenessUs) ||
              !TryParseMetric(fields[11], "QUEUE", out currentQueuedDelta) ||
              !TryParseMetric(fields[12], "REPORT_INTERVAL_US", out currentReportIntervalUs) ||
              !TryParseMetric(fields[13], "GENERAL_DT_CLAMPS", out generalIntervalClamps))) ||
            (fields.Length >= 15 &&
             !TryParseMetric(fields[14], "HOST_RECOVERIES", out hostReceiveRecoveries)) ||
            (fields.Length >= 19 &&
             (!TryParseMetric(fields[15], "HOST_QUEUE_FAILURES", out hostReceiveQueueFailures) ||
              !TryParseMetric(fields[16], "HOST_UNMOUNTS", out hostMouseUnmounts) ||
              !TryParseMetric(fields[17], "HOST_TASK_AGE_MS", out hostTaskAgeMs) ||
              !TryParseMetric(fields[18], "CDC_DROPPED", out cdcDroppedMessages))) ||
            (fields.Length == 23 &&
             (!TryParseMetric(fields[19], "PIO_TX_TIMEOUTS", out pioTxTimeouts) ||
              !TryParseMetric(fields[20], "PIO_RX_FLAG_TIMEOUTS", out pioRxFlagTimeouts) ||
              !TryParseMetric(fields[21], "PIO_RX_PACKET_TIMEOUTS", out pioRxPacketTimeouts) ||
              !TryParseMetric(fields[22], "PIO_SE0_GLITCHES", out pioSe0Glitches))))
        {
            return false;
        }

        update = new FirmwareStatusUpdate(
            FirmwareStatusKind.TransportMetrics,
            HidReportsSent: hidReportsSent,
            HidBusyDeferrals: hidBusyDeferrals,
            MaximumQueuedDelta: maximumQueuedDelta,
            MaximumActiveReportGapUs: maximumActiveReportGapUs,
            HostReportsReceived: hostReportsReceived,
            HostDecodeErrors: hostDecodeErrors,
            HostAccumulatorSaturations: hostAccumulatorSaturations,
            UpstreamDisconnectStops: upstreamDisconnectStops,
            CorrectionDelayedFrames: correctionDelayedFrames,
            MaximumCorrectionLatenessUs: maximumCorrectionLatenessUs,
            CurrentQueuedDelta: currentQueuedDelta,
            CurrentReportIntervalUs: currentReportIntervalUs,
            GeneralIntervalClamps: generalIntervalClamps,
            HostReceiveRecoveries: hostReceiveRecoveries,
            HostReceiveQueueFailures: hostReceiveQueueFailures,
            HostMouseUnmounts: hostMouseUnmounts,
            HostTaskAgeMs: hostTaskAgeMs,
            CdcDroppedMessages: cdcDroppedMessages,
            PioTxTimeouts: pioTxTimeouts,
            PioRxFlagTimeouts: pioRxFlagTimeouts,
            PioRxPacketTimeouts: pioRxPacketTimeouts,
            PioSe0Glitches: pioSe0Glitches);
        return true;
    }

    private static bool TryParseMetric(string field, string name, out uint value)
    {
        value = 0;
        var prefix = name + "=";
        return field.StartsWith(prefix, StringComparison.Ordinal) &&
            uint.TryParse(
                field.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value);
    }
}
