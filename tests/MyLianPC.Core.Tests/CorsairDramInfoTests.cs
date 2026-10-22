using MyLianPC.Hardware.Corsair;

namespace MyLianPC.Core.Tests;

/// <summary>
/// A Corsair DRAM module whose device info cannot be read answers with an all-zero block, and the CRC-8 of an
/// all-zero block is 0 too. Accepting that block made the controller look like a module stuck in an abnormal
/// firmware mode (protocol 0), and the recovery write that followed reset a healthy module to its default colour.
/// </summary>
public class CorsairDramInfoTests
{
    private static byte[] Info(ushort vid = 0x1B1C, ushort pid = 0x0000, byte firmware9 = 1, byte firmware8 = 1, byte protocol = 4)
    {
        var info = new byte[32];
        info[0] = (byte)vid;
        info[1] = (byte)(vid >> 8);
        info[2] = (byte)pid;
        info[3] = (byte)(pid >> 8);
        info[8] = firmware8;
        info[9] = firmware9;
        info[28] = protocol;
        return info;
    }

    [Fact]
    public void An_all_zero_block_from_a_failed_read_is_not_accepted()
    {
        var zeros = new byte[32];
        Assert.Equal(0, CorsairDramController.Crc8(zeros)); // the checksum of a failed read matches by accident
        Assert.False(CorsairDramController.IsVerifiableDeviceInfo(zeros, 0));
        Assert.False(CorsairDramController.IsVerifiableDeviceInfo(zeros, CorsairDramController.Crc8(zeros)));
    }

    [Fact]
    public void A_complete_block_with_the_right_checksum_is_accepted()
    {
        var info = Info();
        Assert.True(CorsairDramController.IsVerifiableDeviceInfo(info, CorsairDramController.Crc8(info)));
    }

    [Fact]
    public void A_block_with_a_wrong_or_missing_checksum_is_rejected()
    {
        var info = Info();
        byte crc = CorsairDramController.Crc8(info);
        Assert.False(CorsairDramController.IsVerifiableDeviceInfo(info, crc + 1));
        Assert.False(CorsairDramController.IsVerifiableDeviceInfo(info, -1));
    }

    [Fact]
    public void A_short_block_is_rejected()
    {
        var info = Info();
        Assert.False(CorsairDramController.IsVerifiableDeviceInfo(info.AsSpan(0, 20), CorsairDramController.Crc8(info.AsSpan(0, 20))));
    }

    [Fact]
    public void A_stored_colour_buffer_is_compared_with_what_was_requested()
    {
        var colors = new[] { new MyLianPC.Core.Lighting.RgbColor(0x00, 0x0D, 0x29), new MyLianPC.Core.Lighting.RgbColor(0xFF, 0x80, 0x00) };
        var stored = new byte[12 * 4];
        for (int i = 0; i < 12; i++)
        {
            var c = colors[Math.Min(i, colors.Length - 1)];
            stored[i * 4] = c.R; stored[i * 4 + 1] = c.G; stored[i * 4 + 2] = c.B; stored[i * 4 + 3] = 0xFF;
        }
        Assert.True(CorsairDramController.ColorsMatch(stored, colors, 12));

        // One LED still holding the previous colour (the "red edges" look) must not be reported as stored.
        stored[9 * 4] = 0xFF; stored[9 * 4 + 1] = 0x00; stored[9 * 4 + 2] = 0x00;
        Assert.False(CorsairDramController.ColorsMatch(stored, colors, 12));

        Assert.False(CorsairDramController.ColorsMatch(stored.AsSpan(0, 8), colors, 12));
        Assert.False(CorsairDramController.ColorsMatch(stored, [], 12));
    }
}
