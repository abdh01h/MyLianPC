namespace MyLianPC.Core.Network;

public readonly record struct TrafficReading(double DownloadBytesPerSecond, double UploadBytesPerSecond)
{
    public static readonly TrafficReading Zero = new(0, 0);
}

/// <summary>Persisted "today" totals so the count survives a service restart.</summary>
public sealed record StoredNetworkTotals
{
    public DateTime DayStart { get; init; } = DateTime.Now.Date;
    public long DownloadedBytes { get; init; }
    public long UploadedBytes { get; init; }
}

/// <summary>
/// Turns cumulative interface counters into per-second rates and adds them to the running totals since
/// midnight (local time). A counter that goes backwards is treated as a reset and contributes no bytes, so a
/// reconnected adapter can never make the day's total jump.
/// </summary>
public sealed class TrafficMeter
{
    private readonly Dictionary<string, LastSample> _last = [];
    private readonly record struct LastSample(long Received, long Sent, DateTime Time);

    public TrafficMeter() : this(DateTime.Now.Date, 0, 0) { }

    public TrafficMeter(DateTime dayStart, long downloadedBytes, long uploadedBytes)
    {
        DayStart = dayStart.Date;
        DownloadedBytes = Math.Max(0, downloadedBytes);
        UploadedBytes = Math.Max(0, uploadedBytes);
    }

    /// <summary>Local midnight the current totals belong to.</summary>
    public DateTime DayStart { get; private set; }

    public long DownloadedBytes { get; private set; }
    public long UploadedBytes { get; private set; }

    public TrafficReading Add(string interfaceId, long receivedBytes, long sentBytes, DateTime now)
    {
        RollDay(now);
        if (!_last.TryGetValue(interfaceId, out var previous))
        {
            // First sample of this adapter: the counters are lifetime totals, so they are not today's traffic.
            _last[interfaceId] = new LastSample(receivedBytes, sentBytes, now);
            return TrafficReading.Zero;
        }

        _last[interfaceId] = new LastSample(receivedBytes, sentBytes, now);
        long down = receivedBytes >= previous.Received ? receivedBytes - previous.Received : 0;
        long up = sentBytes >= previous.Sent ? sentBytes - previous.Sent : 0;
        DownloadedBytes += down;
        UploadedBytes += up;

        double seconds = (now - previous.Time).TotalSeconds;
        // A sample gap (sleep, a busy service) would turn a large delta into a meaningless rate: report zero
        // instead; the bytes are still added to the totals exactly once.
        return seconds is > 0 and < 3600 ? new TrafficReading(down / seconds, up / seconds) : TrafficReading.Zero;
    }

    private void RollDay(DateTime now)
    {
        if (now.Date == DayStart) return;
        DayStart = now.Date;
        DownloadedBytes = 0;
        UploadedBytes = 0;
        _last.Clear(); // the first sample of the new day only establishes the baseline
    }
}
