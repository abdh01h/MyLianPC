using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
using MyLianPC.Core.Ipc;
using MyLianPC.Core.Settings;

namespace MyLianPC.Service;

/// <summary>
/// Detects and (only on explicit administrator request) disables vendor software that would fight over the
/// same hardware: L-Connect 3, Corsair Device Control Service and GIGABYTE Control Center's autostart.
/// Original states are recorded so <see cref="Restore"/> puts everything back exactly as it was.
/// Nothing is ever uninstalled.
/// </summary>
public sealed class VendorManager(ILogger<VendorManager> log)
{
    private static readonly string[] LConnectServices = ["LConnectServiceWatcher", "LConnectService"];
    private const string CorsairService = "CorsairDeviceControlService";
    private const string GccTask = "GCC";

    /// <summary>Process names whose presence means "someone else may change the lighting/cooling".</summary>
    private static readonly string[] ConflictProcesses =
        ["L-Connect-Service", "L-Connect 3", "GCC", "GBT_DL_LIB", "CorsairDeviceControlService", "iCUE", "OpenRGB", "SignalRgb", "SignalRgbLauncher", "FanControl", "NZXT CAM"];

    public sealed record SavedState(Dictionary<string, string> ServiceStartModes, bool? GccTaskEnabled);

    public IReadOnlyList<string> ConflictingProcesses() =>
        ConflictProcesses.Where(n => Process.GetProcessesByName(n).Length > 0).ToList();

    public VendorStatus Status()
    {
        var components = new List<VendorComponent>();
        var lc = LConnectServices.Select(ServiceInfo).ToList();
        components.Add(new("lconnect", "Lian Li L-Connect 3",
            "Owns the cooler (LCD, pump, fans, ring). Must not run, because the cooler accepts only one controlling program.",
            lc.Any(s => s.Present), lc.Any(s => s.Running || s.StartMode == "Auto"),
            string.Join(", ", lc.Where(s => s.Present).Select(s => $"{s.Name}: {(s.Running ? "running" : "stopped")}, {s.StartMode}"))));
        var cdcs = ServiceInfo(CorsairService);
        components.Add(new("corsair-dcs", "Corsair Device Control Service",
            "Used by GIGABYTE Control Center to light the RAM. Shares the SMBus with this app; when its client disconnects it resets the RAM lighting.",
            cdcs.Present, cdcs.Running || cdcs.StartMode is "Auto" or "Manual",
            cdcs.Present ? $"{(cdcs.Running ? "running" : "stopped")}, {cdcs.StartMode}" : "not installed"));
        var gcc = GccTaskState();
        components.Add(new("gcc", "GIGABYTE Control Center autostart",
            "Starts in every Windows account at login and re-applies its own lighting profile, which is what changes the case/RAM lighting when you switch accounts.",
            gcc is not null, gcc == true || Process.GetProcessesByName("GCC").Length > 0,
            gcc is null ? "not installed" : $"logon task {(gcc == true ? "enabled" : "disabled")}{(Process.GetProcessesByName("GCC").Length > 0 ? ", running" : "")}"));
        return new VendorStatus(components, ConflictingProcesses());
    }

