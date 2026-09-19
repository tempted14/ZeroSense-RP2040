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
    MouseHostError
}

public readonly record struct FirmwareStatusUpdate(
    FirmwareStatusKind Kind,
    ushort VendorId = 0,
    ushort ProductId = 0);

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
}
