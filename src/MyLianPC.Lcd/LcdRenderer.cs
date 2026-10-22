using System.Collections.Concurrent;
using MyLianPC.Core.Lighting;
using MyLianPC.Core.Profiles;
using MyLianPC.Core.Sensors;
using MyLianPC.Core.Settings;
using SkiaSharp;

namespace MyLianPC.Lcd;

/// <summary>Values a theme can show, keyed by <see cref="Metrics"/>. Missing or null values render as "--".</summary>
public sealed record LcdMetrics(IReadOnlyDictionary<string, double?> Values, DateTime Time)
{
    public double? this[string key] => Values.TryGetValue(key, out var v) ? v : null;

    public static LcdMetrics Sample => new(new Dictionary<string, double?>
    {
        [Metrics.CpuTemp] = 47, [Metrics.GpuTemp] = 38, [Metrics.CoolantTemp] = 29, [Metrics.CpuLoad] = 12, [Metrics.GpuLoad] = 3,
        [Metrics.CpuClock] = 4850, [Metrics.GpuClock] = 2310, [Metrics.CpuPower] = 42, [Metrics.GpuPower] = 31, [Metrics.RamLoad] = 41,
        [Metrics.RamUsedGb] = 13.2, [Metrics.RamTotalGb] = 31.1, [Metrics.PumpRpm] = 2450,
        [Metrics.AioFan1] = 1180, [Metrics.AioFan2] = 1176, [Metrics.AioFan3] = 1172, [Metrics.GpuFan] = 0,
    }, DateTime.Now);
}

public sealed record LcdTheme(string Id, string Name, string Description);

/// <summary>Renders LCD themes to 480x480 frames. Pure CPU rendering, safe to run headless inside a service.</summary>
public static class LcdRenderer
{
    public const int Size = 480;
    private const float C = Size / 2f;

    /// <summary>Radius of the area text may use; the panel is round and its outer edge sits behind the bezel.</summary>
    private const float SafeRadius = 226;

    public static IReadOnlyList<LcdTheme> Themes { get; } =
    [
        new("minimal-temps", "Minimal temperatures", "Large CPU and GPU temperatures with loads"),
        new("overview", "System overview", "Temperatures, loads, coolant, pump, fans, RAM and clock"),
        new("gaming-dashboard", "Gaming dashboard", "Temperatures, loads and clocks at a glance"),
        new("gauges", "Circular gauges", "Concentric CPU/GPU temperature arcs"),
        new("cyberpunk", "Cyberpunk", "Neon yellow and magenta HUD"),
        new("hud-toxic", "Toxic HUD", "Acid green and spring green HUD"),
        new("hud-synthwave", "Synthwave HUD", "Violet, pink and cyan HUD"),
        new("hud-ice", "Ice HUD", "Frost blue HUD"),
        new("hud-ember", "Ember HUD", "Amber and red HUD"),
        new("neon", "Dark neon", "Glowing ring that shifts with CPU temperature"),
        new("cpu-gpu", "CPU & GPU monitor", "Side-by-side CPU and GPU details"),
        new("media", "Image / animation", "Your picture or GIF, optional clock overlay"),
        new("minimal-clock", "Minimal clock", "Time, date and a quiet temperature line"),
        new("analog-clock", "Analog clock", "Clock face with the CPU and GPU temperatures underneath"),
        new("cooling", "Cooling focus", "Coolant temperature, pump and radiator fan speeds"),
        new("load-bars", "Load bars", "Vertical CPU, GPU and RAM load bars"),
        new("power", "Power draw", "CPU and GPU package power with loads"),
        new("terminal", "Status list", "A compact readout of every important value"),
        new("split", "Diagonal split", "CPU in one corner, GPU in the other"),
        new("multi-metric", "Multi-metric", "Six tiles with the metrics you pick"),
        new("custom", "Custom layout", "Your own movable widgets"),
    ];

    private static readonly ConcurrentDictionary<(string, SKFontStyleWeight), SKTypeface> Typefaces = new();

