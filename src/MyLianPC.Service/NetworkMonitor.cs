using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using MyLianPC.Core.Network;
using MyLianPC.Core.Settings;

namespace MyLianPC.Service;

/// <summary>
/// Samples the active network connection once a second: addresses, gateway, DNS, link speed, the Wi-Fi SSID,
/// current throughput and the bytes moved since midnight. Totals are persisted so they survive a service
/// restart; the counters themselves are cumulative interface statistics, so the stored totals pick up exactly
/// where the previous run stopped instead of double-counting the whole day.
/// </summary>
public sealed class NetworkMonitor
{
    private static readonly string[] VirtualMarkers =
        ["virtual", "vethernet", "vmware", "virtualbox", "hyper-v", "loopback", "pseudo", "wsl", "tailscale", "zerotier"];

    private readonly object _gate = new();
    private readonly string _path;
    private readonly TrafficMeter _meter;
    private NetworkSnapshot _current = new() { Available = true };
    private IReadOnlyList<ConnectionProfile> _profiles = [];
    private DateTime _profilesAt = DateTime.MinValue;
    private DateTime _savedAt = DateTime.MinValue;
    private long _savedDown = -1, _savedUp = -1;
    private DateTime _savedDay = DateTime.MinValue;

    public NetworkMonitor() : this(ServicePaths.Network) { }

    internal NetworkMonitor(string path)
    {
        _path = path;
        _meter = Load(path);
    }

    /// <summary>Latest sample for the dashboard, including the rates and totals up to the last tick.</summary>
    public NetworkSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public void Tick()
    {
        var now = DateTime.Now;
        var profiles = ReadProfiles(now);
        var adapter = Select(profiles);
        if (adapter is null)
        {
            lock (_gate) { _current = new NetworkSnapshot { Available = true, Connected = false }; }
            return;
        }

        var profile = profiles.FirstOrDefault(p => p.Alias.Equals(adapter.Name, StringComparison.OrdinalIgnoreCase));

        long received = 0, sent = 0;
        try
        {
            var stats = adapter.GetIPv4Statistics(); // IPv6 statistics throw on this hardware; IPv4 counters are the link's totals.
            received = stats.BytesReceived;
            sent = stats.BytesSent;
        }
        catch (NetworkInformationException) { }
        catch (PlatformNotSupportedException) { }

        IPInterfaceProperties? properties = null;
        try { properties = adapter.GetIPProperties(); } catch (NetworkInformationException) { }

        bool wireless = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
        WlanInfo.StationInfo? wlan = null;
        if (wireless)
        {
            try { wlan = WlanInfo.Query(adapter.Description); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
        }

        var unicast = properties?.UnicastAddresses.Select(u => u.Address).ToList() ?? [];
        var ipv4 = unicast.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).Distinct().ToList();
        var ipv6All = unicast.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).Select(a => a.ToString()).Distinct().ToList();
        var ipv6 = ipv6All.Where(a => !a.StartsWith("fe80", StringComparison.OrdinalIgnoreCase)).ToList();
        if (ipv6.Count == 0) ipv6 = ipv6All; // link-local is still better than an empty row when that is all there is

