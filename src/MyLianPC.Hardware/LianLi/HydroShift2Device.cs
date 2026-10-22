using System.Diagnostics;
using MyLianPC.Hardware.Usb;
using SkiaSharp;
using P = MyLianPC.Hardware.LianLi.HydroShift2Protocol;

namespace MyLianPC.Hardware.LianLi;

/// <summary>
/// Owns one HydroShift II LCD over WinUSB.
///
/// Rules learned on real hardware and from lian-li-linux / hydroshift2-rs:
/// * Every command gets a 512-byte reply. Replies that are never read fill the device's transmit buffer,
///   after which it stops accepting writes. A background reader therefore keeps a read pending at all times
///   and never cancels a transfer mid-flight.
/// * L-Connect leaves the panel playing H.264 with a PNG overlay on top. <see cref="Initialize"/> stops
///   playback and clears both layers, otherwise our frames appear underneath L-Connect's.
/// * Pump/fan writes are rate limited (3.6 s), and nothing is sent once the device stops answering:
///   it is marked unresponsive and only a gentle wake is tried. A USB port reset is never used, because
///   on this cooler it locks the controller until a power cycle.
/// </summary>
public sealed class HydroShift2Device : IDisposable
{
    /// <summary>The WinUSB device interface GUID registered for the cooler (from its MS OS descriptors).</summary>
    public static readonly Guid InterfaceGuid = new("88BAE032-5A81-49F0-BC3D-A4FF138216D6");

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinPumpFanInterval = TimeSpan.FromMilliseconds(3600);
    private const int MaxJpegBytes = 153_600;
    private const int MaxSilentReplies = 3;

    private readonly WinUsbDevice _usb;
    private readonly object _io = new();
    private readonly Stopwatch _sinceLastPumpFan = new();
    private int _silentReplies;

    public string DevicePath => _usb.DevicePath;
    public string? FirmwareVersion { get; private set; }
    public DateTimeOffset? LastReplyUtc { get; private set; }

    /// <summary>True after several commands went unanswered. Commands are refused until <see cref="TryWake"/> succeeds.</summary>
    public bool Unresponsive => _silentReplies >= MaxSilentReplies;

    /// <summary>Optional diagnostics sink (stale-reply counts, silences).</summary>
    public Action<string>? Log { get; set; }

    private HydroShift2Device(WinUsbDevice usb) => _usb = usb;

    public static IReadOnlyList<string> Find() =>
        [.. WinUsbDevice.FindPaths(InterfaceGuid, P.Vid, P.PidCircle), .. WinUsbDevice.FindPaths(InterfaceGuid, P.Vid, P.PidSquare)];

    public static HydroShift2Device Open(string path)
    {
        var usb = WinUsbDevice.Open(path);
        try
        {
            usb.SetPipeTimeout(P.OutEndpoint, TimeSpan.FromSeconds(5));
            usb.StartContinuousRead(P.InEndpoint, 512);
            return new HydroShift2Device(usb);
        }
        catch
        {
            usb.Dispose();
            throw;
        }
    }

    /// <summary>Sends one packet and waits for its reply (null if none arrived within 2 s).</summary>
    public byte[]? Exchange(byte[] packet)
    {
        lock (_io)
        {
            if (Unresponsive) throw new HydroShiftUnresponsiveException();
            return ExchangeCore(packet);
        }
    }

    private byte[]? ExchangeCore(byte[] packet)
    {
        int stale = _usb.DrainReplies();
        if (stale > 0) Log?.Invoke($"discarded {stale} late repl{(stale == 1 ? "y" : "ies")}");
        try
        {
            _usb.Write(P.OutEndpoint, packet);
        }
        catch (TimeoutException)
        {
            // A wedged controller NAKs every OUT transfer; only removing power clears that state.
            _silentReplies = MaxSilentReplies;
            Log?.Invoke($"command 0x{P.DecryptHeader(packet)[0]:X2} was not accepted (write timed out)");
            return null;
        }
        var reply = _usb.TakeReply(ReplyTimeout);
        if (reply is null)
        {
            _silentReplies++;
            Log?.Invoke($"no reply to command 0x{P.DecryptHeader(packet)[0]:X2} ({_silentReplies} in a row)");
        }
        else
        {
            _silentReplies = 0;
            LastReplyUtc = DateTimeOffset.UtcNow;
        }
        return reply;
    }

    /// <summary>
    /// Re-arms the control channel after playback (StopPlay, StopClock, GetVer, 150 ms apart), as the vendor
    /// driver does. Returns true if the device answered.
    /// </summary>
    public bool TryWake()
    {
        lock (_io)
        {
            _silentReplies = 0;
            byte[]? last = null;
            foreach (var packet in new[] { P.Command(P.Cmd.StopPlay), P.Command(P.Cmd.StopClock, [0]), P.Command(P.Cmd.GetVersion) })
            {
                last = ExchangeCore(packet) ?? last;
                if (Unresponsive) break;
                Thread.Sleep(150);
            }
            if (last is not null) FirmwareVersion = P.ParseVersion(last) ?? FirmwareVersion;
            bool alive = LastReplyUtc is { } t && DateTimeOffset.UtcNow - t < TimeSpan.FromSeconds(5);
            if (!alive) _silentReplies = MaxSilentReplies;
            return alive;
        }
    }

