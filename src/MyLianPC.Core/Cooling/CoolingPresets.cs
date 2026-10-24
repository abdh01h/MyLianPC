namespace MyLianPC.Core.Cooling;

/// <summary>Built-in curves. All of them pass <see cref="FanCurve.Validate"/> (covered by tests).</summary>
public static class CoolingPresets
{
    public static FanCurve Silent(TemperatureSource source) => new()
    {
        Source = source,
        Points = source == TemperatureSource.Coolant
            ? [new(25, 25), new(30, 30), new(34, 45), new(38, 70), new(42, 100)]
            : [new(30, 25), new(50, 30), new(65, 45), new(75, 70), new(85, 100)],
    };

    /// <summary>
    /// Chassis fans: hold the hardware's lowest duty through normal idle and light-load temperatures (the audible
    /// 38-60 °C band), then ramp quickly once the CPU is genuinely hot. Case airflow matters much less than the
    /// cooler's own fans in that range, so spinning up there only adds noise.
    /// </summary>
    public static FanCurve CaseFans(TemperatureSource source) => new()
    {
        Source = source,
        Points = source == TemperatureSource.Coolant
            ? [new(25, 20), new(40, 25), new(45, 45), new(50, 70), new(54, 100)]
            : [new(30, 20), new(60, 25), new(70, 40), new(80, 70), new(85, 100)],
    };

    public static FanCurve Balanced(TemperatureSource source) => new()
    {
        Source = source,
        Points = source == TemperatureSource.Coolant
            ? [new(25, 30), new(30, 40), new(34, 55), new(38, 80), new(40, 100)]
            : [new(30, 30), new(50, 40), new(65, 60), new(75, 80), new(85, 100)],
    };

    public static FanCurve Performance(TemperatureSource source) => new()
    {
        Source = source,
        Points = source == TemperatureSource.Coolant
            ? [new(25, 45), new(30, 60), new(34, 80), new(37, 100)]
            : [new(30, 45), new(50, 60), new(65, 80), new(75, 100)],
    };

    public static FanCurve Maximum(TemperatureSource source) => new()
    {
        Source = source,
        MinimumDutyPercent = 100,
        // Points stay inside the range the curve editor shows (20-60 °C for coolant, 20-100 °C otherwise),
        // so the curve is drawn inside the chart instead of running off its edges.
        Points = source == TemperatureSource.Coolant ? [new(20, 100), new(60, 100)] : [new(20, 100), new(90, 100)],
    };

    /// <summary>Every preset with the name the UI shows, in the order the preset buttons are listed.</summary>
    public static IReadOnlyList<(string Name, Func<TemperatureSource, FanCurve> Make)> All =>
    [
        ("Silent", Silent), ("Case fans", CaseFans), ("Balanced", Balanced), ("Performance", Performance),
        ("Maximum cooling", Maximum),
    ];

    /// <summary>
    /// Name of the built-in preset this curve came from, or null when it has been edited. Only the points are
    /// compared: the minimum duty and hysteresis are free to differ without the curve losing its preset identity.
    /// </summary>
    public static string? NameOf(FanCurve curve) =>
        All.FirstOrDefault(p => p.Make(curve.Source).Points.SequenceEqual(curve.Points)).Name;
}
