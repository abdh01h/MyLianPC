// Corsair DRAM RGB (Vengeance RGB DDR5 and relatives) over SMBus.
// Protocol ported from OpenRGB Controllers/CorsairDRAMController (GPL-2.0-or-later,
// (c) Adam Honse and OpenRGB contributors). See THIRD-PARTY-NOTICES.md.
using MyLianPC.Core.Lighting;
using MyLianPC.Hardware.Smbus;

namespace MyLianPC.Hardware.Corsair;

public enum CorsairDramMode : byte
{
    ColorShift = 0x00,
    ColorPulse = 0x01,
    RainbowWave = 0x03,
    ColorWave = 0x04,
    Visor = 0x05,
    Rain = 0x06,
    Marquee = 0x07,
    Rainbow = 0x08,
    Sequential = 0x09,
    Static = 0x10,
}

/// <summary>Result of a periodic health check of one module.</summary>
public enum CorsairDeviceHealth
{
    /// <summary>The module answers and reports the normal-mode protocol.</summary>
    Normal,
    /// <summary>The module was stuck in an abnormal firmware mode and has just been switched back; its lighting has to be rewritten.</summary>
    Recovered,
    /// <summary>The module answers with a verified device info block but is still in an abnormal mode and ignores lighting writes.</summary>
    Stalled,
    /// <summary>The device info block could not be read (bus glitch, or another program using the SMBus). Nothing may be written in that state.</summary>
    Unreadable,
}

public sealed class CorsairDramController
{
    private const byte RegResetBuffer = 0x0B, RegSetBinaryData = 0x20, RegBinaryStart = 0x21, RegSwitchMode = 0x23, RegStatus = 0x30;
    private const byte RegColorBlock1 = 0x31, RegColorBlock2 = 0x32, RegGetBinaryData = 0x40, RegGetChecksum = 0x42;
    private const byte RegGetDeviceInfo = 0x61, RegWriteConfiguration = 0x82;
    private const byte IdEffectConfiguration = 1, IdColorData = 2;

    /// <summary>The normal-mode protocol version these modules report; anything older means the controller is stalled.</summary>
    private const byte ExpectedProtocolVersion = 4;

    /// <summary>
    /// LED count used for every module. Vengeance RGB DDR5 sticks come with 10 or 12 addressable LEDs and the
    /// device info does not report which; a module silently ignores the surplus entries, so the larger count
    /// covers both (a 10-LED module fed 10 values leaves its two end LEDs on their previous colour).
    /// </summary>
    public const int VengeanceDdr5LedCount = 12;

    private readonly PawnIoSmbus _bus;
    private bool _staticModeSet;

    public byte Address { get; }
    public ushort Vid { get; private set; }
    public ushort Pid { get; private set; }
    public string FirmwareVersion { get; private set; } = "?";
    public byte ProtocolVersion { get; private set; }
    public int LedCount { get; } = VengeanceDdr5LedCount;
    public bool DeviceInfoValid { get; private set; }

    /// <summary>True when the controller was stuck in an abnormal firmware mode and had to be put back into normal mode.</summary>
    public bool Recovered { get; private set; }

    /// <summary>
    /// Outcome of the last static colour write: true = read back from the module and identical, false = the module
    /// acknowledged the transfer but its stored buffer still has the old colours, null = the module could not be read back.
    /// </summary>
    public bool? LastWriteStored { get; private set; }

    private CorsairDramController(PawnIoSmbus bus, byte address)
    {
        _bus = bus;
        Address = address;
    }

    /// <summary>Read-only probe of the RGB controller address ranges used by Corsair modules (never touches SPD 0x50-0x57).</summary>
    public static IReadOnlyList<CorsairDramController> Detect(PawnIoSmbus bus)
    {
        var found = new List<CorsairDramController>();
        foreach (byte addr in Enumerable.Range(0x58, 8).Concat(Enumerable.Range(0x18, 8)).Select(a => (byte)a))
        {
            int r43 = bus.ReadByte(addr, 0x43);
            if (r43 is 0x1A or 0x1B or 0x1C)
            {
                int r44 = bus.ReadByte(addr, 0x44);
                if (r44 is 0x01 or 0x03 or 0x04)
                {
                    var c = new CorsairDramController(bus, addr);
                    c.ReadDeviceInfo();
                    c.EnsureNormalMode();
                    found.Add(c);
                }
            }
            Thread.Sleep(10);
        }
        return found;
    }

    /// <summary>Raw 32-byte device info block (diagnostics).</summary>
    public byte[] DeviceInfo { get; private set; } = [];