    /// <summary>Disables the selected components (all when <paramref name="ids"/> is empty). Requires administrator rights.</summary>
    public VendorStatus Takeover(IReadOnlyCollection<string> ids)
    {
        bool All(string id) => ids.Count == 0 || ids.Contains(id);
        var saved = LoadState() ?? new SavedState([], null);
        if (All("lconnect"))
        {
            foreach (var s in LConnectServices) Disable(s, saved);
            Kill("L-Connect-Service-Watcher", "L-Connect-Service");
            KillUnder(@"\Lian-Li\L-Connect 3\", "CefSharp.BrowserSubprocess");
        }
        if (All("corsair-dcs")) Disable(CorsairService, saved);
        if (All("gcc"))
        {
            var enabled = GccTaskState();
            if (enabled is not null)
            {
                saved = saved with { GccTaskEnabled = saved.GccTaskEnabled ?? enabled };
                RunQuiet("schtasks.exe", $"/Change /TN \"{GccTask}\" /DISABLE");
            }
            Kill("GCC", "GBT_DL_LIB");
        }
        SaveState(saved);
        log.LogInformation("Vendor takeover done for: {Ids}", ids.Count == 0 ? "all" : string.Join(",", ids));
        return Status();
    }

    /// <summary>Puts every component back the way it was before <see cref="Takeover"/>.</summary>
    public VendorStatus Restore()
    {
        var saved = LoadState();
        if (saved is null) return Status();
        foreach (var (name, mode) in saved.ServiceStartModes)
        {
            RunQuiet("sc.exe", $"config \"{name}\" start= {ScStart(mode)}");
            if (mode == "Auto") try { using var sc = new ServiceController(name); sc.Start(); } catch (Exception) { }
        }
        if (saved.GccTaskEnabled == true) RunQuiet("schtasks.exe", $"/Change /TN \"{GccTask}\" /ENABLE");
        File.Delete(ServicePaths.VendorState);
        log.LogInformation("Vendor software restored");
        return Status();
    }

    private void Disable(string service, SavedState saved)
    {
        var info = ServiceInfo(service);
        if (!info.Present) return;
        saved.ServiceStartModes.TryAdd(service, info.StartMode);
        RunQuiet("sc.exe", $"config \"{service}\" start= disabled");
        try
        {
            using var sc = new ServiceController(service);
            if (sc.Status is not ServiceControllerStatus.Stopped and not ServiceControllerStatus.StopPending) sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        }
        catch (Exception e) { log.LogWarning("Stopping {Service}: {Message}", service, e.Message); }
    }

    private static (string Name, bool Present, bool Running, string StartMode) ServiceInfo(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            var mode = sc.StartType switch
            {
                ServiceStartMode.Automatic => "Auto",
                ServiceStartMode.Manual => "Manual",
                ServiceStartMode.Disabled => "Disabled",
                var m => m.ToString(),
            };
            return (name, true, sc.Status == ServiceControllerStatus.Running, mode);
        }
        catch (InvalidOperationException) { return (name, false, false, "-"); }
    }

    private static bool? GccTaskState()
    {
        var (code, output) = RunQuiet("schtasks.exe", $"/Query /TN \"{GccTask}\" /FO CSV /NH");
        if (code != 0) return null;
        return !output.Contains("Disabled", StringComparison.OrdinalIgnoreCase);
    }

    private static string ScStart(string mode) => mode switch { "Auto" => "auto", "Disabled" => "disabled", _ => "demand" };

    private void Kill(params string[] names)
    {
        foreach (var p in names.SelectMany(Process.GetProcessesByName))
        {
            try { p.Kill(); log.LogInformation("Closed {Process} ({Pid})", p.ProcessName, p.Id); }
            catch (Exception e) { log.LogWarning("Could not close {Process}: {Message}", p.ProcessName, e.Message); }
        }
    }

    private void KillUnder(string pathFragment, string name)
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            {
                if (p.MainModule?.FileName.Contains(pathFragment, StringComparison.OrdinalIgnoreCase) == true) p.Kill();
            }
            catch (Exception) { }
        }
    }

    private static (int Code, string Output) RunQuiet(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return (p.ExitCode, output);
        }
        catch (Exception e) { return (-1, e.Message); }
    }

    private static SavedState? LoadState()
    {
        try { return File.Exists(ServicePaths.VendorState) ? JsonSerializer.Deserialize<SavedState>(File.ReadAllText(ServicePaths.VendorState), Json.Options) : null; }
        catch (JsonException) { return null; }
    }

    private static void SaveState(SavedState s) => AtomicFile.WriteAllText(ServicePaths.VendorState, JsonSerializer.Serialize(s, Json.Options));
}
