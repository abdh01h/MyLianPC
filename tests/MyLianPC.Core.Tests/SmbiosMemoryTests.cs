using MyLianPC.Core.Sensors;
using MyLianPC.Hardware.Sensors;

namespace MyLianPC.Core.Tests;

/// <summary>
/// The memory speed and EXPO/XMP readings come from the firmware's SMBIOS table. The raw table handed out by the
/// firmware interface is prefixed with an eight-byte header; both that form and a bare structure table are read.
/// </summary>
public class SmbiosMemoryTests
{
    private static byte[] MemoryDevice(ushort speed, ushort configured, byte memoryType = 0x22, bool emptySlot = false, string[]? strings = null)
    {
        var memory = new byte[0x28];
        memory[0] = 17;
        memory[1] = 0x28;
        if (!emptySlot)
        {
            memory[0x0D] = 0x40; // 16384 MB = 16 GB
        }
        memory[0x12] = memoryType;
        BitConverter.GetBytes(speed).CopyTo(memory, 0x15);
        BitConverter.GetBytes(configured).CopyTo(memory, 0x20);
        if (strings is { Length: > 0 })
        {
            memory[0x17] = 1; // manufacturer string
            memory[0x1A] = 2; // part number string
        }

        var bytes = new List<byte>(memory);
        if (strings is { Length: > 0 })
        {
            foreach (var s in strings) { bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(s)); bytes.Add(0); }
            bytes.Add(0); // end of the string set
        }
        else bytes.AddRange(new byte[] { 0, 0 });
        return bytes.ToArray();
    }

    private static byte[] Structures(bool header, params byte[][] devices)
    {
        var body = devices.SelectMany(d => d).Concat(new byte[] { 127, 4, 0, 0, 0, 0 }).ToArray();
        if (!header) return body;
        var prefix = new byte[8];
        prefix[1] = 3; // SMBIOS 3.x
        prefix[2] = 8;
        BitConverter.GetBytes((uint)body.Length).CopyTo(prefix, 4);
        return prefix.Concat(body).ToArray();
    }

    private static byte[] Table(ushort speed, ushort configured, byte memoryType = 0x22, bool header = true, bool emptySlot = false, string[]? strings = null) =>
        Structures(header, MemoryDevice(speed, configured, memoryType, emptySlot, strings));

    [Fact]
    public void An_overclocked_DDR5_module_is_reported_as_EXPO_on()
    {
        var info = SmbiosMemoryInfo.Parse(Table(speed: 6000, configured: 6000));
        Assert.Equal(1, info.Count);
        Assert.Equal(6000, info.ConfiguredMtps);
        Assert.True(info.XmpExpoActive);
    }

    [Fact]
    public void A_DDR5_module_at_the_JEDEC_baseline_is_reported_as_EXPO_off()
    {
        var info = SmbiosMemoryInfo.Parse(Table(speed: 4800, configured: 4800));
        Assert.Equal(1, info.Count);
        Assert.False(info.XmpExpoActive);
    }

    [Fact]
    public void A_table_without_the_firmware_header_is_read_as_well()
    {
        var info = SmbiosMemoryInfo.Parse(Table(speed: 5600, configured: 5600, memoryType: 0x1A, header: false));
        Assert.Equal(1, info.Count);
        Assert.Equal(5600, info.ConfiguredMtps);
        Assert.True(info.XmpExpoActive); // DDR4 baseline is 2133
    }

    [Fact]
    public void An_empty_slot_is_ignored()
    {
        Assert.Equal(0, SmbiosMemoryInfo.Parse(Table(speed: 4800, configured: 4800, emptySlot: true)).Count);
    }

    [Fact]
    public void A_table_without_memory_devices_yields_no_modules()
    {
        Assert.Equal(0, SmbiosMemoryInfo.Parse([127, 4, 0, 0, 0, 0]).Count);
        Assert.Equal(0, SmbiosMemoryInfo.Parse([]).Count);
    }

    [Fact]
    public void The_maker_part_number_and_size_become_the_card_spec_line()
    {
        var info = SmbiosMemoryInfo.Parse(Table(speed: 4800, configured: 4800, strings: ["Corsair", "CMH32GX5M2E6000Z36"]));
        Assert.Equal("Corsair", info.Modules[0].Manufacturer);
        Assert.Equal("CMH32GX5M2E6000Z36", info.Modules[0].PartNumber);
        Assert.Equal(16, info.Modules[0].SizeGb);
        Assert.Equal("Corsair · CMH32GX5M2E6000Z36 · 16 GB", info.Summary);
    }

    [Fact]
    public void Two_module_kit_is_summarised_as_count_times_size()
    {
        var info = SmbiosMemoryInfo.Parse(Structures(true,
            MemoryDevice(6000, 6000, strings: ["Corsair", "CMH32GX5M2E6000Z36"]),
            MemoryDevice(6000, 6000, strings: ["Corsair", "CMH32GX5M2E6000Z36"])));
        Assert.Equal(2, info.Count);
        Assert.Equal("Corsair · CMH32GX5M2E6000Z36 · 2 × 16 GB", info.Summary);
    }
}

/// <summary>Drive and card sizes are shown the way Windows shows them.</summary>
public class SizeTextTests
{
    [Fact]
    public void Sizes_use_binary_units_like_windows()
    {
        Assert.Equal("931.5 GB", Sizes.Text(1_000_204_886_016));
        Assert.Equal("16 GB", Sizes.Text(16L << 30));
        Assert.Equal("1.5 TB", Sizes.Text((long)(1.5 * (1L << 40))));
        Assert.Equal("512 MB", Sizes.Text(512L << 20));
        Assert.Equal("0 B", Sizes.Text(0));
    }

    [Fact]
    public void Capacities_use_the_size_printed_on_the_drive()
    {
        Assert.Equal("1 TB", Sizes.Capacity(1_000_204_886_016));
        Assert.Equal("500 GB", Sizes.Capacity(500_105_674_752));
        Assert.Equal("2 TB", Sizes.Capacity(2_000_398_934_016));
        Assert.Equal("808 GB", Sizes.Capacity(807_686_770_688));
        Assert.Equal("3.5 TB", Sizes.Capacity(3_499_932_171_366));
        Assert.Equal("—", Sizes.Capacity(0));
    }
}
