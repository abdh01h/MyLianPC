using MyLianPC.Core.Devices;
using MyLianPC.Core.Ipc;
using MyLianPC.Core.Lighting;
using MyLianPC.Core.Profiles;
using MyLianPC.Core.Sensors;
using MyLianPC.Core.Settings;
using MyLianPC.Lcd;

namespace MyLianPC.Core.Tests;

public class EffectFrameTests
{
    [Theory]
    [InlineData(LightingEffect.Off, 1)]
    [InlineData(LightingEffect.Static, 1)]
    [InlineData(LightingEffect.Gradient, 1)]
    [InlineData(LightingEffect.Breathing, EffectFrames.MaxFrames)]
    [InlineData(LightingEffect.Rainbow, EffectFrames.MaxFrames)]
    [InlineData(LightingEffect.RainbowWave, EffectFrames.MaxFrames)]
    [InlineData(LightingEffect.Wave, EffectFrames.MaxFrames)]
    [InlineData(LightingEffect.ColorCycle, EffectFrames.MaxFrames)]
    [InlineData(LightingEffect.Flash, EffectFrames.MaxFrames)]
    public void Frame_counts_fit_the_ring_upload_limit(LightingEffect fx, int frames)
    {
        var anim = EffectFrames.Generate(new ZoneLighting { Effect = fx, Colors = [RgbColor.ParseHex("#FF0000"), RgbColor.ParseHex("#0000FF")] }, 24);
        Assert.Equal(frames, anim.Frames.Count);
        Assert.All(anim.Frames, f => Assert.Equal(24, f.Length));
        Assert.InRange(anim.FrameIntervalMs, 1, 255);
    }

    [Fact]
    public void Gradient_runs_from_first_to_last_color()
    {
        var f = EffectFrames.Generate(new ZoneLighting { Effect = LightingEffect.Gradient, Colors = [RgbColor.ParseHex("#FF0000"), RgbColor.ParseHex("#0000FF")] }, 10).Frames[0];
        Assert.Equal(RgbColor.ParseHex("#FF0000"), f[0]);
        Assert.Equal(RgbColor.ParseHex("#0000FF"), f[^1]);
    }

    [Fact]
    public void Brightness_scales_static_color_and_off_is_black()
    {
        var half = EffectFrames.Generate(new ZoneLighting { Effect = LightingEffect.Static, Colors = [RgbColor.White], Brightness = 50 }, 3).Frames[0];
        Assert.All(half, c => Assert.Equal(new RgbColor(127, 127, 127), c));
        var off = EffectFrames.Generate(new ZoneLighting { Effect = LightingEffect.Off }, 3).Frames[0];
        Assert.All(off, c => Assert.Equal(RgbColor.Black, c));
    }

    [Fact]
    public void Faster_speed_means_shorter_period() =>
        Assert.True(EffectFrames.PeriodMs(100) < EffectFrames.PeriodMs(0));
}

public class LcdTests
{
    [Fact]
    public void Default_lcd_settings_are_valid() => Assert.Empty(new LcdSettings().Validate());

