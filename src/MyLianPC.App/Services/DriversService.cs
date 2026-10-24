using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using MyLianPC.Core.Drivers;
using MyLianPC.Core.Settings;

namespace MyLianPC.App.Services;

/// <summary>What Windows reports about this machine's board and BIOS.</summary>
public sealed record BoardInfo(string Manufacturer, string Product, string BiosVersion, DateTime? BiosDate)
{
    public string Name => string.Join(' ', new[] { Manufacturer, Product }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
}

/// <summary>
/// Reads the installed driver inventory and caches GIGABYTE's download list. Everything here is read-only;
/// no driver is ever installed from the app. The support page is fetched only when the Drivers page asks for it.
/// </summary>
public static class DriversService
{
    public static string CachePath { get; } = Path.Combine(
        Path.GetDirectoryName(UserPreferences.DefaultPath)!, "driver-catalog.json");

    /// <summary>Filesystem date of the cached support list, or null when nothing was ever fetched.</summary>
    public static DateTimeOffset? CacheTime
    {
        get
        {
            try { return File.Exists(CachePath) ? File.GetLastWriteTimeUtc(CachePath) : null; }
            catch (IOException) { return null; }
        }
    }

    public static SupportCatalog? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            return JsonSerializer.Deserialize<SupportCatalog>(File.ReadAllText(CachePath), Json.Options);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static async Task<SupportCatalog> RefreshAsync(CancellationToken stop = default)
    {
        var catalog = await GigabyteSupport.FetchAsync(stop);
        try { AtomicFile.WriteAllText(CachePath, JsonSerializer.Serialize(catalog, Json.Options)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the live result is still shown */ }
        return catalog;
    }

    public static Task<BoardInfo> ReadBoardAsync() => Task.Run(ReadBoard);

    public static Task<IReadOnlyList<InstalledItem>> ReadInstalledAsync() => Task.Run(ReadInstalled);

    private static BoardInfo ReadBoard()
    {
        string manufacturer = "", product = "", bios = "";
        DateTime? date = null;
        try
        {
            using var board = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (ManagementBaseObject item in board.Get())
            {
                using (item)
                {
                    manufacturer = (item["Manufacturer"] as string ?? "").Trim();
                    product = (item["Product"] as string ?? "").Trim();
                    break;
                }
            }
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException) { }

        try
        {
            using var biosQuery = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            foreach (ManagementBaseObject item in biosQuery.Get())
            {
                using (item)
                {
                    bios = (item["SMBIOSBIOSVersion"] as string ?? "").Trim();
                    if (item["ReleaseDate"] is string raw)
                    {
                        try { date = ManagementDateTimeConverter.ToDateTime(raw).Date; }
                        catch (FormatException) { }
                        catch (ArgumentOutOfRangeException) { }
                    }
                    break;
                }
            }
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException) { }

        return new BoardInfo(manufacturer, product, bios, date);
    }

    /// <summary>
    /// Signed PnP drivers (the version Device Manager shows) plus installed programs (the version the vendor's
    /// setup package reports). Both are needed: Realtek's driver device and its setup package use different
    /// version schemes and either can equal the number GIGABYTE lists.
    /// </summary>
    private static IReadOnlyList<InstalledItem> ReadInstalled()
    {
        var items = new List<InstalledItem>();
        try
        {
            using var drivers = new ManagementObjectSearcher("SELECT DeviceName, DriverVersion FROM Win32_PnPSignedDriver");
            foreach (ManagementBaseObject item in drivers.Get())
            {
                using (item)
                {
                    string name = (item["DeviceName"] as string ?? "").Trim();
                    string version = (item["DriverVersion"] as string ?? "").Trim();
                    if (name.Length > 0 && version.Length > 0) items.Add(new InstalledItem(name, version, InstalledItem.Driver));
                }
            }
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException) { }

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (string keyName in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(keyName);
                    if (key is null) continue;
                    if (key.GetValue("SystemComponent") is int system && system == 1) continue;
                    string name = (key.GetValue("DisplayName") as string ?? "").Trim();
                    string version = (key.GetValue("DisplayVersion") as string ?? "").Trim();
                    if (name.Length > 0 && version.Length > 0) items.Add(new InstalledItem(name, version, InstalledItem.Program));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }

        return items;
    }

    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
    }
}
