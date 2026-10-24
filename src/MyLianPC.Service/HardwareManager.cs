using System.Security.Cryptography;
using MyLianPC.Core.Cooling;
using MyLianPC.Core.Devices;
using MyLianPC.Core.Ipc;
using MyLianPC.Core.Lighting;
using MyLianPC.Core.Profiles;
using MyLianPC.Core.Sensors;
using MyLianPC.Core.Settings;
using MyLianPC.Hardware.Adapters;
using MyLianPC.Hardware.Sensors;
using MyLianPC.Hardware.Smbus;
using MyLianPC.Lcd;

namespace MyLianPC.Service;

/// <summary>
/// The single owner of all hardware. Runs the sensor, network, cooling, lighting, LCD and device-watch loops.
/// Everything that changes hardware goes through this class, so no two code paths ever issue
/// contradictory commands.
/// </summary>
public sealed class HardwareManager : BackgroundService
{
    private readonly ILogger<HardwareManager> _log;
    private readonly VendorManager _vendor;
    private readonly object _applyGate = new();
    private readonly Dictionary<string, FanCurveController> _fanControllers = [];
    private readonly Dictionary<string, LcdMedia> _mediaCache = [];
    private SensorSnapshot _lhm = new();
    private DeviceSettings _settings = new();
    private int _lcdVersion;
    private bool _wasHsConnected, _wasFusionConnected, _wasDramConnected;
    private string? _sensorError;

    public ProfileStore Profiles { get; } = new(ServicePaths.Profiles);
    public HydroShiftAdapter Cooler { get; }
    public Fusion2Adapter Motherboard { get; }
    public CorsairDramAdapter Ram { get; }
    public SensorHub Sensors { get; } = new();
    public NetworkMonitor Network { get; } = new();
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public ApplyReport? LastApply { get; private set; }
    public DeviceSettings Settings => _settings;

    public HardwareManager(ILogger<HardwareManager> log, VendorManager vendor)
    {
        _log = log;
        _vendor = vendor;
        Cooler = new HydroShiftAdapter(m => _log.LogInformation("{Message}", m));
        Motherboard = new Fusion2Adapter(m => _log.LogInformation("{Message}", m));
        Ram = new CorsairDramAdapter(m => _log.LogInformation("{Message}", m));
        Cooler.FanLightingRejected = reason =>
        {
            _log.LogWarning("Radiator fan lighting turned off: {Reason}", reason);
            _settings = _settings with { CoolerFanLighting = false };
            _settings.Save(ServicePaths.Devices);
        };
    }

    // ------------------------------------------------------------------ lifecycle

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        ServicePaths.Ensure();
        Profiles.Load();
        _settings = DeviceSettings.Load(ServicePaths.Devices);
        Cooler.FanLighting = _settings.CoolerFanLighting;
        _log.LogInformation("Service starting. Active profile: {Profile}", Profiles.Active?.Name ?? "(none)");
        try { Sensors.Open(); }
        catch (Exception e) { _sensorError = e.Message; _log.LogWarning(e, "Sensor initialisation failed"); }

