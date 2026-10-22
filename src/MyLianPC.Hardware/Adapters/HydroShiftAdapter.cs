using System.Diagnostics;
using MyLianPC.Core.Devices;
using MyLianPC.Core.Lighting;
using MyLianPC.Hardware.LianLi;
using MyLianPC.Hardware.Usb;

namespace MyLianPC.Hardware.Adapters;

/// <summary>Lian Li HydroShift II LCD: LCD panel, pump, 3 radiator fans and the 24-LED pump ring.</summary>
public sealed class HydroShiftAdapter : IDisposable
{
    public const string DeviceId = "lianli-hydroshift2";
    public const string RingZoneId = "hydroshift:ring";
    public const string PumpTarget = "hydroshift:pump";
    public const string FansTarget = "hydroshift:fans";

    private static readonly LightingEffect[] RingEffects =
        [LightingEffect.Off, LightingEffect.Static, LightingEffect.Breathing, LightingEffect.Flash, LightingEffect.ColorCycle,
         LightingEffect.Rainbow, LightingEffect.Wave, LightingEffect.Gradient];

    private readonly object _gate = new();
    private readonly Action<string> _log;
    private HydroShift2Device? _dev;
    private DeviceState _state = DeviceState.Disconnected;
    private string? _error;
    private DateTime _nextAttemptUtc = DateTime.MinValue;
    private int? _lastPumpRpm, _lastFanPercent;
    private DateTime _lastCoolingWriteUtc = DateTime.MinValue;

    public HydroShift2Telemetry? Telemetry { get; private set; }
    public bool Connected => _state == DeviceState.Connected;

    /// <summary>Increases on every successful (re)connection, so callers know the device state was reset.</summary>
    public int Generation { get; private set; }

    /// <summary>Also drive the LEDs of the radiator fans wired to the pump head (experimental).</summary>
    public bool FanLighting { get; set; }

    /// <summary>Called with a reason when the cooler did not accept fan lighting; the option should be turned off.</summary>
    public Action<string>? FanLightingRejected { get; set; }

    public HydroShiftAdapter(Action<string> log) => _log = log;

    /// <summary>Opens and initialises the cooler if needed. Rate-limited; safe to call from any loop.</summary>
    public bool EnsureConnected()
    {
        lock (_gate)
        {
            if (_dev is { Unresponsive: false } && _state == DeviceState.Connected) return true;
            if (DateTime.UtcNow < _nextAttemptUtc) return false;
            _dev?.Dispose();
            _dev = null;

            var path = HydroShift2Device.Find().FirstOrDefault();
            if (path is null) return Fail(DeviceState.Disconnected, "HydroShift II LCD not found on USB.", TimeSpan.FromSeconds(10));
            try
            {
                _dev = HydroShift2Device.Open(path);
                _dev.Log = m => _log("HydroShift: " + m);
                _dev.Initialize();
                Telemetry = _dev.ReadTelemetry() ?? Telemetry; // the fan count is needed before the first lighting upload
                _state = DeviceState.Connected;
                _error = null;
                _lastPumpRpm = _lastFanPercent = null;
                Generation++;
                _log($"HydroShift: connected, firmware {_dev.FirmwareVersion ?? "?"}, lighting fans {Telemetry?.LightingFanCount?.ToString() ?? "unknown"}");
                return true;
            }
            catch (DeviceBusyException)
            {
                var vendor = Process.GetProcessesByName("L-Connect-Service").Length > 0 ? " L-Connect's service is running and owns it." : "";
                return Fail(DeviceState.Busy, "The cooler is in use by another program." + vendor, TimeSpan.FromSeconds(15));
            }
            catch (HydroShiftUnresponsiveException e)
            {
                return Fail(DeviceState.Unresponsive, e.Message, TimeSpan.FromSeconds(60));
            }
            catch (Exception e)
            {
                return Fail(DeviceState.Error, e.Message, TimeSpan.FromSeconds(15));
            }
        }
    }

    private bool Fail(DeviceState state, string error, TimeSpan retryIn)
    {
        if (_state != state || _error != error) _log($"HydroShift: {state}: {error}");
        _dev?.Dispose();
        _dev = null;
        _state = state;
        _error = error;
        _nextAttemptUtc = DateTime.UtcNow + retryIn;
        return false;
    }