        var gateway = properties?.GatewayAddresses
            .Select(g => g.Address)
            .Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any) && !a.Equals(IPAddress.Loopback) && !a.Equals(IPAddress.IPv6Loopback))
            .Select(a => a.ToString())
            .FirstOrDefault();
        var dns = properties?.DnsAddresses.Select(a => a.ToString()).Distinct().ToList() ?? [];
        string mac = adapter.GetPhysicalAddress().ToString();

        TrafficReading reading;
        long todayDown, todayUp;
        lock (_gate)
        {
            reading = _meter.Add(adapter.Id, received, sent, now);
            todayDown = _meter.DownloadedBytes;
            todayUp = _meter.UploadedBytes;
            _current = new NetworkSnapshot
            {
                Available = true,
                Connected = true,
                AdapterName = adapter.Name,
                AdapterDescription = adapter.Description,
                IsWireless = wireless,
                NetworkName = wireless ? wlan?.Ssid ?? profile?.Name : null,
                Connectivity = profile?.Connectivity,
                SignalPercent = wlan?.SignalPercent,
                Channel = wlan?.Channel,
                Band = wlan?.Band,
                LinkSpeedBitsPerSecond = LinkSpeed(adapter, wlan),
                IPv4Addresses = ipv4,
                IPv6Addresses = ipv6,
                Gateway = gateway,
                DnsServers = dns,
                MacAddress = NetworkText.Mac(mac),
                DownloadBytesPerSecond = reading.DownloadBytesPerSecond,
                UploadBytesPerSecond = reading.UploadBytesPerSecond,
                DownloadBytesToday = todayDown,
                UploadBytesToday = todayUp,
            };
        }

        Save();
    }

    /// <summary>Persists the daily totals, at most every 30 seconds. Called from the tick and once when the service stops.</summary>
    public void Save(bool force = false)
    {
        StoredNetworkTotals totals;
        lock (_gate)
        {
            var now = DateTime.Now;
            bool unchanged = _meter.DownloadedBytes == _savedDown && _meter.UploadedBytes == _savedUp && _meter.DayStart == _savedDay;
            if (!force && (unchanged || now - _savedAt < TimeSpan.FromSeconds(30))) return;
            _savedAt = now;
            totals = new StoredNetworkTotals
            {
                DayStart = _meter.DayStart,
                DownloadedBytes = _meter.DownloadedBytes,
                UploadedBytes = _meter.UploadedBytes,
            };
            _savedDown = totals.DownloadedBytes;
            _savedUp = totals.UploadedBytes;
            _savedDay = totals.DayStart;
            _savedAt = DateTime.Now;
        }

        try { AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(totals, Json.Options)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static long? LinkSpeed(NetworkInterface adapter, WlanInfo.StationInfo? wlan) =>
        wlan?.LinkSpeedBitsPerSecond is { } wlanSpeed && wlanSpeed > 0 ? wlanSpeed : adapter.Speed > 0 ? adapter.Speed : null;

    private static TrafficMeter Load(string path)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredNetworkTotals>(File.ReadAllText(path), Json.Options);
            if (stored is not null) return new TrafficMeter(stored.DayStart, stored.DownloadedBytes, stored.UploadedBytes);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return new TrafficMeter();
    }

    // ------------------------------------------------------------------ connection profiles + adapter choice

    private sealed record ConnectionProfile(string Alias, string Name, int IPv4, int IPv6)
    {
        public string? Connectivity =>
            IPv4 == 4 || IPv6 == 4 ? "Internet"
            : IPv4 == 3 ? "Local network"
            : IPv4 == 2 ? "Subnet only"
            : IPv4 == 1 ? "No traffic"
            : IPv4 == 0 && IPv6 == 0 ? "Disconnected"
            : null;
    }

    private IReadOnlyList<ConnectionProfile> ReadProfiles(DateTime now)
    {
        if (now - _profilesAt < TimeSpan.FromSeconds(30)) return _profiles;
        _profilesAt = now;
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2",
                "SELECT Name, InterfaceAlias, IPv4Connectivity, IPv6Connectivity FROM MSFT_NetConnectionProfile");
            using var results = searcher.Get();
            var list = new List<ConnectionProfile>();
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    string alias = item["InterfaceAlias"] as string ?? "";
                    if (alias.Length == 0) continue;
                    list.Add(new ConnectionProfile(alias, item["Name"] as string ?? alias,
                        Connectivity(item["IPv4Connectivity"]), Connectivity(item["IPv6Connectivity"])));
                }
            }
            _profiles = list;
        }
        catch (ManagementException) { _profiles = []; }
        catch (UnauthorizedAccessException) { _profiles = []; }
        catch (COMException) { _profiles = []; }
        catch (InvalidOperationException) { _profiles = []; }
        return _profiles;
    }

    private static int Connectivity(object? value) => value switch
    {
        ushort u => u,
        uint u => (int)u,
        int i => i,
        byte b => b,
        _ => Convert.ToString(value) switch
        {
            "Internet" => 4,
            "LocalNetwork" => 3,
            "Subnet" => 2,
            "NoTraffic" => 1,
            _ => 0,
        },
    };

    /// <summary>
    /// The adapter carrying the default route: prefer one whose connection profile says Internet, then one with
    /// a real gateway, then a physical adapter over a virtual one, then the fastest link.
    /// </summary>
    private static NetworkInterface? Select(IReadOnlyList<ConnectionProfile> profiles)
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .ToList();
        if (candidates.Count == 0) return null;

        return candidates
            .OrderByDescending(n => profiles.Any(p => p.Alias.Equals(n.Name, StringComparison.OrdinalIgnoreCase) && (p.IPv4 == 4 || p.IPv6 == 4)))
            .ThenByDescending(HasGateway)
            .ThenByDescending(n => !VirtualMarkers.Any(m =>
                n.Name.Contains(m, StringComparison.OrdinalIgnoreCase) || n.Description.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(n => n.Speed)
            .First();
    }

    private static bool HasGateway(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().GatewayAddresses.Any(g =>
                !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
        }
        catch (NetworkInformationException) { return false; }
    }
}
