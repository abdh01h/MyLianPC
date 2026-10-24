using System.Runtime.InteropServices;
using System.Text;

namespace MyLianPC.Service;

/// <summary>
/// The Wi-Fi details Windows only exposes through the WLAN API: SSID, signal quality, band and the current
/// PHY rate. All calls are read-only queries. On Windows 11 the API can refuse a caller that lacks the
/// location permission; every failure returns null and the dashboard falls back to the connection profile
/// and the adapter's IP information.
/// </summary>
internal static class WlanInfo
{
    public sealed record StationInfo(string? Ssid, int? SignalPercent, int? Channel, string? Band, long? LinkSpeedBitsPerSecond);

    private const uint ClientVersion = 2; // Windows Vista or later
    private const int OpcodeCurrentConnection = 7;
    private const int InterfaceStateConnected = 1;

    public static StationInfo? Query(string? adapterDescription)
    {
        uint error = WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out IntPtr client);
        if (error != 0) return null;

        try
        {
            if (WlanEnumInterfaces(client, IntPtr.Zero, out IntPtr listPointer) != 0) return null;
            try
            {
                var info = FindConnectedInterface(listPointer, adapterDescription);
                if (info is not { } connected) return null;

                if (WlanQueryInterface(client, ref connected.InterfaceGuid, OpcodeCurrentConnection, IntPtr.Zero,
                        out _, out IntPtr data, out _) != 0 || data == IntPtr.Zero)
                    return null;
                try
                {
                    var attributes = Marshal.PtrToStructure<WlanConnectionAttributes>(data);
                    var assoc = attributes.Association;
                    string? ssid = assoc.Ssid.Length > 0
                        ? Encoding.UTF8.GetString(assoc.Ssid.Bytes, 0, (int)Math.Min(assoc.Ssid.Length, (uint)assoc.Ssid.Bytes.Length))
                        : null;

                    var (band, channel) = BandAndChannel(client, connected.InterfaceGuid, assoc);
                    long? link = assoc.TxRateKbps > 0 || assoc.RxRateKbps > 0
                        ? Math.Max(assoc.TxRateKbps, assoc.RxRateKbps) * 1000L
                        : null;
                    return new StationInfo(ssid, (int)Math.Min(assoc.SignalQuality, 100u), channel, band, link);
                }
                finally { WlanFreeMemory(data); }
            }
            finally { WlanFreeMemory(listPointer); }
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        finally { WlanCloseHandle(client, IntPtr.Zero); }
    }

    private static WlanInterfaceInfo? FindConnectedInterface(IntPtr listPointer, string? description)
    {
        int count = Marshal.ReadInt32(listPointer); // dwNumberOfItems
        int stride = Marshal.SizeOf<WlanInterfaceInfo>();
        WlanInterfaceInfo? fallback = null;
        for (int i = 0; i < count; i++)
        {
            var info = Marshal.PtrToStructure<WlanInterfaceInfo>(listPointer + 8 + i * stride);
            if (info.State != InterfaceStateConnected) continue;
            if (description is not null && string.Equals(info.Description, description, StringComparison.OrdinalIgnoreCase)) return info;
            fallback ??= info;
        }
        return fallback;
    }

    private static (string? Band, int? Channel) BandAndChannel(IntPtr client, Guid interfaceGuid, WlanAssociation association)
    {
        // The BSS list carries the centre frequency, which is the only reliable way to tell 6 GHz channels
        // from the identically numbered 2.4 GHz ones.
        if (WlanGetNetworkBssList(client, ref interfaceGuid, IntPtr.Zero, 1 /* any */, false, IntPtr.Zero, out IntPtr list) != 0 || list == IntPtr.Zero)
            return (null, null);
        try
        {
            int count = Marshal.ReadInt32(list, 4); // dwNumberOfItems (after dwTotalSize)
            int stride = Marshal.SizeOf<WlanBssEntry>();
            WlanBssEntry? first = null, sameSsid = null;
            for (int i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<WlanBssEntry>(list + 8 + i * stride);
                first ??= entry;
                if (entry.Bssid.AsSpan().SequenceEqual(association.Bssid)) return FromFrequency(entry.CenterFrequencyKhz);
                if (sameSsid is null && entry.Ssid.Bytes.AsSpan(0, (int)Math.Min(entry.Ssid.Length, (uint)entry.Ssid.Bytes.Length))
                        .SequenceEqual(association.Ssid.Bytes.AsSpan(0, (int)Math.Min(association.Ssid.Length, (uint)association.Ssid.Bytes.Length))))
                    sameSsid = entry;
            }
            var chosen = sameSsid ?? first;
            return chosen is { } bss ? FromFrequency(bss.CenterFrequencyKhz) : (null, null);
        }
        finally { WlanFreeMemory(list); }
    }

    private static (string?, int?) FromFrequency(uint centerFrequencyKhz)
    {
        double mhz = centerFrequencyKhz / 1000.0;
        if (mhz is >= 5950 and < 7125) return ("6 GHz", (int)Math.Round((mhz - 5950) / 5));
        if (mhz is >= 5150 and < 5925) return ("5 GHz", (int)Math.Round((mhz - 5000) / 5));
        if (mhz is >= 2400 and < 2500) return ("2.4 GHz", (int)(Math.Abs(mhz - 2484) < 1 ? 14 : Math.Round((mhz - 2407) / 5)));
        return (null, null);
    }

    // ------------------------------------------------------------------ native layout

    [StructLayout(LayoutKind.Sequential)]
    private struct Dot11Ssid
    {
        public uint Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanAssociation
    {
        public Dot11Ssid Ssid;
        public int BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;
        public int PhyType;
        public uint PhyIndex;
        public uint SignalQuality;
        public uint RxRateKbps;
        public uint TxRateKbps;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanSecurityAttributes
    {
        [MarshalAs(UnmanagedType.Bool)] public bool SecurityEnabled;
        [MarshalAs(UnmanagedType.Bool)] public bool OneXEnabled;
        public int AuthAlgorithm;
        public int CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionAttributes
    {
        public int State;
        public int ConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
        public WlanAssociation Association;
        public WlanSecurityAttributes Security;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanRateSet
    {
        public uint RateSetLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)] public ushort[] Rates;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanBssEntry
    {
        public Dot11Ssid Ssid;
        public uint PhyId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;
        public int BssType;
        public int PhyType;
        public int Rssi;
        public uint LinkQuality;
        [MarshalAs(UnmanagedType.U1)] public bool InRegDomain;
        public ushort BeaconPeriod;
        public ulong Timestamp;
        public ulong HostTimestamp;
        public ushort CapabilityInformation;
        public uint CenterFrequencyKhz;
        public WlanRateSet RateSet;
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr client);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr client, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr client, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr client, ref Guid interfaceGuid, int opcode, IntPtr reserved,
        out uint size, out IntPtr data, out int opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetNetworkBssList(IntPtr client, ref Guid interfaceGuid, IntPtr ssid, int bssType,
        [MarshalAs(UnmanagedType.Bool)] bool securityEnabled, IntPtr reserved, out IntPtr bssList);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}
