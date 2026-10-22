using MyLianPC.Core.Cooling;
using MyLianPC.Core.Lighting;
using MyLianPC.Core.Sensors;

namespace MyLianPC.Core.Profiles;

/// <summary>
/// A machine-wide hardware configuration shared by every Windows user. Any section may be null,
/// meaning "leave that part of the hardware alone" when the profile is applied.
/// </summary>
public sealed record HardwareProfile
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public LightingSettings? Lighting { get; init; }
    public LcdSettings? Lcd { get; init; }
    public CoolingSettings? Cooling { get; init; }
    public DateTimeOffset ModifiedUtc { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) problems.Add("Profile name is required.");
        else if (Name.Length > 64) problems.Add("Profile name must be 64 characters or fewer.");
        if (Cooling is not null)
            foreach (var (target, curve) in Cooling.Curves)
                problems.AddRange(curve.Validate().Select(p => $"Cooling '{target}': {p}"));
        if (Lcd is not null) problems.AddRange(Lcd.Validate());
        return problems;
    }
}

public sealed record LightingSettings
{
    /// <summary>When set, every zone uses this and <see cref="Zones"/> is ignored.</summary>
    public ZoneLighting? SyncAll { get; init; }

    /// <summary>Per-zone settings keyed by stable zone id (e.g. "fusion2:D_LED1", "corsair-dram:0", "hydroshift:ring").</summary>
    public IReadOnlyDictionary<string, ZoneLighting> Zones { get; init; } = new Dictionary<string, ZoneLighting>();

    public ZoneLighting? For(string zoneId) => SyncAll ?? Zones.GetValueOrDefault(zoneId);
}

public enum LcdMode { Theme, Image, Animation, Off }

public enum GaugeStyle { Arc, Ring, Bar }

public enum LcdWidgetType { Metric, Gauge, Clock, Date, Text, Bar }

/// <summary>One movable element of the "custom" LCD layout. Coordinates are panel pixels (0-480).</summary>
public sealed record LcdWidget
{
    public LcdWidgetType Type { get; init; } = LcdWidgetType.Metric;
    public string Metric { get; init; } = Sensors.Metrics.CpuTemp;
    public string? Label { get; init; }
    public string? Text { get; init; }
    public float X { get; init; } = 240;
    public float Y { get; init; } = 240;
    public float Size { get; init; } = 48;
    public RgbColor? Color { get; init; }
}

public sealed record LcdSettings
{
    public const int MaxWidgets = 16;

    public LcdMode Mode { get; init; } = LcdMode.Theme;
    public string ThemeId { get; init; } = "minimal-temps";
    public RgbColor Background { get; init; } = RgbColor.ParseHex("#05070C");
    public RgbColor TextColor { get; init; } = RgbColor.ParseHex("#E8ECF4");
    public RgbColor Accent { get; init; } = RgbColor.ParseHex("#00E0B0");
    public RgbColor Secondary { get; init; } = RgbColor.ParseHex("#7A5CFF");
    public string FontFamily { get; init; } = "Segoe UI";
    public double FontScale { get; init; } = 1.0;
    public Settings.TemperatureUnit Unit { get; init; } = Settings.TemperatureUnit.Celsius;
    /// <summary>Show the clock as 24-hour ("17:04"); off shows 12-hour time with AM/PM ("5:04 PM").</summary>
    public bool Clock24Hour { get; init; } = true;
    public GaugeStyle GaugeStyle { get; init; } = GaugeStyle.Arc;
    /// <summary>Metrics shown by the multi-metric and dashboard themes (in order).</summary>
    public IReadOnlyList<string> Metrics { get; init; } =
        [Sensors.Metrics.CpuTemp, Sensors.Metrics.GpuTemp, Sensors.Metrics.CpuLoad, Sensors.Metrics.GpuLoad, Sensors.Metrics.CoolantTemp, Sensors.Metrics.RamLoad];
    public IReadOnlyList<LcdWidget> Widgets { get; init; } = DefaultWidgets;
    /// <summary>Image or GIF stored by the service in its media folder (file name only).</summary>
    public string? MediaFile { get; init; }
    /// <summary>Draw the clock and CPU temperature over images/animations.</summary>
    public bool MediaOverlay { get; init; }
    public int Brightness { get; init; } = 100;
    public int RotationDegrees { get; init; }
    public int RefreshMilliseconds { get; init; } = 1000;

    public static readonly IReadOnlyList<LcdWidget> DefaultWidgets =
    [
        new() { Type = LcdWidgetType.Clock, X = 240, Y = 120, Size = 56 },
        new() { Type = LcdWidgetType.Gauge, Metric = Sensors.Metrics.CpuTemp, Label = "CPU", X = 160, Y = 270, Size = 110 },
        new() { Type = LcdWidgetType.Gauge, Metric = Sensors.Metrics.GpuTemp, Label = "GPU", X = 320, Y = 270, Size = 110 },
        new() { Type = LcdWidgetType.Metric, Metric = Sensors.Metrics.CoolantTemp, Label = "Coolant", X = 240, Y = 400, Size = 30 },
    ];

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Brightness is < 0 or > 100) problems.Add("LCD brightness must be between 0 and 100.");
        if (RotationDegrees is not (0 or 90 or 180 or 270)) problems.Add("LCD rotation must be 0, 90, 180 or 270.");
        if (RefreshMilliseconds is < 500 or > 60_000) problems.Add("LCD refresh must be between 500 ms and 60 s.");
        if (FontScale is < 0.5 or > 2.0) problems.Add("LCD font scale must be between 0.5 and 2.");
        if (Widgets.Count > MaxWidgets) problems.Add($"The custom layout supports at most {MaxWidgets} widgets.");
        if (Widgets.Any(w => w.X is < 0 or > 480 || w.Y is < 0 or > 480 || w.Size is < 8 or > 300)) problems.Add("Widget positions must be within the panel (0-480) and sizes 8-300.");
        if (Mode is LcdMode.Image or LcdMode.Animation && string.IsNullOrWhiteSpace(MediaFile))
            problems.Add("Choose an image or animation file for this LCD mode.");
        if (MediaFile is { } f && (f.IndexOfAny(['/', '\\', ':']) >= 0 || f.Contains("..")))
            problems.Add("Invalid media file name.");
        return problems;
    }
}

public sealed record CoolingSettings
{
    /// <summary>Curves keyed by fan target id (e.g. "hydroshift:pump", "hydroshift:fans", "mb:SYS_FAN1").</summary>
    public IReadOnlyDictionary<string, FanCurve> Curves { get; init; } = new Dictionary<string, FanCurve>();
}
