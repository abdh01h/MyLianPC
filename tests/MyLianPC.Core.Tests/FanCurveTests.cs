using MyLianPC.Core.Cooling;

namespace MyLianPC.Core.Tests;

public class FanCurveTests
{
    private static FanCurve Curve(params (double t, int d)[] pts) => new()
    {
        Source = TemperatureSource.CpuPackage,
        Points = [.. pts.Select(p => new CurvePoint(p.t, p.d))],
    };

    [Theory]
    [MemberData(nameof(AllPresets))]
    public void Every_preset_is_valid(FanCurve preset) => Assert.Empty(preset.Validate());

    [Fact]
    public void Preset_names_round_trip_and_edited_curves_have_none()
    {
        foreach (var src in Enum.GetValues<TemperatureSource>())
            foreach (var (name, make) in CoolingPresets.All)
                Assert.Equal(name, CoolingPresets.NameOf(make(src)));

        var edited = CoolingPresets.Balanced(TemperatureSource.Coolant) with
        {
            Points = [.. CoolingPresets.Balanced(TemperatureSource.Coolant).Points.Select(p => p with { DutyPercent = p.DutyPercent + 1 })],
        };
        Assert.Null(CoolingPresets.NameOf(edited));
    }

    [Fact]
    public void Preset_name_ignores_minimum_duty_and_hysteresis()
    {
        var tuned = CoolingPresets.Silent(TemperatureSource.Coolant) with { MinimumDutyPercent = 30, HysteresisC = 5 };
        Assert.Equal("Silent", CoolingPresets.NameOf(tuned));
    }

    public static TheoryData<FanCurve> AllPresets()
    {
        var data = new TheoryData<FanCurve>();
        foreach (var src in Enum.GetValues<TemperatureSource>())
        {
            data.Add(CoolingPresets.Silent(src));
            data.Add(CoolingPresets.Balanced(src));
            data.Add(CoolingPresets.Performance(src));
            data.Add(CoolingPresets.Maximum(src));
        }
        return data;
    }

    [Fact]
    public void Interpolates_linearly_between_points()
    {
        var c = Curve((40, 30), (60, 50), (80, 100));
        Assert.Equal(40, c.Evaluate(50));
        Assert.Equal(75, c.Evaluate(70));
    }

    [Fact]
    public void Clamps_below_first_point_to_minimum()
    {
        var c = Curve((40, 30), (80, 100)) with { MinimumDutyPercent = 35 };
        Assert.Equal(35, c.Evaluate(10));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(-50.0)]
    [InlineData(200.0)]
    public void Missing_or_implausible_sensor_reading_runs_failsafe(double? reading)
    {
        var c = Curve((30, 20), (85, 100));
        Assert.Equal(100, c.Evaluate(reading));
    }

    [Fact]
    public void Critical_temperature_always_commands_full_speed() =>
        Assert.Equal(100, Curve((30, 20), (85, 100)).Evaluate(95));

    [Fact]
    public void Rejects_curve_that_never_reaches_full_speed()
    {
        var problems = Curve((30, 20), (100, 60)).Validate();
        Assert.Contains(problems, p => p.Contains("must reach 100%"));
    }

    [Fact]
    public void Rejects_decreasing_duty_and_non_increasing_temperatures()
    {
        var problems = Curve((30, 50), (30, 60), (50, 40), (80, 100)).Validate();
        Assert.Contains(problems, p => p.Contains("strictly increase"));
        Assert.Contains(problems, p => p.Contains("must not decrease"));
    }

    [Fact]
    public void Rejects_duty_below_minimum_and_out_of_range_values()
    {
        var problems = Curve((30, 5), (80, 140)).Validate();
        Assert.Contains(problems, p => p.Contains("below the minimum"));
        Assert.Contains(problems, p => p.Contains("out of range"));
    }

    [Fact]
    public void Controller_applies_hysteresis_only_when_falling()
    {
        var ctl = new FanCurveController(Curve((40, 30), (60, 50), (80, 100)) with { HysteresisC = 3 });
        Assert.Equal(50, ctl.Next(60));   // rise: immediate
        Assert.Equal(50, ctl.Next(58.5)); // small drop: hold
        Assert.Equal(47, ctl.Next(57));   // dropped 3 °C: follow curve
        Assert.Equal(75, ctl.Next(70));   // rise: immediate
        Assert.Equal(100, ctl.Next(null)); // sensor lost: failsafe
    }
}
