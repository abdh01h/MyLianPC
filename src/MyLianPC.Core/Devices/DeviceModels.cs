using MyLianPC.Core.Lighting;

namespace MyLianPC.Core.Devices;

public enum DeviceState
{
    Connected,
    Disconnected,
    /// <summary>Another process holds the device (e.g. vendor software).</summary>
    Busy,
    /// <summary>The device stopped answering. Usually needs a power cycle.</summary>
    Unresponsive,
    /// <summary>A required driver or component is missing (e.g. PawnIO).</summary>
    NeedsDriver,
    Unsupported,
    Error,
}

/// <summary>A lighting zone and exactly what it can do on this hardware.</summary>
public sealed record ZoneInfo
{
    public required string Id { get; init; }
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public int LedCount { get; init; }
    public IReadOnlyList<LightingEffect> Effects { get; init; } = [];
    public int MaxColors { get; init; } = 1;
    public bool HasBrightness { get; init; } = true;
    public bool HasSpeed { get; init; } = true;
    /// <summary>True if the zone's LED count can be configured by the user (plain ARGB headers).</summary>
    public bool LedCountConfigurable { get; init; }
    public string? Note { get; init; }

    public ZoneCapabilities ToCapabilities() =>
        new(new HashSet<LightingEffect>(Effects), HasBrightness, LedCount > 1, LedCount, MaxColors);
}

public sealed record FanChannelInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Who drives this fan, e.g. "HydroShift II pump head" or "Motherboard (ITE IT8696E)".</summary>
    public required string Controller { get; init; }
    public double? Rpm { get; init; }
    public int? DutyPercent { get; init; }
    public bool Controllable { get; init; }
    /// <summary>Motherboard headers are monitoring-only until the user opts in.</summary>
    public bool ControlEnabled { get; init; }
    public string? Note { get; init; }
}

public sealed record DeviceStatus
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Manufacturer { get; init; }
    public required string Kind { get; init; }
    public DeviceState State { get; init; }
    public string? Firmware { get; init; }
    public string? Location { get; init; }
    public IReadOnlyList<ZoneInfo> Zones { get; init; } = [];
    public IReadOnlyList<FanChannelInfo> Fans { get; init; } = [];
    public bool HasLcd { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? Error { get; init; }
    public string? Troubleshooting { get; init; }
    public DateTimeOffset? LastContactUtc { get; init; }
}

/// <summary>Result of applying one part of a profile to one device.</summary>
public sealed record ApplyResult(string DeviceId, string Part, bool Success, string Message);

public sealed record ApplyReport
{
    public string ProfileName { get; init; } = "";
    public DateTimeOffset TimeUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<ApplyResult> Results { get; init; } = [];
    public bool AllSucceeded => Results.All(r => r.Success);
}
