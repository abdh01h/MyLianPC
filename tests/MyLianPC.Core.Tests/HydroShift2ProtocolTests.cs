using System.Buffers.Binary;
using MyLianPC.Hardware.LianLi;
using P = MyLianPC.Hardware.LianLi.HydroShift2Protocol;

namespace MyLianPC.Core.Tests;

public class HydroShift2ProtocolTests
{
    [Fact]
    public void Header_is_512_bytes_with_trailer_and_decrypts_to_expected_plaintext()
    {
        var h = P.Header(0x65, [0, 1, 0x23, 0x45], timestamp: 100_001);
        Assert.Equal(512, h.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0xA1, 0x1A }, h[504..]);

        var plain = P.DecryptHeader(h);
        var expected = new byte[500];
        expected[0] = 0x65; expected[2] = 0x1A; expected[3] = 0x6D;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(4), 100_001);
        expected[8] = 0; expected[9] = 1; expected[10] = 0x23; expected[11] = 0x45;
        Assert.Equal(expected, plain);
    }

    [Fact]
    public void Image_packet_carries_big_endian_length_then_payload()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var pkt = P.Image(jpeg, png: false, timestamp: 1);
        Assert.Equal(512 + 4, pkt.Length);
        Assert.Equal(jpeg, pkt[512..]);
        var plain = P.DecryptHeader(pkt);
        Assert.Equal(0x65, plain[0]);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(8)));
    }

    [Theory]
    [InlineData(new byte[0], 0x0000)]
    [InlineData(new byte[] { 0x01 }, 0x1021)]
    [InlineData(new byte[] { (byte)'1', (byte)'2', (byte)'3', (byte)'4', (byte)'5', (byte)'6', (byte)'7', (byte)'8', (byte)'9' }, 0x31C3)]
    public void Crc16_matches_reference_vectors(byte[] data, int expected) => Assert.Equal((ushort)expected, P.Crc16(data));

    [Theory]
    [InlineData(1000, 1500)] // clamped to 1600 RPM minimum
    [InlineData(1600, 1500)]
    [InlineData(2000, 841)]
    [InlineData(2001, 838)]
    [InlineData(2500, 10)]
    [InlineData(9999, 10)]   // clamped to Circle maximum
    public void Pump_mapping_matches_reference(int rpm, int expected) => Assert.Equal((ushort)expected, P.PumpControlValue(rpm));

    [Fact]
    public void SyncPumpFan_block_layout_and_crc()
    {
        var c = P.SyncPumpFanParameters(2000, 10, 20, 30);
        Assert.Equal(new byte[] { 0xFF, 0x0F, 0xA2, 0x00 }, c[..4]);
        Assert.Equal(841, BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(4)));
        Assert.Equal(new byte[] { 10, 20, 30 }, c[6..9]);
        Assert.Equal(P.Crc16(c.AsSpan(0, 12)), BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(12)));
    }

    [Fact]
    public void Parses_telemetry_reply()
    {
        var r = new byte[512];
        r[0] = 0xFA; r[13] = 31;
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(14), 1200);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(16), 1210);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(18), 1190);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(20), 2450);
        var t = P.ParseTelemetry(r)!;
        Assert.Equal(31, t.CoolantC);
        Assert.Equal([1200, 1210, 1190], t.FanRpm);
        Assert.Equal(2450, t.PumpRpm);
        Assert.Null(t.LightingFanCount);
        Assert.Null(P.ParseTelemetry(new byte[10]));
        r[8] = 0x0A; r[9] = 3;
        Assert.Equal(3, P.ParseTelemetry(r)!.LightingFanCount);
    }

    [Fact]
    public void Parses_firmware_version()
    {
        var r = new byte[512];
        "lianlih2_0003_0020"u8.CopyTo(r.AsSpan(8));
        Assert.Equal("lianlih2_0003_0020", P.ParseVersion(r));
    }
}
