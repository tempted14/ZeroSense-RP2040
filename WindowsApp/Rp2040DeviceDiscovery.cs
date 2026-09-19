using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Security;

namespace RainbowRecoil
{
    /// <summary>
    /// Finds present RP2040/RP2350 CDC interfaces. The Raspberry Pi VID is preferred;
    /// legacy Microsoft/Logitech identities remain discoverable so existing
    /// flashed boards can be migrated. Every candidate must still pass the
    /// firmware-specific serial handshake before the application accepts it.
    /// </summary>
    internal static class Rp2040DeviceDiscovery
    {
        // Raspberry Pi is current; the other prefixes support legacy project builds.
        private const string RaspberryPiVidPrefix = "VID_2E8A&PID_";
        private const string MicrosoftVidPrefix = "VID_045E&PID_";
        private const string LogitechVidPrefix = "VID_046D&PID_";

        public static string? FindRuntimePort(string? preferredPort = null) =>
            FindRuntimePorts(preferredPort).FirstOrDefault();

        public static IReadOnlyList<string> FindRuntimePorts(string? preferredPort = null)
        {
            var presentPorts = new HashSet<string>(
                SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);

            if (presentPorts.Count == 0)
            {
                return Array.Empty<string>();
            }

            List<string> rp2040Ports;
            try
            {
                rp2040Ports = FindRaspberryPiUsbPorts(presentPorts)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(PortNumber)
                    .ToList();
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"USB registry discovery failed: {ex.Message}");
                rp2040Ports = new List<string>();
            }

            // Windows registry access can be restricted, and older firmware may
            // enumerate with another VID. The serial handshake safely filters the
            // remaining ports without relying on fragile registry traversal.
            if (rp2040Ports.Count == 0)
            {
                rp2040Ports = presentPorts.OrderBy(PortNumber).ToList();
            }

            if (!string.IsNullOrWhiteSpace(preferredPort))
            {
                var preferredIndex = rp2040Ports.FindIndex(port =>
                    port.Equals(preferredPort, StringComparison.OrdinalIgnoreCase));
                if (preferredIndex > 0)
                {
                    var preferred = rp2040Ports[preferredIndex];
                    rp2040Ports.RemoveAt(preferredIndex);
                    rp2040Ports.Insert(0, preferred);
                }
            }

            return rp2040Ports;
        }

        private static IEnumerable<string> FindRaspberryPiUsbPorts(
            IReadOnlySet<string> presentPorts)
        {
            using var usb = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb == null)
            {
                yield break;
            }

            // Search current and legacy project identities before the handshake fallback.
            foreach (var hardwareKeyName in usb.GetSubKeyNames())
            {
                if (!hardwareKeyName.StartsWith(
                        RaspberryPiVidPrefix, StringComparison.OrdinalIgnoreCase) &&
                    !hardwareKeyName.StartsWith(
                        MicrosoftVidPrefix, StringComparison.OrdinalIgnoreCase) &&
                    !hardwareKeyName.StartsWith(
                        LogitechVidPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var hardwareKey = usb.OpenSubKey(hardwareKeyName);
                if (hardwareKey == null)
                {
                    continue;
                }

                foreach (var instanceName in hardwareKey.GetSubKeyNames())
                {
                    using var parameters = hardwareKey.OpenSubKey(
                        instanceName + @"\Device Parameters");
                    var portName = parameters?.GetValue("PortName") as string;
                    if (!string.IsNullOrWhiteSpace(portName) && presentPorts.Contains(portName))
                    {
                        yield return portName;
                    }
                }
            }
        }

        private static int PortNumber(string portName)
        {
            return portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
                   int.TryParse(portName.AsSpan(3), out var number)
                ? number
                : int.MaxValue;
        }
    }
}
