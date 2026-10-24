namespace MyLianPC.Core.Network;

/// <summary>
/// One sampling of the PC's network connection: the adapter in use, how it is connected (SSID, signal,
/// addresses) and the traffic counters. Produced by the service so the daily totals keep counting while the
/// window is closed.
/// </summary>
public sealed record NetworkSnapshot
{
    /// <summary>False until the service has taken its first sample (or when an older service answers).</summary>
    public bool Available { get; init; }
    public bool Connected { get; init; }
    public string? AdapterName { get; init; }
    public string? AdapterDescription { get; init; }
    public bool IsWireless { get; init; }
    /// <summary>The Wi-Fi network name (SSID), or null on Ethernet.</summary>
    public string? NetworkName { get; init; }
    /// <summary>"Internet", "Local network" or "Limited" — the Windows connection profile's verdict.</summary>
    public string? Connectivity { get; init; }
    public double? SignalPercent { get; init; }
    public int? Channel { get; init; }
    public string? Band { get; init; }
    public long? LinkSpeedBitsPerSecond { get; init; }
    public IReadOnlyList<string> IPv4Addresses { get; init; } = [];
    public IReadOnlyList<string> IPv6Addresses { get; init; } = [];
    public string? Gateway { get; init; }
    public IReadOnlyList<string> DnsServers { get; init; } = [];
    public string? MacAddress { get; init; }
    public double DownloadBytesPerSecond { get; init; }
    public double UploadBytesPerSecond { get; init; }
    public long DownloadBytesToday { get; init; }
    public long UploadBytesToday { get; init; }
}

/// <summary>Formats the network card's values the way Windows and the rest of the dashboard present them.</summary>
public static class NetworkText
{
    /// <summary>Bytes per second as megabytes ("1.2 MB/s", "0 MB/s"), the unit the network card and chart use.</summary>
    public static string Rate(double bytesPerSecond) =>
        !double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0
            ? "0 MB/s"
            : $"{bytesPerSecond / (1L << 20):0.##} MB/s";

    public static string LinkSpeed(long? bitsPerSecond) => bitsPerSecond switch
    {
        null or <= 0 => "—",
        >= 1_000_000_000 => $"{bitsPerSecond / 1_000_000_000.0:0.##} Gbps",
        >= 1_000_000 => $"{bitsPerSecond / 1_000_000.0:0.#} Mbps",
        _ => $"{bitsPerSecond / 1_000.0:0} Kbps",
    };

    /// <summary>
    /// A day's traffic always in GB ("3.42 GB"), because today's download and upload are read as a pair and
    /// mixing units ("820 MB" next to "1.2 GB") makes them harder to compare.
    /// </summary>
    public static string TodayBytes(long bytes) => $"{Math.Max(0, bytes) / (double)(1L << 30):0.##} GB";

    /// <summary>"4446485DC912" → "44:46:48:5D:C9:12".</summary>
    public static string Mac(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length % 2 != 0) return "—";
        return string.Join(':', Enumerable.Range(0, raw.Length / 2).Select(i => raw.Substring(i * 2, 2)));
    }
}
