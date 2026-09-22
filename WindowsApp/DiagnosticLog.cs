using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RainbowRecoil;

/// <summary>Small in-memory structured log. It never stores screenshots.</summary>
internal static class DiagnosticLog
{
    private const int MaximumEntries = 200;
    private static readonly object Sync = new();
    private static readonly Queue<DiagnosticEntry> Entries = new();
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    public static void Record(string category, string message)
    {
        var entry = new DiagnosticEntry(
            DateTimeOffset.UtcNow,
            Clean(category, 32),
            Clean(message, 1024));
        lock (Sync)
        {
            Entries.Enqueue(entry);
            while (Entries.Count > MaximumEntries)
            {
                Entries.Dequeue();
            }
        }
    }

    public static string BuildReport(
        Settings settings,
        IRecoilDeviceConnection? connection,
        string? selectedOperator,
        WeaponProfile? selectedProfile,
        bool armed)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DiagnosticEntry[] entries;
        lock (Sync)
        {
            entries = Entries.ToArray();
        }

        using var process = Process.GetCurrentProcess();
        var metrics = connection?.GetMetrics() ?? default;
        var scale = settings.CalculateSensitivityScale(selectedProfile);
        var builder = new StringBuilder();
        builder.AppendLine("ZeroSense diagnostic report");
        builder.AppendLine($"App version: {typeof(DiagnosticLog).Assembly.GetName().Version}");
        builder.AppendLine($"Generated UTC: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine($"App uptime: {DateTimeOffset.UtcNow - StartedAt:g}");
        builder.AppendLine($"OS: {Environment.OSVersion.VersionString}");
        builder.AppendLine($"Runtime: {Environment.Version}");
        builder.AppendLine($"Working set: {process.WorkingSet64 / (1024.0 * 1024.0):F1} MiB");
        builder.AppendLine($"Settings schema: {settings.CalibrationVersion}");
        builder.AppendLine($"Operator: {selectedOperator ?? "none"}");
        builder.AppendLine($"Weapon: {selectedProfile?.Name ?? "none"}");
        builder.AppendLine($"Mode: {settings.CompensationMode}");
        builder.AppendLine($"General timing variance: {(settings.GeneralTimingVarianceEnabled ? "enabled" : "disabled")}");
        builder.AppendLine($"Generated delta noise: {(settings.DeltaNoiseEnabled ? "enabled" : "disabled")}");
        builder.AppendLine($"Sensitivity scale: H {scale.Horizontal:F3}, V {scale.Vertical:F3}");
        builder.AppendLine($"Master recoil gain: {settings.MasterRecoilGain:F2}x");
        builder.AppendLine($"2.5x automatic vertical boost: {settings.TwoPointFiveAutoVerticalBoost:F2}x");
        if (!string.IsNullOrWhiteSpace(selectedProfile?.Name))
        {
            builder.AppendLine(
                $"Effective recoil gain: {settings.GetEffectiveOutputGain(selectedProfile.Name):F2}x");
            var horizontal = settings.GetWeaponHorizontalTuning(selectedProfile.Name);
            builder.AppendLine(
                $"Horizontal pattern: {(horizontal.Enabled ? HorizontalRecoilModel.DescribeMode(horizontal.Mode) : "Disabled")}, " +
                $"{horizontal.Strength:F2}x");
        }
        builder.AppendLine($"Output state: {(armed ? "armed" : "safe")}");
        builder.AppendLine($"Connection: {(connection is null ? "none" : connection.IsSimulator ? "simulator" : "hardware")}");
        builder.AppendLine($"Commands: {metrics.CommandsSent} sent, {metrics.FailedCommands} failed");
        builder.AppendLine($"Acknowledgements: {metrics.Acknowledgements}, last {metrics.LastAcknowledgementMilliseconds:F1} ms, average {metrics.AverageAcknowledgementMilliseconds:F1} ms");
        builder.AppendLine($"Operator OCR: {settings.OperatorDetectionMode}, threshold {settings.OperatorDetectionConfidence:P0}");
        builder.AppendLine($"Weapon OCR: {(settings.WeaponDetectionEnabled ? "enabled" : "disabled")}, threshold {settings.WeaponDetectionConfidence:P0}");
        builder.AppendLine();
        builder.AppendLine("Recent events (screenshots are never stored):");
        foreach (var entry in entries)
        {
            builder.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{entry.Timestamp:O} [{entry.Category}] {entry.Message}"));
        }
        return builder.ToString();
    }

    private static string Clean(string? value, int maximumLength)
    {
        var clean = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return clean.Length <= maximumLength ? clean : clean[..maximumLength] + "…";
    }

    private sealed record DiagnosticEntry(
        DateTimeOffset Timestamp,
        string Category,
        string Message);
}
