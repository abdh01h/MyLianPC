using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyLianPC.Core.Lighting;

/// <summary>An 8-bit-per-channel color as sent to lighting hardware. Serialized as "#RRGGBB".</summary>
[JsonConverter(typeof(RgbColorJsonConverter))]
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static readonly RgbColor Black = new(0, 0, 0);
    public static readonly RgbColor White = new(255, 255, 255);

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public override string ToString() => ToHex();

    /// <summary>Accepts "#RRGGBB", "RRGGBB", "#RGB" or "RGB" (case-insensitive).</summary>
    public static bool TryParseHex(string? text, out RgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
            return false;
        color = new RgbColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static RgbColor ParseHex(string text) =>
        TryParseHex(text, out var c) ? c : throw new FormatException($"'{text}' is not a valid hex color.");

    /// <summary>Scales the color by a brightness percentage (0-100), for devices without a hardware brightness control.</summary>
    public RgbColor WithBrightness(int percent)
    {
        var p = Math.Clamp(percent, 0, 100);
        return new RgbColor((byte)(R * p / 100), (byte)(G * p / 100), (byte)(B * p / 100));
    }

    /// <summary>Linear interpolation between two colors; t is clamped to [0,1].</summary>
    public static RgbColor Lerp(RgbColor a, RgbColor b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new RgbColor(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    /// <summary>HSV to RGB with hue in degrees and saturation/value in [0,1]. Used by rainbow effects.</summary>
    public static RgbColor FromHsv(double hue, double saturation = 1, double value = 1)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);
        double c = value * saturation, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = value - c;
        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return new RgbColor((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}

public sealed class RgbColorJsonConverter : JsonConverter<RgbColor>
{
    public override RgbColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        RgbColor.TryParseHex(reader.GetString(), out var c) ? c : throw new JsonException("Expected a color in #RRGGBB form.");

    public override void Write(Utf8JsonWriter writer, RgbColor value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToHex());
}
