using System;

namespace NetWatch
{
    /// <summary>Physical/logical kind of a network adapter.</summary>
    public enum LinkKind
    {
        Unknown,
        Ethernet,
        WiFi,
        Vpn,
        Loopback,
        Other
    }

    /// <summary>Diagnostic role of a ping target, used for grouping and colouring.</summary>
    public enum TargetKind
    {
        Internet,
        Gateway,
        Vpn,
        Dns,
        Custom
    }

    /// <summary>Result of a single ICMP echo request.</summary>
    public sealed class PingSample
    {
        /// <summary>Target label/host exactly as configured.</summary>
        public string Target { get; set; }
        /// <summary>Resolved IPv4 address actually probed.</summary>
        public string Addr { get; set; }
        /// <summary>UTC time the probe completed.</summary>
        public DateTime Time { get; set; }
        /// <summary>Round-trip time in milliseconds (valid only when <see cref="Success"/>).</summary>
        public double RttMs { get; set; }
        /// <summary>True when an echo reply was received.</summary>
        public bool Success { get; set; }
        /// <summary>Failure reason when <see cref="Success"/> is false.</summary>
        public string Error { get; set; }
        /// <summary>Diagnostic role of this target.</summary>
        public TargetKind Kind { get; set; }
    }

    /// <summary>A single traceroute hop.</summary>
    public sealed class Hop
    {
        /// <summary>TTL value used to elicit this hop (1-based).</summary>
        public int Ttl { get; set; }
        /// <summary>Responding router IP, or empty when the hop timed out.</summary>
        public string Addr { get; set; }
        /// <summary>Reverse-DNS name of the responder, optional.</summary>
        public string Host { get; set; }
        /// <summary>Round-trip time to this hop in milliseconds.</summary>
        public double RttMs { get; set; }
        /// <summary>True when this responder is the final destination.</summary>
        public bool Reached { get; set; }
        /// <summary>True when the hop did not respond within the timeout.</summary>
        public bool Timeout { get; set; }
    }

    /// <summary>One complete traceroute run.</summary>
    public sealed class TraceResult
    {
        public string Target { get; set; }
        public string Addr { get; set; }
        public DateTime Time { get; set; }
        /// <summary>Ordered hops; index 0 corresponds to TTL 1.</summary>
        public Hop[] Hops { get; set; }
        /// <summary>True when the path differs from the previous run.</summary>
        public bool Changed { get; set; }
        public string Error { get; set; }
    }

    /// <summary>One internet speed-test measurement.</summary>
    public sealed class SpeedResult
    {
        public DateTime Time { get; set; }
        /// <summary>Download throughput in megabits/s.</summary>
        public double DownMbps { get; set; }
        /// <summary>Upload throughput in megabits/s.</summary>
        public double UpMbps { get; set; }
        /// <summary>Unloaded latency in milliseconds.</summary>
        public double LatencyMs { get; set; }
        /// <summary>Latency jitter in milliseconds.</summary>
        public double JitterMs { get; set; }
        /// <summary>Server / edge identifier (e.g. Cloudflare colo).</summary>
        public string Server { get; set; }
        public string Error { get; set; }
    }

    /// <summary>A point-in-time snapshot of one network adapter.</summary>
    public sealed class InterfaceSample
    {
        public DateTime Time { get; set; }
        /// <summary>Friendly adapter name (e.g. "Wi-Fi", "Ethernet").</summary>
        public string Name { get; set; }
        /// <summary>Adapter description (driver/vendor string).</summary>
        public string Description { get; set; }
        public LinkKind Kind { get; set; }
        /// <summary>True when the adapter is operationally up.</summary>
        public bool Up { get; set; }
        /// <summary>True when this adapter carries the active default route.</summary>
        public bool IsActive { get; set; }
        /// <summary>Nominal link speed in bits/s.</summary>
        public long LinkSpeedBps { get; set; }
        public string IpAddress { get; set; }
        public string Gateway { get; set; }
        public string[] DnsServers { get; set; }

        /// <summary>Receive throughput in megabits/s (delta-based).</summary>
        public double RecvMbps { get; set; }
        /// <summary>Send throughput in megabits/s (delta-based).</summary>
        public double SendMbps { get; set; }
        public long BytesRecv { get; set; }
        public long BytesSent { get; set; }
        public long InErrors { get; set; }
        public long OutErrors { get; set; }
        public long InDiscards { get; set; }
        public long OutDiscards { get; set; }

        // Wi-Fi specific (default/zero when not a wireless adapter).
        public bool IsWifi { get; set; }
        public string Ssid { get; set; }
        public string Bssid { get; set; }
        /// <summary>Signal quality 0-100 (Native Wifi wlanSignalQuality).</summary>
        public int SignalPct { get; set; }
        /// <summary>Approximate RSSI in dBm derived from signal quality (-100..-50).</summary>
        public int RssiDbm { get; set; }
        public int Channel { get; set; }
        /// <summary>Negotiated receive rate in megabits/s.</summary>
        public double WifiRxMbps { get; set; }
        /// <summary>Negotiated transmit rate in megabits/s.</summary>
        public double WifiTxMbps { get; set; }
        /// <summary>Radio/PHY type if available (e.g. "802.11ac").</summary>
        public string Radio { get; set; }
    }

    /// <summary>Detected VPN adapter state.</summary>
    public sealed class VpnStatus
    {
        /// <summary>True when a VPN-like adapter exists on the machine.</summary>
        public bool Present { get; set; }
        /// <summary>True when the VPN adapter is up and has an IP address.</summary>
        public bool Connected { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        /// <summary>Identified vendor/technology (WireGuard, OpenVPN, TAP, WAN Miniport, ...).</summary>
        public string Vendor { get; set; }
        public string IpAddress { get; set; }
        /// <summary>Tunnel-side gateway, used as a ping target to measure tunnel latency.</summary>
        public string Gateway { get; set; }
    }

    /// <summary>Single source of truth for version and attribution metadata.</summary>
    internal static class BuildInfo
    {
        public const string Version = "1.0.0";
        public const string Repo = "https://github.com/nks-hub/netwatch";
        public const string Author = "LuRy <lury@lury.cz>";
    }

    /// <summary>Display helpers shared across the dashboard, log and reports.</summary>
    internal static class Display
    {
        /// <summary>Human display word for a link kind (Wi-Fi / VPN / Ethernet / ...).</summary>
        public static string KindWord(LinkKind k)
        {
            return k == LinkKind.WiFi ? "Wi-Fi" : k == LinkKind.Vpn ? "VPN" : k.ToString();
        }
    }
}
