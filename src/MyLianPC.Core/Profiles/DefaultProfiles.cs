using MyLianPC.Core.Cooling;
using MyLianPC.Core.Lighting;

namespace MyLianPC.Core.Profiles;

/// <summary>Example profiles seeded on first run. Zone and fan target ids match the hardware adapters.</summary>
public static class DefaultProfiles
{
    public static IReadOnlyList<HardwareProfile> All() =>
    [
        Make("Gaming", new ZoneLighting { Effect = LightingEffect.Rainbow, Colors = [], Speed = 60 }, CoolingPresets.Performance, "gaming-dashboard"),
        Make("Work", Static("#FFFFFF", 60), CoolingPresets.Balanced, "minimal-temps"),
        Make("Silent", Static("#3050FF", 40), CoolingPresets.Silent, "minimal-clock"),
        Make("Night Mode", Static("#FF3000", 15), CoolingPresets.Silent, "minimal-clock", lcdBrightness: 20),
        Make("Custom RGB", Static("#00FFAA", 100), CoolingPresets.Balanced, "neon"),
        Make("Maximum Cooling", Static("#00A0FF", 100), CoolingPresets.Maximum, "multi-metric"),
    ];

    private static ZoneLighting Static(string hex, int brightness) =>
        new() { Effect = LightingEffect.Static, Colors = [RgbColor.ParseHex(hex)], Brightness = brightness };

    private static HardwareProfile Make(string name, ZoneLighting lighting, Func<TemperatureSource, FanCurve> cooling, string theme, int lcdBrightness = 100) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Lighting = new LightingSettings { SyncAll = lighting },
        Lcd = new LcdSettings { Mode = LcdMode.Theme, ThemeId = theme, Brightness = lcdBrightness },
        Cooling = new CoolingSettings
        {
            Curves = new Dictionary<string, FanCurve>
            {
                ["hydroshift:fans"] = cooling(TemperatureSource.Coolant),
                ["hydroshift:pump"] = cooling(TemperatureSource.Coolant),
            },
        },
    };
}
