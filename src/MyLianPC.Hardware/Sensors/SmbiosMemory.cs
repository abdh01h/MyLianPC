using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace MyLianPC.Hardware.Sensors;

/// <summary>One SMBIOS type 17 (Memory Device) entry: the SMBIOS memory type code and the two speeds in MT/s.</summary>
public sealed record SmbiosMemoryModule(byte MemoryType, ushort SpeedMtps, ushort ConfiguredMtps)
{
    public string? Manufacturer { get; init; }
    public string? PartNumber { get; init; }
    /// <summary>Module capacity in GB.</summary>
    public double SizeGb { get; init; }
}

/// <summary>
/// Memory module speeds from the firmware's SMBIOS table (the same data Windows reports). <see cref="SpeedMtps"/>
/// is the modules' rated maximum, <see cref="ConfiguredMtps"/> is the speed they actually run at. An XMP or EXPO
/// profile is active when they run faster than the JEDEC baseline of their type (DDR5-4800, DDR4-2133): a board
/// that falls back to the safe default after a failed memory training reports the baseline here.
/// </summary>
public sealed record SmbiosMemoryInfo(IReadOnlyList<SmbiosMemoryModule> Modules)
{
    public static readonly SmbiosMemoryInfo Empty = new([]);

    public int Count => Modules.Count;
    public ushort SpeedMtps => Modules.Count == 0 ? (ushort)0 : Modules.Max(m => m.SpeedMtps);
    public ushort ConfiguredMtps => Modules.Count == 0 ? (ushort)0 : Modules.Max(m => m.ConfiguredMtps);
    public bool XmpExpoActive => Modules.Any(m => m.ConfiguredMtps > JedecBaselineMtps(m.MemoryType));

    /// <summary>e.g. "Corsair · CMH32GX5M2E6000Z36 · 2 × 16 GB" for the dashboard's Memory card.</summary>
    public string? Summary
    {
        get
        {
            if (Modules.Count == 0) return null;
            var first = Modules[0];
            var parts = new List<string>();
            if (first.Manufacturer is { Length: > 0 } maker) parts.Add(maker);
            if (first.PartNumber is { Length: > 0 } part) parts.Add(part);
            double perModule = Math.Round(first.SizeGb);
            double total = Math.Round(Modules.Sum(m => m.SizeGb));
            parts.Add(Modules.Count == 1 || perModule <= 0 ? $"{total:0.#} GB" : $"{Modules.Count} × {perModule:0.#} GB");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>JEDEC speed of a memory type without an overclocking profile; 0 when the type is unknown.</summary>
    public static ushort JedecBaselineMtps(byte memoryType) => memoryType switch
    {
        0x18 => 1333, // DDR3
        0x1A => 2133, // DDR4
        0x1B => 4266, // LPDDR4
        0x1C => 4266, // LPDDR4X
        0x22 => 4800, // DDR5
        0x23 => 6400, // LPDDR5
        _ => 0,
    };

    /// <summary>Reads the table from the firmware. Returns <see cref="Empty"/> when it is unavailable.</summary>
    public static SmbiosMemoryInfo Read()
    {
        const uint RawSmbiosProvider = 0x52534D42; // 'RSMB'
        try
        {
            uint size = GetSystemFirmwareTable(RawSmbiosProvider, 0, IntPtr.Zero, 0);
            if (size == 0) return Empty;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetSystemFirmwareTable(RawSmbiosProvider, 0, buffer, size) != size) return Empty;
                var bytes = new byte[size];
                Marshal.Copy(buffer, bytes, 0, (int)size);
                return Parse(bytes);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception) { return Empty; }
    }

    /// <summary>
    /// Parses a raw SMBIOS table. The firmware interface ('RSMB') prefixes it with the eight-byte
    /// RawSMBIOSData header (used-20-calling-method, major, minor, DMI revision, length); a table that starts
    /// directly with the first structure (type 0) is accepted as well.
    /// </summary>
    public static SmbiosMemoryInfo Parse(ReadOnlySpan<byte> table)
    {
        int start = 0;
        if (table.Length > 8 && BinaryPrimitives.ReadUInt32LittleEndian(table.Slice(4)) == table.Length - 8) start = 8;

        var modules = new List<SmbiosMemoryModule>();
        int i = start;
        while (i + 4 <= table.Length)
        {
            byte type = table[i], length = table[i + 1];
            if (length < 4 || i + length > table.Length) break;
            if (type == 127) break; // end-of-table
            if (type == 17 && length >= 0x1B)
            {
                var strings = ReadStrings(table, i + length);
                if (ReadMemoryDevice(table.Slice(i, length), strings) is { } module) modules.Add(module);
            }
            int next = i + length;
            while (next + 1 < table.Length && !(table[next] == 0 && table[next + 1] == 0)) next++;
            next += 2;
            if (next <= i) break;
            i = next;
        }
        return new SmbiosMemoryInfo(modules);
    }

    /// <summary>The structure's string set: NUL-terminated strings, ended by an empty one. Index 1 is the first.</summary>
    private static IReadOnlyList<string> ReadStrings(ReadOnlySpan<byte> table, int offset)
    {
        var strings = new List<string>();
        int i = offset;
        while (i < table.Length && table[i] != 0)
        {
            int end = i;
            while (end < table.Length && table[end] != 0) end++;
            strings.Add(System.Text.Encoding.ASCII.GetString(table.Slice(i, end - i)).Trim());
            i = end + 1;
        }
        return strings;
    }

    private static string? String(IReadOnlyList<string> strings, ReadOnlySpan<byte> s, int offset) =>
        offset < s.Length && s[offset] is > 0 and var index && index <= strings.Count ? strings[index - 1] : null;

    private static SmbiosMemoryModule? ReadMemoryDevice(ReadOnlySpan<byte> s, IReadOnlyList<string> strings)
    {
        // Size (MB) at 0x0C; 0xFFFF means "see the extended size at 0x1C". Both zero: the slot is empty.
        ushort sizeMb = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(0x0C));
        if (sizeMb == 0) return null;
        uint size = sizeMb;
        if (sizeMb == 0xFFFF)
        {
            if (s.Length < 0x20) return null;
            size = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(0x1C)) & 0x7FFFFFFF; // low 31 bits = size in MB
            if (size == 0) return null;
        }
        ushort speed = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(0x15));
        ushort configured = s.Length >= 0x22 ? BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(0x20)) : (ushort)0;
        if (speed == 0xFFFF) speed = 0;
        if (configured == 0xFFFF) configured = 0;
        return new SmbiosMemoryModule(s[0x12], speed, configured)
        {
            Manufacturer = String(strings, s, 0x17),
            PartNumber = String(strings, s, 0x1A),
            SizeGb = size / 1024.0,
        };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint providerSignature, uint tableId, IntPtr buffer, uint bufferSize);
}
