using MyLianPC.Core.Devices;
using MyLianPC.Core.Lighting;
using MyLianPC.Hardware.Corsair;
using MyLianPC.Hardware.Smbus;

namespace MyLianPC.Hardware.Adapters;

/// <summary>
/// Corsair Vengeance RGB DDR5 modules over SMBus (PawnIO). Effects are stored on the modules themselves,
/// so identical settings are not rewritten (saves module flash wear).
/// </summary>
public sealed class CorsairDramAdapter : IDisposable
{
    public const string DeviceId = "corsair-dram";

    private static readonly LightingEffect[] Effects =
        [LightingEffect.Off, LightingEffect.Static, LightingEffect.Gradient, LightingEffect.Breathing, LightingEffect.Flash, LightingEffect.ColorCycle, LightingEffect.Rainbow, LightingEffect.RainbowWave, LightingEffect.Wave];

    private readonly object _gate = new();
    private readonly Action<string> _log;
    private readonly Dictionary<string, ZoneLighting> _applied = [];
    private readonly HashSet<byte> _reported = [];
    private PawnIoSmbus? _bus;
    private IReadOnlyList<CorsairDramController> _modules = [];
    private DeviceState _state = DeviceState.Disconnected;
    private string? _error;
    private DateTimeOffset? _lastContact;
    private DateTime _nextAttemptUtc = DateTime.MinValue;
    private DateTime _nextHealthCheckUtc = DateTime.MinValue;
    private DateTime _nextReapplyUtc = DateTime.MinValue;
    private TimeSpan _reapplyDelay = TimeSpan.FromMinutes(1);

    public CorsairDramAdapter(Action<string> log) => _log = log;

    public static string ZoneId(CorsairDramController m) => $"corsair-dram:{m.Address:X2}";

    /// <summary>Set when a module was recovered from a stalled state and its stored lighting has to be rewritten.</summary>
    public bool NeedsReapply { get; private set; }

    /// <summary>True when the stored lighting has to be rewritten now (a failed rewrite is retried after a minute).</summary>
    public bool DueForReapply => NeedsReapply && DateTime.UtcNow >= _nextReapplyUtc;

    public void ClearNeedsReapply()
    {
        NeedsReapply = false;
        // Back off while a module keeps rejecting its configuration, so a broken one is not rewritten every minute.
        _nextReapplyUtc = DateTime.UtcNow + _reapplyDelay;
        _reapplyDelay = _reapplyDelay < TimeSpan.FromMinutes(10) ? _reapplyDelay * 2 : TimeSpan.FromMinutes(10);
    }

    public bool EnsureConnected()
    {
        lock (_gate)
        {
            if (_bus is not null && _modules.Count > 0)
            {
                if (DateTime.UtcNow >= _nextHealthCheckUtc) CheckHealth();
                return true;
            }
            if (DateTime.UtcNow < _nextAttemptUtc) return false;
            _nextAttemptUtc = DateTime.UtcNow.AddSeconds(30);
            if (!PawnIoSmbus.IsDriverInstalled) return Fail(DeviceState.NeedsDriver, "The PawnIO driver is not installed.");
            try
            {
                _bus ??= PawnIoSmbus.OpenPiix4(0);
                lock (SmbusGate.Lock) _modules = CorsairDramController.Detect(_bus);
                if (_modules.Count == 0) return Fail(DeviceState.Disconnected, "No Corsair DRAM RGB controllers answered on the SMBus.");
                _state = DeviceState.Connected;
                _error = null;
                _lastContact = DateTimeOffset.UtcNow;
                _log($"Corsair DRAM: {_modules.Count} module(s): " + string.Join(", ", _modules.Select(m =>
                    m.DeviceInfoValid ? $"0x{m.Address:X2} fw {m.FirmwareVersion}" : $"0x{m.Address:X2} (no verifiable device info)")));
                foreach (var m in _modules.Where(x => x.Recovered)) OnRecovered(m);
                _nextHealthCheckUtc = DateTime.UtcNow.AddMinutes(2);
                return true;
            }
            catch (Exception e) { return Fail(DeviceState.Error, e.Message); }
        }
    }

    private void OnRecovered(CorsairDramController m, bool needsRewrite = true)
    {
        _log($"Corsair DRAM: module 0x{m.Address:X2} was stuck in an abnormal firmware mode and was switched back to normal; rewriting its lighting.");
        _reported.Remove(m.Address);
        _applied.Clear();
        if (needsRewrite) NeedsReapply = true;
    }

