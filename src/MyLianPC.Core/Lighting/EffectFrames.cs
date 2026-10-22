namespace MyLianPC.Core.Lighting;

/// <summary>
/// Generates frame sequences for effects on devices without a built-in effect engine (the HydroShift pump
/// ring) and for previews in the app. Animations are at most <see cref="MaxFrames"/> frames and loop seamlessly.
/// </summary>
public static class EffectFrames
{
    public const int MaxFrames = 48;

    public sealed record Animation(IReadOnlyList<RgbColor[]> Frames, int FrameIntervalMs);

    /// <summary>Effect period in ms for a speed percentage: 0% = 6 s, 100% = 0.8 s.</summary>
    public static int PeriodMs(int speedPercent) => (int)(6000 - Math.Clamp(speedPercent, 0, 100) / 100.0 * 5200);

    public static Animation Generate(ZoneLighting z, int ledCount)
    {
        ledCount = Math.Max(1, ledCount);
        var colors = z.Colors.Count > 0 ? z.Colors : [RgbColor.White];
        int interval = Math.Clamp(PeriodMs(z.Speed) / MaxFrames, 20, 250);

        RgbColor[] Fill(RgbColor c) => Enumerable.Repeat(c.WithBrightness(z.Brightness), ledCount).ToArray();
        Animation Anim(Func<int, int, RgbColor> pixel) => new(
            Enumerable.Range(0, MaxFrames).Select(f => Enumerable.Range(0, ledCount).Select(i => pixel(f, i).WithBrightness(z.Brightness)).ToArray()).ToList(),
            interval);

        switch (z.Effect)
        {
            case LightingEffect.Off:
                return new([Enumerable.Repeat(RgbColor.Black, ledCount).ToArray()], 100);
            case LightingEffect.Static:
                return new([Fill(colors[0])], 100);
            case LightingEffect.Gradient:
                return new([Enumerable.Range(0, ledCount).Select(i => Sample(colors, ledCount == 1 ? 0 : i / (double)(ledCount - 1)).WithBrightness(z.Brightness)).ToArray()], 100);
            case LightingEffect.Breathing:
                return Anim((f, _) =>
                {
                    double level = 0.5 - 0.5 * Math.Cos(2 * Math.PI * f / MaxFrames);
                    var c = colors[f * colors.Count / MaxFrames % colors.Count];
                    return new RgbColor((byte)(c.R * level), (byte)(c.G * level), (byte)(c.B * level));
                });
            case LightingEffect.Flash:
                return Anim((f, _) => f % 12 < 3 ? colors[f / 12 % colors.Count] : RgbColor.Black);
            case LightingEffect.ColorCycle:
                return colors.Count >= 2
                    ? Anim((f, _) => SampleCyclic(colors, f / (double)MaxFrames))
                    : Anim((f, _) => RgbColor.FromHsv(360.0 * f / MaxFrames));
            case LightingEffect.Rainbow:
                return Anim((f, i) => RgbColor.FromHsv(360.0 * i / ledCount - 360.0 * f / MaxFrames));
            case LightingEffect.Wave:
                return Anim((f, i) => SampleCyclic(colors.Count >= 2 ? colors : [colors[0], RgbColor.Black], (i / (double)ledCount + f / (double)MaxFrames) % 1));
            default:
                return new([Fill(colors[0])], 100);
        }
    }

    /// <summary>Color at position t ∈ [0,1] along a multi-stop gradient.</summary>
    public static RgbColor Sample(IReadOnlyList<RgbColor> stops, double t)
    {
        if (stops.Count == 1) return stops[0];
        t = Math.Clamp(t, 0, 1) * (stops.Count - 1);
        int i = Math.Min((int)t, stops.Count - 2);
        return RgbColor.Lerp(stops[i], stops[i + 1], t - i);
    }

    private static RgbColor SampleCyclic(IReadOnlyList<RgbColor> stops, double t)
    {
        t = (t % 1 + 1) % 1 * stops.Count;
        int i = (int)t % stops.Count;
        return RgbColor.Lerp(stops[i], stops[(i + 1) % stops.Count], t - Math.Floor(t));
    }
}
