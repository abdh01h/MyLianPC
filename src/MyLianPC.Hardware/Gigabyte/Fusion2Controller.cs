// GIGABYTE RGB Fusion 2 USB controller (ITE IT5711 / IT8297 family), HID feature reports, report id 0xCC.
// Protocol ported from OpenRGB Controllers/GigabyteRGBFusion2USBController (GPL-2.0-or-later,
// (c) OpenRGB contributors). See THIRD-PARTY-NOTICES.md.
using System.Buffers.Binary;
using HidSharp;
using MyLianPC.Core.Lighting;

namespace MyLianPC.Hardware.Gigabyte;

public enum Fusion2Effect : byte
{
    Static = 1,
    Pulse = 2,
    Blinking = 3,
    ColorCycle = 4,
    Wave = 6,
    Random = 8,
}

/// <summary>A lighting zone on the controller: an addressable header, or a single onboard LED group.</summary>
public sealed record Fusion2Zone(string Id, string Name, int LedIndex, bool Addressable, byte DirectHeader, int StripSlot, int BuiltinEffectBit);

public sealed class Fusion2Controller : IDisposable
{
    public const ushort Vid = 0x048D;
    private const byte ReportId = 0xCC;
    private const int PacketSize = 64;

    /// <summary>Zones of the B850 AORUS ELITE WIFI7 (OpenRGB layout "it5711_9_device").</summary>
    public static readonly IReadOnlyList<Fusion2Zone> B850AorusEliteWifi7Zones =
    [
        new("fusion2:argb1", "ARGB_V2_1 header", 5, true, 0x58, 0, 0x01),
        new("fusion2:argb2", "ARGB_V2_2 header", 6, true, 0x59, 1, 0x02),
        new("fusion2:argb3", "ARGB_V2_3 header", 7, true, 0x62, 2, 0x08),
        new("fusion2:chipset", "Chipset accent", 2, false, 0, -1, 0),
        new("fusion2:ledc", "LED_C", 4, false, 0, -1, 0),
    ];

    private readonly HidStream _stream;
    private readonly object _io = new();
    private readonly uint[] _byteOrder = new uint[6];
    private int _builtinDisabledMask = -1;

    public string DevicePath { get; }
    public ushort ProductId { get; }
    public string Description { get; private set; } = "";
    public string FirmwareVersion { get; private set; } = "";
    public uint ChipId { get; private set; }
    public byte SupportFlags { get; private set; }
    public byte StripDetect { get; private set; }
    public byte DeviceNum { get; private set; }

    /// <summary>Per-header LED byte-order calibration as reported by the controller (0x00RRGGBB positions; diagnostics).</summary>
    public IReadOnlyList<uint> ByteOrders => _byteOrder;

    private Fusion2Controller(HidDevice device, HidStream stream)
    {
        DevicePath = device.DevicePath;
        ProductId = (ushort)device.ProductID;
        _stream = stream;
    }

    /// <summary>Finds the vendor collection (usage page 0xFF89, usage 0xCC, 64-byte feature reports).</summary>
    public static HidDevice? Find() =>
        DeviceList.Local.GetHidDevices(Vid)
            .Where(d => d.ProductID is 0x5702 or 0x5711 or 0x8297 or 0x8950)
            .FirstOrDefault(d =>
            {
                try { return d.GetMaxFeatureReportLength() >= PacketSize && d.GetReportDescriptor().DeviceItems.Any(i => i.Usages.GetAllValues().Contains(0xFF8900CCu)); }
                catch { return false; }
            });

    public static Fusion2Controller Open(HidDevice device)
    {
        var stream = device.Open();
        stream.ReadTimeout = stream.WriteTimeout = 1000;
        var c = new Fusion2Controller(device, stream);
        c.RefreshInfo();
        return c;
    }

    /// <summary>Puts the controller into a known state: LampArray (Windows Dynamic Lighting) and audio beat mode off, effect registers cleared.</summary>
    public void Initialize()
    {
        lock (_io)
        {
            // The headers' effect-engine state is unknown after a reset, a power cycle or another program, so the
            // next SetEffect must send 0x32 again. Skipping it left the ARGB headers ignoring every effect.
            _builtinDisabledMask = -1;
            if ((SupportFlags & 0x02) != 0) Send(0x48, 0); // disable HID LampArray mode
            Reset();
            Send(0x31, 0);                                 // disable beat (audio sync) mode
        }
    }