    /// <param name="media">Current image/animation frame for the "media" theme (already 480x480), or null.</param>
    public static SKBitmap Render(LcdSettings s, LcdMetrics m, SKBitmap? media = null)
    {
        var bmp = new SKBitmap(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var c = new SKCanvas(bmp);
        using var ctx = new Ctx(c, s, m);
        c.Clear(Sk(s.Background));
        string theme = s.Mode switch { LcdMode.Image or LcdMode.Animation => "media", LcdMode.Off => "off", _ => s.ThemeId };
        switch (theme)
        {
            case "off": c.Clear(SKColors.Black); break;
            case "overview": Overview(ctx); break;
            case "gaming-dashboard": GamingDashboard(ctx); break;
            case "gauges": Gauges(ctx); break;
            case "cyberpunk": Hud(ctx, CyberHud); break;
            case "hud-toxic": Hud(ctx, ToxicHud); break;
            case "hud-synthwave": Hud(ctx, SynthwaveHud); break;
            case "hud-ice": Hud(ctx, IceHud); break;
            case "hud-ember": Hud(ctx, EmberHud); break;
            case "neon": Neon(ctx); break;
            case "cpu-gpu": CpuGpu(ctx); break;
            case "media": Media(ctx, media); break;
            case "minimal-clock": MinimalClock(ctx); break;
            case "analog-clock": AnalogClock(ctx); break;
            case "cooling": Cooling(ctx); break;
            case "load-bars": LoadBars(ctx); break;
            case "power": Power(ctx); break;
            case "terminal": Terminal(ctx); break;
            case "split": Split(ctx); break;
            case "multi-metric": MultiMetric(ctx); break;
            case "custom": Custom(ctx); break;
            default: MinimalTemps(ctx); break;
        }
        MaskCircle(c);
        return bmp;
    }

    /// <summary>Baseline JPEG for the panel. Quality steps down until it fits the panel's payload limit.</summary>
    public static byte[] EncodeJpeg(SKBitmap bmp, int maxBytes = 150_000)
    {
        using var image = SKImage.FromBitmap(bmp);
        for (int q = 88; ; q -= 10)
        {
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, q);
            if (data.Size <= maxBytes || q <= 28) return data.ToArray();
        }
    }

    public static string FormatTemp(double? celsius, TemperatureUnit unit) => celsius switch
    {
        null => "—°",
        { } v when unit == TemperatureUnit.Fahrenheit => $"{v * 9 / 5 + 32:0}°",
        { } v => $"{v:0}°",
    };

    /// <summary>
    /// Clock text in the format the profile asks for: 24-hour ("17:04"), or 12-hour with AM/PM ("5:04 PM").
    /// </summary>
    public static string ClockText(DateTime time, bool twentyFourHour, bool seconds = false)
    {
        if (twentyFourHour) return time.ToString(seconds ? "HH:mm:ss" : "HH:mm");
        return time.ToString(seconds ? "h:mm:ss" : "h:mm") + (time.Hour < 12 ? " AM" : " PM");
    }

    public static string Format(string metric, double? value, TemperatureUnit unit)
    {
        var info = Metrics.Find(metric);
        if (value is null) return "—";
        if (metric == Metrics.RamXmp) return value >= 0.5 ? "On" : "Off";
        return info?.Unit switch
        {
            "°C" => FormatTemp(value, unit),
            "%" => $"{value:0}%",
            "MHz" => value >= 1000 ? $"{value / 1000:0.00} GHz" : $"{value:0} MHz",
            "W" => $"{value:0} W",
            "RPM" => $"{value:0} rpm",
            "GB" => $"{value:0.0} GB",
            "MT/s" => $"{value:0} MT/s",
            _ => $"{value:0.#}",
        };
    }

    // ---------------------------------------------------------------- themes
    // Sizes are chosen for a 2.8" round panel read from a desk away: main values >= 70 px bold,
    // labels >= 22 px bold. Ctx.Text shrinks any line that would run past the round edge.

    private static void MinimalTemps(Ctx x)
    {
        x.Text($"CPU  {x.F(Metrics.CpuLoad)}", C, 100, 32, x.Accent, SKFontStyleWeight.Bold);
        x.Text(x.T(Metrics.CpuTemp), C, 205, 118, x.Fg, SKFontStyleWeight.SemiBold);
        x.Line(120, 240, 360, 240, x.Fg, 3, 70);
        x.Text(x.T(Metrics.GpuTemp), C, 350, 118, x.Fg, SKFontStyleWeight.SemiBold);
        x.Text($"GPU  {x.F(Metrics.GpuLoad)}", C, 392, 32, x.Second, SKFontStyleWeight.Bold);
        x.Text($"H₂O {x.T(Metrics.CoolantTemp)}  ·  {x.Clock()}", C, 432, 26, x.Fg, SKFontStyleWeight.SemiBold, 190);
    }

    private static void Overview(Ctx x)
    {
        x.Text(x.Clock(), C, 84, 38, x.Fg, SKFontStyleWeight.Bold);
        x.Text(x.M.Time.ToString("ddd d MMM"), C, 112, 20, x.Fg, SKFontStyleWeight.SemiBold, 170);
        x.Line(C, 140, C, 228, x.Fg, 2, 50);
        x.Text(x.T(Metrics.CpuTemp), 158, 192, 72, x.Accent, SKFontStyleWeight.Bold, maxWidth: 150);
        x.Text(x.T(Metrics.GpuTemp), 322, 192, 72, x.Second, SKFontStyleWeight.Bold, maxWidth: 150);
        x.Text($"CPU {x.F(Metrics.CpuLoad)}", 158, 224, 22, x.Fg, SKFontStyleWeight.Bold, 220, maxWidth: 150);
        x.Text($"GPU {x.F(Metrics.GpuLoad)}", 322, 224, 22, x.Fg, SKFontStyleWeight.Bold, 220, maxWidth: 150);
        x.Stat(112, 292, x.T(Metrics.CoolantTemp), "COOLANT", x.Accent);
        x.Stat(C, 292, x.N(x.M[Metrics.PumpRpm]), "PUMP RPM", x.Fg);
        x.Stat(368, 292, x.N(x.AioFanRpm), "FAN RPM", x.Second);
        x.Text($"RAM {x.F(Metrics.RamUsedGb)}  ·  {x.F(Metrics.RamLoad)}", C, 360, 22, x.Fg, SKFontStyleWeight.Bold, 220);
        x.Bar(130, 380, 220, 12, Frac(x.M[Metrics.RamLoad], 100), x.Accent);
        x.Text($"{x.F(Metrics.CpuClock)}  ·  {x.F(Metrics.CpuPower)}", C, 424, 22, x.Fg, SKFontStyleWeight.SemiBold, 180);
    }

