namespace MyLianPC.Core.Drivers;

/// <summary>One BIOS release listed on the board's support page.</summary>
public sealed record BiosEntry(
    string Version,
    string? Date,
    string? Size,
    string DownloadUrl,
    IReadOnlyList<string> Notes);

/// <summary>One downloadable driver package for the board (already filtered to a Windows 11 build).</summary>
public sealed record DriverEntry(
    string Category,
    string Name,
    string Version,
    string? Date,
    string? Size,
    IReadOnlyList<string> Os,
    string DownloadUrl);

/// <summary>What GIGABYTE's public support page currently offers for the board.</summary>
public sealed record SupportCatalog
{
    public string SourceUrl { get; init; } = "";
    public string Board { get; init; } = "";
    public DateTimeOffset FetchedUtc { get; init; }
    public IReadOnlyList<BiosEntry> Bios { get; init; } = [];
    public IReadOnlyList<DriverEntry> Drivers { get; init; } = [];
}

/// <summary>An installed driver or program, from PnP or the uninstall registry.</summary>
public sealed record InstalledItem(string Name, string Version, string Source)
{
    public const string Driver = "driver", Program = "program";
}

public enum DriverState
{
    /// <summary>The installed version could not be read or compared.</summary>
    Unknown,
    UpToDate,
    UpdateAvailable,
    /// <summary>The installed driver is newer than the one GIGABYTE lists (e.g. from Windows Update).</summary>
    NewerInstalled,
    /// <summary>No matching device or program was found.</summary>
    NotDetected,
    /// <summary>Both versions were found, but their numbering schemes cannot be compared.</summary>
    Manual,
    /// <summary>Shipped only for Windows Setup (RAID preinstall), so it is not normally installed.</summary>
    SetupOnly,
}

public sealed record DriverStatus(DriverState State, string Label, InstalledItem? Installed);