    [Theory]
    [InlineData(150, 1000, 1.0, "Brightness")]
    [InlineData(100, 100, 1.0, "refresh")]
    [InlineData(100, 1000, 3.0, "font scale")]
    public void Rejects_out_of_range_values(int brightness, int refresh, double scale, string expected) =>
        Assert.Contains(new LcdSettings { Brightness = brightness, RefreshMilliseconds = refresh, FontScale = scale }.Validate(),
            p => p.Contains(expected, StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData("..\\secret.png")]
    [InlineData("C:\\Windows\\x.png")]
    [InlineData("a/b.gif")]
    public void Rejects_media_paths_that_escape_the_media_folder(string file) =>
        Assert.Contains(new LcdSettings { Mode = LcdMode.Image, MediaFile = file }.Validate(), p => p.Contains("media file", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Image_mode_requires_a_file() =>
        Assert.Contains(new LcdSettings { Mode = LcdMode.Image }.Validate(), p => p.Contains("Choose an image"));

    public static TheoryData<string> ThemeIds()
    {
        var d = new TheoryData<string>();
        foreach (var t in LcdRenderer.Themes) d.Add(t.Id);
        return d;
    }

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void Every_theme_renders_a_panel_sized_frame_within_the_upload_limit(string theme)
    {
        using var bmp = LcdRenderer.Render(new LcdSettings { ThemeId = theme }, LcdMetrics.Sample);
        Assert.Equal(480, bmp.Width);
        Assert.Equal(480, bmp.Height);
        Assert.InRange(LcdRenderer.EncodeJpeg(bmp).Length, 1000, 150_000);
    }

    [Fact]
    public void A_snapshot_with_drives_round_trips_through_the_wire_format()
    {
        var snapshot = new SensorSnapshot
        {
            Values = new Dictionary<string, double?> { [Metrics.StorageTemp] = 41, [Metrics.StorageHealth] = 97 },
            Storage = [new StorageDevice { Name = "Samsung SSD 990 PRO 2TB", TemperatureCelsius = 41, HealthPercent = 97 }],
        };
        var back = IpcJson.From<SensorSnapshot>(IpcJson.ToElement(snapshot))!;
        Assert.Equal(41, back[Metrics.StorageTemp]);
        var drive = Assert.Single(back.Storage);
        Assert.Equal("Samsung SSD 990 PRO 2TB", drive.Name);
        Assert.Equal(41, drive.TemperatureCelsius);
        Assert.Equal(97, drive.HealthPercent);
        Assert.Contains(Metrics.All, m => m.Key == Metrics.StorageTemp && m.Group == "Storage");
        Assert.Contains(Metrics.All, m => m.Key == Metrics.StorageHealth && m.Group == "Storage");
    }

    [Fact]
    public void Missing_sensors_render_as_dashes_not_zero()
    {
        Assert.Equal("—°", LcdRenderer.FormatTemp(null, TemperatureUnit.Celsius));
        Assert.Equal("—", LcdRenderer.Format(Core.Sensors.Metrics.CpuLoad, null, TemperatureUnit.Celsius));
        Assert.Equal("122°", LcdRenderer.FormatTemp(50, TemperatureUnit.Fahrenheit));
    }
}

public class IpcTests
{
    [Fact]
    public void Profile_round_trips_through_the_wire_format()
    {
        var p = DefaultProfiles.All()[0];
        var back = IpcJson.From<HardwareProfile>(IpcJson.ToElement(p))!;
        Assert.Equal(p.Name, back.Name);
        Assert.Equal(p.Lighting!.SyncAll!.Effect, back.Lighting!.SyncAll!.Effect);
        Assert.Equal(p.Cooling!.Curves.Keys, back.Cooling!.Curves.Keys);
        Assert.Empty(back.Validate());
    }

    [Fact]
    public void Status_with_devices_round_trips()
    {
        var s = new ServiceStatus
        {
            Version = "1.0.0",
            Devices = [new DeviceStatus { Id = "x", Name = "Dev", Manufacturer = "M", Kind = "K", State = DeviceState.Unresponsive, Zones = [new ZoneInfo { Id = "z", DeviceId = "x", Name = "Zone", Effects = [LightingEffect.Static] }] }],
        };
        var back = IpcJson.From<ServiceStatus>(IpcJson.ToElement(s))!;
        Assert.Equal(DeviceState.Unresponsive, back.Devices[0].State);
        Assert.Equal(LightingEffect.Static, back.Devices[0].Zones[0].Effects[0]);
    }

    [Fact]
    public void Device_settings_are_clamped()
    {
        var s = new DeviceSettings { SensorIntervalMs = 10, ZoneLedCounts = new Dictionary<string, int> { ["a"] = 9999, ["b"] = 0 } }.Normalized();
        Assert.Equal(500, s.SensorIntervalMs);
        Assert.Equal(256, s.ZoneLedCounts["a"]);
        Assert.Equal(1, s.ZoneLedCounts["b"]);
    }
}