    private static void GamingDashboard(Ctx x)
    {
        x.Text(x.Clock(), C, 78, 34, x.Fg, SKFontStyleWeight.Bold, 200);
        x.Gauge(150, 186, 78, Frac(x.M[Metrics.CpuTemp], 100), x.Accent);
        x.Gauge(330, 186, 78, Frac(x.M[Metrics.GpuTemp], 100), x.Second);
        x.Text(x.T(Metrics.CpuTemp), 150, 203, 50, x.Fg, SKFontStyleWeight.Bold, maxWidth: 120);
        x.Text(x.T(Metrics.GpuTemp), 330, 203, 50, x.Fg, SKFontStyleWeight.Bold, maxWidth: 120);
        x.Text("CPU", 150, 238, 22, x.Accent, SKFontStyleWeight.Bold);
        x.Text("GPU", 330, 238, 22, x.Second, SKFontStyleWeight.Bold);
        x.BarRow("CPU", Metrics.CpuLoad, 296, x.Accent);
        x.BarRow("GPU", Metrics.GpuLoad, 334, x.Second);
        x.BarRow("RAM", Metrics.RamLoad, 372, x.Fg);
        x.Text($"{x.F(Metrics.CpuClock)}  ·  {x.F(Metrics.CpuPower)}  ·  H₂O {x.T(Metrics.CoolantTemp)}", C, 420, 22, x.Fg, SKFontStyleWeight.SemiBold, 190);
    }

    private static void Gauges(Ctx x)
    {
        x.Arc(C, C, 215, 20, Frac(x.M[Metrics.CpuTemp], 100), x.Accent);
        x.Arc(C, C, 186, 20, Frac(x.M[Metrics.GpuTemp], 100), x.Second);
        x.Text(x.Clock(), C, 142, 28, x.Fg, SKFontStyleWeight.Bold, 170);
        x.Text(x.T(Metrics.CpuTemp), C, 234, 96, x.Fg, SKFontStyleWeight.Bold, maxWidth: 260);
        x.Text($"CPU  {x.F(Metrics.CpuLoad)}", C, 272, 28, x.Accent, SKFontStyleWeight.Bold);
        x.Text($"GPU {x.T(Metrics.GpuTemp)}  ·  {x.F(Metrics.GpuLoad)}", C, 318, 28, x.Second, SKFontStyleWeight.Bold, maxWidth: 280);
        x.Text($"H₂O {x.T(Metrics.CoolantTemp)}", C, 356, 28, x.Fg, SKFontStyleWeight.Bold, 190, maxWidth: 220);
    }

    /// <summary>Colours of a HUD theme: the frame and title, the two big temperatures, the details (grid, clock, bars) and the footer line.</summary>
    private sealed record HudPalette(string Title, RgbColor Frame, RgbColor Cpu, RgbColor Gpu, RgbColor Detail, RgbColor Footer);

    private static readonly HudPalette CyberHud = new("// SYS.STATUS",
        RgbColor.ParseHex("#FCEE0A"), RgbColor.ParseHex("#FCEE0A"), RgbColor.ParseHex("#FF2A6D"),
        RgbColor.ParseHex("#05D9E8"), RgbColor.ParseHex("#FF2A6D"));

    private static readonly HudPalette ToxicHud = new("// TOX.LOAD",
        RgbColor.ParseHex("#A8FF00"), RgbColor.ParseHex("#D8FF2E"), RgbColor.ParseHex("#00FFA3"),
        RgbColor.ParseHex("#7BFF3A"), RgbColor.ParseHex("#00FFA3"));

    private static readonly HudPalette SynthwaveHud = new("// SYNTH.WAVE",
        RgbColor.ParseHex("#B967FF"), RgbColor.ParseHex("#FF71CE"), RgbColor.ParseHex("#01CDFE"),
        RgbColor.ParseHex("#05FFA1"), RgbColor.ParseHex("#FFFB96"));

    private static readonly HudPalette IceHud = new("// CRYO.CORE",
        RgbColor.ParseHex("#7FE7FF"), RgbColor.ParseHex("#D8F6FF"), RgbColor.ParseHex("#4FB6FF"),
        RgbColor.ParseHex("#9BE7FF"), RgbColor.ParseHex("#6AD3FF"));

