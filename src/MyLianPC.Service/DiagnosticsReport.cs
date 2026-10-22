using System.Text;
using MyLianPC.Core.Devices;

namespace MyLianPC.Service;

/// <summary>Plain-text report users can export when troubleshooting.</summary>
public sealed class DiagnosticsReport(HardwareManager hw, VendorManager vendor, FileLoggerProvider logs)
{
    public string Build()
    {
        var sb = new StringBuilder();
        var status = hw.Status();
        sb.AppendLine($"MyLianPC diagnostics — {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Service {status.Version}, running since {status.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version}, machine {Environment.MachineName}");
        sb.AppendLine($"PawnIO installed: {status.PawnIoInstalled}");
        sb.AppendLine($"Active profile: {status.ActiveProfileName ?? "(none)"}");
        sb.AppendLine();
        foreach (var d in status.Devices)
        {
            sb.AppendLine($"[{d.State}] {d.Manufacturer} {d.Name}  ({d.Kind})");
            if (d.Firmware is not null) sb.AppendLine($"    firmware: {d.Firmware}");
            if (d.Location is not null) sb.AppendLine($"    location: {d.Location}");
            if (d.LastContactUtc is { } t) sb.AppendLine($"    last contact: {t.ToLocalTime():HH:mm:ss}");
            foreach (var z in d.Zones) sb.AppendLine($"    zone {z.Id}: {z.Name}, {z.LedCount} LED(s), effects {string.Join("/", z.Effects)}");
            foreach (var f in d.Fans) sb.AppendLine($"    fan {f.Id}: {f.Name} via {f.Controller}, {f.Rpm?.ToString("0") ?? "n/a"} rpm, control {(f.ControlEnabled ? "on" : "off")}");
            foreach (var w in d.Warnings) sb.AppendLine($"    warning: {w}");
            if (d.Error is not null) sb.AppendLine($"    ERROR: {d.Error}");
            if (d.Troubleshooting is not null) sb.AppendLine($"    fix: {d.Troubleshooting}");
        }
        sb.AppendLine();
        var v = vendor.Status();
        sb.AppendLine("Vendor software:");
        foreach (var c in v.Components) sb.AppendLine($"    {c.Name}: {(c.Present ? c.State : "not installed")}");
        sb.AppendLine($"    conflicting processes: {(v.ConflictingProcesses.Count == 0 ? "none" : string.Join(", ", v.ConflictingProcesses))}");
        if (status.LastApply is { } a)
        {
            sb.AppendLine();
            sb.AppendLine($"Last apply: '{a.ProfileName}' at {a.TimeUtc.ToLocalTime():HH:mm:ss}");
            foreach (var r in a.Results) sb.AppendLine($"    {(r.Success ? "OK  " : "FAIL")} {r.DeviceId} / {r.Part}: {r.Message}");
        }
        sb.AppendLine();
        sb.AppendLine("Sensors:");
        foreach (var s in hw.Snapshot.All.Where(s => s.Value is not null).Take(200)) sb.AppendLine($"    {s.Group} / {s.Label}: {s.Value:0.#} {s.Unit}");
        sb.AppendLine();
        sb.AppendLine("Recent service log:");
        foreach (var l in logs.Tail.TakeLast(150)) sb.AppendLine("    " + l);
        return sb.ToString();
    }
}