    /// <summary>Runs an operation against the open device; failures mark it for reconnection.</summary>
    private T Run<T>(Func<HydroShift2Device, T> op, T fallback)
    {
        if (!EnsureConnected()) return fallback;
        try
        {
            lock (_gate) return _dev is { } d ? op(d) : fallback;
        }
        catch (HydroShiftUnresponsiveException e) { lock (_gate) Fail(DeviceState.Unresponsive, e.Message, TimeSpan.FromSeconds(60)); }
        catch (Exception e) { lock (_gate) Fail(DeviceState.Error, e.Message, TimeSpan.FromSeconds(10)); }
        return fallback;
    }

    public ZoneInfo RingZone => new()
    {
        Id = RingZoneId, DeviceId = DeviceId, Name = "Pump head ring", LedCount = HydroShift2Protocol.RingLedCount,
        Effects = RingEffects, MaxColors = 6, HasBrightness = true, HasSpeed = true,
        Note = "Animations are uploaded once and looped by the cooler itself.",
    };

    public ApplyResult ApplyRing(ZoneLighting z)
    {
        var anim = EffectFrames.Generate(z, HydroShift2Protocol.RingLedCount);
        var frames = anim.Frames.Select(f => f.SelectMany(c => new[] { c.R, c.G, c.B }).ToArray()).ToList();

        // Always drive the pump ring with a ring-only upload first: the combined ring+fans upload is
        // experimental and on some firmware only drives the radiator fans, so the ring must not depend on it.
        bool ringOk = Run(d => d.SetRing(frames, (byte)Math.Clamp(anim.FrameIntervalMs, 1, 255)), false);
        string ringError = _error ?? "The cooler did not acknowledge the ring upload.";

        int fans = Telemetry?.LightingFanCount is int n and >= 1 and <= 4 ? n : 0;
        if (fans == 0)
            return new(DeviceId, "Pump ring lighting", ringOk, ringOk ? $"{z.Effect} applied" : ringError);

        if (!FanLighting)
        {
            // Fan lighting is off: upload the ring plus black fan frames so the fan LEDs are switched off.
            // The ring is already correct, so a fan-packet failure only leaves the fans as they were.
            var (offFrames, offInterval) = WithFans(anim, fans, lit: false);
            bool fansOff = Run(d => d.SetRing(offFrames, offInterval, retry: false), false);
            return new(DeviceId, "Pump ring lighting", ringOk,
                ringOk ? (fansOff ? $"{z.Effect} applied to the ring; radiator fan LEDs off" : $"{z.Effect} applied") : ringError);
        }

        var (fanFrames, fanInterval) = WithFans(anim, fans, lit: true);
        if (Run(d => d.SetRing(fanFrames, fanInterval, retry: false), false))
            return new(DeviceId, "Pump ring lighting", true, $"{z.Effect} applied to the ring and {fans} radiator fans");

        // Never retry fan lighting: an upload the firmware does not like could leave it needing a power cycle.
        FanLighting = false;
        _log($"HydroShift: fan lighting ({fans} fans) was not accepted; turned it off");
        FanLightingRejected?.Invoke($"The cooler did not accept lighting for {fans} fans.");
        return new(DeviceId, "Pump ring lighting", ringOk, ringOk ? $"{z.Effect} applied" : ringError);
    }

    /// <summary>
    /// Ring frames followed by the same 24 colors for every fan, or by black when the fan LEDs are turned off.
    /// Frames are thinned out (and the interval stretched to keep the speed) so the animation fits the cooler's
    /// decode cache.
    /// </summary>
    private static (List<byte[]> Frames, byte Interval) WithFans(EffectFrames.Animation anim, int fans, bool lit)
    {
        int frameBytes = HydroShift2Protocol.RingLedCount * 3 + fans * HydroShift2Protocol.FanLedCount * 3;
        int maxFrames = Math.Max(1, HydroShift2Protocol.MaxRawLightingBytes / frameBytes);
        int step = (anim.Frames.Count + maxFrames - 1) / maxFrames;
        var black = new byte[HydroShift2Protocol.FanLedCount * 3];
        var frames = anim.Frames.Where((_, i) => i % step == 0).Select(f =>
        {
            var ring = f.SelectMany(c => new[] { c.R, c.G, c.B }).ToArray();
            var fan = lit ? ring : black;
            var frame = new byte[ring.Length + fans * fan.Length];
            ring.CopyTo(frame, 0);
            for (int i = 0; i < fans; i++) fan.CopyTo(frame, ring.Length + i * fan.Length);
            return frame;
        }).ToList();
        return (frames, (byte)Math.Clamp(anim.FrameIntervalMs * step, 1, 255));
    }

