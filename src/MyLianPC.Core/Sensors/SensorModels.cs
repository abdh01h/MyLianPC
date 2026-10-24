namespace MyLianPC.Core.Sensors;

/// <summary>Well-known metric keys used by the dashboard, LCD themes and fan curves.</summary>
public static class Metrics
{
    public const string CpuTemp = "cpu.temp", CpuLoad = "cpu.load", CpuClock = "cpu.clock", CpuPower = "cpu.power";
    public const string GpuTemp = "gpu.temp", GpuLoad = "gpu.load", GpuClock = "gpu.clock", GpuPower = "gpu.power", GpuFan = "gpu.fan";
    public const string GpuVramUsed = "gpu.vram.used", GpuVramTotal = "gpu.vram.total", GpuVramFree = "gpu.vram.free";
    public const string RamLoad = "ram.load", RamUsedGb = "ram.used", RamTotalGb = "ram.total";
    public const string RamTemp = "ram.temp", RamSpeed = "ram.speed", RamXmp = "ram.xmp";
    public const string StorageTemp = "storage.temp", StorageHealth = "storage.health";
    public const string CoolantTemp = "coolant.temp", PumpRpm = "pump.rpm";
    public const string AioFan1 = "aio.fan1", AioFan2 = "aio.fan2", AioFan3 = "aio.fan3";

    public sealed record Info(string Key, string Label, string Unit, string Group);

    public static readonly IReadOnlyList<Info> All =
    [
        new(CpuTemp, "CPU temperature", "°C", "CPU"),
        new(CpuLoad, "CPU utilization", "%", "CPU"),
        new(CpuClock, "CPU clock", "MHz", "CPU"),
        new(CpuPower, "CPU power", "W", "CPU"),
        new(GpuTemp, "GPU temperature", "°C", "GPU"),
        new(GpuLoad, "GPU utilization", "%", "GPU"),
        new(GpuClock, "GPU clock", "MHz", "GPU"),
        new(GpuPower, "GPU power", "W", "GPU"),
        new(GpuFan, "GPU fan", "RPM", "GPU"),
        new(GpuVramTotal, "VRAM total", "GB", "GPU"),
        new(GpuVramUsed, "VRAM used", "GB", "GPU"),
        new(GpuVramFree, "VRAM free", "GB", "GPU"),
        new(RamLoad, "Memory usage", "%", "Memory"),
        new(RamUsedGb, "Memory used", "GB", "Memory"),
        new(RamTemp, "Memory temperature", "°C", "Memory"),
        new(RamSpeed, "Memory speed", "MT/s", "Memory"),
        new(RamXmp, "EXPO / XMP", "", "Memory"),
        new(CoolantTemp, "Coolant temperature", "°C", "Cooler"),
        new(PumpRpm, "Pump speed", "RPM", "Cooler"),
        new(AioFan1, "Radiator fan 1", "RPM", "Cooler"),
        new(AioFan2, "Radiator fan 2", "RPM", "Cooler"),
        new(AioFan3, "Radiator fan 3", "RPM", "Cooler"),
        new(StorageTemp, "Drive temperature", "°C", "Storage"),
        new(StorageHealth, "Drive health", "%", "Storage"),
    ];

    public static Info? Find(string key) => All.FirstOrDefault(i => i.Key == key);
}

public sealed record SensorReading(string Key, string Label, string Unit, string Group, double? Value);

/// <summary>One drive of the PC: what the dashboard's Storage card lists.</summary>
public sealed record StorageDevice
{
    public string Name { get; init; } = "";
    public double? TemperatureCelsius { get; init; }
    /// <summary>Remaining life in percent (100 = as new); null when the drive does not report it.</summary>
    public double? HealthPercent { get; init; }
    /// <summary>The firmware's own verdict ("Healthy", "Warning", "Unhealthy") when the drive reports it.</summary>
    public string? HealthState { get; init; }
    /// <summary>Capacity in bytes, and how much of it is free (sum of the drive's volumes).</summary>
    public long? TotalBytes { get; init; }
    public long? FreeBytes { get; init; }
    /// <summary>Drive letters of the volumes on this disk, e.g. "C:, D:".</summary>
    public string? Volumes { get; init; }
}

/// <summary>Names and specifications of the parts, shown in the dashboard cards.</summary>
public sealed record HardwareNames
{
    public string? Cpu { get; init; }
    public string? Gpu { get; init; }
    public string? Memory { get; init; }
    public string? Storage { get; init; }
    public string? Cooler { get; init; }
}

/// <summary>Byte sizes the way Windows shows them (1024-based, labelled GB/TB).</summary>
public static class Sizes
{
    public static string Text(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.##} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{Math.Max(0, bytes)} B",
    };

    /// <summary>
    /// Drive capacity the way the label on the box reads it (decimal units, rounded): 931.5 GiB → "1 TB",
    /// 465.8 GiB → "500 GB". Used for the short drive labels.
    /// </summary>
    public static string Capacity(long bytes)
    {
        if (bytes <= 0) return "—";
        double gb = bytes / 1e9;
        if (gb >= 999.5) return $"{gb / 1000:0.##} TB";
        if (gb >= 1) return $"{gb:0} GB";
        return $"{bytes / 1e6:0} MB";
    }
}

/// <summary>One sampling of all sensors. Values are null when a sensor is unavailable.</summary>
public sealed record SensorSnapshot
{
    public DateTimeOffset TimeUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyDictionary<string, double?> Values { get; init; } = new Dictionary<string, double?>();
    /// <summary>Every reading found (including motherboard fans/temps, DIMM temps), for monitoring and diagnostics.</summary>
    public IReadOnlyList<SensorReading> All { get; init; } = [];
    /// <summary>Every physical drive with its temperature, remaining life and capacity.</summary>
    public IReadOnlyList<StorageDevice> Storage { get; init; } = [];
    /// <summary>What the hardware is: model names and sizes for the dashboard cards.</summary>
    public HardwareNames Names { get; init; } = new();

    public double? this[string key] => Values.TryGetValue(key, out var v) ? v : null;
}
