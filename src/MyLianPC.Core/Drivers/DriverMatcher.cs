namespace MyLianPC.Core.Drivers;

/// <summary>
/// Matches a GIGABYTE download entry to the driver or program actually installed on this PC, then compares
/// the versions. The rules are keyword based on purpose: the support page names packages ("Realtek LAN
/// Driver") while Windows lists devices ("Realtek PCIe 2.5GbE Family Controller"). Where a driver's Windows
/// version and the vendor package version use different schemes (the AMD graphics driver), the result is
/// "Check manually" instead of a false update.
/// </summary>
public static class DriverMatcher
{
    private sealed record Rule(
        string[] EntryContains,
        string[] InstalledContains,
        string[] Exclude,
        bool PreferProgram,
        bool SkipCompare = false);

    private static readonly Rule[] Rules =
    [
        new(["hd audio"], ["realtek audio driver", "realtek high definition audio"], ["nvidia", "bluetooth"], PreferProgram: true),
        new(["realtek lan"], ["realtek ethernet controller driver", "realtek ethernet controller", "realtek pcie", "realtek gbe"],
            ["wireless", "wifi", "wi-fi", "bluetooth", "virtual"], PreferProgram: true),
        new(["realtek wifi"], ["realtek 8922", "realtek wifi", "realtek wireless", "realtek wi-fi"], ["bluetooth"], PreferProgram: false),
        new(["realtek bluetooth"], ["realtek leai", "realtek bluetooth"], ["le audio", "acx"], PreferProgram: false),
        new(["acx"], ["realtek bluetooth le audio", "le audio"], [], PreferProgram: false),
        new(["amd chipset"], ["amd chipset", "amd_chipset"], ["audio", "graphics", "radeon"], PreferProgram: true),
        new(["amd apu"], ["amd radeon", "amd software"], ["audio"], PreferProgram: false, SkipCompare: true),
        new(["raidxpert2"], ["raidxpert2"], [], PreferProgram: true),
    ];

    public static DriverStatus Evaluate(DriverEntry entry, IReadOnlyList<InstalledItem> installed)
    {
        var match = Match(entry, installed);
        if (match is null)
        {
            return entry.Name.Contains("preinstall", StringComparison.OrdinalIgnoreCase)
                ? new DriverStatus(DriverState.SetupOnly, "Setup only", null)
                : new DriverStatus(DriverState.NotDetected, "Not detected", null);
        }

        var rule = RuleFor(entry);
        if (rule?.SkipCompare == true) return new DriverStatus(DriverState.Manual, "Check manually", match);

        int? cmp = Versions.CompareNumeric(match.Version, entry.Version);
        if (cmp is null) return new DriverStatus(DriverState.Manual, "Check manually", match);
        return cmp switch
        {
            < 0 => new DriverStatus(DriverState.UpdateAvailable, "Update available", match),
            0 => new DriverStatus(DriverState.UpToDate, "Up to date", match),
            _ => new DriverStatus(DriverState.NewerInstalled, "Newer installed", match),
        };
    }

    private static Rule? RuleFor(DriverEntry entry) =>
        Rules.FirstOrDefault(r => r.EntryContains.All(t => entry.Name.Contains(t, StringComparison.OrdinalIgnoreCase)));

    private static InstalledItem? Match(DriverEntry entry, IReadOnlyList<InstalledItem> installed)
    {
        var rule = RuleFor(entry);

        if (rule is not null)
        {
            foreach (string keyword in rule.InstalledContains)
            {
                var hits = installed
                    .Where(i => i.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    .Where(i => !rule.Exclude.Any(e => i.Name.Contains(e, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (hits.Count == 0) continue;
                // A PnP device and an uninstall entry can both match with different versions (the Realtek LAN
                // driver reports 1125.29.50.202 as a device and 11.29.50.0202 as a package). Prefer the source
                // whose version equals the listed one, then the source the rule expects.
                return hits
                    .OrderByDescending(i => MatchesExactly(i, entry))
                    .ThenByDescending(i => rule.PreferProgram ? i.Source == InstalledItem.Program : i.Source == InstalledItem.Driver)
                    .First();
            }
        }

        // Unknown package: the longest meaningful word in its name, so a newly listed driver still has a chance.
        string? token = entry.Name.Split([' ', '-', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 4)
            .OrderByDescending(t => t.Length)
            .FirstOrDefault();
        return token is null ? null : installed.FirstOrDefault(i => i.Name.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesExactly(InstalledItem item, DriverEntry entry) => Versions.CompareNumeric(item.Version, entry.Version) == 0;
}