    private static readonly HudPalette EmberHud = new("// THERM.EMBR",
        RgbColor.ParseHex("#FF9E2C"), RgbColor.ParseHex("#FFC46B"), RgbColor.ParseHex("#FF4D3D"),
        RgbColor.ParseHex("#FFB27A"), RgbColor.ParseHex("#FFD24A"));

    /// <summary>The neon HUD shared by the Cyberpunk, Toxic, Synthwave, Ice and Ember themes; only the palette differs.</summary>
    private static void Hud(Ctx x, HudPalette p)
    {
        using (var grid = x.StrokePaint(p.Detail, 1, 28))
            for (int i = 0; i <= Size; i += 24) { x.Canvas.DrawLine(i, 0, i, Size, grid); x.Canvas.DrawLine(0, i, Size, i, grid); }
        using (var frame = SKPath.ParseSvgPathData("M62 118 L418 118 L440 140 L440 318 L418 340 L62 340 L40 318 L40 140 Z"))
        {
            using var stroke = x.StrokePaint(p.Frame, 3, 255);
            using var fill = x.FillPaint(RgbColor.Black, 170);
            x.Canvas.DrawPath(frame, fill);
            x.Canvas.DrawPath(frame, stroke);
        }
        x.Text(p.Title, C, 98, 24, p.Frame, SKFontStyleWeight.Bold);
        x.Line(C, 150, C, 312, p.Detail, 2, 90);
        x.Text(x.T(Metrics.CpuTemp), 140, 238, 100, p.Cpu, SKFontStyleWeight.Bold, maxWidth: 180);
        x.Text(x.T(Metrics.GpuTemp), 340, 238, 100, p.Gpu, SKFontStyleWeight.Bold, maxWidth: 180);
        x.Text($"CPU {x.F(Metrics.CpuLoad)}", 140, 280, 26, p.Detail, SKFontStyleWeight.Bold, maxWidth: 180);
        x.Text($"GPU {x.F(Metrics.GpuLoad)}", 340, 280, 26, p.Detail, SKFontStyleWeight.Bold, maxWidth: 180);
        x.Bar(80, 306, 120, 10, Frac(x.M[Metrics.CpuLoad], 100), p.Cpu);
        x.Bar(280, 306, 120, 10, Frac(x.M[Metrics.GpuLoad], 100), p.Gpu);
        x.Text(x.Clock(seconds: true), C, 394, 44, p.Detail, SKFontStyleWeight.Bold);
        x.Text($"H₂O {x.T(Metrics.CoolantTemp)}  ·  {x.F(Metrics.PumpRpm)}", C, 430, 22, p.Footer, SKFontStyleWeight.Bold);
    }

    private static void Neon(Ctx x)
    {
        double t = Frac(x.M[Metrics.CpuTemp], 100) ?? 0;
        var ring = RgbColor.Lerp(x.S.Accent, RgbColor.ParseHex("#FF2050"), Math.Clamp((t - 0.45) / 0.4, 0, 1));
        for (int i = 6; i >= 1; i--)
            using (var glow = x.StrokePaint(ring, i * 6, (byte)(18 + (6 - i) * 6)))
                x.Canvas.DrawCircle(C, C, 205, glow);
        using (var core = x.StrokePaint(ring, 4, 255)) x.Canvas.DrawCircle(C, C, 205, core);
        x.Text(x.Clock(), C, 140, 30, x.Fg, SKFontStyleWeight.Bold, 200);
        x.Text(x.T(Metrics.CpuTemp), C, 264, 124, x.Fg, SKFontStyleWeight.SemiBold, maxWidth: 300);
        x.Text($"CPU  {x.F(Metrics.CpuLoad)}", C, 306, 30, ring, SKFontStyleWeight.Bold);
        x.Text($"GPU {x.T(Metrics.GpuTemp)}   ·   H₂O {x.T(Metrics.CoolantTemp)}", C, 352, 26, x.Second, SKFontStyleWeight.Bold, maxWidth: 270);
    }

    private static void CpuGpu(Ctx x)
    {
        x.Line(C, 105, C, 350, x.Fg, 2, 60);
        void Column(float cx, string title, string temp, string load, string clock, string power, RgbColor color)
        {
            x.Text(title, cx, 124, 30, color, SKFontStyleWeight.Bold);
            x.Text(x.T(temp), cx, 202, 76, x.Fg, SKFontStyleWeight.Bold, maxWidth: 170);
            x.Text(x.F(load), cx, 248, 34, color, SKFontStyleWeight.Bold, maxWidth: 170);
            x.Text(x.F(clock), cx, 288, 24, x.Fg, SKFontStyleWeight.SemiBold, 210, maxWidth: 170);
            x.Text(x.F(power), cx, 322, 24, x.Fg, SKFontStyleWeight.SemiBold, 210, maxWidth: 170);
        }
        Column(135, "CPU", Metrics.CpuTemp, Metrics.CpuLoad, Metrics.CpuClock, Metrics.CpuPower, x.Accent);
        Column(345, "GPU", Metrics.GpuTemp, Metrics.GpuLoad, Metrics.GpuClock, Metrics.GpuPower, x.Second);
        x.Text($"RAM {x.F(Metrics.RamLoad)}  ·  H₂O {x.T(Metrics.CoolantTemp)}", C, 398, 26, x.Fg, SKFontStyleWeight.Bold, 200);
    }