    /// <summary>
    /// A module occasionally drops back into a stalled firmware mode where it still acknowledges writes but
    /// ignores them. Checking the reported protocol version catches that without a service restart.
    /// A block that cannot be read is handled separately: it says nothing about the module's state, so nothing
    /// is written to it, and the check is simply repeated a little later.
    /// </summary>
    private void CheckHealth()
    {
        bool retrySoon = false;
        try
        {
            lock (SmbusGate.Lock)
            {
                foreach (var m in _modules)
                {
                    switch (m.CheckHealth())
                    {
                        case CorsairDeviceHealth.Recovered:
                            OnRecovered(m);
                            break;
                        case CorsairDeviceHealth.Stalled:
                            // Its stored lighting is lost: rewrite it as soon as the module answers normally again.
                            _applied.Clear();
                            NeedsReapply = true;
                            retrySoon = true;
                            if (_reported.Add(m.Address))
                                _log($"Corsair DRAM: module 0x{m.Address:X2} is in an abnormal firmware mode (protocol {m.ProtocolVersion}) and ignores lighting writes; retrying every 30 s.");
                            break;
                        case CorsairDeviceHealth.Unreadable:
                            NeedsReapply = true;
                            retrySoon = true;
                            if (_reported.Add(m.Address))
                                _log($"Corsair DRAM: module 0x{m.Address:X2} did not return a verifiable status read; leaving it alone and retrying every 30 s.");
                            break;
                        default:
                            _reported.Remove(m.Address);
                            break;
                    }
                }
            }
            _lastContact = DateTimeOffset.UtcNow;
        }
        catch (Exception e) { Fail(DeviceState.Error, e.Message); }
        _nextHealthCheckUtc = DateTime.UtcNow.Add(retrySoon ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(2));
    }

    private bool Fail(DeviceState state, string error)
    {
        if (_error != error) _log($"Corsair DRAM: {state}: {error}");
        _state = state;
        _error = error;
        return false;
    }

    public IReadOnlyList<ZoneInfo> Zones => _modules.Select((m, i) => new ZoneInfo
    {
        Id = ZoneId(m),
        DeviceId = DeviceId,
        Name = $"Memory module {i + 1}",
        LedCount = m.LedCount,
        Effects = Effects,
        MaxColors = m.LedCount,
        Note = "Settings are stored on the module and survive reboots without software.",
    }).ToList();

    public ApplyResult Apply(string zoneId, ZoneLighting z, bool force)
    {
        if (!EnsureConnected()) return new(DeviceId, zoneId, false, _error ?? "Not connected.");
        var m = _modules.FirstOrDefault(x => ZoneId(x) == zoneId);
        if (m is null) return new(DeviceId, zoneId, false, "Unknown memory module.");
        string name = $"Memory module at 0x{m.Address:X2}";
        lock (_gate)
        {
            if (!force && _applied.TryGetValue(zoneId, out var prev) && Same(prev, z)) return new(DeviceId, name, true, "Already applied");
            var c1 = z.Colors.Count > 0 ? z.Colors[0] : RgbColor.White;
            var c2 = z.Colors.Count > 1 ? z.Colors[1] : c1;
            byte bright = (byte)(Math.Clamp(z.Brightness, 0, 100) * 255 / 100);
            int speed = z.Speed < 34 ? 0 : z.Speed < 67 ? 1 : 2;
            bool ok;
            try
            {
                lock (SmbusGate.Lock)
                {
                    // A module that fell back into a stalled firmware mode acknowledges writes but ignores them.
                    if (m.DeviceInfoValid && m.ProtocolVersion < 4)
                    {
                        if (!m.EnsureNormalMode())
                        {
                            NeedsReapply = true; // its stored lighting is lost: rewrite it once the module is back
                            return new(DeviceId, name, false, $"Memory module at 0x{m.Address:X2} is in an abnormal firmware mode (protocol {m.ProtocolVersion}) and ignores lighting writes. Click Rescan; if that does not help, shut down and switch the power supply off for 15 seconds.");
                        }
                        OnRecovered(m, needsRewrite: false);
                    }
                    ok = Write(m, z, c1, c2, bright, speed);
                    // Acknowledged but not stored: the module is in the stalled state even though it did not say so.
                    // Recover it right now (that wipes its stored lighting) and write the requested lighting again,
                    // instead of leaving the old colours ("red edges") on the module until the next health check.
                    if (!ok && m.LastWriteStored == false && m.Recover())
                    {
                        OnRecovered(m, needsRewrite: false);
                        ok = Write(m, z, c1, c2, bright, speed);
                        if (ok) _log($"Corsair DRAM: module 0x{m.Address:X2} had fallen into an abnormal firmware mode; it was recovered and the requested lighting was written again.");
                    }
                }
            }
            catch (NotSupportedException e) { return new(DeviceId, name, false, e.Message); }
            catch (Exception e)
            {
                Fail(DeviceState.Error, e.Message);
                NeedsReapply = true; // the lighting may not have been stored: rewrite it when the bus works again
                return new(DeviceId, name, false, e.Message);
            }
            if (ok)
            {
                _applied[zoneId] = z;
                _lastContact = DateTimeOffset.UtcNow;
                _reapplyDelay = TimeSpan.FromMinutes(1); // the module accepts writes: retry quickly the next time
                _reported.Remove(m.Address);
                return new(DeviceId, name, true, $"{z.Effect} stored on the module");
            }

            // Not stored: repair on the next watch tick (30 s) instead of waiting for the health-check backoff.
            NeedsReapply = true;
            _nextReapplyUtc = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            bool keptOldColors = m.LastWriteStored == false;
            if (_reported.Add(m.Address))
                _log($"Corsair DRAM: module 0x{m.Address:X2} " + (keptOldColors
                    ? "acknowledged the colours but kept its previous ones; rewriting it."
                    : "rejected the configuration (checksum mismatch); rewriting it."));
            return new(DeviceId, name, false, keptOldColors
                ? "The module acknowledged the colours but kept its previous ones. Rewriting automatically; click Rescan if it stays wrong."
                : "The module rejected the configuration (checksum mismatch). Another program may be using the SMBus.");
        }
    }