    /// <summary>
    /// Reads and verifies the device info block.
    /// A module that is busy or whose transfer was disturbed answers with a block of zeros, and the CRC-8 of an
    /// all-zero block is 0 as well, so a failed read looks exactly like a valid one with protocol version 0. Only a
    /// complete block with a matching checksum is accepted; a failed read is retried and then ignored, never parsed.
    /// </summary>
    public bool ReadDeviceInfo()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (ReadDeviceInfoBlock(out var info, out int checksum) && IsVerifiableDeviceInfo(info, checksum))
            {
                DeviceInfo = info;
                DeviceInfoValid = true;
                Vid = (ushort)(info[1] << 8 | info[0]);
                Pid = (ushort)(info[3] << 8 | info[2]);
                FirmwareVersion = $"{info[9]}.{info[8]}.{info[11] << 8 | info[10]}";
                ProtocolVersion = info[28];
                return true;
            }
            Thread.Sleep(20);
        }
        DeviceInfoValid = false;
        return false;
    }

    /// <summary>One read of the device info block; false when a single transfer failed, so failures never look like data.</summary>
    private bool ReadDeviceInfoBlock(out byte[] info, out int checksum)
    {
        checksum = -1;
        _bus.WriteByte(Address, RegGetDeviceInfo, 0);
        _bus.WriteByte(Address, RegBinaryStart, 0);
        info = new byte[32];
        for (int i = 0; i < info.Length; i++)
        {
            int b = _bus.ReadByte(Address, RegGetBinaryData);
            if (b < 0) return false;
            info[i] = (byte)b;
        }
        checksum = _bus.ReadByte(Address, RegGetChecksum);
        return checksum >= 0;
    }

    /// <summary>
    /// Hardware effect stored on the module (persists without software). Speed 0-2 (slow-fast),
    /// brightness 0-255. Returns false if the module rejected the configuration checksum.
    /// </summary>
    public bool SetEffect(CorsairDramMode mode, int speed, byte direction, bool randomColors, byte brightness, RgbColor c1, RgbColor c2)
    {
        byte[] d =
        [
            (byte)mode, (byte)Math.Clamp(speed, 0, 2), (byte)(randomColors ? 0 : 1), direction,
            c1.R, c1.G, c1.B, brightness,
            c2.R, c2.G, c2.B, brightness,
            0, 0, 0, 0, 0, 0, 0, 0,
        ];
        _staticModeSet = false;
        return UploadConfiguration(d, IdEffectConfiguration);
    }

    /// <summary>
    /// Static per-LED colors stored on the module ("Custom" in iCUE terms): static effect + color buffer.
    /// As in OpenRGB, the effect is only written when the mode changes and the module gets 15 ms before the
    /// colors follow; writing both back to back left some LEDs on their previous color.
    /// The stored buffer is read back afterwards: a module that is stuck in an abnormal firmware mode
    /// acknowledges the upload (the transfer checksum matches) but keeps showing its old colors — the LEDs that
    /// were left over then look like the "red edges" this hardware is known for. When the read-back does not
    /// match, the static effect is re-established and the colors are written once more.
    /// </summary>
    public bool SetStaticColors(IReadOnlyList<RgbColor> colors, int? ledCount = null)
    {
        int count = ledCount ?? LedCount;
        LastWriteStored = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!_staticModeSet)
            {
                if (!SetEffect(CorsairDramMode.Static, 0, 2, false, 0, RgbColor.Black, RgbColor.Black)) return false;
                _staticModeSet = true;
                Thread.Sleep(15);
            }
            if (!UploadStaticColors(colors, count)) return false;
            Thread.Sleep(5);
            var stored = StaticColorsStored(colors, count);
            LastWriteStored = stored;
            if (stored != false) return true;
            // Acknowledged but not stored: start over from the effect, once.
            _staticModeSet = false;
        }
        return false;
    }

    /// <summary>
    /// True when the colour buffer stored on the module matches <paramref name="colors"/>; null when the module
    /// cannot be read back (in that case nothing is assumed either way).
    /// </summary>
    public bool? StaticColorsStored(IReadOnlyList<RgbColor> colors, int ledCount)
    {
        try
        {
            var (data, checksum) = ReadConfiguration(IdColorData, ledCount * 4);
            if (checksum < 0) return null;
            return ColorsMatch(data, colors, ledCount);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Compares a stored colour buffer (R,G,B,0xFF per LED) with the colours that should be in it.</summary>
    public static bool ColorsMatch(ReadOnlySpan<byte> stored, IReadOnlyList<RgbColor> colors, int ledCount)
    {
        if (colors.Count == 0) return false;
        for (int i = 0; i < ledCount; i++)
        {
            if (stored.Length < (i + 1) * 4) return false;
            var c = colors[Math.Min(i, colors.Count - 1)];
            if (stored[i * 4] != c.R || stored[i * 4 + 1] != c.G || stored[i * 4 + 2] != c.B) return false;
        }
        return true;
    }

    /// <summary>Writes only the stored colour buffer; the module must already be in the static ("custom") effect.</summary>
    public bool UploadStaticColors(IReadOnlyList<RgbColor> colors, int ledCount)
    {
        var data = new byte[ledCount * 4];
        for (int i = 0; i < ledCount; i++)
        {
            var c = colors[Math.Min(i, colors.Count - 1)];
            data[i * 4] = c.R; data[i * 4 + 1] = c.G; data[i * 4 + 2] = c.B; data[i * 4 + 3] = 0xFF;
        }
        return UploadConfiguration(data, IdColorData);
    }

    private bool UploadConfiguration(byte[] data, byte bufferId)
    {
        _bus.WriteByte(Address, RegResetBuffer, 0);
        _bus.WriteByte(Address, RegBinaryStart, 0);
        foreach (var b in data) _bus.WriteByte(Address, RegSetBinaryData, b);
        if (_bus.ReadByte(Address, RegGetChecksum) != Crc8(data)) return false;
        _bus.WriteByte(Address, RegWriteConfiguration, bufferId);
        Thread.Sleep(5); // give the module time to raise its busy flag before polling it
        return WaitReady();
    }

    /// <summary>
    /// Diagnostics: reads back a stored configuration buffer (1 = effect, 2 = colors) through register 0x63,
    /// which OpenRGB's header documents as "select configuration buffer to read by ID".
    /// </summary>
    public (byte[] Data, int Checksum) ReadConfiguration(byte bufferId, int length)
    {
        _bus.WriteByte(Address, 0x63, bufferId);
        _bus.WriteByte(Address, RegBinaryStart, 0);
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)Math.Max(0, _bus.ReadByte(Address, RegGetBinaryData));
        return (data, _bus.ReadByte(Address, RegGetChecksum));
    }

    /// <summary>
    /// Re-reads the device info and recovers the controller if it has fallen back into an abnormal firmware
    /// mode. An unreadable block is reported as such: it is not evidence of a stall and must not cause a write.
    /// </summary>
    public CorsairDeviceHealth CheckHealth()
    {
        Recovered = false;
        if (!ReadDeviceInfo()) return CorsairDeviceHealth.Unreadable;
        if (ProtocolVersion >= ExpectedProtocolVersion) return CorsairDeviceHealth.Normal;
        if (!EnsureNormalMode()) return DeviceInfoValid ? CorsairDeviceHealth.Stalled : CorsairDeviceHealth.Unreadable;
        return CorsairDeviceHealth.Recovered;
    }

    /// <summary>
    /// Puts a controller that is stuck in a non-normal firmware mode back into normal mode.
    /// Such a module still answers on SMBus and acknowledges writes, but ignores every effect and colour
    /// (its LEDs stay dark or on their last frame) and reports an older protocol version in its device info.
    /// Writing 1 to the mode register (0x23) restores it; detected on this machine after a vendor tool
    /// or a partially completed firmware update left one module running in this state.
    /// The mode write discards the lighting stored on the module, so it is only ever done with a verified
    /// device info block in hand; a failed read that merely looked like protocol version 0 used to reset
    /// a perfectly healthy module to its default colour.
    /// </summary>
    public bool EnsureNormalMode()
    {
        if (ProtocolVersion >= ExpectedProtocolVersion) return true;
        return Recover();
    }

    /// <summary>
    /// The recovery write itself. Only call this with a verified device info block in hand and with positive
    /// evidence that the module ignores lighting writes (protocol older than expected, or an acknowledged write
    /// that was not stored). The lighting stored on the module is lost, so the caller has to rewrite it.
    /// </summary>
    public bool Recover()
    {
        if (!DeviceInfoValid && !ReadDeviceInfo()) return false;
        _bus.WriteByte(Address, RegSwitchMode, 1);
        Thread.Sleep(60);
        _staticModeSet = false;
        Recovered = false;
        if (!ReadDeviceInfo()) return false;
        Recovered = ProtocolVersion >= ExpectedProtocolVersion;
        return Recovered;
    }

    /// <summary>Forget what mode the module is in (another program may have changed it).</summary>
    public void ForgetState() => _staticModeSet = false;

    private bool WaitReady()
    {
        for (int i = 0; i < 20; i++)
        {
            int s = _bus.ReadByte(Address, RegStatus);
            if (s >= 0 && (s & 0x08) == 0) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    /// <summary>
    /// True when a device info block can be trusted: 32 bytes, a matching checksum and not the all-zero block a
    /// failed transfer returns (its CRC-8 is 0 as well, so the checksum alone cannot tell them apart).
    /// </summary>
    public static bool IsVerifiableDeviceInfo(ReadOnlySpan<byte> info, int checksum) =>
        info.Length == 32 && checksum >= 0 && Crc8(info) == checksum && (info[0] | info[1] | info[2] | info[3]) != 0;

    /// <summary>CRC-8/SMBUS (poly 0x07, init 0).</summary>
    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
        }
        return crc;
    }
}
