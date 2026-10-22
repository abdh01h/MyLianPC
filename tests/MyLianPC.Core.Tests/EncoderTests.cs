using System.Buffers.Binary;
using MyLianPC.Hardware.Corsair;
using MyLianPC.Hardware.LianLi;
using P = MyLianPC.Hardware.LianLi.HydroShift2Protocol;

namespace MyLianPC.Core.Tests;

public class EncoderTests
{
    [Theory]
    [InlineData("abcabcabcabcabc---hydroshift---hydroshift")]
    [InlineData("x")]
    [InlineData("0123456789abcdefghijklmnopqrstuvwxyz")]
    public void TinyUz_round_trips_text(string s)
    {
        var input = System.Text.Encoding.ASCII.GetBytes(s);
        Assert.Equal(input, TinyUz.Decompress(TinyUz.Compress(input), input.Length));
    }

    [Fact]
    public void TinyUz_round_trips_a_full_rainbow_animation()
    {
        var rnd = new Random(42);
        var input = new byte[48 * 72];
        for (int i = 0; i < input.Length; i++) input[i] = (byte)(i % 7 == 0 ? rnd.Next(256) : i * 31 % 251);
        Assert.Equal(input, TinyUz.Decompress(TinyUz.Compress(input), input.Length));
    }

    [Fact]
    public void Ring_packet_has_header_compressed_payload_and_trailer_fields()
    {
        var frame = Enumerable.Repeat(new byte[] { 255, 0, 0 }, 24).SelectMany(x => x).ToArray();
        var pkt = P.RingFrames([frame], 50, timestamp: 1);
        var plain = P.DecryptHeader(pkt);
        Assert.Equal(0xFC, plain[0]);
        var payloadLen = (int)BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(12));
        Assert.Equal(pkt.Length - 512, payloadLen);
        var payload = pkt[512..];
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(payload.Length - 4)));
        Assert.Equal(50, payload[^2]);
        Assert.Equal(24, payload[^1]);
        Assert.Equal(frame, TinyUz.Decompress(payload[..^4], frame.Length));
    }

    [Fact]
    public void Ring_rejects_too_many_frames()
    {
        var frame = new byte[72];
        Assert.Throws<ArgumentException>(() => P.RingFrames(Enumerable.Repeat(frame, P.MaxRingFrames + 1).ToList(), 50));
    }

    [Fact]
    public void Ring_with_three_fans_declares_96_leds()
    {
        var frame = new byte[(24 + 3 * 24) * 3];
        var payload = P.RingFrames([frame, frame], 40, timestamp: 1)[512..];
        Assert.Equal(96, payload[^1]);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(payload.Length - 4)));
    }

    [Fact]
    public void Ring_rejects_partial_fans_and_oversized_animations()
    {
        Assert.Throws<ArgumentException>(() => P.RingFrames([new byte[(24 + 10) * 3]], 40));
        var fans3 = new byte[96 * 3];
        Assert.Throws<ArgumentException>(() => P.RingFrames(Enumerable.Repeat(fans3, 15).ToList(), 40)); // 15 x 288 B > 4 KB
    }

    [Theory]
    [InlineData("123456789", 0xF4)] // CRC-8/SMBUS check value
    [InlineData("", 0x00)]
    public void Crc8_matches_smbus_check_value(string s, int expected) =>
        Assert.Equal((byte)expected, CorsairDramController.Crc8(System.Text.Encoding.ASCII.GetBytes(s)));
}