    private static void Media(Ctx x, SKBitmap? media)
    {
        if (media is not null)
        {
            using var img = SKImage.FromBitmap(media);
            x.Canvas.DrawImage(img, new SKRect(0, 0, Size, Size), new SKSamplingOptions(SKFilterMode.Linear), null);
        }
        else x.Text("No image selected", C, C, 28, x.Fg, SKFontStyleWeight.SemiBold, 160);
        if (x.S.MediaOverlay)
        {
            using (var shade = x.FillPaint(RgbColor.Black, 130)) x.Canvas.DrawRect(0, 320, Size, 160, shade);
            x.Text(x.Clock(), C, 384, 52, RgbColor.White, SKFontStyleWeight.Bold);
            x.Text($"CPU {x.T(Metrics.CpuTemp)}   GPU {x.T(Metrics.GpuTemp)}", C, 422, 26, RgbColor.White, SKFontStyleWeight.Bold, 230);
        }
    }

    private static void MinimalClock(Ctx x)
    {
        x.Text(x.Clock(), C, 240, 136, x.Fg, SKFontStyleWeight.SemiBold);
        x.Text(x.M.Time.ToString("dddd, d MMMM"), C, 292, 30, x.Accent, SKFontStyleWeight.Bold);
        x.Text($"CPU {x.T(Metrics.CpuTemp)}   GPU {x.T(Metrics.GpuTemp)}", C, 352, 28, x.Fg, SKFontStyleWeight.SemiBold, 200);
        x.Text($"H₂O {x.T(Metrics.CoolantTemp)}", C, 392, 26, x.Second, SKFontStyleWeight.SemiBold);
    }

    private static void AnalogClock(Ctx x)
    {
        const float cy = 210, r = 148;
        using (var rim = x.StrokePaint(x.Fg, 2, 60)) x.Canvas.DrawCircle(C, cy, r, rim);
        for (int i = 0; i < 12; i++)
        {
            double a = i * Math.PI / 6;
            float x0 = C + (float)(Math.Sin(a) * (r - 16)), y0 = cy - (float)(Math.Cos(a) * (r - 16));
            float x1 = C + (float)(Math.Sin(a) * (r - 5)), y1 = cy - (float)(Math.Cos(a) * (r - 5));
            x.Line(x0, y0, x1, y1, x.Fg, i % 3 == 0 ? 5 : 2, (byte)(i % 3 == 0 ? 220 : 120));
        }
        var t = x.M.Time;
        double hourAngle = ((t.Hour % 12) + t.Minute / 60.0) * Math.PI / 6;
        double minuteAngle = (t.Minute + t.Second / 60.0) * Math.PI / 30;
        double secondAngle = t.Second * Math.PI / 30;
        x.Line(C, cy, C + (float)(Math.Sin(hourAngle) * 84), cy - (float)(Math.Cos(hourAngle) * 84), x.Fg, 9, 255);
        x.Line(C, cy, C + (float)(Math.Sin(minuteAngle) * 124), cy - (float)(Math.Cos(minuteAngle) * 124), x.Fg, 5, 255);
        x.Line(C, cy, C + (float)(Math.Sin(secondAngle) * 132), cy - (float)(Math.Cos(secondAngle) * 132), x.Accent, 2, 255);
        using (var dot = x.FillPaint(x.Accent)) x.Canvas.DrawCircle(C, cy, 7, dot);
        x.Text($"{x.T(Metrics.CpuTemp)} CPU   {x.T(Metrics.GpuTemp)} GPU", C, 406, 26, x.Fg, SKFontStyleWeight.Bold, 210, maxWidth: 220);
        x.Text(t.ToString("ddd d MMM"), C, 438, 20, x.Second, SKFontStyleWeight.Bold, 190, maxWidth: 200);
    }

    private static void Cooling(Ctx x)
    {
        x.Text("COOLING", C, 56, 22, x.Second, SKFontStyleWeight.Bold);
        x.Arc(C, 212, 118, 18, Frac(x.M[Metrics.CoolantTemp], 60), x.Accent);
        x.Text(x.T(Metrics.CoolantTemp), C, 244, 88, x.Fg, SKFontStyleWeight.Bold, maxWidth: 180);
        x.Text("COOLANT", C, 276, 20, x.Accent, SKFontStyleWeight.Bold);
        x.Stat(134, 358, x.N(x.M[Metrics.PumpRpm]), "PUMP RPM", x.Fg);
        x.Stat(346, 358, x.N(x.AioFanRpm), "FAN RPM", x.Second);
        x.Text(x.Clock(), C, 430, 24, x.Fg, SKFontStyleWeight.Bold, 200);
    }

