using System.Text.Json;

namespace MyLianPC.Core.Settings;

public enum AppearanceMode { Dark, Light, System }

public enum TemperatureUnit { Celsius, Fahrenheit }

/// <summary>
/// Per-Windows-user interface preferences. Deliberately has no reference to hardware profiles:
/// nothing here may change lighting, LCD or cooling state.
/// </summary>
public sealed record UserPreferences
{
    public AppearanceMode Appearance { get; init; } = AppearanceMode.Dark;
    public TemperatureUnit TemperatureUnit { get; init; } = TemperatureUnit.Celsius;
    public bool MinimizeToTray { get; init; } = true;
    public bool LaunchAtLogin { get; init; }
    public int DashboardRefreshMilliseconds { get; init; } = 1000;
    /// <summary>Interface scale set with Ctrl+Plus / Ctrl+Minus; kept inside the range the layout is designed for.</summary>
    public double UiScale { get; init; } = 1.0;
    public const double MinUiScale = 0.8, MaxUiScale = 1.4;
    public IReadOnlyList<string> DashboardMetrics { get; init; } = Sensors.Metrics.All.Select(m => m.Key).ToList();
    public IReadOnlyList<Lighting.RgbColor> SavedColors { get; init; } = [];
    /// <summary>True once this account was asked about turning off Windows Dynamic Lighting for the motherboard.</summary>
    public bool DynamicLightingPromptShown { get; init; }
    /// <summary>Null when the file was written before the memory temperature/speed and EXPO/XMP metrics existed.</summary>
    public int? PreferencesVersion { get; init; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyLianPC", "preferences.json");

    public static UserPreferences Load(string path)
    {
        try
        {
            if (!File.Exists(path)) LegacyData.MigrateUserData();
            return File.Exists(path)
                ? JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(path), Json.Options) ?? new()
                : new();
        }
        catch (JsonException) { return new(); }
    }

    public void Save(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, Json.Options));

    public UserPreferences Normalized()
    {
        var result = this with
        {
            DashboardRefreshMilliseconds = Math.Clamp(DashboardRefreshMilliseconds, 250, 10_000),
            UiScale = double.IsFinite(UiScale) ? Math.Clamp(Math.Round(UiScale, 2), MinUiScale, MaxUiScale) : 1.0,
        };
        // Version 2 regrouped the dashboard into one card per component: make each card complete once for accounts
        // whose preferences were written before that, without re-adding readings the user hides afterwards.
        if (result.PreferencesVersion is null or < 2)
            result = result with
            {
                PreferencesVersion = 2,
                DashboardMetrics = [.. result.DashboardMetrics.Union(
                [
                    Sensors.Metrics.CpuClock, Sensors.Metrics.CpuPower, Sensors.Metrics.GpuClock, Sensors.Metrics.GpuPower,
                    Sensors.Metrics.RamUsedGb, Sensors.Metrics.RamTemp, Sensors.Metrics.RamSpeed, Sensors.Metrics.RamXmp,
                    Sensors.Metrics.PumpRpm, Sensors.Metrics.AioFan1, Sensors.Metrics.AioFan2, Sensors.Metrics.AioFan3,
                ])],
            };
        // Version 3 added the drive card (temperature and remaining life of every disk).
        if (result.PreferencesVersion is null or < 3)
            result = result with
            {
                PreferencesVersion = 3,
                DashboardMetrics = [.. result.DashboardMetrics.Union([Sensors.Metrics.StorageTemp, Sensors.Metrics.StorageHealth])],
            };
        // Version 4 shows every reading by default; the customize popup is there to hide what is not wanted.
        if (result.PreferencesVersion is null or < 4)
            result = result with
            {
                PreferencesVersion = 4,
                DashboardMetrics = [.. Sensors.Metrics.All.Select(m => m.Key)],
            };
        return result;
    }
}
