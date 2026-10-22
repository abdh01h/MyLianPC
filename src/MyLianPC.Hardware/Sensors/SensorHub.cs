using LibreHardwareMonitor.Hardware;
using MyLianPC.Core.Devices;
using MyLianPC.Core.Sensors;
using MyLianPC.Hardware.Smbus;

namespace MyLianPC.Hardware.Sensors;

/// <summary>
/// Hardware monitoring through LibreHardwareMonitorLib (PawnIO driver for CPU/Super I/O/SMBus).
/// Also exposes motherboard fan headers; they are only ever driven when the user opted in, and are
/// handed back to BIOS control on <see cref="ReleaseAllFans"/>.
/// </summary>
public sealed class SensorHub : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsStorageEnabled = true,
        // Off on purpose: LHM's AIO/HID controller support would compete with our own cooler driver.
        IsControllerEnabled = false,
        IsNetworkEnabled = false,
        IsPsuEnabled = false,
    };
    private readonly object _gate = new();
    private readonly HashSet<string> _drivenFans = [];
    private bool _opened;
    private SmbiosMemoryInfo _memory = SmbiosMemoryInfo.Empty;
    private DateTime _nextStorageUpdateUtc = DateTime.MinValue;

    public string? Error { get; private set; }
    public string? BoardName { get; private set; }
    public string? SuperIoName { get; private set; }

    public void Open()
    {
        lock (_gate)
        {
            if (_opened) return;
            lock (SmbusGate.Lock) _computer.Open();
            _opened = true;
            _memory = SmbiosMemoryInfo.Read();
            var mb = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
            BoardName = mb?.Name;
            SuperIoName = mb?.SubHardware.FirstOrDefault(s => s.HardwareType == HardwareType.SuperIO)?.Name;
        }
    }

    /// <summary>Samples every sensor once. Extra readings (e.g. from the cooler) are merged in by the caller.</summary>
    public SensorSnapshot Sample()
    {
        lock (_gate)
        {
            if (!_opened) Open();
            var all = new List<SensorReading>();
            var values = new Dictionary<string, double?>();
            try
            {
                lock (SmbusGate.Lock)
                    foreach (var hw in _computer.Hardware) UpdateTree(hw);
                Error = null;
            }
            catch (Exception e) { Error = e.Message; }

            IHardware? Hw(params HardwareType[] types) => _computer.Hardware.FirstOrDefault(h => types.Contains(h.HardwareType));
            static double? S(IHardware? hw, SensorType type, params string[] names)
            {
                if (hw is null) return null;
                foreach (var n in names)
                {
                    var v = hw.Sensors.Concat(hw.SubHardware.SelectMany(x => x.Sensors))
                        .FirstOrDefault(s => s.SensorType == type && s.Name.Equals(n, StringComparison.OrdinalIgnoreCase))?.Value;
                    if (v is { } f && !float.IsNaN(f)) return f;
                }
                return null;
            }

            var cpu = Hw(HardwareType.Cpu);
            var gpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
                      ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd && !h.Name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase))
                      ?? Hw(HardwareType.GpuAmd, HardwareType.GpuIntel);
            var mem = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory && h.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                      ?? Hw(HardwareType.Memory);

            values[Metrics.CpuTemp] = Positive(S(cpu, SensorType.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Core Max"));
            values[Metrics.CpuLoad] = S(cpu, SensorType.Load, "CPU Total");
            values[Metrics.CpuPower] = Positive(S(cpu, SensorType.Power, "Package", "CPU Package"));
            values[Metrics.CpuClock] = Positive(S(cpu, SensorType.Clock, "Cores (Average)")) ?? Average(cpu, SensorType.Clock, "Core #");
            values[Metrics.GpuTemp] = Positive(S(gpu, SensorType.Temperature, "GPU Core", "GPU Hot Spot"));
            values[Metrics.GpuLoad] = S(gpu, SensorType.Load, "GPU Core", "D3D 3D");
            values[Metrics.GpuClock] = Positive(S(gpu, SensorType.Clock, "GPU Core"));
            values[Metrics.GpuPower] = Positive(S(gpu, SensorType.Power, "GPU Package", "GPU Power", "GPU Core"));
            values[Metrics.GpuFan] = S(gpu, SensorType.Fan, "GPU Fan 1", "GPU Fan");
            values[Metrics.RamLoad] = S(mem, SensorType.Load, "Memory");
            var used = S(mem, SensorType.Data, "Memory Used");
            var avail = S(mem, SensorType.Data, "Memory Available");
            values[Metrics.RamUsedGb] = used;
            values[Metrics.RamTotalGb] = used + avail;
            values[Metrics.RamTemp] = MemoryTemperature();
            values[Metrics.RamSpeed] = _memory.ConfiguredMtps > 0 ? _memory.ConfiguredMtps : null;
            values[Metrics.RamXmp] = _memory.Count == 0 ? null : _memory.XmpExpoActive ? 1 : 0;

            foreach (var hw in _computer.Hardware)
                Collect(hw, hw.Name, all);
            var storage = StorageDevices();
            var driveTemps = storage.Select(d => d.TemperatureCelsius).OfType<double>().Where(t => t > 0).ToList();
            values[Metrics.StorageTemp] = driveTemps.Count > 0 ? driveTemps.Max() : null;
            var driveHealth = storage.Select(d => d.HealthPercent).OfType<double>().ToList();
            values[Metrics.StorageHealth] = driveHealth.Count > 0 ? driveHealth.Min() : null;
            var names = new HardwareNames
            {
                Cpu = cpu?.Name,
                Gpu = gpu?.Name,
                Memory = _memory.Summary,
                Storage = storage.Count == 0
                    ? null
                    : $"{storage.Count} drive{(storage.Count == 1 ? "" : "s")} · {Sizes.Capacity(storage.Sum(d => d.TotalBytes ?? 0))}",
            };
            return new SensorSnapshot { Values = values, All = all, Storage = storage, Names = names };
        }
    }

    /// <summary>Every physical drive with its temperature and remaining life (SMART).</summary>
    private IReadOnlyList<StorageDevice> StorageDevices()
    {
        var lhm = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage)
            .Select(h => new { Name = h.Name, Temperature = Hottest(h), Life = RemainingLife(h) })
            .ToList();
        // Several controllers do not pass SMART through to LibreHardwareMonitor; the Windows storage provider still
        // knows those drives, so it is asked for whatever is missing.
        var wmi = lhm.Any(d => d.Temperature is null || d.Life is null) || lhm.Count == 0
            ? StorageWmi.Read()
            : new Dictionary<string, StorageWmi.DriveData>();

        var drives = lhm.Select(d =>
        {
            var other = wmi.FirstOrDefault(kv => SameDrive(kv.Key, d.Name)).Value;
            double? temperature = d.Temperature ?? (other?.TemperatureCelsius is > 0 ? Math.Round(other.TemperatureCelsius.Value) : null);
            double? health = d.Life ?? (other?.WearPercent is { } wear ? Math.Clamp(100 - wear, 0, 100) : null);
            return new StorageDevice
            {
                Name = d.Name,
                TemperatureCelsius = temperature,
                HealthPercent = health,
                HealthState = other?.HealthState,
                TotalBytes = other?.TotalBytes,
                FreeBytes = other?.FreeBytes,
                Volumes = other?.Volumes,
            };
        }).ToList();

        // Nothing found by LibreHardwareMonitor: fall back to the provider's own list.
        if (drives.Count == 0)
            drives = wmi.Select(kv => new StorageDevice
            {
                Name = kv.Key,
                TemperatureCelsius = kv.Value.TemperatureCelsius is > 0 ? Math.Round(kv.Value.TemperatureCelsius.Value) : null,
                HealthPercent = kv.Value.WearPercent is { } wear ? Math.Clamp(100 - wear, 0, 100) : null,
                HealthState = kv.Value.HealthState,
                TotalBytes = kv.Value.TotalBytes,
                FreeBytes = kv.Value.FreeBytes,
                Volumes = kv.Value.Volumes,
            }).ToList();

        return drives.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool SameDrive(string a, string b) =>
        a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Temperature of a drive: the composite NVMe reading when there is one (that is the one the drive throttles on),
    /// otherwise its single temperature sensor. Warning/critical thresholds are limits, not readings, and a drive
    /// that reports nothing reads 0 — both are ignored.
    /// </summary>
    private static double? Hottest(IHardware drive)
    {
        var sensors = drive.Sensors
            .Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 150)
            .Where(s => !s.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                        && !s.Name.Contains("Critical", StringComparison.OrdinalIgnoreCase)
                        && !s.Name.Contains("Limit", StringComparison.OrdinalIgnoreCase)
                        && !s.Name.Contains("Resolution", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var sensor = sensors.FirstOrDefault(s => s.Name.Equals("Composite Temperature", StringComparison.OrdinalIgnoreCase))
                     ?? sensors.FirstOrDefault(s => s.Name.Equals("Temperature", StringComparison.OrdinalIgnoreCase))
                     ?? sensors.OrderByDescending(s => s.Value).FirstOrDefault();
        return sensor?.Value is { } value ? Math.Round(value) : null;
    }

    /// <summary>
    /// Remaining life as a percentage. LibreHardwareMonitor reports it as a sensor named "Life" (a 0-100 level, or a
    /// 0-1 factor on some drives). Zero means the drive did not report any wear data (several drives expose none),
    /// so it is treated as unknown instead of "about to fail".
    /// </summary>
    private static double? RemainingLife(IHardware drive)
    {
        var sensor = drive.Sensors.FirstOrDefault(s => s.Name.Contains("Life", StringComparison.OrdinalIgnoreCase) && s.Value is not null);
        if (sensor?.Value is not { } value || float.IsNaN(value) || value <= 0) return null;
        double percent = sensor.SensorType == SensorType.Factor ? value * 100 : value;
        return percent is > 0 and <= 100 ? Math.Round(percent) : null;
    }

    /// <summary>Average DIMM temperature reported by the memory modules themselves (DDR5 SPD hub); null when none is exposed.</summary>
    private double? MemoryTemperature()
    {
        var temps = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Memory)
            .SelectMany(h => h.Sensors.Concat(h.SubHardware.SelectMany(s => s.Sensors)))
            .Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 150)
            .Select(s => (double)s.Value!.Value).ToList();
        return temps.Count > 0 ? temps.Average() : null;
    }

    /// <summary>Motherboard fan headers found on the Super I/O chip.</summary>
    public IReadOnlyList<FanChannelInfo> MotherboardFans(IReadOnlyCollection<string> enabled)
    {
        lock (_gate)
        {
            var sio = SuperIo();
            if (sio is null) return [];
            var fans = sio.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
            var controls = sio.Sensors.Where(s => s.SensorType == SensorType.Control).ToList();
            return fans.Select(f =>
            {
                var id = FanId(f);
                var ctl = controls.FirstOrDefault(c => c.Index == f.Index)?.Control;
                return new FanChannelInfo
                {
                    Id = id,
                    Name = f.Name.Replace("Fan #", "Motherboard fan header #"),
                    Controller = $"Motherboard ({sio.Name})",
                    Rpm = f.Value,
                    DutyPercent = controls.FirstOrDefault(c => c.Index == f.Index)?.Value is { } v ? (int)v : null,
                    Controllable = ctl is not null,
                    ControlEnabled = ctl is not null && enabled.Contains(id),
                    Note = enabled.Contains(id) ? null : "Monitoring only. BIOS Smart Fan controls this header.",
                };
            }).ToList();
        }
    }

    /// <summary>Drives a motherboard fan header in software. Only call for headers the user opted into.</summary>
    public bool SetFanDuty(string fanId, int percent)
    {
        lock (_gate)
        {
            var c = ControlFor(fanId);
            if (c is null) return false;
            float duty = Math.Clamp(percent, Math.Max(20, (int)c.MinSoftwareValue), (int)c.MaxSoftwareValue);
            lock (SmbusGate.Lock) c.SetSoftware(duty);
            _drivenFans.Add(fanId);
            return true;
        }
    }

    /// <summary>Returns a header to its BIOS/firmware default control.</summary>
    public void ReleaseFan(string fanId)
    {
        lock (_gate)
        {
            if (ControlFor(fanId) is { } c) lock (SmbusGate.Lock) c.SetDefault();
            _drivenFans.Remove(fanId);
        }
    }

    public void ReleaseAllFans()
    {
        foreach (var id in _drivenFans.ToList()) ReleaseFan(id);
    }

    private IHardware? SuperIo() =>
        _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard)?
            .SubHardware.FirstOrDefault(s => s.HardwareType == HardwareType.SuperIO);

    private IControl? ControlFor(string fanId)
    {
        var sio = SuperIo();
        var fan = sio?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && FanId(s) == fanId);
        return fan is null ? null : sio!.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Control && s.Index == fan.Index)?.Control;
    }

    private static string FanId(ISensor fan) => $"mb:fan{fan.Index + 1}";

    private void UpdateTree(IHardware hw)
    {
        // SMART queries are slow (tens of milliseconds per drive), and drive temperatures move slowly: read the
        // drives every 15 s, everything else on every sample.
        if (hw.HardwareType == HardwareType.Storage)
        {
            if (DateTime.UtcNow < _nextStorageUpdateUtc) return;
            _nextStorageUpdateUtc = DateTime.UtcNow.AddSeconds(15);
        }
        hw.Update();
        foreach (var sub in hw.SubHardware) UpdateTree(sub);
    }

    private static void Collect(IHardware hw, string group, List<SensorReading> all)
    {
        foreach (var s in hw.Sensors.Where(s => s.SensorType is SensorType.Temperature or SensorType.Fan or SensorType.Load or SensorType.Power or SensorType.Clock or SensorType.Control or SensorType.Factor))
        {
            if (s.SensorType == SensorType.Temperature && s.Name.Contains("Limit", StringComparison.OrdinalIgnoreCase)) continue;
            if (s.SensorType == SensorType.Temperature && s.Name.Contains("Resolution", StringComparison.OrdinalIgnoreCase)) continue;
            string unit = s.SensorType switch
            {
                SensorType.Temperature => "°C", SensorType.Fan => "RPM", SensorType.Power => "W",
                SensorType.Clock => "MHz", SensorType.Factor => "%", _ => "%",
            };
            all.Add(new SensorReading($"{hw.Identifier}/{s.SensorType}/{s.Index}", s.Name, unit, group, s.Value is { } v && !float.IsNaN(v) ? v : null));
        }
        foreach (var sub in hw.SubHardware) Collect(sub, $"{group} / {sub.Name}", all);
    }

    private static double? Positive(double? v) => v is > 0 ? v : null;

    private static double? Average(IHardware? hw, SensorType type, string prefix)
    {
        var vals = hw?.Sensors.Where(s => s.SensorType == type && s.Name.StartsWith(prefix) && !s.Name.Contains("Effective") && s.Value > 0).Select(s => (double)s.Value!.Value).ToList();
        return vals is { Count: > 0 } ? vals.Average() : null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_opened) return;
            ReleaseAllFans();
            lock (SmbusGate.Lock) _computer.Close();
            _opened = false;
        }
    }
}
