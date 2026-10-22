using MyLianPC.Core.Devices;
using MyLianPC.Core.Lighting;
using MyLianPC.Hardware.Gigabyte;

namespace MyLianPC.Hardware.Adapters;

/// <summary>GIGABYTE motherboard lighting (ARGB headers incl. the case strip/fans, chipset accent, LED_C).</summary>
public sealed class Fusion2Adapter : IDisposable
{
    public const string DeviceId = "gigabyte-fusion2";
    public const int DefaultHeaderLeds = 32;

    private static readonly LightingEffect[] HeaderEffects =
        [LightingEffect.Off, LightingEffect.Static, LightingEffect.Breathing, LightingEffect.Flash, LightingEffect.ColorCycle, LightingEffect.Rainbow, LightingEffect.Wave, LightingEffect.Gradient];
    private static readonly LightingEffect[] SingleEffects =
        [LightingEffect.Off, LightingEffect.Static, LightingEffect.Breathing, LightingEffect.Flash, LightingEffect.ColorCycle, LightingEffect.Rainbow, LightingEffect.Wave, LightingEffect.Gradient];

    private readonly object _gate = new();
    private readonly Action<string> _log;
    private Fusion2Controller? _ctl;
    private DeviceState _state = DeviceState.Disconnected;
    private string? _error;
    private DateTimeOffset? _lastContact;
    private DateTime _nextAttemptUtc = DateTime.MinValue;
    private bool _directActive;

    /// <summary>Per-LED animations the controller has no hardware effect for (rainbow, wave): frames are streamed.</summary>
    private sealed record ZoneAnimation(EffectFrames.Animation Animation, int Frame, DateTime NextUtc);
    private readonly Dictionary<string, ZoneAnimation> _animations = [];

    public Fusion2Adapter(Action<string> log) => _log = log;

    /// <summary>True while per-LED animation frames are being streamed to one or more headers.</summary>
    public bool Animating { get { lock (_gate) return _animations.Count > 0; } }

    /// <summary>How long the caller may sleep before the next animation frame is due (1 s while nothing animates).</summary>
    public int NextAnimationDelayMs()
    {
        lock (_gate)
        {
            if (_animations.Count == 0) return 1000;
            double ms = _animations.Values.Min(a => a.NextUtc).Subtract(DateTime.UtcNow).TotalMilliseconds;
            return (int)Math.Clamp(ms, 5, 250);
        }
    }

    /// <summary>Pushes every animation frame that is due. Cheap no-op when no zone animates.</summary>
    public void RenderAnimations()
    {
        lock (_gate)
        {
            if (_animations.Count == 0 || _ctl is null || _state != DeviceState.Connected) return;
            try
            {
                var now = DateTime.UtcNow;
                foreach (var (zoneId, state) in _animations.ToList())
                {
                    if (now < state.NextUtc) continue;
                    var zone = ControllerZones.FirstOrDefault(z => z.Id == zoneId);
                    if (zone is not { Addressable: true }) { _animations.Remove(zoneId); continue; }
                    var frames = state.Animation.Frames;
                    _ctl.SetDirect(zone, frames[state.Frame % frames.Count]);
                    _animations[zoneId] = state with { Frame = state.Frame + 1, NextUtc = now.AddMilliseconds(state.Animation.FrameIntervalMs) };
                }
            }
            catch (Exception e) { Fail(DeviceState.Error, e.Message); }
        }
    }

    public IReadOnlyList<Fusion2Zone> ControllerZones => Fusion2Controller.B850AorusEliteWifi7Zones;

    public bool EnsureConnected()
    {
        lock (_gate)
        {
            if (_ctl is not null && _state == DeviceState.Connected) return true;
            if (DateTime.UtcNow < _nextAttemptUtc) return false;
            try
            {
                var dev = Fusion2Controller.Find();
                if (dev is null) return Fail(DeviceState.Disconnected, "RGB Fusion 2 controller (048D:5711) not found.");
                _ctl = Fusion2Controller.Open(dev);
                _ctl.Initialize();
                _state = DeviceState.Connected;
                _error = null;
                _lastContact = DateTimeOffset.UtcNow;
                _log($"Fusion2: connected {_ctl.Description} fw {_ctl.FirmwareVersion}");
                return true;
            }
            catch (Exception e) { return Fail(DeviceState.Error, e.Message); }
        }
    }

