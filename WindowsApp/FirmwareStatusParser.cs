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
    uint HostAccumulatorSaturations = 0);

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
        if (fields.Length != 8 || fields[0] != "METRICS" ||
            !TryParseMetric(fields[1], "HID_SENT", out var hidReportsSent) ||
            !TryParseMetric(fields[2], "HID_BUSY", out var hidBusyDeferrals) ||
            !TryParseMetric(fields[3], "MAX_QUEUE", out var maximumQueuedDelta) ||
            !TryParseMetric(fields[4], "MAX_ACTIVE_GAP_US", out var maximumActiveReportGapUs) ||
            !TryParseMetric(fields[5], "HOST_REPORTS", out var hostReportsReceived) ||
            !TryParseMetric(fields[6], "HOST_DECODE_ERRORS", out var hostDecodeErrors) ||
            !TryParseMetric(fields[7], "HOST_SATURATIONS", out var hostAccumulatorSaturations))
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
            HostAccumulatorSaturations: hostAccumulatorSaturations);
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
