using MyLianPC.Core.Lighting;

namespace MyLianPC.Core.Tests;

public class ColorAndLightingTests
{
    [Theory]
    [InlineData("#00FFAA", 0x00, 0xFF, 0xAA)]
    [InlineData("00ffaa", 0x00, 0xFF, 0xAA)]
    [InlineData("#f0a", 0xFF, 0x00, 0xAA)]
    [InlineData("  #123456 ", 0x12, 0x34, 0x56)]
    public void Parses_hex_forms(string text, int r, int g, int b) =>
        Assert.Equal(new RgbColor((byte)r, (byte)g, (byte)b), RgbColor.ParseHex(text));

    [Theory]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData(null)]
    public void Rejects_invalid_hex(string? text) => Assert.False(RgbColor.TryParseHex(text, out _));

    [Fact]
    public void Hex_round_trips() => Assert.Equal("#0A0B0C", new RgbColor(10, 11, 12).ToHex());

    [Fact]
    public void Brightness_scales_channels() =>
        Assert.Equal(new RgbColor(127, 50, 0), new RgbColor(255, 100, 0).WithBrightness(50));

    [Theory]
    [InlineData(0, 255, 0, 0)]
    [InlineData(120, 0, 255, 0)]
    [InlineData(240, 0, 0, 255)]
    [InlineData(360, 255, 0, 0)]
    public void Hsv_primaries(double hue, int r, int g, int b) =>
        Assert.Equal(new RgbColor((byte)r, (byte)g, (byte)b), RgbColor.FromHsv(hue));

    [Fact]
    public void Zone_validation_rejects_unsupported_effect_and_too_many_colors()
    {
        var caps = new ZoneCapabilities(new HashSet<LightingEffect> { LightingEffect.Static, LightingEffect.Off }, true, false, 1, 1);
        var req = new ZoneLighting { Effect = LightingEffect.Wave, Colors = [RgbColor.White, RgbColor.Black] };
        var problems = req.Validate(caps);
        Assert.Contains(problems, p => p.Contains("not supported"));
        Assert.Contains(problems, p => p.Contains("at most 1"));
    }
}