        await Task.WhenAll(
            Loop("sensors", SensorTick, () => _settings.SensorIntervalMs, stop),
            Loop("network", Network.Tick, () => 1000, stop),
            Loop("cooling", CoolingTick, () => 2000, stop),
            Loop("watch", WatchTick, () => 5000, stop),
            Loop("lighting", Motherboard.RenderAnimations, Motherboard.NextAnimationDelayMs, stop),
            Task.Run(() => LcdLoop(stop), stop));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        _log.LogInformation("Service stopping: handing cooling back to safe fixed values and BIOS control");
        try { Network.Save(force: true); } catch (Exception e) { _log.LogWarning(e, "Saving network totals failed"); }
        try { Cooler.SafeDefaults(); } catch (Exception e) { _log.LogWarning(e, "Cooler safe defaults failed"); }
        try { Sensors.ReleaseAllFans(); } catch (Exception e) { _log.LogWarning(e, "Releasing motherboard fans failed"); }
        Cooler.Dispose();
        Motherboard.Dispose();
        Ram.Dispose();
        Sensors.Dispose();
        foreach (var m in _mediaCache.Values) m.Dispose();
    }

    private async Task Loop(string name, Action tick, Func<int> intervalMs, CancellationToken stop)
    {
        await Task.Yield();
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Run(tick, stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e) { _log.LogError(e, "{Loop} loop error", name); }
            try { await Task.Delay(intervalMs(), stop); } catch (OperationCanceledException) { break; }
        }
    }

    // ------------------------------------------------------------------ sensors

    private void SensorTick()
    {
        try
        {
            _lhm = Sensors.Sample();
            _sensorError = Sensors.Error;
        }
        catch (Exception e) { _sensorError = e.Message; }
    }

    /// <summary>Latest readings: LibreHardwareMonitor plus the cooler's own telemetry.</summary>
    public SensorSnapshot Snapshot
    {
        get
        {
            var lhm = _lhm;
            var t = Cooler.Connected ? Cooler.Telemetry : null;
            var values = new Dictionary<string, double?>(lhm.Values)
            {
                [Metrics.CoolantTemp] = t?.CoolantC,
                [Metrics.PumpRpm] = t?.PumpRpm,
                [Metrics.AioFan1] = t?.FanRpm.ElementAtOrDefault(0),
                [Metrics.AioFan2] = t?.FanRpm.ElementAtOrDefault(1),
                [Metrics.AioFan3] = t?.FanRpm.ElementAtOrDefault(2),
            };
            var all = lhm.All.ToList();
            if (t is not null)
            {
                all.Add(new(Metrics.CoolantTemp, "Coolant", "°C", "HydroShift II", t.CoolantC));
                all.Add(new(Metrics.PumpRpm, "Pump", "RPM", "HydroShift II", t.PumpRpm));
                for (int i = 0; i < t.FanRpm.Length; i++) all.Add(new($"aio.fan{i + 1}", $"Radiator fan {i + 1}", "RPM", "HydroShift II", t.FanRpm[i]));
            }
            var hs = Cooler.Status;
            var names = lhm.Names with
            {
                Cooler = hs.State == DeviceState.Connected
                    ? $"{hs.Manufacturer} {hs.Name}" + (hs.Firmware is { Length: > 0 } fw ? $" · fw {fw}" : "")
                    : null,
            };
            return new SensorSnapshot { TimeUtc = lhm.TimeUtc, Values = values, All = all, Storage = lhm.Storage, Names = names };
        }
    }

    // ------------------------------------------------------------------ device watch

    private void WatchTick()
    {
        bool hs = Cooler.EnsureConnected();
        bool mb = Motherboard.EnsureConnected();
        bool ram = Ram.EnsureConnected();
        var profile = Profiles.Active;
        if (profile?.Lighting is not null)
        {
            // A device that (re)appears gets the active profile's lighting.
            if (mb && !_wasFusionConnected) ApplyLighting(profile, force: false, persist: false, only: Fusion2Adapter.DeviceId);
            if (ram && !_wasDramConnected) ApplyLighting(profile, force: false, persist: false, only: CorsairDramAdapter.DeviceId);
            else if (ram && Ram.DueForReapply)
            {
                // A module came back from a stalled firmware state (or a write failed) and lost its stored lighting.
                Ram.ClearNeedsReapply();
                ApplyLighting(profile, force: true, persist: false, only: CorsairDramAdapter.DeviceId);
            }
            if (hs && !_wasHsConnected) ApplyLighting(profile, force: true, persist: false, only: HydroShiftAdapter.DeviceId);
        }
        (_wasHsConnected, _wasFusionConnected, _wasDramConnected) = (hs, mb, ram);
    }

    public void Rescan()
    {
        Ram.Rescan();
        Motherboard.Reopen(fullReset: true); // a fresh handle plus a controller reset
        _wasHsConnected = _wasFusionConnected = _wasDramConnected = false;
        WatchTick();
    }

    // ------------------------------------------------------------------ profiles & lighting

    public ApplyReport ActivateProfile(Guid id, string? sid = null)
    {
        Profiles.SetActiveFor(sid, id);
        return ApplyActive("profile activated");
    }

    /// <summary>
    /// A Windows account signed in: switch to the profile that account last activated. The profile list is
    /// shared by every account; only the active choice is remembered per account (by SID).
    /// </summary>
    public ApplyReport? ActivateForUser(string? sid, string reason)
    {
        if (Profiles.ActiveFor(sid) is not { } profile) return null;
        if (profile.Id == Profiles.Active?.Id)
        {
            ReassertAfterSessionChange(reason);
            return null;
        }
        _log.LogInformation("Switching to the profile '{Profile}' last used by this account ({Reason})", profile.Name, reason);
        Profiles.SetActiveFor(sid, profile.Id);
        return ApplyActive($"signed-in account profile ({reason})");
    }
    /// <summary>Applies the whole active profile (lighting now; cooling and LCD are picked up by their loops).</summary>
    public ApplyReport ApplyActive(string reason)
    {
        var p = Profiles.Active;
        if (p is null) return LastApply = new ApplyReport { ProfileName = "(none)", Results = [new("service", "Profile", false, "No active profile.")] };
        _log.LogInformation("Applying profile '{Profile}' ({Reason})", p.Name, reason);
        var results = new List<ApplyResult>();
        results.AddRange(ApplyLighting(p, force: true, persist: true).Results);
        lock (_fanControllers) _fanControllers.Clear();
        Interlocked.Increment(ref _lcdVersion);
        results.Add(new(HydroShiftAdapter.DeviceId, "LCD", Cooler.Connected, Cooler.Connected ? "LCD layout scheduled" : Cooler.Status.Error ?? "Cooler not connected"));
        results.Add(new(HydroShiftAdapter.DeviceId, "Cooling", Cooler.Connected,
            !Cooler.Connected ? "Cooler not reachable; curves will be applied when it reconnects"
            : p.Cooling is null ? "Using default (Balanced) curves" : $"{p.Cooling.Curves.Count} curve(s) active"));
        var report = new ApplyReport { ProfileName = p.Name, Results = results };
        foreach (var r in results.Where(r => !r.Success)) _log.LogWarning("Apply {Device}/{Part} failed: {Message}", r.DeviceId, r.Part, r.Message);
        return LastApply = report;
    }

    /// <summary>All lighting zones currently known, with their real capabilities.</summary>
    public IReadOnlyList<ZoneInfo> Zones =>
        [.. (Motherboard.EnsureConnected() ? Motherboard.Zones(_settings.ZoneLedCounts) : []), .. Ram.Zones, Cooler.RingZone];

    public ApplyReport ApplyLighting(HardwareProfile p, bool force, bool persist, string? only = null)
    {
        var results = new List<ApplyResult>();
        if (p.Lighting is null) return new ApplyReport { ProfileName = p.Name };
        lock (_applyGate)
        {
            // The controller may have been reset or repainted since the last apply (power cycle, Fast Startup, other software),
            // and its HID handle can stop accepting effect writes while still answering queries. A full profile apply re-opens it.
            if (force && only is null or Fusion2Adapter.DeviceId) Motherboard.Reopen();
            if (only is null or Fusion2Adapter.DeviceId) Motherboard.BeginBatch();
            bool touchedFusion = false;
            // The cooler waits for its animation engine before it can update the radiator-fan LEDs. Do it last so
            // that wait never delays the motherboard headers or the RAM modules.
            foreach (var zone in Zones.Where(z => only is null || z.DeviceId == only)
                         .OrderBy(z => z.DeviceId == HydroShiftAdapter.DeviceId ? 1 : 0))
            {
                if (p.Lighting.For(zone.Id) is not { } requested) continue;
                var (lighting, substitute) = Fit(requested, zone);
                ApplyResult r = zone.DeviceId switch
                {
                    Fusion2Adapter.DeviceId => Motherboard.Apply(zone.Id, lighting, zone.LedCount),
                    CorsairDramAdapter.DeviceId => Ram.Apply(zone.Id, lighting, force),
                    HydroShiftAdapter.DeviceId => Cooler.ApplyRing(lighting),
                    _ => new(zone.DeviceId, zone.Name, false, "Unknown device"),
                };
                touchedFusion |= zone.DeviceId == Fusion2Adapter.DeviceId;
                results.Add(substitute is null
                    ? r with { Part = zone.Name }
                    : r with { Part = zone.Name, Message = $"{requested.Effect} is not available on this zone; {substitute} was used instead. {r.Message}".TrimEnd() });
            }
            if (touchedFusion) results.Add(Motherboard.Commit(persist && _settings.SaveMotherboardLightingToFlash));
        }
        return new ApplyReport { ProfileName = p.Name, Results = results };
    }

    /// <summary>Maps a request onto what a zone supports; returns the substitute effect name when one was needed.</summary>
    private static (ZoneLighting, LightingEffect?) Fit(ZoneLighting z, ZoneInfo zone)
    {
        var colors = z.Colors.Take(Math.Max(1, zone.MaxColors)).ToList();
        var fitted = z with { Colors = colors.Count > 0 ? colors : [RgbColor.White], Brightness = Math.Clamp(z.Brightness, 0, 100), Speed = Math.Clamp(z.Speed, 0, 100) };
        if (zone.Effects.Contains(z.Effect)) return (fitted, null);
        LightingEffect[] fallbacks = z.Effect switch
        {
            LightingEffect.Wave => [LightingEffect.Rainbow, LightingEffect.ColorCycle, LightingEffect.Static],
            LightingEffect.Rainbow => [LightingEffect.ColorCycle, LightingEffect.Static],
            LightingEffect.RainbowWave => [LightingEffect.Rainbow, LightingEffect.ColorCycle, LightingEffect.Static],
            LightingEffect.Gradient => [LightingEffect.Static],
            LightingEffect.Flash => [LightingEffect.Breathing, LightingEffect.Static],
            _ => [LightingEffect.Static],
        };
        var sub = fallbacks.First(f => zone.Effects.Contains(f));
        return (fitted with { Effect = sub }, sub);
    }

    /// <summary>Re-asserts lighting after a Windows session change (another account's software may have repainted it).</summary>
    public void ReassertAfterSessionChange(string reason)
    {
        var p = Profiles.Active;
        if (p?.Lighting is null) return;
        _log.LogInformation("Re-applying lighting after {Reason}", reason);
        Motherboard.Reopen();
        ApplyLighting(p, force: false, persist: false, only: Fusion2Adapter.DeviceId);
        // RAM settings live in module flash. Only rewrite them when something else may have changed them.
        if (_vendor.ConflictingProcesses().Count > 0)
        {
            Ram.Invalidate();
            ApplyLighting(p, force: true, persist: false, only: CorsairDramAdapter.DeviceId);
        }
    }

    public void UpdateDeviceSettings(DeviceSettings s)
    {
        bool fanLightingChanged = s.CoolerFanLighting != _settings.CoolerFanLighting;
        _settings = s.Normalized();
        _settings.Save(ServicePaths.Devices);
        var keep = _settings.ControlledMotherboardFans.ToHashSet();
        foreach (var fan in Sensors.MotherboardFans([]).Where(f => !keep.Contains(f.Id))) Sensors.ReleaseFan(fan.Id);
        if (Profiles.Active is not { } p) return;
        ApplyLighting(p, force: false, persist: false, only: Fusion2Adapter.DeviceId);
        if (fanLightingChanged)
        {
            _log.LogInformation("Radiator fan lighting {State}", _settings.CoolerFanLighting ? "on" : "off");
            Cooler.FanLighting = _settings.CoolerFanLighting;
            ApplyLighting(p, force: true, persist: false, only: HydroShiftAdapter.DeviceId);
        }
    }

    /// <summary>
    /// After sleep or a Fast Startup boot the service is still running, but every device was reset or
    /// power-cycled behind its back: reconnect the cooler, re-take the motherboard controller and re-apply.
    /// </summary>
    public void ReassertAfterResume()
    {
        _log.LogInformation("Resumed from sleep/hibernation: reconnecting devices and re-applying the active profile");
        Cooler.Reconnect();
        Rescan();
    }

    // ------------------------------------------------------------------ cooling

    // Cached so the curve controllers (which hold hysteresis state) are not recreated on every tick.
    public static readonly IReadOnlyDictionary<string, FanCurve> DefaultCurves = new Dictionary<string, FanCurve>
    {
        [HydroShiftAdapter.PumpTarget] = CoolingPresets.Balanced(TemperatureSource.Coolant),
        [HydroShiftAdapter.FansTarget] = CoolingPresets.Balanced(TemperatureSource.Coolant),
    };

    private IReadOnlyDictionary<string, FanCurve> EffectiveCurves()
    {
        var curves = new Dictionary<string, FanCurve>(DefaultCurves);
        if (Profiles.Active?.Cooling is { } c)
            foreach (var (k, v) in c.Curves.Where(kv => kv.Value.IsValid)) curves[k] = v;
        return curves;
    }

    private void CoolingTick()
    {
        var curves = EffectiveCurves();
        var snap = Snapshot;
        double? Temp(TemperatureSource s) => s switch
        {
            TemperatureSource.CpuPackage => snap[Metrics.CpuTemp],
            TemperatureSource.GpuCore => snap[Metrics.GpuTemp],
            _ => snap[Metrics.CoolantTemp],
        };
        int Duty(string target, FanCurve curve)
        {
            lock (_fanControllers)
            {
                if (!_fanControllers.TryGetValue(target, out var ctl) || !ReferenceEquals(ctl.Curve, curve))
                    _fanControllers[target] = ctl = new FanCurveController(curve);
                return ctl.Next(Temp(curve.Source));
            }
        }

        if (Cooler.EnsureConnected())
        {
            Cooler.PollTelemetry();
            snap = Snapshot;
            int pump = Duty(HydroShiftAdapter.PumpTarget, curves[HydroShiftAdapter.PumpTarget]);
            int fans = Duty(HydroShiftAdapter.FansTarget, curves[HydroShiftAdapter.FansTarget]);
            Cooler.SetCooling(PumpRpmForDuty(pump), fans);
        }

        foreach (var fanId in _settings.ControlledMotherboardFans)
        {
            if (curves.TryGetValue(fanId, out var curve)) Sensors.SetFanDuty(fanId, Duty(fanId, curve));
            else Sensors.ReleaseFan(fanId);
        }
    }

    /// <summary>0-100% maps to the pump's 1600-2500 RPM range.</summary>
    public static int PumpRpmForDuty(int duty) => 1600 + Math.Clamp(duty, 0, 100) * 9;

    public void RestoreCoolingDefaults()
    {
        if (Profiles.Active is { } p && p.Cooling is not null) Profiles.Upsert(p with { Cooling = null });
        _settings = _settings with { ControlledMotherboardFans = [] };
        _settings.Save(ServicePaths.Devices);
        Sensors.ReleaseAllFans();
        lock (_fanControllers) _fanControllers.Clear();
        _log.LogInformation("Cooling restored to defaults: motherboard fans on BIOS control, cooler on Balanced curve");
    }

    // ------------------------------------------------------------------ LCD

    public void LcdChanged() => Interlocked.Increment(ref _lcdVersion);

    private async Task LcdLoop(CancellationToken stop)
    {
        int shownVersion = -1, shownGeneration = -1;
        bool shownConnected = false;
        byte[]? lastHash = null;
        DateTime lastPush = DateTime.MinValue;
        int frame = 0;
        while (!stop.IsCancellationRequested)
        {
            int delay = 1000;
            try
            {
                if (!Cooler.EnsureConnected()) { shownConnected = false; delay = 3000; }
                else
                {
                    var s = Profiles.Active?.Lcd ?? new LcdSettings();
                    int version = Volatile.Read(ref _lcdVersion);
                    if (version != shownVersion || !shownConnected || Cooler.Generation != shownGeneration)
                    {
                        Cooler.SetLcdBrightness(s.Mode == LcdMode.Off ? 0 : s.Brightness);
                        Cooler.SetLcdRotation(s.RotationDegrees);
                        shownVersion = version;
                        shownGeneration = Cooler.Generation;
                        shownConnected = true;
                        lastHash = null;
                        frame = 0;
                    }
                    var media = s.Mode is LcdMode.Image or LcdMode.Animation ? LoadMedia(s.MediaFile) : null;
                    var metrics = new LcdMetrics(Snapshot.Values, DateTime.Now);
                    SkiaSharp.SKBitmap? mediaFrame = media?.Frames[frame % media.Frames.Count];
                    using var bmp = LcdRenderer.Render(s, metrics, mediaFrame);
                    var jpeg = LcdRenderer.EncodeJpeg(bmp);
                    var hash = SHA1.HashData(jpeg);
                    if (lastHash is null || !hash.AsSpan().SequenceEqual(lastHash) || DateTime.UtcNow - lastPush > TimeSpan.FromSeconds(30))
                    {
                        if (Cooler.ShowJpeg(jpeg)) { lastHash = hash; lastPush = DateTime.UtcNow; }
                    }
                    if (media is { IsAnimated: true }) { delay = media.DurationsMs[frame % media.Frames.Count]; frame++; }
                    else delay = s.Mode == LcdMode.Off ? 5000 : s.RefreshMilliseconds;
                }
            }
            catch (Exception e) { _log.LogError(e, "LCD loop error"); delay = 3000; }

            // Wake early when the LCD configuration changes.
            int v0 = Volatile.Read(ref _lcdVersion);
            for (int waited = 0; waited < delay && !stop.IsCancellationRequested && Volatile.Read(ref _lcdVersion) == v0; waited += 50)
                await Task.Delay(Math.Min(50, delay - waited), CancellationToken.None);
        }
    }

    private LcdMedia? LoadMedia(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        lock (_mediaCache)
        {
            if (_mediaCache.TryGetValue(file, out var m)) return m;
            var path = Path.Combine(ServicePaths.Media, Path.GetFileName(file));
            if (!File.Exists(path)) return null;
            foreach (var old in _mediaCache.Values) old.Dispose();
            _mediaCache.Clear();
            return _mediaCache[file] = LcdMedia.Load(File.ReadAllBytes(path));
        }
    }

    /// <summary>Validates and stores an uploaded image/GIF; returns the stored file name.</summary>
    public string StoreMedia(string fileName, byte[] data)
    {
        if (data.Length > 25 * 1024 * 1024) throw new ArgumentException("Files up to 25 MB are supported.");
        var ext = Path.GetExtension(fileName);
        if (!LcdMedia.SupportedExtensions.Contains(ext)) throw new ArgumentException($"Unsupported file type '{ext}'. Use PNG, JPEG, BMP, GIF or WebP.");
        using (LcdMedia.Load(data)) { } // throws if the data does not decode
        var name = Convert.ToHexString(SHA256.HashData(data))[..20].ToLowerInvariant() + ext.ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(ServicePaths.Media, name), data);
        return name;
    }

    // ------------------------------------------------------------------ status

    public ServiceStatus Status()
    {
        var mbFans = SafeFans();
        var sensorsDevice = new DeviceStatus
        {
            Id = "sensors",
            Name = Sensors.SuperIoName is { } sio ? $"Hardware sensors ({sio})" : "Hardware sensors",
            Manufacturer = Sensors.BoardName ?? "Motherboard",
            Kind = "Sensors and fan headers",
            State = !PawnIoSmbus.IsDriverInstalled ? DeviceState.NeedsDriver : _sensorError is null ? DeviceState.Connected : DeviceState.Error,
            Fans = mbFans,
            Capabilities = ["CPU/GPU temperature, load, clock, power", "Memory usage", "Motherboard fan RPM", "Motherboard fan control (opt-in)"],
            Dependencies = ["PawnIO driver (CPU temperatures, Super I/O)", "LibreHardwareMonitorLib"],
            Error = _sensorError,
            Troubleshooting = PawnIoSmbus.IsDriverInstalled ? null : "Install PawnIO: winget install namazso.PawnIO, then restart the MyLianPC service.",
            LastContactUtc = _lhm.TimeUtc,
        };
        var warnings = new List<string>();
        if (!PawnIoSmbus.IsDriverInstalled) warnings.Add("PawnIO is not installed: CPU temperatures, motherboard fans and RAM lighting are unavailable.");
        var conflicts = _vendor.ConflictingProcesses();
        if (conflicts.Count > 0) warnings.Add("Other hardware software is running and may override settings: " + string.Join(", ", conflicts));
        return new ServiceStatus
        {
            Version = typeof(HardwareManager).Assembly.GetName().Version?.ToString(3) ?? "",
            StartedUtc = StartedUtc,
            PawnIoInstalled = PawnIoSmbus.IsDriverInstalled,
            Devices = [Cooler.Status, Motherboard.Status(_settings.ZoneLedCounts), Ram.Status, sensorsDevice],
            ActiveProfileId = Profiles.Active?.Id,
            ActiveProfileName = Profiles.Active?.Name,
            LastApply = LastApply,
            Warnings = warnings,
            DeviceSettings = _settings,
        };
    }

    private IReadOnlyList<FanChannelInfo> SafeFans()
    {
        try { return Sensors.MotherboardFans(_settings.ControlledMotherboardFans); }
        catch { return []; }
    }
}
