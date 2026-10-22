using System.Text.Json;

namespace MyLianPC.Core.Settings;

/// <summary>
/// Machine-wide hardware configuration that is not part of a profile: LED counts of plain ARGB headers,
/// which motherboard fan headers the user allowed us to control, and similar. Stored next to the profiles.
/// </summary>
public sealed record DeviceSettings
{
    /// <summary>LED count per addressable zone id (e.g. "fusion2:argb1"). Only used for per-LED effects (gradients).</summary>
    public IReadOnlyDictionary<string, int> ZoneLedCounts { get; init; } = new Dictionary<string, int>();

    /// <summary>Motherboard fan header ids the user explicitly opted into controlling. Everything else is monitor-only.</summary>
    public IReadOnlyList<string> ControlledMotherboardFans { get; init; } = [];

    /// <summary>Save motherboard effects to the controller's flash so they survive power loss without software.</summary>
    public bool SaveMotherboardLightingToFlash { get; init; } = true;

    /// <summary>
    /// Also light the radiator fans wired to the HydroShift II pump head (experimental); when off, their LEDs are
    /// switched off instead of following the ring. The service turns it off by itself if the cooler does not accept it.
    /// </summary>
    public bool CoolerFanLighting { get; init; }

    /// <summary>Sensor polling interval for the service (ms).</summary>
    public int SensorIntervalMs { get; init; } = 1000;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyLianPC", "devices.json");

    public DeviceSettings Normalized() => this with
    {
        SensorIntervalMs = Math.Clamp(SensorIntervalMs, 500, 10_000),
        ZoneLedCounts = ZoneLedCounts.ToDictionary(kv => kv.Key, kv => Math.Clamp(kv.Value, 1, 256)),
    };

    public static DeviceSettings Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? (JsonSerializer.Deserialize<DeviceSettings>(File.ReadAllText(path), Json.Options) ?? new()).Normalized()
                : new();
        }
        catch (JsonException) { return new(); }
    }

    public void Save(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, Json.Options));
}
