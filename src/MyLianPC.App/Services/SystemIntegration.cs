using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using MyLianPC.Core.Settings;

namespace MyLianPC.App.Services;

/// <summary>Windows integration that belongs to the current user: theme, login startup, Dynamic Lighting, elevation.</summary>
public static class SystemIntegration
{
    public const string ServiceName = "MyLianPCService";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "MyLianPC";

    /// <summary>Value the installer of the previous app name wrote; still cleaned up and honoured.</summary>
    private const string LegacyRunValue = "MyPcRgb";

    /// <summary>
    /// Applies the interface theme. Only touches WPF resources; it never talks to the service, so it cannot
    /// change lighting, LCD or cooling state. System mode follows the Windows app theme live.
    /// </summary>
    public static void ApplyTheme(AppearanceMode mode)
    {
        System.Windows.Application.Current.ThemeMode = mode switch
        {
            AppearanceMode.Light => System.Windows.ThemeMode.Light,
            AppearanceMode.System => System.Windows.ThemeMode.System,
            _ => System.Windows.ThemeMode.Dark,
        };
    }

    /// <summary>
    /// Whether the window is started with Windows. The installer writes a machine-wide entry; the Settings page
    /// writes a per-account one. Either of them (also under the previous app name) counts as enabled.
    /// </summary>
    public static bool LaunchAtLogin =>
        StartupValue(Registry.CurrentUser) is not null || StartupValue(Registry.LocalMachine) is not null;

    /// <summary>Where the start-up entry lives, for the caption in Settings. Null when the app does not start with Windows.</summary>
    public static string? StartupLocation =>
        StartupValue(Registry.CurrentUser) is not null ? "this account"
        : StartupValue(Registry.LocalMachine) is not null ? "all accounts"
        : null;

    /// <summary>Points a per-account start-up entry left behind by the old app name at this installation.</summary>
    public static void AdoptLegacyStartupEntry()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k?.GetValue(LegacyRunValue) is not string) return;
            k.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
            // The installer's machine-wide entry already starts the app; a second entry would start it twice.
            if (StartupValue(Registry.LocalMachine) is not null) return;
            k.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray");
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string? StartupValue(RegistryKey hive)    {
        using var k = hive.OpenSubKey(RunKey);
        return (k?.GetValue(RunValue) ?? k?.GetValue(LegacyRunValue)) as string;
    }

    /// <summary>
    /// Turns the start-up entry on or off for every account. Returns false when the machine-wide entry of the
    /// installer had to be removed and the user declined the administrator prompt.
    /// </summary>
    public static async Task<bool> SetLaunchAtLoginAsync(bool enabled)
    {
        bool machineEntry = StartupValue(Registry.LocalMachine) is not null;
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            k.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
            if (enabled && !machineEntry) k.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        if (!enabled && machineEntry)
        {
            // The installer's entry is machine-wide, so removing it needs administrator approval.
            await RunElevatedAsync("powershell.exe",
                $"-NoProfile -Command \"Remove-ItemProperty -LiteralPath 'HKLM:\\{RunKey}' -Name {RunValue},{LegacyRunValue} -ErrorAction SilentlyContinue\"");
            return StartupValue(Registry.LocalMachine) is null;
        }
        return true;
    }

    // ---------------------------------------------------------------- Windows Dynamic Lighting (per user)

    private const string LightingDevices = @"Software\Microsoft\Lighting\Devices";

    /// <summary>
    /// Whether Windows Dynamic Lighting controls the motherboard's LampArray interface in this account. It is a
    /// per-user setting, so each Windows account must turn it off once (the app offers this on first run).
    /// </summary>
    public static bool? DynamicLightingEnabledForMotherboard
    {
        get
        {
            using var devices = Registry.CurrentUser.OpenSubKey(LightingDevices);
            if (devices is null) return null;
            var entries = devices.GetSubKeyNames().Where(n => n.Contains("VID_048D", StringComparison.OrdinalIgnoreCase)).ToList();
            if (entries.Count == 0) return null;
            return entries.Any(n =>
            {
                using var d = devices.OpenSubKey(n);
                return d?.GetValue("AmbientLightingEnabled") is not int v || v != 0;
            });
        }
    }

    public static void DisableDynamicLightingForMotherboard()
    {
        using var devices = Registry.CurrentUser.OpenSubKey(LightingDevices, writable: true);
        if (devices is null) return;
        foreach (var n in devices.GetSubKeyNames().Where(n => n.Contains("VID_048D", StringComparison.OrdinalIgnoreCase)))
        {
            using var d = devices.OpenSubKey(n, writable: true);
            d?.SetValue("AmbientLightingEnabled", 0, RegistryValueKind.DWord);
            d?.SetValue("ControlledByForegroundApp", 0, RegistryValueKind.DWord);
        }
    }

    // ---------------------------------------------------------------- service control (elevated helpers)

    public static string ServiceState()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status.ToString();
        }
        catch (InvalidOperationException) { return "Not installed"; }
    }

    public static string ServiceExe => Path.Combine(AppContext.BaseDirectory, "MyLianPC.Service.exe");

    /// <summary>Runs a command elevated (one UAC prompt). Returns false if the user declined.</summary>
    public static async Task<bool> RunElevatedAsync(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // UAC declined
    }

    public static Task<bool> RestartServiceAsync() =>
        RunElevatedAsync("powershell.exe", $"-NoProfile -Command \"Restart-Service -Name {ServiceName} -Force\"");

    public static Task<bool> VendorTakeoverAsync() => RunElevatedAsync(ServiceExe, "--vendor-takeover");

    public static Task<bool> VendorRestoreAsync() => RunElevatedAsync(ServiceExe, "--vendor-restore");
}
