namespace MyLianPC.Core.Lighting;

/// <summary>
/// Effects the application knows about. An adapter advertises the subset its hardware really
/// implements (see <see cref="ZoneCapabilities"/>); the UI must not offer anything outside that set.
/// </summary>
public enum LightingEffect
{
    Off,
    Static,
    Breathing,
    ColorCycle,
    Rainbow,
    Wave,
    Gradient,
    Flash,
}

/// <summary>What one lighting zone can actually do, as reported by its adapter.</summary>
public sealed record ZoneCapabilities(
    IReadOnlySet<LightingEffect> Effects,
    bool HardwareBrightness,
    bool PerLedColor,
    int LedCount,
    int MaxColors);

/// <summary>The requested lighting for one zone. Speed and brightness are percentages.</summary>
public sealed record ZoneLighting
{
    public LightingEffect Effect { get; init; } = LightingEffect.Static;
    public IReadOnlyList<RgbColor> Colors { get; init; } = [RgbColor.White];
    public int Brightness { get; init; } = 100;
    public int Speed { get; init; } = 50;

    /// <summary>Checks the request against what the zone supports. Returns human-readable problems; empty means valid.</summary>
    public IReadOnlyList<string> Validate(ZoneCapabilities caps)
    {
        var problems = new List<string>();
        if (!caps.Effects.Contains(Effect)) problems.Add($"Effect '{Effect}' is not supported by this zone.");
        if (Brightness is < 0 or > 100) problems.Add("Brightness must be between 0 and 100.");
        if (Speed is < 0 or > 100) problems.Add("Speed must be between 0 and 100.");
        if (Effect is not (LightingEffect.Off or LightingEffect.Rainbow) && Colors.Count == 0)
            problems.Add("At least one color is required for this effect.");
        if (Colors.Count > caps.MaxColors) problems.Add($"This zone accepts at most {caps.MaxColors} color(s).");
        return problems;
    }
}