    /// <summary>
    /// Re-takes control without the visible reset of <see cref="Initialize"/>: LampArray off, and the headers'
    /// effect-engine bits are re-sent with the next effect.
    /// </summary>
    public void Reassert()
    {
        lock (_io)
        {
            _builtinDisabledMask = -1;
            if ((SupportFlags & 0x02) != 0) Send(0x48, 0);
            Send(0x31, 0); // beat (audio sync) mode off: it makes the headers ignore the configured effects
        }
    }

    public void RefreshInfo()
    {
        lock (_io)
        {
            var r = Query(0x60);
            DeviceNum = r[2];
            StripDetect = r[3];
            var fw = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(4));
            FirmwareVersion = $"{fw & 0xFF}.{fw >> 8 & 0xFF}.{fw >> 16 & 0xFF}.{fw >> 24 & 0xFF}";
            SupportFlags = r[11];
            Description = System.Text.Encoding.ASCII.GetString(r, 12, 28).Split('\0')[0].Trim();
            _byteOrder[0] = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(44));
            _byteOrder[1] = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(48));
            ChipId = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(56));
            if (ProductId == 0x5711)
            {
                var c = Query(0x61);
                for (int i = 0; i < 4; i++) _byteOrder[2 + i] = BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(4 + i * 4));
            }
        }
    }

    public bool SupportsGen2 => DeviceNum == 0 && (SupportFlags & 0x01) != 0 && StripDetect == 1;

    /// <summary>
    /// Asks the controller how many LEDs are on a Gen2-capable header (slot 0-3). Returns null if detection is
    /// unsupported, or 0 when the attached strip does not report itself (non-Gen2 strips).
    /// </summary>
    public int? DetectStripLeds(int slot)
    {
        if (!SupportsGen2 || slot is < 0 or > 3) return null;
        ReadOnlySpan<byte> delta = [4, 5, 0, 1];
        byte scan = (byte)(0x38 + delta[slot]);
        lock (_io)
        {
            Send(scan, 0);
            Thread.Sleep(700);
            Send((byte)(scan + 2), 0);
            var buf = new byte[PacketSize];
            buf[0] = ReportId;
            _stream.GetFeature(buf);
            int segments = Math.Min((int)buf[1], 15), total = 0;
            for (int k = 0; k < segments; k++) total += BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(2 + k * 2));
            Send(0x34, 0, 0, 0); // restore default LED-count buckets
            Thread.Sleep(20);
            Send(0x47, 0);
            Thread.Sleep(20);
            return total;
        }
    }

    /// <summary>
    /// Hardware effect on one zone. Speed 0 (slowest) .. 100 (fastest); brightness 0-100.
    /// Call <see cref="Apply"/> afterwards (once for a batch of zones).
    /// </summary>
    public void SetEffect(Fusion2Zone zone, Fusion2Effect effect, RgbColor color, int brightnessPercent, int speedPercent, bool random = false)
    {
        lock (_io)
        {
            if (zone.Addressable) SetBuiltinEffects(zone, enabled: true);
            var p = new byte[PacketSize];
            p[0] = ReportId;
            int led = zone.LedIndex;
            uint zoneMask = 1u << led;
            p[1] = (byte)(led < 8 ? 0x20 + led : 0x90 + (led - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(2), zoneMask);
            p[11] = (byte)effect;
            // The controller misbehaves with pulse brightness above 100 (noted by OpenRGB), so pulse is scaled to 0-100.
            int maxB = effect == Fusion2Effect.Pulse ? 100 : 255;
            p[12] = (byte)(Math.Clamp(brightnessPercent, 0, 100) * maxB / 100);
            p[13] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(14), (uint)(color.B | color.G << 8 | color.R << 16));
            int s = 9 - (int)Math.Round(Math.Clamp(speedPercent, 0, 100) * 9 / 100.0); // controller: 0 = fast, 9 = slow
            ushort p0 = 0, p1 = 0, p2 = 0;
            byte e0 = random ? (byte)7 : (byte)0, e1 = 0;
            switch (effect)
            {
                case Fusion2Effect.Pulse:
                    p0 = (ushort)(s <= 6 ? 400 + s * 100 : 1000 + (s - 6) * 200); p1 = p0; p2 = 200; break;
                case Fusion2Effect.Blinking:
                    p0 = 100; p1 = 100; p2 = (ushort)(s * 200 + 700); break;
                case Fusion2Effect.ColorCycle:
                    p0 = (ushort)(s * 100 + 300 + (s > 8 ? 1300 * (s - 8) : 0)); p1 = (ushort)(p0 - 200); e0 = 7; break;
                case Fusion2Effect.Wave:
                    p0 = (ushort)(((s + 1) * (s + 1) + (s + 1) + 10) * 5 / 2); e0 = 7; e1 = 1; break;
                case Fusion2Effect.Random:
                    p0 = 100; e0 = 1; e1 = 5; break;
            }
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(22), p0);
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(24), p1);
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(26), p2);
            p[30] = e0;
            p[31] = e1;
            _stream.SetFeature(p);
        }
    }

    /// <summary>Commits all pending effect packets.</summary>
    public void Apply()
    {
        lock (_io) Send(0x28, 0xFF, (byte)(ProductId == 0x5711 ? 0x07 : 0x00));
    }

    /// <summary>
    /// Per-LED colors on an addressable header (direct mode). Not stored in flash; the service re-applies it
    /// after boot. Disables the header's built-in effect engine while active.
    /// </summary>
    public void SetDirect(Fusion2Zone zone, IReadOnlyList<RgbColor> colors)
    {
        if (!zone.Addressable) throw new ArgumentException("Direct mode needs an addressable header.", nameof(zone));
        lock (_io)
        {
            SetBuiltinEffects(zone, enabled: false);
            uint order = _byteOrder[zone.StripSlot];
            int boR = (int)(order >> 16 & 0xFF), boG = (int)(order >> 8 & 0xFF), boB = (int)(order & 0xFF);
            if (order == 0 || boR > 2 || boG > 2 || boB > 2) (boR, boG, boB) = (1, 0, 2); // GRB, the WS2812 default
            int sent = 0;
            for (int start = 0; start < colors.Count; start += 19)
            {
                int n = Math.Min(19, colors.Count - start);
                var p = new byte[PacketSize];
                p[0] = ReportId;
                p[1] = zone.DirectHeader;
                BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), (ushort)sent);
                p[4] = (byte)(n * 3);
                for (int i = 0; i < n; i++)
                {
                    var c = colors[start + i];
                    int o = 5 + i * 3;
                    p[o + boR] = c.R; p[o + boG] = c.G; p[o + boB] = c.B;
                }
                sent += n * 3;
                _stream.SetFeature(p);
            }
        }
    }

    /// <summary>Saves the current hardware effects to the controller's flash so they survive power-off with no software running.</summary>
    public void SaveToFlash()
    {
        lock (_io)
        {
            Send(0x47, 1);
            Thread.Sleep(20);
            Send(0x5E, 0);
        }
    }

    private void SetBuiltinEffects(Fusion2Zone zone, bool enabled)
    {
        int baseMask = _builtinDisabledMask < 0 ? 0 : _builtinDisabledMask;
        int mask = enabled ? baseMask & ~zone.BuiltinEffectBit : baseMask | zone.BuiltinEffectBit;
        if (_builtinDisabledMask >= 0 && mask == _builtinDisabledMask) return;
        _builtinDisabledMask = mask;
        Send(0x32, (byte)mask);
        Thread.Sleep(50);
    }

    /// <summary>
    /// Re-arms the headers' effect engine once before a batch of effects. The controller stops obeying effect
    /// packets after a while unless this register is written again (seen on this board), but writing it per zone
    /// makes the strip step visibly, so it is done once per apply.
    /// </summary>
    public void BeginBatch()
    {
        lock (_io)
        {
            _builtinDisabledMask = 0;
            Send(0x32, 0);
            Thread.Sleep(50);
        }
    }

    private void Reset()
    {
        for (byte reg = 0x20; reg <= 0x27; reg++) Send(reg, 0, 0);
        if (ProductId == 0x5711) for (byte reg = 0x90; reg <= 0x92; reg++) Send(reg, 0, 0);
        Apply();
    }

    /// <summary>Diagnostics: reads a register block back from the controller (e.g. 0x20 for a header's effect state).</summary>
    public byte[] ReadBack(byte reg)
    {
        lock (_io) return Query(reg);
    }

    private byte[] Query(byte command)
    {
        Send(command, 0);
        var buf = new byte[PacketSize];
        buf[0] = ReportId;
        _stream.GetFeature(buf);
        return buf;
    }

    private void Send(byte a, byte b, byte c = 0, byte d = 0)
    {
        var p = new byte[PacketSize];
        p[0] = ReportId; p[1] = a; p[2] = b; p[3] = c; p[4] = d;
        _stream.SetFeature(p);
    }

    public void Dispose() => _stream.Dispose();
}
