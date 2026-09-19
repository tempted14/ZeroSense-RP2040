using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace RainbowRecoil;

/// <summary>Pure encoder for the versioned ZeroSense firmware protocol.</summary>
internal static class SerialProtocol
{
    internal const int MaximumPayloadLength = 63;
    private const int MaximumProfileNameLength = 49;

    public static byte[] BuildCommand(string commandType, string? payload = null)
    {
        var command = GetCommandType(commandType);
        var payloadBytes = Encoding.UTF8.GetBytes(payload ?? string.Empty);
        return BuildPacket(command, payloadBytes);
    }

    public static byte[] BuildProfileCommand(WeaponProfile profile, CompensationMode mode)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var nameBytes = EncodeUtf8Prefix(profile.Name, MaximumProfileNameLength);
        var patternCount = UsesPattern(mode) && profile.HasWeaponPattern
            ? Math.Min(profile.Pattern.Length, 160)
            : 0;

        // Version 2 profile payload:
        // [version u8][mode u8][vertical f32 LE][horizontal f32 LE]
        // [burst u8][rpm u16 LE][pattern count u8][name UTF-8]
        var payload = new byte[14 + nameBytes.Length];
        payload[0] = 2;
        // Experimental is a host-side tuning mode. It reuses the stable v2
        // firmware pattern representation instead of expanding the wire protocol.
        payload[1] = (byte)(patternCount > 0
            ? CompensationMode.WeaponPattern
            : CompensationMode.General);
        BinaryPrimitives.WriteSingleLittleEndian(
            payload.AsSpan(2, sizeof(float)),
            (float)profile.VerticalCompensation);
        BinaryPrimitives.WriteSingleLittleEndian(
            payload.AsSpan(6, sizeof(float)),
            (float)profile.HorizontalCompensation);
        payload[10] = (byte)Math.Clamp(profile.BurstProgression, 0, 100);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(11, sizeof(ushort)),
            (ushort)Math.Clamp(profile.RoundsPerMinute, 0, ushort.MaxValue));
        payload[13] = (byte)patternCount;
        nameBytes.CopyTo(payload, 14);
        return BuildPacket(CommandType.Profile, payload);
    }

    public static IReadOnlyList<byte[]> BuildPatternCommands(WeaponProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        const int pointsPerPacket = 15;
        var pointCount = Math.Min(profile.Pattern.Length, 160);
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
        if (!float.IsFinite(scale.Horizontal) || !float.IsFinite(scale.Vertical))
        {
            throw new ArgumentOutOfRangeException(
                nameof(scale),
                "Sensitivity values must be finite numbers.");
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

    private static byte[] BuildPacket(CommandType command, byte[] payloadBytes)
    {
        if (payloadBytes.Length > MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadBytes),
                $"Protocol payloads cannot exceed {MaximumPayloadLength} bytes.");
        }

        var packet = new byte[3 + payloadBytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)payloadBytes.Length);
        packet[2] = (byte)command;
        payloadBytes.CopyTo(packet, 3);
        return packet;
    }

    private static byte[] EncodeUtf8Prefix(string? value, int maximumBytes)
    {
        value ??= string.Empty;
        var characterCount = value.Length;
        while (characterCount > 0 &&
               Encoding.UTF8.GetByteCount(value.AsSpan(0, characterCount)) > maximumBytes)
        {
            --characterCount;
        }

        if (characterCount > 0 && char.IsHighSurrogate(value[characterCount - 1]))
        {
            --characterCount;
        }

        return Encoding.UTF8.GetBytes(value[..characterCount]);
    }

    private static short ToFixedPoint(float value)
    {
        if (!float.IsFinite(value))
        {
            return 0;
        }

        var clamped = Math.Clamp(value, -127.0f, 127.0f);
        return (short)Math.Round(clamped * 256.0f, MidpointRounding.AwayFromZero);
    }

    internal static bool UsesPattern(CompensationMode mode) =>
        mode is CompensationMode.WeaponPattern or CompensationMode.Experimental;

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
        Reset = 0xFF
    }
}