    private static bool Same(ZoneLighting a, ZoneLighting b) =>
        a.Effect == b.Effect && a.Brightness == b.Brightness && a.Speed == b.Speed && a.Colors.SequenceEqual(b.Colors);

    /// <summary>Stores one lighting setting on one module; static-style effects verify the stored colour buffer.</summary>
    private static bool Write(CorsairDramController m, ZoneLighting z, RgbColor c1, RgbColor c2, byte bright, int speed) => z.Effect switch
    {
        LightingEffect.Off => m.SetStaticColors([RgbColor.Black]),
        LightingEffect.Static => m.SetStaticColors([c1.WithBrightness(z.Brightness)]),
        LightingEffect.Gradient => m.SetStaticColors(EffectFrames.Generate(z, m.LedCount).Frames[0]),
        LightingEffect.Breathing or LightingEffect.Flash => m.SetEffect(CorsairDramMode.ColorPulse, speed, 0, false, bright, c1, c2),
        LightingEffect.ColorCycle => m.SetEffect(CorsairDramMode.ColorShift, speed, 0, z.Colors.Count < 2, bright, c1, c2),
        LightingEffect.Rainbow => m.SetEffect(CorsairDramMode.RainbowWave, speed, 1, false, 255, c1, c2),
        LightingEffect.RainbowWave => m.SetEffect(CorsairDramMode.Rainbow, speed, 1, false, 255, c1, c2),
        LightingEffect.Wave => m.SetEffect(CorsairDramMode.ColorWave, speed, 1, false, bright, c1, c2),
        _ => throw new NotSupportedException($"{z.Effect} is not supported on Corsair DRAM."),
    };

    public DeviceStatus Status => new()
    {
        Id = DeviceId,
        Name = "Vengeance RGB DDR5",
        Manufacturer = "Corsair",
        Kind = "Memory lighting",
        State = _state,
        Firmware = _modules.FirstOrDefault()?.FirmwareVersion,
        Location = _bus?.Name,
        Zones = Zones,
        Capabilities = [$"{_modules.Count} module(s) × {CorsairDramController.VengeanceDdr5LedCount} LEDs", "Hardware effects stored on the module"],
        Dependencies = ["PawnIO driver", "Corsair Device Control Service must be disabled (shared SMBus)"],
        Warnings =
        [
            "iCUE, GIGABYTE Control Center (via Corsair Device Control Service) or OpenRGB writing to the RAM at the same time can corrupt transfers.",
            .. _modules.Where(m => m.DeviceInfoValid && m.ProtocolVersion < 4).Select(m =>
                $"Memory module at 0x{m.Address:X2} reports an abnormal firmware state (protocol {m.ProtocolVersion}) and ignores lighting writes. Click Rescan to recover it; if that does not help, shut down and switch the power supply off for 15 seconds."),
        ],
        Error = _error,
        Troubleshooting = _state switch
        {
            DeviceState.NeedsDriver => "Install PawnIO: winget install namazso.PawnIO (or https://pawnio.eu), then restart the service.",
            DeviceState.Error => "Disable Corsair Device Control Service (Settings → Vendor software), then click Rescan.",
            _ => null,
        },
        LastContactUtc = _lastContact,
    };

    /// <summary>Forget what was applied so the next apply rewrites the modules (e.g. after another program changed them).</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _applied.Clear();
            foreach (var m in _modules) m.ForgetState();
        }
    }

    public void Rescan()
    {
        lock (_gate)
        {
            _modules = [];
            _nextAttemptUtc = DateTime.MinValue;
            _nextHealthCheckUtc = DateTime.MinValue;
            _applied.Clear();
            _reported.Clear();
            NeedsReapply = false;
            _nextReapplyUtc = DateTime.MinValue;
            _reapplyDelay = TimeSpan.FromMinutes(1);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _bus?.Dispose();
            _bus = null;
        }
    }
}