    /// <summary>
    /// Wake, sync the clock, stop the built-in clock widget, clear both layers (transparent PNG overlay +
    /// black background) and set 30 fps. Throws if the device does not answer.
    /// </summary>
    public void Initialize()
    {
        lock (_io)
        {
            if (!TryWake()) throw new HydroShiftUnresponsiveException();
            ExchangeCore(P.Clock(DateTime.UtcNow));
            ExchangeCore(P.Command(P.Cmd.StopClock, [0]));
            ExchangeCore(P.Image(TransparentPng.Value, png: true));
            ExchangeCore(P.Image(BlackJpeg.Value, png: false));
            ExchangeCore(P.Command(P.Cmd.FrameRate, [30]));
            if (Unresponsive) throw new HydroShiftUnresponsiveException();
        }
    }

    /// <summary>Shows a 480x480 baseline JPEG as the background layer. It stays on the panel after the app exits.</summary>
    public bool ShowJpeg(byte[] jpeg)
    {
        if (jpeg.Length > MaxJpegBytes) throw new ArgumentException($"JPEG is {jpeg.Length} bytes; the panel accepts at most {MaxJpegBytes}.");
        return Exchange(P.Image(jpeg, png: false)) is not null;
    }

    public bool ShowPngOverlay(byte[] png) => Exchange(P.Image(png, png: true)) is not null;

    /// <summary>Brightness 0-100.</summary>
    public bool SetBrightness(int percent) => Exchange(P.Command(P.Cmd.Brightness, [(byte)Math.Clamp(percent, 0, 100)])) is not null;

    /// <summary>Rotation in 90° steps (0-3).</summary>
    public bool SetRotation(int quarterTurns) => Exchange(P.Command(P.Cmd.Rotation, [(byte)(quarterTurns & 3)])) is not null;

    /// <summary>Coolant temperature, pump and fan RPM, or null if no plausible reading was obtained.</summary>
    public HydroShift2Telemetry? ReadTelemetry()
    {
        lock (_io)
        {
            var t = P.ParseTelemetry(Exchange(P.Command(P.Cmd.GetParams)) ?? []);
            return t is { CoolantC: > 5 and < 80, PumpRpm: < 5000 } ? t : null;
        }
    }

    /// <summary>
    /// Sets pump RPM and the three fan duties (percent). Duties are floored at <paramref name="minFanPercent"/>,
    /// the pump at its 1600 RPM minimum, and writes are rate-limited to the firmware-safe 3.6 s cadence
    /// unless something is being raised to full speed. Returns false if the write was deferred.
    /// </summary>
    public bool SetPumpAndFans(int pumpRpm, int fan1Percent, int fan2Percent, int fan3Percent, int minFanPercent = 20)
    {
        static byte Raw(int pct, int min) => (byte)Math.Round(Math.Clamp(pct, min, 100) * 255 / 100.0);
        bool urgent = pumpRpm >= P.PumpMaxRpmCircle || fan1Percent >= 100 || fan2Percent >= 100 || fan3Percent >= 100;
        lock (_io)
        {
            if (_sinceLastPumpFan.IsRunning && _sinceLastPumpFan.Elapsed < MinPumpFanInterval && !urgent) return false;
            var parameters = P.SyncPumpFanParameters(Math.Max(pumpRpm, P.PumpMinRpm), Raw(fan1Percent, minFanPercent), Raw(fan2Percent, minFanPercent), Raw(fan3Percent, minFanPercent));
            Exchange(P.Command(P.Cmd.SyncPumpFan, parameters));
            _sinceLastPumpFan.Restart();
            return true;
        }
    }

    /// <summary>
    /// Uploads a lighting animation (each frame = 24 ring RGB triplets, optionally plus 24 per fan). The device
    /// loops it on its own. If the firmware does not acknowledge, the channel is re-armed and, when
    /// <paramref name="retry"/> is set, the upload is tried once more.
    /// </summary>
    public bool SetRing(IReadOnlyList<byte[]> frames, byte intervalTicks, bool retry = true)
    {
        var packet = P.RingFrames(frames, intervalTicks);
        lock (_io)
        {
            for (int attempt = 0; attempt < (retry ? 2 : 1); attempt++)
            {
                var reply = Exchange(packet);
                if (reply is { Length: > 0 } && reply[0] == P.Cmd.PushRgb) return true;
                if (!TryWake()) return false;
            }
            return false;
        }
    }

    private static readonly Lazy<byte[]> TransparentPng = new(() => Encode(SKColors.Transparent, SKEncodedImageFormat.Png));
    private static readonly Lazy<byte[]> BlackJpeg = new(() => Encode(SKColors.Black, SKEncodedImageFormat.Jpeg));

    private static byte[] Encode(SKColor color, SKEncodedImageFormat format)
    {
        using var bmp = new SKBitmap(P.Width, P.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bmp.Erase(color);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>Diagnostics: sends one packet, waits up to <paramref name="wait"/> and returns (reply, latency, stale count). No health accounting.</summary>
    public (byte[]? Reply, TimeSpan Latency, int Stale) Probe(byte[] packet, TimeSpan wait)
    {
        lock (_io)
        {
            int stale = _usb.DrainReplies();
            var sw = Stopwatch.StartNew();
            _usb.Write(P.OutEndpoint, packet);
            var reply = _usb.TakeReply(wait);
            return (reply, sw.Elapsed, stale);
        }
    }

    /// <summary>Diagnostics: collects replies that arrive within <paramref name="wait"/> without sending anything.</summary>
    public IReadOnlyList<byte[]> CollectLate(TimeSpan wait)
    {
        var list = new List<byte[]>();
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline && _usb.TakeReply(deadline - DateTime.UtcNow) is { } r) list.Add(r);
        return list;
    }

    public void Dispose() => _usb.Dispose();
}

public sealed class HydroShiftUnresponsiveException()
    : IOException("The HydroShift II controller stopped answering. If it does not recover on its own, shut the PC down and switch the PSU off for 15 seconds.");