    private bool Fail(DeviceState state, string error)
    {
        if (_error != error) _log($"Fusion2: {state}: {error}");
        _animations.Clear();
        _ctl?.Dispose();
        _ctl = null;
        _state = state;
        _error = error;
        _nextAttemptUtc = DateTime.UtcNow.AddSeconds(10);
        return false;
    }

    public IReadOnlyList<ZoneInfo> Zones(IReadOnlyDictionary<string, int> ledCounts) =>
        ControllerZones.Select(z => new ZoneInfo
        {
            Id = z.Id,
            DeviceId = DeviceId,
            Name = z.Name,
            LedCount = z.Addressable ? ledCounts.GetValueOrDefault(z.Id, DefaultHeaderLeds) : 1,
            Effects = z.Addressable ? HeaderEffects : SingleEffects,
            MaxColors = z.Addressable ? 6 : 1,
            LedCountConfigurable = z.Addressable,
            Note = z.Addressable ? "Gradient, rainbow and wave are drawn per LED: set the LED count. They are re-applied at every start and are not stored in flash." : null,
        }).ToList();

    /// <summary>Queues a zone change; call <see cref="Commit"/> once after all zones.</summary>
    public ApplyResult Apply(string zoneId, ZoneLighting z, int ledCount)
    {
        var zone = ControllerZones.FirstOrDefault(x => x.Id == zoneId);
        if (zone is null) return new(DeviceId, zoneId, false, "Unknown zone.");
        if (!EnsureConnected()) return new(DeviceId, zone.Name, false, _error ?? "Not connected.");
        try
        {
            lock (_gate)
            {
                var color = z.Colors.Count > 0 ? z.Colors[0] : RgbColor.White;
                switch (z.Effect)
                {
                    case LightingEffect.Off: _ctl!.SetEffect(zone, Fusion2Effect.Static, RgbColor.Black, 0, 0); break;
                    case LightingEffect.Static: _ctl!.SetEffect(zone, Fusion2Effect.Static, color, z.Brightness, z.Speed); break;
                    case LightingEffect.Breathing: _ctl!.SetEffect(zone, Fusion2Effect.Pulse, color, z.Brightness, z.Speed); break;
                    case LightingEffect.Flash: _ctl!.SetEffect(zone, Fusion2Effect.Blinking, color, z.Brightness, z.Speed); break;
                    case LightingEffect.ColorCycle: _ctl!.SetEffect(zone, Fusion2Effect.ColorCycle, color, z.Brightness, z.Speed); break;
                    case LightingEffect.Rainbow when zone.Addressable:
                    case LightingEffect.Wave when zone.Addressable:
                        // The controller has no per-LED rainbow or wave (its colour-cycle engine just steps one colour
                        // at a time), so these are streamed as per-LED frames in direct mode instead.
                        var anim = EffectFrames.Generate(z, Math.Clamp(ledCount, 1, 256));
                        int interval = Math.Clamp(anim.FrameIntervalMs, 40, 250); // one HID report per 19 LEDs per frame
                        var animation = new ZoneAnimation(new(anim.Frames, interval), 1, DateTime.UtcNow.AddMilliseconds(interval));
                        _ctl!.SetDirect(zone, anim.Frames[0]);
                        _animations[zone.Id] = animation;
                        _directActive = true;
                        _log($"Fusion2: {zone.Name} shows {z.Effect} per LED ({ledCount} LEDs, {anim.Frames.Count} frames every {interval} ms)");
                        break;
                    case LightingEffect.Rainbow:
                    case LightingEffect.Wave:
                        // A single onboard LED has no room for a rainbow or a wave: the colour-cycle engine at least
                        // moves through the selected colours.
                        _ctl!.SetEffect(zone, Fusion2Effect.ColorCycle, color, z.Brightness, z.Speed);
                        break;
                    case LightingEffect.Gradient when zone.Addressable:
                        var frame = EffectFrames.Generate(z, Math.Clamp(ledCount, 1, 256)).Frames[0];
                        _ctl!.SetDirect(zone, frame);
                        _directActive = true;
                        break;
                    case LightingEffect.Gradient:
                        _ctl!.SetEffect(zone, Fusion2Effect.Static, color, z.Brightness, z.Speed);
                        break;
                    default: return new(DeviceId, zone.Name, false, $"{z.Effect} is not supported on {zone.Name}.");
                }
                _lastContact = DateTimeOffset.UtcNow;
            }
            return new(DeviceId, zone.Name, true, $"{z.Effect} applied");
        }
        catch (Exception e)
        {
            lock (_gate) Fail(DeviceState.Error, e.Message);
            return new(DeviceId, zone.Name, false, e.Message);
        }
    }