    private static void LoadBars(Ctx x)
    {
        x.Text(x.Clock(), C, 88, 26, x.Fg, SKFontStyleWeight.Bold, 200);
        void Column(float cx, string label, string metric, RgbColor color)
        {
            double f = Frac(x.M[metric], 100) ?? 0;
            x.Text(x.F(metric), cx, 172, 34, color, SKFontStyleWeight.Bold, maxWidth: 112);
            using (var track = x.FillPaint(x.Fg, 30))
                x.Canvas.DrawRoundRect(new SKRect(cx - 20, 196, cx + 20, 356), 20, 20, track);
            if (f > 0)
            {
                float top = (float)(356 - 160 * f);
                using var fill = x.FillPaint(color);
                x.Canvas.DrawRoundRect(new SKRect(cx - 20, top, cx + 20, 356), 20, 20, fill);
            }
            x.Text(label, cx, 392, 24, color, SKFontStyleWeight.Bold, maxWidth: 120);
        }
        Column(148, "CPU", Metrics.CpuLoad, x.Accent);
        Column(240, "GPU", Metrics.GpuLoad, x.Second);
        Column(332, "RAM", Metrics.RamLoad, x.Fg);
        x.Text($"{x.F(Metrics.CpuClock)}  ·  {x.F(Metrics.CpuPower)}", C, 432, 20, x.Fg, SKFontStyleWeight.SemiBold, 170, maxWidth: 220);
    }

    private static void Power(Ctx x)
    {
        x.Text("POWER DRAW", C, 92, 24, x.Second, SKFontStyleWeight.Bold);
        x.Line(C, 148, C, 282, x.Fg, 2, 60);
        x.Text(x.F(Metrics.CpuPower), 150, 222, 64, x.Accent, SKFontStyleWeight.Bold, maxWidth: 152);
        x.Text("CPU", 150, 262, 24, x.Accent, SKFontStyleWeight.Bold);
        x.Text(x.F(Metrics.GpuPower), 330, 222, 64, x.Second, SKFontStyleWeight.Bold, maxWidth: 152);
        x.Text("GPU", 330, 262, 24, x.Second, SKFontStyleWeight.Bold);
        x.BarRow("CPU", Metrics.CpuLoad, 324, x.Accent);
        x.BarRow("GPU", Metrics.GpuLoad, 366, x.Second);
        x.Text($"H₂O {x.T(Metrics.CoolantTemp)}   ·   {x.N(x.M[Metrics.PumpRpm])} rpm", C, 426, 20, x.Fg, SKFontStyleWeight.SemiBold, 180, maxWidth: 230);
    }

    private static void Terminal(Ctx x)
    {
        x.Text("> system.status", C, 86, 22, x.Accent, SKFontStyleWeight.Bold);
        x.Line(84, 104, 396, 104, x.Fg, 1, 70);
        float y = 146;
        void Row(string label, string value, RgbColor color)
        {
            x.Text(label, 104, y, 22, x.Fg, SKFontStyleWeight.SemiBold, 205, SKTextAlign.Left);
            x.Text(value, 376, y, 22, color, SKFontStyleWeight.Bold, 255, SKTextAlign.Right);
            y += 33;
        }
        Row("cpu temp", x.T(Metrics.CpuTemp), x.Accent);
        Row("gpu temp", x.T(Metrics.GpuTemp), x.Second);
        Row("cpu load", x.F(Metrics.CpuLoad), x.Fg);
        Row("gpu load", x.F(Metrics.GpuLoad), x.Fg);
        Row("ram", $"{x.F(Metrics.RamUsedGb)} / {x.F(Metrics.RamTotalGb)}", x.Fg);
        Row("coolant", x.T(Metrics.CoolantTemp), x.Accent);
        Row("pump", x.N(x.M[Metrics.PumpRpm]), x.Second);
        x.Text(x.Clock(seconds: true), C, 430, 22, x.Fg, SKFontStyleWeight.Bold, 200);
    }

    private static void Split(Ctx x)
    {
        x.Line(46, 396, 434, 84, x.Fg, 2, 55);
        x.Text(x.T(Metrics.CpuTemp), 144, 168, 84, x.Accent, SKFontStyleWeight.Bold, maxWidth: 176);
        x.Text($"CPU   {x.F(Metrics.CpuLoad)}", 144, 206, 24, x.Fg, SKFontStyleWeight.Bold, maxWidth: 176);
        x.Text(x.F(Metrics.CpuClock), 144, 240, 20, x.Fg, SKFontStyleWeight.SemiBold, 190, maxWidth: 176);
        x.Text(x.T(Metrics.GpuTemp), 336, 300, 84, x.Second, SKFontStyleWeight.Bold, maxWidth: 176);
        x.Text($"GPU   {x.F(Metrics.GpuLoad)}", 336, 338, 24, x.Fg, SKFontStyleWeight.Bold, maxWidth: 176);
        x.Text(x.F(Metrics.GpuClock), 336, 372, 20, x.Fg, SKFontStyleWeight.SemiBold, 190, maxWidth: 176);
        x.Text(x.Clock(), C, 432, 24, x.Fg, SKFontStyleWeight.Bold, 200);
    }

