// Packet format for the Lian Li HydroShift II LCD (USB 1CBE:A021 Circle / 1CBE:A034 Square).
// Ported from the MIT-licensed community implementations:
//   hydroshift2-rs  (c) 2026 andyjusa        https://github.com/andyjusa/hydroshift2-rs
//   lian-li-linux   (c) sgtaziz and contributors  https://github.com/sgtaziz/lian-li-linux
// See THIRD-PARTY-NOTICES.md for the full license texts.
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MyLianPC.Hardware.LianLi;

public static class HydroShift2Protocol
{
    public const ushort Vid = 0x1CBE;
    public const ushort PidCircle = 0xA021;
    public const ushort PidSquare = 0xA034;
    public const byte OutEndpoint = 0x01;
    public const byte InEndpoint = 0x81;
    public const int Width = 480;
    public const int Height = 480;
    public const int RingLedCount = 24;
    public const ushort PumpMinRpm = 1600;
    public const ushort PumpMaxRpmCircle = 2500;

    public const int HeaderSize = 512;
    private const int PlainSize = 500;
    private const int EncryptedSize = 504;
    private static readonly byte[] Key = "slv3tuzx"u8.ToArray();

    public static class Cmd
    {
        public const byte GetVersion = 0x0A;
        public const byte Rotation = 0x0D;
        public const byte Brightness = 0x0E;
        public const byte FrameRate = 0x0F;
        public const byte SetClock = 0x33;
        public const byte StopClock = 0x34;
        public const byte Jpeg = 0x65;
        public const byte PngOverlay = 0x66;
        public const byte StopPlay = 0x7B;
        public const byte GetParams = 0xFA;
        public const byte SyncPumpFan = 0xFB;
        public const byte PushRgb = 0xFC;
    }

    /// <summary>Millisecond timestamp the firmware expects: ms elapsed since local midnight of the previous day (L-Connect compatible).</summary>
    public static uint Timestamp(DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var yesterdayMidnight = new DateTimeOffset(local.Date.AddDays(-1), local.Offset);
        return (uint)(now - yesterdayMidnight).TotalMilliseconds;
    }

    /// <summary>Builds the 512-byte encrypted header: DES-CBC/PKCS7 over 500 plaintext bytes, then zero padding and the A1 1A trailer.</summary>
    public static byte[] Header(byte command, ReadOnlySpan<byte> parameters, uint timestamp)
    {
        if (parameters.Length > PlainSize - 8) throw new ArgumentException("Parameters exceed 492 bytes.", nameof(parameters));
        var plain = new byte[PlainSize];
        plain[0] = command;
        plain[2] = 0x1A;
        plain[3] = 0x6D;
        BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(4), timestamp);
        parameters.CopyTo(plain.AsSpan(8));

        using var des = DES.Create();
        des.Key = Key;
        var encrypted = des.EncryptCbc(plain, Key, PaddingMode.PKCS7);
        if (encrypted.Length != EncryptedSize) throw new CryptographicException("Unexpected DES output length.");