    /// <summary>Commits queued effects and optionally stores them in the controller's flash (skipped while per-LED mode is in use).</summary>
    public ApplyResult Commit(bool saveToFlash)
    {
        if (!EnsureConnected()) return new(DeviceId, "Commit", false, _error ?? "Not connected.");
        try
        {
            lock (_gate)
            {
                _ctl!.Apply();
                bool saved = saveToFlash && !_directActive;
                if (saved) _ctl.SaveToFlash();
                _directActive = false;
                return new(DeviceId, "Motherboard lighting", true, saved ? "Applied and saved to controller flash" : "Applied");
            }
        }
        catch (Exception e)
        {
            lock (_gate) Fail(DeviceState.Error, e.Message);
            return new(DeviceId, "Commit", false, e.Message);
        }
    }

    /// <summary>Re-asserts our ownership (LampArray off) after another session/app may have taken over.</summary>
    public void Reinitialize()
    {
        lock (_gate)
        {
            if (_ctl is null) return;
            try { _ctl.Initialize(); }
            catch (Exception e) { Fail(DeviceState.Error, e.Message); }
        }
    }

    /// <summary>
    /// Re-opens the HID device and re-takes control. Verified on this board: after a while the controller still
    /// answers queries on the existing handle but silently ignores effect writes, and only a fresh open (not a
    /// reset on the old handle) makes the headers accept colours again. A full reset is only needed on an explicit
    /// rescan; the plain reopen avoids the visible blink of <see cref="Fusion2Controller.Initialize"/>.
    /// </summary>
    public void Reopen(bool fullReset = false)
    {
        lock (_gate)
        {
            try { _ctl?.Dispose(); } catch { /* already gone */ }
            _ctl = null;
            _state = DeviceState.Disconnected;
            _nextAttemptUtc = DateTime.MinValue;
            if (!fullReset && Fusion2Controller.Find() is { } dev)
            {
                try
                {
                    _ctl = Fusion2Controller.Open(dev);
                    _ctl.Reassert();
                    _state = DeviceState.Connected;
                    _error = null;
                    _lastContact = DateTimeOffset.UtcNow;
                    return;
                }
                catch (Exception e) { Fail(DeviceState.Error, e.Message); }
            }
            EnsureConnected();
        }
    }

    /// <summary>Re-arms the headers' effect engine once before a batch of zone updates.</summary>
    public void BeginBatch()
    {
        lock (_gate)
        {
            _animations.Clear(); // the batch below decides again which zones animate
            if (_ctl is null) return;
            try { _ctl.BeginBatch(); }
            catch (Exception e) { Fail(DeviceState.Error, e.Message); }
        }
    }

    /// <summary>Cheap re-take of control before a full apply (no visible reset).</summary>
    public void Reassert()
    {
        lock (_gate)
        {
            if (_ctl is null) return;
            try { _ctl.Reassert(); }
            catch (Exception e) { Fail(DeviceState.Error, e.Message); }
        }
    }

    public DeviceStatus Status(IReadOnlyDictionary<string, int> ledCounts) => new()
    {
        Id = DeviceId,
        Name = _ctl?.Description is { Length: > 0 } d ? $"RGB Fusion 2 ({d})" : "RGB Fusion 2",
        Manufacturer = "GIGABYTE",
        Kind = "Motherboard lighting",
        State = _state,
        Firmware = _ctl?.FirmwareVersion,
        Location = "USB HID 048D:5711",
        Zones = Zones(ledCounts),
        Capabilities = ["3 ARGB headers (case lighting and fans)", "Chipset accent", "LED_C", "Hardware effects stored in flash"],
        Dependencies = ["None (HID, works for standard users)"],
        Warnings = ["GIGABYTE Control Center and Windows Dynamic Lighting can override these settings. Disable them in Settings → Vendor software."],
        Error = _error,
        Troubleshooting = _state == DeviceState.Error ? "Click Rescan. If it keeps failing, close GIGABYTE Control Center." : null,
        LastContactUtc = _lastContact,
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _ctl?.Dispose();
            _ctl = null;
        }
    }
}