    private static void MultiMetric(Ctx x)
    {
        var metrics = x.S.Metrics.Take(6).ToList();
        float[] cols = [158, 322];
        float[] rows = [152, 256, 360];
        for (int i = 0; i < metrics.Count; i++)
        {
            float cx = cols[i % 2], cy = rows[i / 2];
            var info = Metrics.Find(metrics[i]);
            var color = i % 2 == 0 ? x.Accent : x.Second;
            using (var tile = x.FillPaint(x.S.TextColor, 20)) x.Canvas.DrawRoundRect(new SKRect(cx - 76, cy - 46, cx + 76, cy + 46), 16, 16, tile);
            x.Text(x.F(metrics[i]), cx, cy + 10, 40, x.Fg, SKFontStyleWeight.Bold, maxWidth: 140);
            x.Text(info?.Label.Replace(" temperature", "").Replace(" utilization", " load") ?? metrics[i], cx, cy + 35, 18, color, SKFontStyleWeight.Bold, maxWidth: 140);
        }
        x.Text(x.Clock(), C, 78, 30, x.Fg, SKFontStyleWeight.Bold, 170);
    }

    private static void Custom(Ctx x)
    {
        foreach (var w in x.S.Widgets.Take(LcdSettings.MaxWidgets))
        {
            var color = w.Color ?? x.S.TextColor;
            switch (w.Type)
            {
                case LcdWidgetType.Clock: x.Text(x.Clock(), w.X, w.Y + w.Size * 0.35f, w.Size, color, SKFontStyleWeight.SemiBold); break;
                case LcdWidgetType.Date: x.Text(x.M.Time.ToString("ddd d MMM"), w.X, w.Y + w.Size * 0.35f, w.Size, color, SKFontStyleWeight.Bold); break;
                case LcdWidgetType.Text: x.Text(w.Text ?? "", w.X, w.Y + w.Size * 0.35f, w.Size, color, SKFontStyleWeight.Bold); break;
                case LcdWidgetType.Metric:
                    x.Text(x.F(w.Metric), w.X, w.Y + w.Size * 0.35f, w.Size, color, SKFontStyleWeight.Bold);
                    if (w.Label is { Length: > 0 }) x.Text(w.Label, w.X, w.Y + w.Size * 0.35f + w.Size * 0.6f, w.Size * 0.45f, w.Color ?? x.S.Accent, SKFontStyleWeight.Bold);
                    break;
                case LcdWidgetType.Gauge:
                    double? frac = Frac(x.M[w.Metric], 100);
                    var gc = w.Color ?? x.S.Accent;
                    if (x.S.GaugeStyle == GaugeStyle.Bar) x.Bar(w.X - w.Size / 2, w.Y, w.Size, 12, frac, gc);
                    else x.Gauge(w.X, w.Y, w.Size / 2, frac, gc, full: x.S.GaugeStyle == GaugeStyle.Ring);
                    x.Text(x.F(w.Metric), w.X, w.Y + w.Size * 0.12f, w.Size * 0.32f, x.S.TextColor, SKFontStyleWeight.Bold, maxWidth: w.Size * 0.8f);
                    if (w.Label is { Length: > 0 }) x.Text(w.Label, w.X, w.Y + w.Size * 0.32f, w.Size * 0.16f, gc, SKFontStyleWeight.Bold);
                    break;
                case LcdWidgetType.Bar:
                    x.Bar(w.X - w.Size, w.Y, w.Size * 2, Math.Max(6, w.Size / 6), Frac(x.M[w.Metric], 100), w.Color ?? x.S.Accent);
                    if (w.Label is { Length: > 0 }) x.Text($"{w.Label} {x.F(w.Metric)}", w.X, w.Y - 8, Math.Max(14, w.Size / 4), x.S.TextColor, SKFontStyleWeight.Bold);
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static double? Frac(double? v, double max) => v is { } d ? Math.Clamp(d / max, 0, 1) : null;

    private static void MaskCircle(SKCanvas c)
    {
        // The panel is round: paint everything outside the inscribed circle black (a 100 px ring covers the corners).
        using var black = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 100 };
        c.DrawCircle(C, C, C + 50, black);
    }

    internal static SKColor Sk(RgbColor c, byte a = 255) => new(c.R, c.G, c.B, a);

    private sealed class Ctx(SKCanvas canvas, LcdSettings s, LcdMetrics m) : IDisposable
    {
        public SKCanvas Canvas { get; } = canvas;
        public LcdSettings S { get; } = s;
        public LcdMetrics M { get; } = m;
        public RgbColor Fg => S.TextColor;
        public RgbColor Accent => S.Accent;
        public RgbColor Second => S.Secondary;

        public string T(string metric) => FormatTemp(M[metric], S.Unit);
        public string F(string metric) => Format(metric, M[metric], S.Unit);
        public string N(double? value) => value is { } v ? $"{v:0}" : "—";

        /// <summary>Current time in the 12/24-hour format this profile selected.</summary>
        public string Clock(bool seconds = false) => ClockText(M.Time, S.Clock24Hour, seconds);

        /// <summary>Average of the radiator fans that report a speed.</summary>
        public double? AioFanRpm
        {
            get
            {
                var rpms = new[] { M[Metrics.AioFan1], M[Metrics.AioFan2], M[Metrics.AioFan3] }.Where(v => v > 0).Select(v => v!.Value).ToList();
                return rpms.Count > 0 ? rpms.Average() : null;
            }
        }

        /// <param name="maxWidth">Widest the text may be; centered text is also kept inside the round panel.</param>
        public void Text(string text, float x, float y, float size, RgbColor color, SKFontStyleWeight weight = SKFontStyleWeight.Normal, byte alpha = 255, SKTextAlign align = SKTextAlign.Center, float maxWidth = 0)
        {
            var tf = Typefaces.GetOrAdd((S.FontFamily, weight), k => SKTypeface.FromFamilyName(k.Item1, k.Item2, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default);
            using var font = new SKFont(tf, (float)(size * S.FontScale)) { Edging = SKFontEdging.Antialias };
            float limit = align == SKTextAlign.Center ? VisibleWidth(x, y, font.Size) : 0;
            if (maxWidth > 0) limit = limit > 0 ? Math.Min(limit, maxWidth) : maxWidth;
            if (limit > 0 && font.MeasureText(text) is var w && w > limit) font.Size *= limit / w;
            using var paint = FillPaint(color, alpha);
            Canvas.DrawText(text, x, y, align, font, paint);
        }

        /// <summary>Width a line centered on x can have at baseline y without leaving the round panel's safe area.</summary>
        private static float VisibleWidth(float x, float y, float fontSize)
        {
            float dy = Math.Max(Math.Abs(y - C), Math.Abs(y - fontSize * 0.72f - C));
            if (dy >= SafeRadius) return 1;
            float half = MathF.Sqrt(SafeRadius * SafeRadius - dy * dy);
            return Math.Max(1, 2 * Math.Min(x - (C - half), C + half - x));
        }

        /// <summary>A value with a small caption underneath (one column of a stat row).</summary>
        public void Stat(float cx, float y, string value, string caption, RgbColor color)
        {
            Text(value, cx, y, 32, Fg, SKFontStyleWeight.Bold, maxWidth: 118);
            Text(caption, cx, y + 25, 16, color, SKFontStyleWeight.Bold, maxWidth: 118);
        }

        public void Line(float x0, float y0, float x1, float y1, RgbColor color, float width, byte alpha)
        {
            using var p = StrokePaint(color, width, alpha);
            Canvas.DrawLine(x0, y0, x1, y1, p);
        }

        public void Arc(float cx, float cy, float r, float width, double? fraction, RgbColor color)
        {
            var rect = new SKRect(cx - r, cy - r, cx + r, cy + r);
            using var track = StrokePaint(S.TextColor, width, 35);
            using var fill = StrokePaint(color, width, 255);
            track.StrokeCap = fill.StrokeCap = SKStrokeCap.Round;
            Canvas.DrawArc(rect, 135, 270, false, track);
            if (fraction is { } f && f > 0) Canvas.DrawArc(rect, 135, (float)(270 * f), false, fill);
        }

        public void Gauge(float cx, float cy, float r, double? fraction, RgbColor color, bool full = false)
        {
            if (!full) { Arc(cx, cy, r, Math.Max(6, r / 7), fraction, color); return; }
            var rect = new SKRect(cx - r, cy - r, cx + r, cy + r);
            using var track = StrokePaint(S.TextColor, Math.Max(6, r / 7), 35);
            using var fill = StrokePaint(color, Math.Max(6, r / 7), 255);
            fill.StrokeCap = SKStrokeCap.Round;
            Canvas.DrawCircle(cx, cy, r, track);
            if (fraction is { } f && f > 0) Canvas.DrawArc(rect, -90, (float)(360 * f), false, fill);
        }

        public void Bar(float x, float y, float width, float height, double? fraction, RgbColor color)
        {
            using var track = FillPaint(S.TextColor, 35);
            using var fill = FillPaint(color, 255);
            Canvas.DrawRoundRect(new SKRect(x, y - height / 2, x + width, y + height / 2), height / 2, height / 2, track);
            if (fraction is { } f && f > 0)
                Canvas.DrawRoundRect(new SKRect(x, y - height / 2, x + (float)(width * f), y + height / 2), height / 2, height / 2, fill);
        }

        public void BarRow(string label, string metric, float y, RgbColor color)
        {
            Text(label, 92, y + 8, 22, color, SKFontStyleWeight.Bold, align: SKTextAlign.Left);
            Bar(152, y, 178, 14, Frac(M[metric], 100), color);
            Text(F(metric), 390, y + 8, 22, Fg, SKFontStyleWeight.Bold, 220, SKTextAlign.Right);
        }

        public SKPaint FillPaint(RgbColor c, byte a = 255) => new() { Color = Sk(c, a), IsAntialias = true };

        public SKPaint StrokePaint(RgbColor c, float w, byte a) => new() { Color = Sk(c, a), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w };

        public void Dispose() { }
    }
}