        var packet = new byte[HeaderSize];
        encrypted.CopyTo(packet, 0);
        packet[510] = 0xA1;
        packet[511] = 0x1A;
        return packet;
    }

    /// <summary>Inverse of <see cref="Header"/>; used by tests and diagnostics.</summary>
    public static byte[] DecryptHeader(ReadOnlySpan<byte> packet)
    {
        using var des = DES.Create();
        des.Key = Key;
        return des.DecryptCbc(packet[..EncryptedSize].ToArray(), Key, PaddingMode.PKCS7);
    }

    public static byte[] Command(byte command, ReadOnlySpan<byte> parameters = default) =>
        Header(command, parameters, Timestamp(DateTimeOffset.Now));

    /// <summary>Header + JPEG (background, 0x65) or PNG (transparent overlay, 0x66) in one transfer. Size is big-endian at p[8..12].</summary>
    public static byte[] Image(ReadOnlySpan<byte> data, bool png, uint? timestamp = null)
    {
        Span<byte> p = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(p, (uint)data.Length);
        var header = Header(png ? Cmd.PngOverlay : Cmd.Jpeg, p, timestamp ?? Timestamp(DateTimeOffset.Now));
        var packet = new byte[header.Length + data.Length];
        header.CopyTo(packet, 0);
        data.CopyTo(packet.AsSpan(header.Length));
        return packet;
    }

    public static byte[] Clock(DateTime utc) => Command(Cmd.SetClock,
    [
        (byte)(utc.Year >> 8), (byte)utc.Year, (byte)utc.Month, (byte)utc.Day,
        (byte)utc.Hour, (byte)utc.Minute, (byte)utc.Second, 2,
    ]);

    /// <summary>CRC-16/XMODEM (poly 0x1021, init 0).</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    /// <summary>Maps a pump RPM target to the firmware's inverse control value (Circle model). Lower value = faster pump.</summary>
    public static ushort PumpControlValue(int rpm)
    {
        int r = Math.Clamp(rpm, PumpMinRpm, PumpMaxRpmCircle);
        int v = r <= 1720 ? 1500 - (int)((r - 1600) * 1.625)
              : r <= 1870 ? 1300 - (r - 1720) * 2
              : r <= 2000 ? 1000 - (int)((r - 1870) * 1.23)
              : r <= 2300 ? 840 - (r - 2000) * 2
              : r <= 2400 ? 240 - (int)((r - 2300) * 1.8)
              : 60 - (int)((r - 2400) * 0.5);
        return (ushort)v;
    }

    /// <summary>SyncPumpFan control block: FF 0F A2 00, pump value (BE), three raw fan duties (0-255), CRC16 over bytes 0..11 at 12..13 (BE).</summary>
    public static byte[] SyncPumpFanParameters(int pumpRpm, byte fan1, byte fan2, byte fan3)
    {
        var c = new byte[16];
        c[0] = 0xFF; c[1] = 0x0F; c[2] = 0xA2;
        BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(4), PumpControlValue(pumpRpm));
        c[6] = fan1; c[7] = fan2; c[8] = fan3;
        BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(12), Crc16(c.AsSpan(0, 12)));
        return c;
    }

    /// <summary>Most frames in one animation.</summary>
    public const int MaxRingFrames = 48;

    /// <summary>Largest uncompressed animation we upload: keeps the data inside the firmware's 4 KB decode cache.</summary>
    public const int MaxRawLightingBytes = 4096;

    /// <summary>LEDs per radiator fan driven by the pump head (UNI FAN CL style: 8 center + 16 outer).</summary>
    public const int FanLedCount = 24;

    /// <summary>
    /// PushRgbData (0xFC): TinyUZ(raw frames of RGB triplets) + frameCount (u16 BE) + interval ticks + LED count.
    /// A frame is the 24 ring LEDs, optionally followed by 24 LEDs per fan wired to the pump head (the layout
    /// lian-li-linux uses for the same effect engine over RF). The payload length is big-endian at p[12..16].
    /// The device loops the animation by itself.
    /// </summary>
    public static byte[] RingFrames(IReadOnlyList<byte[]> frames, byte intervalTicks, uint? timestamp = null)
    {
        if (frames.Count is 0 or > MaxRingFrames) throw new ArgumentException($"Ring animations need 1-{MaxRingFrames} frames.", nameof(frames));
        int frameBytes = frames[0].Length, leds = frameBytes / 3;
        if (frameBytes % 3 != 0 || leds < RingLedCount || (leds - RingLedCount) % FanLedCount != 0 || leds > RingLedCount + 4 * FanLedCount)
            throw new ArgumentException($"Each frame must be {RingLedCount} ring LEDs plus {FanLedCount} per fan (0-4 fans).", nameof(frames));
        if (frames.Count * frameBytes > MaxRawLightingBytes)
            throw new ArgumentException($"The animation is larger than the cooler's {MaxRawLightingBytes}-byte decode cache.", nameof(frames));
        var raw = new byte[frames.Count * frameBytes];
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].Length != frameBytes) throw new ArgumentException("All frames must have the same length.", nameof(frames));
            frames[i].CopyTo(raw, i * frameBytes);
        }
        var compressed = TinyUz.Compress(raw);
        if (!TinyUz.Decompress(compressed, raw.Length).AsSpan().SequenceEqual(raw))
            throw new InvalidOperationException("TinyUZ round-trip verification failed.");

        var payload = new byte[compressed.Length + 4];
        compressed.CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(compressed.Length), (ushort)frames.Count);
        payload[^2] = intervalTicks;
        payload[^1] = (byte)leds;

        var p = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), (uint)payload.Length);
        var header = Header(Cmd.PushRgb, p, timestamp ?? Timestamp(DateTimeOffset.Now));
        return [.. header, .. payload];
    }

    /// <summary>
    /// Parses a GetParams (0xFA) reply. Returns null if the reply is too short to be one. Byte 9 is the number of
    /// fans whose lighting the pump head drives, when byte 8 carries the 0x0A marker (lian-li-linux issue #101).
    /// </summary>
    public static HydroShift2Telemetry? ParseTelemetry(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 28) return null;
        return new HydroShift2Telemetry(
            CoolantC: reply[13],
            FanRpm: [BinaryPrimitives.ReadUInt16BigEndian(reply[14..]), BinaryPrimitives.ReadUInt16BigEndian(reply[16..]), BinaryPrimitives.ReadUInt16BigEndian(reply[18..])],
            PumpRpm: BinaryPrimitives.ReadUInt16BigEndian(reply[20..]),
            Mac: Convert.ToHexString(reply[22..28]),
            LightingFanCount: reply[8] == 0x0A && reply[9] <= 4 ? reply[9] : null);
    }

    /// <summary>Firmware version string from a GetVersion (0x0A) reply (bytes 8..40, NUL terminated).</summary>
    public static string? ParseVersion(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 40) return null;
        var s = System.Text.Encoding.ASCII.GetString(reply[8..40]).Split('\0')[0].Trim();
        return s.Length == 0 ? null : s;
    }
}

public sealed record HydroShift2Telemetry(int CoolantC, int[] FanRpm, int PumpRpm, string Mac, int? LightingFanCount = null);
