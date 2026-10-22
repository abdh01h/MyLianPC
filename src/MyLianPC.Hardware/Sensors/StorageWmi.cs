using System.Management;

namespace MyLianPC.Hardware.Sensors;

/// <summary>
/// Drive temperature, wear and health state from the Windows storage stack (root\Microsoft\Windows\Storage).
/// LibreHardwareMonitor only reads SMART from some controllers — several drives answer with zeros — so this fills
/// the gaps and also covers drives it does not see at all. The provider needs administrator or LocalSystem rights;
/// for a standard user it returns nothing and the caller keeps what LibreHardwareMonitor found.
/// </summary>
public static class StorageWmi
{
    private const string Namespace = @"root\Microsoft\Windows\Storage";

    /// <summary>The last problem hit while querying the storage stack (null when everything answered).</summary>
    public static string? LastError { get; private set; }

    /// <summary>Temperature in °C, wear in percent (0 = as new), power-on hours and the firmware's health verdict.</summary>
    public sealed record DriveData(double? TemperatureCelsius, double? WearPercent, double? PowerOnHours, string? HealthState)
    {
        /// <summary>Capacity and free space (from the drive's volumes) when they are known.</summary>
        public long? TotalBytes { get; init; }
        public long? FreeBytes { get; init; }
        /// <summary>Drive letters of the volumes on this disk, e.g. "C:, D:".</summary>
        public string? Volumes { get; init; }
    }

    public static IReadOnlyDictionary<string, DriveData> Read()
    {
        var result = new Dictionary<string, DriveData>(StringComparer.OrdinalIgnoreCase);
        LastError = null;
        Dictionary<int, (long Free, string Letters)> volumes = [];
        try
        {
            var scope = new ManagementScope(Namespace);
            scope.Connect();
            volumes = ReadVolumes(scope);
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, ObjectId, FriendlyName, HealthStatus, Size FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject disk in searcher.Get())
            {
                using (disk)
                {
                    if (disk["FriendlyName"] is not string name || name.Length == 0) continue;
                    // MSFT_PhysicalDisk.DeviceId is the disk number as a string ("0", "1", ...); MSFT_Partition.DiskNumber is a number.
                    var volume = int.TryParse(disk["DeviceId"] as string, out int diskNumber) && volumes.TryGetValue(diskNumber, out var found)
                        ? found
                        : ((long Free, string Letters)?)null;
                    var data = new DriveData(null, null, null, HealthState(Number(disk["HealthStatus"])))
                    {
                        // The disk's own size is the capacity printed on the label; the partitions leave gaps unaccounted for.
                        TotalBytes = Number(disk["Size"]) is { } size and > 0 ? (long)size : null,
                        FreeBytes = volume?.Free,
                        Volumes = volume?.Letters is { Length: > 0 } letters ? letters : null,
                    };
                    // GetRelated() throws on this namespace, so the reliability counter is fetched with an ASSOCIATORS query.
                    if (disk["ObjectId"] is string objectId && objectId.Length > 0)
                    {
                        try
                        {
                            string escaped = objectId.Replace("\\", "\\\\").Replace("\"", "\\\"");
                            using var counters = new ManagementObjectSearcher(scope, new ObjectQuery(
                                $"ASSOCIATORS OF {{MSFT_PhysicalDisk.ObjectId=\"{escaped}\"}} WHERE ResultClass=MSFT_StorageReliabilityCounter"));
                            foreach (ManagementObject counter in counters.Get())
                            {
                                using (counter)
                                {
                                    data = data with
                                    {
                                        TemperatureCelsius = Number(counter["Temperature"]),
                                        WearPercent = Number(counter["Wear"]),
                                        PowerOnHours = Number(counter["PowerOnHours"]),
                                    };
                                }
                                break;
                            }
                        }
                        catch (ManagementException) { /* this drive exposes no reliability counters */ }
                    }
                    result[name] = data;
                }
            }
        }
        catch (Exception ex) { LastError = $"physical disks: {ex.Message}"; /* provider unavailable or access denied: nothing to add */ }
        return result;
    }

    private static string? HealthState(double? status) => status switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => null,
    };

    /// <summary>Free space and drive letters per physical disk, summed over its volumes.</summary>
    private static Dictionary<int, (long Free, string Letters)> ReadVolumes(ManagementScope scope)
    {
        var perDisk = new Dictionary<int, (long Free, string Letters)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition"));
            foreach (ManagementObject partition in searcher.Get())
            {
                using (partition)
                {
                    if (Number(partition["DiskNumber"]) is not { } diskNumber) continue;
                    int disk = (int)diskNumber;
                    long free = 0;
                    string letter = "";
                    // DriveLetter is a char that is 0 or a space when the partition has no letter (recovery, MSR, ...).
                    if (Number(partition["DriveLetter"]) is { } letterValue and > 0 && (char)(int)letterValue is var c && c is >= 'A' and <= 'Z')
                    {
                        try { free = new DriveInfo($"{c}:").AvailableFreeSpace; }
                        catch (Exception) { /* no volume mounted for that letter */ }
                        letter = $"{c}:";
                    }
                    var current = perDisk.GetValueOrDefault(disk);
                    // The default tuple has a null Letters, so it is checked with a pattern instead of being dereferenced.
                    perDisk[disk] = (current.Free + free, current.Letters is { Length: > 0 } existing
                        ? (letter.Length == 0 ? existing : $"{existing}, {letter}")
                        : letter);
                }
            }
        }
        catch (Exception ex) { LastError = $"volumes: {ex.Message}"; /* no capacity information */ }
        return perDisk;
    }

    private static bool SameDrive(string a, string b) =>
        a.Length > 0 && b.Length > 0 &&
        (a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase));

    private static double? Number(object? value) => value switch
    {
        char c => c,
        byte b => b,
        ushort u => u,
        uint i => i,
        ulong l => l,
        short s => s,
        int n => n,
        long q => q,
        _ => null,
    };
}