    /// <summary>Drops the connection so the next call reopens and re-initialises the cooler (after sleep or Fast Startup).</summary>
    public void Reconnect()
    {
        lock (_gate)
        {
            _dev?.Dispose();
            _dev = null;
            _state = DeviceState.Disconnected;
            _nextAttemptUtc = DateTime.MinValue;
        }
    }

    public bool ShowJpeg(byte[] jpeg) => Run(d => d.ShowJpeg(jpeg), false);

    public bool SetLcdBrightness(int percent) => Run(d => d.SetBrightness(percent), false);

    public bool SetLcdRotation(int degrees) => Run(d => d.SetRotation(degrees / 90), false);

    public HydroShift2Telemetry? PollTelemetry()
    {
        var t = Run(d => d.ReadTelemetry(), null);
        if (t is not null) Telemetry = t;
        return t;
    }

    /// <summary>
    /// Pump RPM (1600-2500) and one duty for all three radiator fans. The cooler only honours a commanded setpoint for
    /// a few seconds and then hands control back to its own curve, so the value is re-sent every
    /// <see cref="SetpointKeepAlive"/> even when it has not changed. Rate limited by the device class.
    /// </summary>
    public bool SetCooling(int pumpRpm, int fanPercent)
    {
        bool changed = _lastPumpRpm != pumpRpm || _lastFanPercent != fanPercent;
        if (!changed && DateTime.UtcNow - _lastCoolingWriteUtc < SetpointKeepAlive) return true;
        bool sent = Run(d => d.SetPumpAndFans(pumpRpm, fanPercent, fanPercent, fanPercent), false);
        if (sent)
        {
            (_lastPumpRpm, _lastFanPercent) = (pumpRpm, fanPercent);
            _lastCoolingWriteUtc = DateTime.UtcNow;
        }
        return sent;
    }

    /// <summary>How often the commanded pump/fan setpoint is repeated; the firmware reverts to its own curve after ~10 s.</summary>
    private static readonly TimeSpan SetpointKeepAlive = TimeSpan.FromSeconds(5);

    /// <summary>Fixed, safe cooling used when the service stops: nothing else will adjust the pump afterwards.</summary>
    public void SafeDefaults()
    {
        _lastPumpRpm = _lastFanPercent = null;
        Run(d => d.SetPumpAndFans(2500, 70, 70, 70), false);
    }

    public DeviceStatus Status
    {
        get
        {
            var t = Telemetry;
            return new DeviceStatus
            {
                Id = DeviceId,
                Name = "HydroShift II LCD-C",
                Manufacturer = "Lian Li",
                Kind = "AIO cooler",
                State = _state,
                Firmware = _dev?.FirmwareVersion,
                Location = "USB 1CBE:A021 (WinUSB)",
                HasLcd = true,
                Zones = [RingZone],
                Fans =
                [
                    new() { Id = PumpTarget, Name = "Pump", Controller = "HydroShift II pump head", Rpm = t?.PumpRpm, Controllable = true, ControlEnabled = true, Note = "1600-2500 RPM" },
                    new() { Id = FansTarget, Name = "Radiator fans (3)", Controller = "HydroShift II pump head", Rpm = t?.FanRpm.Where(r => r > 0).DefaultIfEmpty(0).Average(), Controllable = true, ControlEnabled = true },
                ],
                Capabilities = ["LCD 480×480 (JPEG frames)", "Pump speed", "Radiator fan speed", "Coolant temperature", "Pump ring 24 LEDs"],
                Dependencies = ["WinUSB (built into Windows)", "L-Connect 3 must not be running"],
                Warnings = FanLighting
                    ? [$"Radiator fan lighting is on (experimental): the fans follow the pump ring ({t?.LightingFanCount?.ToString() ?? "?"} fans reported)."]
                    : ["Radiator fan LEDs are switched off. Settings → Service → \"Light the radiator fans with the pump ring\" makes them follow the ring instead."],
                Error = _error,
                Troubleshooting = _state switch
                {
                    DeviceState.Busy => "Close L-Connect 3 (Settings → Vendor software → Take over), then click Rescan.",
                    DeviceState.Unresponsive => "Shut the PC down, switch the PSU off for 15 seconds, then start it again. Keep L-Connect disabled so it does not start streaming video to the panel.",
                    DeviceState.Disconnected => "Check the cooler's internal USB cable to the motherboard USB 2.0 header.",
                    _ => null,
                },
                LastContactUtc = _dev?.LastReplyUtc,
            };
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _dev?.Dispose();
            _dev = null;
        }
    }
}
