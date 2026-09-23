using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace RainbowRecoil;

/// <summary>Pure encoder for the versioned ZeroSense firmware protocol.</summary>
internal static class SerialProtocol
{
    internal const int MaximumPayloadLength = 63;
    internal const int MaximumProfileNameLength = 49;
    internal const byte ConfigurationSchemaVersion = 4;
    internal const byte FrameMagicFirst = 0xA5;
    internal const byte FrameMagicSecond = 0x5A;
    internal const byte FrameVersion = 1;
    private static int _nextSequence;

    public static byte[] BuildCommand(string commandType, string? payload = null)
    {
        var command = GetCommandType(commandType);
        var payloadBytes = Encoding.UTF8.GetBytes(payload ?? string.Empty);
        return BuildPacket(command, payloadBytes);
    }

    public static byte[] BuildProfileCommand(WeaponProfile profile, CompensationMode mode)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (!double.IsFinite(profile.VerticalCompensation) ||
            profile.VerticalCompensation is < FirmwareContract.MinimumVerticalCompensation or
                > FirmwareContract.MaximumVerticalCompensation ||
            !double.IsFinite(profile.HorizontalCompensation) ||
            profile.HorizontalCompensation is < FirmwareContract.MinimumHorizontalCompensation or
                > FirmwareContract.MaximumHorizontalCompensation)
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                "General compensation is outside the firmware contract.");
        }
        if (profile.BurstProgression is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                "Burst progression must be between 0 and 100.");
        }

        var nameBytes = EncodeProfileName(profile.Name);
        var usesPattern = UsesPattern(mode) && profile.HasWeaponPattern;
        var patternCount = usesPattern ? profile.Pattern.Length : 0;
        if (patternCount > FirmwareContract.MaximumPatternPoints)
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                $"Patterns cannot exceed {FirmwareContract.MaximumPatternPoints} points.");
        }
        if (usesPattern && profile.RoundsPerMinute is
            < FirmwareContract.MinimumPatternRoundsPerMinute or
            > FirmwareContract.MaximumPatternRoundsPerMinute)
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                $"Pattern RPM must be between " +
                $"{FirmwareContract.MinimumPatternRoundsPerMinute} and " +
                $"{FirmwareContract.MaximumPatternRoundsPerMinute}.");
        }
        ValidatePatternPoints(profile, patternCount);

        // Version 2 profile payload:
        // [version u8][mode u8][vertical f32 LE][horizontal f32 LE]
        // [burst u8][rpm u16 LE][pattern count u8][name UTF-8]
        var payload = new byte[14 + nameBytes.Length];
        payload[0] = 2;
        // Experimental and Research Estimate are host-side pattern builders.
        // Both reuse the stable v2 firmware representation instead of expanding
        // the wire protocol.
        payload[1] = (byte)(patternCount > 0
            ? CompensationMode.WeaponPattern
            : CompensationMode.General);
        BinaryPrimitives.WriteSingleLittleEndian(
            payload.AsSpan(2, sizeof(float)),
            (float)profile.VerticalCompensation);
        BinaryPrimitives.WriteSingleLittleEndian(
            payload.AsSpan(6, sizeof(float)),
            (float)profile.HorizontalCompensation);
        payload[10] = (byte)profile.BurstProgression;
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(11, sizeof(ushort)),
            (ushort)(usesPattern ? profile.RoundsPerMinute : 0));
        payload[13] = (byte)patternCount;
        nameBytes.CopyTo(payload, 14);
        return BuildPacket(CommandType.Profile, payload);
    }

    public static IReadOnlyList<byte[]> BuildPatternCommands(WeaponProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        const int pointsPerPacket = 15;
        if (profile.Pattern.Length > FirmwareContract.MaximumPatternPoints)
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                $"Patterns cannot exceed {FirmwareContract.MaximumPatternPoints} points.");
        }
        var pointCount = profile.Pattern.Length;
        ValidatePatternPoints(profile, pointCount);
        var packets = new List<byte[]>((pointCount + pointsPerPacket - 1) / pointsPerPacket);

        for (var offset = 0; offset < pointCount; offset += pointsPerPacket)
        {
            var count = Math.Min(pointsPerPacket, pointCount - offset);
            // [version u8][offset u8][count u8][x/y Q8.8 int16 LE pairs]
            var payload = new byte[3 + count * sizeof(short) * 2];
            payload[0] = 1;
            payload[1] = (byte)offset;
            payload[2] = (byte)count;
            for (var index = 0; index < count; ++index)
            {
                var point = profile.Pattern[offset + index];
                var payloadOffset = 3 + index * 4;
                BinaryPrimitives.WriteInt16LittleEndian(
                    payload.AsSpan(payloadOffset, sizeof(short)),
                    ToFixedPoint(point.Horizontal));
                BinaryPrimitives.WriteInt16LittleEndian(
                    payload.AsSpan(payloadOffset + sizeof(short), sizeof(short)),
                    ToFixedPoint(point.Vertical));
            }
            packets.Add(BuildPacket(CommandType.Pattern, payload));
        }

        return packets;
    }

    public static byte[] BuildSensitivityCommand(SensitivityScale scale)
    {
        if (!float.IsFinite(scale.Horizontal) || !float.IsFinite(scale.Vertical) ||
            scale.Horizontal is < FirmwareContract.MinimumSensitivityFactor or
                > FirmwareContract.MaximumSensitivityFactor ||
            scale.Vertical is < FirmwareContract.MinimumSensitivityFactor or
                > FirmwareContract.MaximumSensitivityFactor)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scale),
                "Sensitivity values must be finite and inside the firmware contract.");
        }

        var payload = new byte[sizeof(float) * 2];
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(0, sizeof(float)), scale.Horizontal);
        BinaryPrimitives.WriteSingleLittleEndian(
            payload.AsSpan(sizeof(float), sizeof(float)),
            scale.Vertical);
        return BuildPacket(CommandType.Sensitivity, payload);
    }

    public static byte[] BuildRapidFireCommand(bool enabled, int roundsPerMinute)
    {
        if (enabled && roundsPerMinute is < 60 or > 1200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(roundsPerMinute),
                "Rapid-fire rate must be between 60 and 1200 RPM.");
        }

        var payload = new byte[3];
        payload[0] = enabled ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(1, sizeof(ushort)),
            (ushort)(enabled ? roundsPerMinute : 0));
        return BuildPacket(CommandType.RapidFire, payload);
    }

    public static byte[] BuildGeneralSettingsCommand(
        bool timingVarianceEnabled,
        bool deltaNoiseEnabled = false) =>
        BuildPacket(
            CommandType.GeneralSettings,
            [
                timingVarianceEnabled ? (byte)1 : (byte)0,
                deltaNoiseEnabled ? (byte)1 : (byte)0
            ]);

    public static byte[] BuildFirstBulletKickCommand(float multiplier)
    {
        if (!float.IsFinite(multiplier) || multiplier is < 1.0f or > 4.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        }
        var payload = new byte[sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(payload, multiplier);
        return BuildPacket(CommandType.FirstBulletKick, payload);
    }

    public static byte[] BuildArmLeaseCommand(bool enabled) =>
        BuildPacket(CommandType.ArmLease, [enabled ? (byte)1 : (byte)0]);

    public static byte[] BuildConfigurationBeginCommand(uint transactionId, uint configurationHash)
    {
        var payload = new byte[9];
        payload[0] = ConfigurationSchemaVersion;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, sizeof(uint)), transactionId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(5, sizeof(uint)), configurationHash);
        return BuildPacket(CommandType.ConfigurationBegin, payload);
    }

    public static byte[] BuildConfigurationCommitCommand(uint transactionId, uint configurationHash)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, sizeof(uint)), transactionId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, sizeof(uint)), configurationHash);
        return BuildPacket(CommandType.ConfigurationCommit, payload);
    }

    public static byte[] BuildConfigurationAbortCommand(uint transactionId)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, transactionId);
        return BuildPacket(CommandType.ConfigurationAbort, payload);
    }

    public static byte[] BuildStatusCommand() => BuildPacket(CommandType.Status, []);

    /// <summary>
    /// Returns the canonical FNV-1a hash used by protocol v4. The firmware
    /// computes the same byte stream at commit time, so a successful commit is
    /// proof that every staged value was transferred without silent coercion.
    /// </summary>
    public static uint ComputeConfigurationHash(
        WeaponProfile profile,
        CompensationMode mode,
        SensitivityScale scale,
        bool rapidFireEnabled,
        int rapidFireRoundsPerMinute,
        bool generalTimingVarianceEnabled = false,
        bool deltaNoiseEnabled = false,
        float firstBulletKick = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(profile);
        // Reuse the encoders for all contract validation before hashing.
        _ = BuildProfileCommand(profile, mode);
        _ = BuildSensitivityCommand(scale);
        _ = BuildRapidFireCommand(rapidFireEnabled, rapidFireRoundsPerMinute);
        _ = BuildFirstBulletKickCommand(firstBulletKick);

        var usesPattern = UsesPattern(mode) && profile.HasWeaponPattern;
        var pointCount = usesPattern
            ? profile.Pattern.Length
            : 0;
        ValidatePatternPoints(profile, pointCount);
        var nameBytes = EncodeProfileName(profile.Name);
        var canonical = new List<byte>(32 + nameBytes.Length + pointCount * 4)
        {
            ConfigurationSchemaVersion,
            usesPattern ? (byte)CompensationMode.WeaponPattern : (byte)CompensationMode.General
        };
        AppendSingle(canonical, (float)profile.VerticalCompensation);
        AppendSingle(canonical, (float)profile.HorizontalCompensation);
        canonical.Add((byte)profile.BurstProgression);
        AppendUInt16(canonical, (ushort)(usesPattern ? profile.RoundsPerMinute : 0));
        canonical.Add((byte)pointCount);
        canonical.Add((byte)nameBytes.Length);
        canonical.AddRange(nameBytes);
        AppendSingle(canonical, scale.Horizontal);
        AppendSingle(canonical, scale.Vertical);
        canonical.Add(rapidFireEnabled ? (byte)1 : (byte)0);
        AppendUInt16(canonical, (ushort)(rapidFireEnabled ? rapidFireRoundsPerMinute : 0));
        canonical.Add(generalTimingVarianceEnabled ? (byte)1 : (byte)0);
        canonical.Add(deltaNoiseEnabled ? (byte)1 : (byte)0);
        AppendSingle(canonical, firstBulletKick);

        for (var index = 0; index < pointCount; ++index)
        {
            AppendInt16(canonical, ToFixedPoint(profile.Pattern[index].Horizontal));
            AppendInt16(canonical, ToFixedPoint(profile.Pattern[index].Vertical));
        }

        var hash = 2166136261u;
        foreach (var value in canonical)
        {
            hash ^= value;
            hash *= 16777619u;
        }
        return hash;
    }

    private static byte[] BuildPacket(CommandType command, byte[] payloadBytes)
    {
        if (payloadBytes.Length > MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadBytes),
                $"Protocol payloads cannot exceed {MaximumPayloadLength} bytes.");
        }

        // v4 frame: [A5 5A][version][sequence u16 LE][command][length u8]
        // [payload][CRC16-CCITT u16 LE]. The sync word, bounded length, CRC and
        // parser timeout allow the firmware to recover after truncated/noisy data.
        var packet = new byte[9 + payloadBytes.Length];
        packet[0] = FrameMagicFirst;
        packet[1] = FrameMagicSecond;
        packet[2] = FrameVersion;
        var sequence = unchecked((ushort)Interlocked.Increment(ref _nextSequence));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(3, 2), sequence);
        packet[5] = (byte)command;
        packet[6] = (byte)payloadBytes.Length;
        payloadBytes.CopyTo(packet, 7);
        var crc = ComputeCrc16(packet.AsSpan(2, 5 + payloadBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(7 + payloadBytes.Length, 2), crc);
        return packet;
    }

    internal static ushort ComputeCrc16(ReadOnlySpan<byte> bytes)
    {
        var crc = (ushort)0xFFFF;
        foreach (var value in bytes)
        {
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; ++bit)
            {
                crc = (ushort)((crc & 0x8000) != 0
                    ? (crc << 1) ^ 0x1021
                    : crc << 1);
            }
        }
        return crc;
    }

    private static byte[] EncodeProfileName(string? value)
    {
        value ??= string.Empty;
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Profile names cannot contain control characters.",
                nameof(value));
        }
        var encoded = Encoding.UTF8.GetBytes(value);
        if (encoded.Length > MaximumProfileNameLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Profile names cannot exceed {MaximumProfileNameLength} UTF-8 bytes.");
        }
        return encoded;
    }

    private static void ValidatePatternPoints(WeaponProfile profile, int pointCount)
    {
        for (var index = 0; index < pointCount; ++index)
        {
            var point = profile.Pattern[index];
            if (!float.IsFinite(point.Horizontal) || point.Horizontal is < -127.0f or > 127.0f ||
                !float.IsFinite(point.Vertical) || point.Vertical is < 0.0f or > 127.0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(profile),
                    $"Pattern point {index} is outside the exact Q8.8 wire range.");
            }
        }
    }

    private static short ToFixedPoint(float value)
    {
        if (!float.IsFinite(value) || value is < -127.0f or > 127.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Pattern values must be finite and between -127 and 127.");
        }

        return (short)Math.Round(value * 256.0f, MidpointRounding.AwayFromZero);
    }

    private static void AppendSingle(List<byte> destination, float value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        destination.AddRange(bytes.ToArray());
    }

    private static void AppendUInt16(List<byte> destination, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        destination.AddRange(bytes.ToArray());
    }

    private static void AppendInt16(List<byte> destination, short value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(short)];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        destination.AddRange(bytes.ToArray());
    }

    internal static bool UsesPattern(CompensationMode mode) =>
        mode is CompensationMode.WeaponPattern or CompensationMode.Experimental or
            CompensationMode.ResearchEstimate;

    private static CommandType GetCommandType(string name) => name switch
    {
        "PING" => CommandType.Ping,
        "START" => CommandType.Start,
        "STOP" => CommandType.Stop,
        "PROFILE" => CommandType.Profile,
        "SENSITIVITY" => CommandType.Sensitivity,
        "PATTERN" => CommandType.Pattern,
        "RAPID_FIRE" => CommandType.RapidFire,
        "KEEPALIVE" => CommandType.KeepAlive,
        "ARM_LEASE" => CommandType.ArmLease,
        "CONFIG_BEGIN" => CommandType.ConfigurationBegin,
        "CONFIG_COMMIT" => CommandType.ConfigurationCommit,
        "CONFIG_ABORT" => CommandType.ConfigurationAbort,
        "STATUS" => CommandType.Status,
        "GENERAL_SETTINGS" => CommandType.GeneralSettings,
        "FIRST_BULLET_KICK" => CommandType.FirstBulletKick,
        "RESET" => CommandType.Reset,
        _ => throw new ArgumentException($"Unknown command type '{name}'.", nameof(name))
    };

    private enum CommandType : byte
    {
        Ping = 0xF0,
        Start = 0xF1,
        Stop = 0xF2,
        Profile = 0xF3,
        Sensitivity = 0xF4,
        Pattern = 0xF5,
        RapidFire = 0xF6,
        KeepAlive = 0xF7,
        ArmLease = 0xF8,
        ConfigurationBegin = 0xF9,
        ConfigurationCommit = 0xFA,
        ConfigurationAbort = 0xFB,
        Status = 0xFC,
        GeneralSettings = 0xFD,
        FirstBulletKick = 0xFE,
        Reset = 0xFF
    }
}
