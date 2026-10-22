namespace MyLianPC.Core.Cooling;

public enum TemperatureSource
{
    CpuPackage,
    GpuCore,
    Coolant,
}

public readonly record struct CurvePoint(double TemperatureC, int DutyPercent);

/// <summary>
/// A temperature-to-duty curve with the safety rules every curve must satisfy before it can be
/// applied. Evaluation is fail-safe: a missing or implausible sensor reading yields the curve's
/// failsafe duty (100% by default) rather than the last value or a low speed.
/// </summary>
public sealed record FanCurve
{
    /// <summary>Above this temperature every curve must command 100%.</summary>
    public const double CriticalTemperatureC = 90;

    public required TemperatureSource Source { get; init; }
    public required IReadOnlyList<CurvePoint> Points { get; init; }

    /// <summary>Lowest duty the curve may command. Adapters raise this to the hardware minimum if it is higher.</summary>
    public int MinimumDutyPercent { get; init; } = 20;

    /// <summary>Degrees the temperature must drop before duty is allowed to decrease (prevents fan hunting).</summary>
    public double HysteresisC { get; init; } = 3;

    public int FailsafeDutyPercent { get; init; } = 100;

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Points.Count < 2) problems.Add("A curve needs at least two points.");
        if (MinimumDutyPercent is < 0 or > 100) problems.Add("Minimum duty must be between 0 and 100%.");
        if (FailsafeDutyPercent is < 50 or > 100) problems.Add("Failsafe duty must be between 50 and 100%.");
        if (HysteresisC is < 0 or > 10) problems.Add("Hysteresis must be between 0 and 10 °C.");

        for (int i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            if (p.TemperatureC is < 0 or > 110) problems.Add($"Point {i + 1}: temperature {p.TemperatureC} °C is out of range (0-110).");
            if (p.DutyPercent is < 0 or > 100) problems.Add($"Point {i + 1}: duty {p.DutyPercent}% is out of range (0-100).");
            if (p.DutyPercent < MinimumDutyPercent) problems.Add($"Point {i + 1}: duty {p.DutyPercent}% is below the minimum of {MinimumDutyPercent}%.");
            if (i > 0)
            {
                var prev = Points[i - 1];
                if (p.TemperatureC <= prev.TemperatureC) problems.Add($"Point {i + 1}: temperatures must strictly increase.");
                if (p.DutyPercent < prev.DutyPercent) problems.Add($"Point {i + 1}: duty must not decrease as temperature rises.");
            }
        }

        if (Points.Count >= 2 && Points.All(p => p.DutyPercent is >= 0 and <= 100))
        {
            var atCritical = Interpolate(CriticalTemperatureC);
            if (atCritical < 100) problems.Add($"The curve must reach 100% by {CriticalTemperatureC} °C (it commands {atCritical}%).");
        }
        return problems;
    }

    public bool IsValid => Validate().Count == 0;

    /// <summary>Stateless duty for a temperature, clamped to the minimum. Null or implausible readings return the failsafe duty.</summary>
    public int Evaluate(double? temperatureC)
    {
        if (temperatureC is not { } t || double.IsNaN(t) || t < -20 || t > 150) return FailsafeDutyPercent;
        if (t >= CriticalTemperatureC) return 100;
        return Math.Max(MinimumDutyPercent, Interpolate(t));
    }

    private int Interpolate(double t)
    {
        if (t <= Points[0].TemperatureC) return Points[0].DutyPercent;
        for (int i = 1; i < Points.Count; i++)
        {
            var (a, b) = (Points[i - 1], Points[i]);
            if (t <= b.TemperatureC)
            {
                var f = (t - a.TemperatureC) / (b.TemperatureC - a.TemperatureC);
                return (int)Math.Round(a.DutyPercent + f * (b.DutyPercent - a.DutyPercent));
            }
        }
        // Past the last point the curve holds its last duty; Validate() rejects curves below 100% at the critical temperature.
        return Points[^1].DutyPercent;
    }
}

/// <summary>Applies a curve with hysteresis: duty rises immediately but only falls once temperature drops by HysteresisC.</summary>
public sealed class FanCurveController(FanCurve curve)
{
    private double? _tempAtLastDuty;
    private int? _lastDuty;

    public FanCurve Curve { get; } = curve;

    public int Next(double? temperatureC)
    {
        var target = Curve.Evaluate(temperatureC);
        if (temperatureC is null || _lastDuty is null || target >= _lastDuty)
        {
            _lastDuty = target;
            _tempAtLastDuty = temperatureC;
            return target;
        }
        if (_tempAtLastDuty is { } t0 && t0 - temperatureC.Value >= Curve.HysteresisC)
        {
            _lastDuty = target;
            _tempAtLastDuty = temperatureC;
        }
        return _lastDuty.Value;
    }
}
