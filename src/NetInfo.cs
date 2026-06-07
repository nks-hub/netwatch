using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace NetWatch
{
    /// <summary>
    /// Opaque store of previous byte counters per adapter, used by
    /// <see cref="NetInfo.Sample"/> to compute delta-based throughput.
    /// Keyed by <see cref="NetworkInterface.Id"/>.
    /// </summary>
    public sealed class ThroughputState
    {
        internal readonly Dictionary<string, Counter> Prev =
            new Dictionary<string, Counter>(StringComparer.OrdinalIgnoreCase);

        public ThroughputState() { }

        internal struct Counter
        {
            public long Recv;
            public long Sent;
            public DateTime Time;
        }
    }

    /// <summary>
    /// Adapter enumeration, throughput, active-route detection, VPN detection
    /// and Wi-Fi signal reading for a non-admin, locale-independent user.
    /// None of the public methods throw.
    /// </summary>
    public static class NetInfo
    {
        // VPN vendor keywords, lowercase. Order matters only for the chosen vendor label.
        // Brand-only names (cisco/pulse/sophos/proton) are spelled out to the VPN product
        // string so a non-VPN NIC carrying the bare brand is not misclassified as a tunnel.
        private static readonly string[] VpnKeywords =
        {
            "wireguard", "wintun", "tap-windows", "tap-", "openvpn",
            "wan miniport (ip)", "wan miniport (ikev2)", "wan miniport (l2tp)",
            "wan miniport (pptp)", "wan miniport (sstp)",
            "anyconnect", "cisco anyconnect", "fortinet", "forticlient", "pulse secure",
            "globalprotect", "palo alto", "zscaler", "sonicwall",
            "check point", "checkpoint", "sophos ssl vpn", "sophos connect",
            "nordlynx", "proton vpn", "protonvpn",
            "mullvad", "tailscale", "zerotier"
        };

        /// <summary>
        /// Enumerates all operationally-up, non-loopback adapters and builds an
        /// <see cref="InterfaceSample"/> for each, including throughput deltas
        /// and Wi-Fi signal where applicable. Never throws.
        /// </summary>
        public static InterfaceSample[] Sample(ThroughputState state)
        {
            var now = DateTime.UtcNow;
            var result = new List<InterfaceSample>();

            NetworkInterface[] nics;
            try
            {
                nics = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch
            {
                return new InterfaceSample[0];
            }

            // Query Wi-Fi data once and match by GUID, so wlanapi is touched a single time.
            Dictionary<Guid, WifiInfo> wifiByGuid = QueryAllWifi();

            foreach (var nic in nics)
            {
                try
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    var s = new InterfaceSample
                    {
                        Time = now,
                        Name = nic.Name,
                        Description = nic.Description,
                        Up = true,
                        DnsServers = new string[0]
                    };

                    try { s.LinkSpeedBps = nic.Speed; } catch { s.LinkSpeedBps = 0; }

                    s.Kind = MapKind(nic.NetworkInterfaceType);

                    // Capture physical wireless-ness before any VPN override so a wireless
                    // adapter reclassified to Vpn still reports SSID/signal/rates.
                    bool physWifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;

                    FillIpProperties(nic, s);
                    FillStats(nic, s);
                    ComputeThroughput(nic, s, state, now);

                    // VPN vendor match may override Kind to Vpn.
                    string vendor = MatchVendor(nic.Description, nic.Name);
                    if (vendor != null && s.Kind != LinkKind.Vpn)
                        s.Kind = LinkKind.Vpn;

                    if (physWifi)
                    {
                        s.IsWifi = true;
                        FillWifi(nic, s, wifiByGuid);
                    }

                    result.Add(s);
                }
                catch
                {
                    // Skip adapters that fail individually; keep enumerating the rest.
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// Returns the adapter carrying the default route (prefer a non-VPN,
        /// up adapter with a gateway and IPv4 address, most traffic / highest
        /// link speed, Ethernet over Wi-Fi). Sets <see cref="InterfaceSample.IsActive"/>
        /// on the chosen sample and clears it on the rest of the supplied array.
        /// Returns null when none qualifies.
        /// </summary>
        public static InterfaceSample Active(InterfaceSample[] all)
        {
            if (all != null)
                foreach (var s in all)
                    if (s != null) s.IsActive = false;

            if (all == null || all.Length == 0) return null;

            InterfaceSample best = null;
            int bestScore = int.MinValue;

            foreach (var s in all)
            {
                if (s == null) continue;
                if (!s.Up) continue;
                if (s.Kind == LinkKind.Loopback) continue;
                if (string.IsNullOrEmpty(s.Gateway)) continue;
                if (string.IsNullOrEmpty(s.IpAddress)) continue;

                int score = ScoreCandidate(s, best);

                if (best == null || score > bestScore)
                {
                    best = s;
                    bestScore = score;
                }
                else if (score == bestScore && PreferOver(s, best))
                {
                    best = s;
                }
            }

            if (best != null) best.IsActive = true;
            return best;
        }

        /// <summary>
        /// Detects VPN adapters. <see cref="VpnStatus.Present"/> is true when any
        /// VPN-like adapter exists (even if down); <see cref="VpnStatus.Connected"/>
        /// is true when an up adapter with an IPv4 address is found.
        /// </summary>
        public static VpnStatus DetectVpn(InterfaceSample[] all)
        {
            var status = new VpnStatus { Present = false, Connected = false };
            if (all == null || all.Length == 0) return status;

            InterfaceSample connected = null;
            InterfaceSample anyPresent = null;

            foreach (var s in all)
            {
                if (s == null) continue;
                if (!IsVpnLike(s)) continue;

                if (anyPresent == null) anyPresent = s;
                status.Present = true;

                bool up = s.Up && !string.IsNullOrEmpty(s.IpAddress);
                if (up && connected == null) connected = s;
            }

            if (!status.Present) return status;

            var pick = connected ?? anyPresent;
            status.Name = pick.Name;
            status.Description = pick.Description;
            status.Vendor = MatchVendor(pick.Description, pick.Name) ?? "VPN";
            status.IpAddress = pick.IpAddress;
            status.Gateway = pick.Gateway;
            status.Connected = pick.Up && !string.IsNullOrEmpty(pick.IpAddress);

            // GatewayAddresses is populated for SSL-VPN / WAN Miniport adapters but empty for
            // WireGuard-style /32 peers. Best-effort fallback: read the tunnel next hop from the
            // IPv4 route table bound to this adapter's interface index. Never throws; on any
            // failure Gateway is left as-is (possibly empty).
            if (string.IsNullOrEmpty(status.Gateway))
            {
                string viaRoute = TryResolveTunnelGateway(pick);
                if (!string.IsNullOrEmpty(viaRoute)) status.Gateway = viaRoute;
            }

            return status;
        }

        // ---- route-table tunnel gateway fallback -------------------------------

        /// <summary>
        /// Re-enumerates adapters to find the one matching the picked VPN sample, reads its
        /// IPv4 interface index and looks up the off-link next hop in the route table.
        /// Returns null on any failure or when no off-link next hop exists for the adapter.
        /// </summary>
        private static string TryResolveTunnelGateway(InterfaceSample pick)
        {
            try
            {
                if (pick == null) return null;

                int ifIndex = -1;
                string ownIp = pick.IpAddress;

                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        // Match on description + name; these uniquely identify the VPN adapter
                        // picked by DetectVpn (InterfaceSample carries no GUID/index of its own).
                        if (!string.Equals(nic.Description, pick.Description, StringComparison.Ordinal)) continue;
                        if (!string.Equals(nic.Name, pick.Name, StringComparison.Ordinal)) continue;

                        var props = nic.GetIPProperties();
                        var v4 = props.GetIPv4Properties();
                        if (v4 == null) continue;
                        ifIndex = v4.Index;

                        // Prefer the adapter's own IPv4 from the live enumeration if the sample
                        // didn't carry one, so the "next hop != own IP" guard stays accurate.
                        if (string.IsNullOrEmpty(ownIp))
                        {
                            foreach (var ua in props.UnicastAddresses)
                            {
                                if (ua.Address != null &&
                                    ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                {
                                    ownIp = ua.Address.ToString();
                                    break;
                                }
                            }
                        }
                        break;
                    }
                    catch
                    {
                        // Skip an adapter that fails to expose properties; keep scanning.
                    }
                }

                if (ifIndex < 0) return null;
                return LookupRouteGateway(ifIndex, ownIp);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Reads MIB_IPFORWARDTABLE and returns the dotted IPv4 next hop bound to
        /// <paramref name="ifIndex"/> that is off-link (non-zero, not 0.0.0.0, not the adapter's
        /// own IP). Prefers the default route (dest 0.0.0.0); otherwise the most specific route
        /// (longest prefix mask). Returns null when none qualifies or on any failure. Frees the
        /// native buffer in all paths.
        /// </summary>
        private static string LookupRouteGateway(int ifIndex, string ownIp)
        {
            IntPtr table = IntPtr.Zero;
            try
            {
                uint ownNbo = 0;
                bool haveOwn = TryIpToNetworkOrder(ownIp, out ownNbo);

                int size = 0;
                // First call sizes the buffer (returns ERROR_INSUFFICIENT_BUFFER = 122).
                uint rc = GetIpForwardTable(IntPtr.Zero, ref size, false);
                if (size <= 0) return null;

                table = Marshal.AllocHGlobal(size);
                rc = GetIpForwardTable(table, ref size, false);
                if (rc != 0) return null; // NO_ERROR == 0

                int numEntries = Marshal.ReadInt32(table); // dwNumEntries
                int rowSize = Marshal.SizeOf(typeof(MIB_IPFORWARDROW));
                long rowsBase = table.ToInt64() + 4; // skip dwNumEntries

                uint bestNextHop = 0;
                bool found = false;
                bool foundDefault = false;
                uint bestMaskHost = 0; // host-order mask; larger = more specific

                for (int i = 0; i < numEntries; i++)
                {
                    MIB_IPFORWARDROW row;
                    try
                    {
                        IntPtr rowPtr = new IntPtr(rowsBase + (long)i * rowSize);
                        row = (MIB_IPFORWARDROW)Marshal.PtrToStructure(rowPtr, typeof(MIB_IPFORWARDROW));
                    }
                    catch
                    {
                        continue;
                    }

                    if (row.dwForwardIfIndex != (uint)ifIndex) continue;

                    uint nextHop = row.dwForwardNextHop; // network byte order
                    if (nextHop == 0) continue;                 // on-link / no gateway
                    if (haveOwn && nextHop == ownNbo) continue;  // not the adapter's own IP

                    bool isDefault = row.dwForwardDest == 0; // 0.0.0.0 destination
                    uint maskHost = NetworkOrderToHost(row.dwForwardMask);

                    if (isDefault)
                    {
                        // Default route is the strongest signal for the tunnel gateway.
                        if (!foundDefault)
                        {
                            bestNextHop = nextHop;
                            foundDefault = true;
                            found = true;
                        }
                    }
                    else if (!foundDefault && (!found || maskHost > bestMaskHost))
                    {
                        // Otherwise keep the most specific (longest-prefix) off-link route.
                        bestNextHop = nextHop;
                        bestMaskHost = maskHost;
                        found = true;
                    }
                }

                if (!found) return null;
                return NetworkOrderToDotted(bestNextHop);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (table != IntPtr.Zero)
                {
                    try { Marshal.FreeHGlobal(table); } catch { }
                }
            }
        }

        /// <summary>Parses dotted IPv4 to a network-byte-order DWORD. Returns false on failure.</summary>
        private static bool TryIpToNetworkOrder(string ip, out uint nbo)
        {
            nbo = 0;
            try
            {
                if (string.IsNullOrEmpty(ip)) return false;
                IPAddress addr;
                if (!IPAddress.TryParse(ip, out addr)) return false;
                if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
                byte[] b = addr.GetAddressBytes(); // already network order for IPv4
                nbo = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Converts a network-byte-order DWORD to host order.</summary>
        private static uint NetworkOrderToHost(uint nbo)
        {
            return ((nbo & 0x000000FFu) << 24) |
                   ((nbo & 0x0000FF00u) << 8) |
                   ((nbo & 0x00FF0000u) >> 8) |
                   ((nbo & 0xFF000000u) >> 24);
        }

        /// <summary>Formats a network-byte-order DWORD as dotted IPv4 (InvariantCulture).</summary>
        private static string NetworkOrderToDotted(uint nbo)
        {
            byte b0 = (byte)(nbo & 0xFF);
            byte b1 = (byte)((nbo >> 8) & 0xFF);
            byte b2 = (byte)((nbo >> 16) & 0xFF);
            byte b3 = (byte)((nbo >> 24) & 0xFF);
            return b0.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." +
                   b1.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." +
                   b2.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." +
                   b3.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Returns the active adapter's IPv4 gateway, or null.</summary>
        public static string DefaultGateway(InterfaceSample[] all)
        {
            var active = Active(all);
            return active != null ? active.Gateway : null;
        }

        // ---- candidate scoring -------------------------------------------------

        private static int ScoreCandidate(InterfaceSample s, InterfaceSample _)
        {
            int score = 0;
            // Strongly prefer non-VPN adapters as the default route bearer.
            if (s.Kind != LinkKind.Vpn) score += 1000;
            // Prefer adapters with observed traffic.
            if (s.RecvMbps > 0 || s.SendMbps > 0) score += 100;
            if (s.BytesRecv > 0 || s.BytesSent > 0) score += 10;
            return score;
        }

        private static bool PreferOver(InterfaceSample candidate, InterfaceSample current)
        {
            // Tie-break: higher link speed wins.
            if (candidate.LinkSpeedBps != current.LinkSpeedBps)
                return candidate.LinkSpeedBps > current.LinkSpeedBps;
            // Then Ethernet over Wi-Fi.
            int cr = KindRank(candidate.Kind);
            int rr = KindRank(current.Kind);
            return cr > rr;
        }

        private static int KindRank(LinkKind k)
        {
            switch (k)
            {
                case LinkKind.Ethernet: return 3;
                case LinkKind.WiFi: return 2;
                case LinkKind.Other: return 1;
                default: return 0;
            }
        }

        // ---- helpers -----------------------------------------------------------

        private static LinkKind MapKind(NetworkInterfaceType t)
        {
            switch (t)
            {
                case NetworkInterfaceType.Wireless80211:
                    return LinkKind.WiFi;
                case NetworkInterfaceType.Ethernet:
                case NetworkInterfaceType.GigabitEthernet:
                case NetworkInterfaceType.FastEthernetT:
                case NetworkInterfaceType.FastEthernetFx:
                    return LinkKind.Ethernet;
                case NetworkInterfaceType.Ppp:
                case NetworkInterfaceType.Tunnel:
                    return LinkKind.Vpn;
                case NetworkInterfaceType.Loopback:
                    return LinkKind.Loopback;
                default:
                    return LinkKind.Other;
            }
        }

        private static string MatchVendor(string description, string name)
        {
            string d = (description ?? "").ToLowerInvariant();
            string n = (name ?? "").ToLowerInvariant();
            foreach (var kw in VpnKeywords)
            {
                if (d.IndexOf(kw, StringComparison.Ordinal) >= 0 ||
                    n.IndexOf(kw, StringComparison.Ordinal) >= 0)
                    return kw;
            }
            return null;
        }

        private static bool IsVpnLike(InterfaceSample s)
        {
            if (s.Kind == LinkKind.Vpn) return true;
            return MatchVendor(s.Description, s.Name) != null;
        }

        private static void FillIpProperties(NetworkInterface nic, InterfaceSample s)
        {
            try
            {
                var props = nic.GetIPProperties();

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address != null &&
                        ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        s.IpAddress = ua.Address.ToString();
                        break;
                    }
                }

                foreach (var gw in props.GatewayAddresses)
                {
                    if (gw.Address != null &&
                        gw.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        !IsZero(gw.Address))
                    {
                        s.Gateway = gw.Address.ToString();
                        break;
                    }
                }

                var dns = new List<string>();
                foreach (IPAddress addr in props.DnsAddresses)
                {
                    if (addr != null &&
                        addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        dns.Add(addr.ToString());
                }
                s.DnsServers = dns.ToArray();
            }
            catch
            {
                if (s.DnsServers == null) s.DnsServers = new string[0];
            }
        }

        private static bool IsZero(IPAddress addr)
        {
            var b = addr.GetAddressBytes();
            foreach (var x in b) if (x != 0) return false;
            return true;
        }

        private static void FillStats(NetworkInterface nic, InterfaceSample s)
        {
            try
            {
                var st = nic.GetIPStatistics();
                s.BytesRecv = st.BytesReceived;
                s.BytesSent = st.BytesSent;
                s.InErrors = st.IncomingPacketsWithErrors;
                s.OutErrors = st.OutgoingPacketsWithErrors;
                s.InDiscards = st.IncomingPacketsDiscarded;
                s.OutDiscards = st.OutgoingPacketsDiscarded;
            }
            catch
            {
                // Some adapters/types don't expose statistics; leave at defaults.
            }
        }

        private static void ComputeThroughput(NetworkInterface nic, InterfaceSample s,
            ThroughputState state, DateTime now)
        {
            if (state == null) return;
            try
            {
                string id = nic.Id;
                if (string.IsNullOrEmpty(id)) return;

                if (state.Prev.TryGetValue(id, out var prev))
                {
                    double dt = (now - prev.Time).TotalSeconds;
                    if (dt > 0)
                    {
                        double recvBps = (s.BytesRecv - prev.Recv) * 8.0 / dt;
                        double sendBps = (s.BytesSent - prev.Sent) * 8.0 / dt;
                        s.RecvMbps = recvBps > 0 ? recvBps / 1e6 : 0;
                        s.SendMbps = sendBps > 0 ? sendBps / 1e6 : 0;
                    }
                }

                state.Prev[id] = new ThroughputState.Counter
                {
                    Recv = s.BytesRecv,
                    Sent = s.BytesSent,
                    Time = now
                };
            }
            catch
            {
                // Throughput is best-effort.
            }
        }

        private static void FillWifi(NetworkInterface nic, InterfaceSample s,
            Dictionary<Guid, WifiInfo> wifiByGuid)
        {
            if (wifiByGuid == null) return;
            try
            {
                if (!TryParseNicGuid(nic.Id, out Guid g)) return;
                if (!wifiByGuid.TryGetValue(g, out var w)) return;

                s.Ssid = w.Ssid;
                s.Bssid = w.Bssid;
                s.SignalPct = w.SignalPct;
                s.RssiDbm = w.RssiDbm;
                s.WifiRxMbps = w.RxMbps;
                s.WifiTxMbps = w.TxMbps;
                s.Radio = w.Radio;
                s.Channel = w.Channel;
            }
            catch
            {
                // Leave Wi-Fi fields at defaults on any failure.
            }
        }

        private static bool TryParseNicGuid(string id, out Guid g)
        {
            g = Guid.Empty;
            if (string.IsNullOrEmpty(id)) return false;
            // NetworkInterface.Id is typically "{GUID}".
            try
            {
                return Guid.TryParse(id, out g);
            }
            catch
            {
                return false;
            }
        }

        // =====================================================================
        //  IPv4 route table (iphlpapi.dll) — tunnel next-hop fallback
        // =====================================================================

        // MIB_IPFORWARDROW: one IPv4 forwarding entry. All address/mask fields are
        // DWORDs in network byte order. Layout matches the Win32 header exactly.
        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_IPFORWARDROW
        {
            public uint dwForwardDest;
            public uint dwForwardMask;
            public uint dwForwardPolicy;
            public uint dwForwardNextHop;
            public uint dwForwardIfIndex;
            public uint dwForwardType;
            public uint dwForwardProto;
            public uint dwForwardAge;
            public uint dwForwardNextHopAS;
            public uint dwForwardMetric1;
            public uint dwForwardMetric2;
            public uint dwForwardMetric3;
            public uint dwForwardMetric4;
            public uint dwForwardMetric5;
        }

        // Fills pIpForwardTable with the IPv4 forwarding table (MIB_IPFORWARDTABLE).
        // First call with a null buffer reports the required size via pdwSize.
        [DllImport("iphlpapi.dll", SetLastError = false)]
        private static extern uint GetIpForwardTable(
            IntPtr pIpForwardTable, ref int pdwSize, bool bOrder);

        // =====================================================================
        //  Native Wifi (wlanapi.dll) — locale-independent, works as standard user
        // =====================================================================

        private struct WifiInfo
        {
            public string Ssid;
            public string Bssid;
            public int SignalPct;
            public int RssiDbm;
            public double RxMbps;
            public double TxMbps;
            public string Radio;
            public int Channel;
        }

        private enum WLAN_INTF_OPCODE : int
        {
            wlan_intf_opcode_current_connection = 7,
            wlan_intf_opcode_channel_number = 8
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_INTERFACE_INFO
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strInterfaceDescription;
            public int isState;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DOT11_SSID
        {
            public uint uSSIDLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] ucSSID;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_ASSOCIATION_ATTRIBUTES
        {
            public DOT11_SSID dot11Ssid;
            public int dot11BssType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] dot11Bssid;
            public int dot11PhyType;
            public uint uDot11PhyIndex;
            public uint wlanSignalQuality;
            public uint ulRxRate;
            public uint ulTxRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_SECURITY_ATTRIBUTES
        {
            [MarshalAs(UnmanagedType.I1)] public bool bSecurityEnabled;
            [MarshalAs(UnmanagedType.I1)] public bool bOneXEnabled;
            public int dot11AuthAlgorithm;
            public int dot11CipherAlgorithm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_CONNECTION_ATTRIBUTES
        {
            public int isState;
            public int wlanConnectionMode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strProfileName;
            public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
            public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
        }

        [DllImport("wlanapi.dll")]
        private static extern uint WlanOpenHandle(
            uint dwClientVersion, IntPtr pReserved,
            out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanEnumInterfaces(
            IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanQueryInterface(
            IntPtr hClientHandle, ref Guid pInterfaceGuid, WLAN_INTF_OPCODE OpCode,
            IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData, IntPtr pWlanOpcodeValueType);

        [DllImport("wlanapi.dll")]
        private static extern void WlanFreeMemory(IntPtr pMemory);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

        /// <summary>
        /// Opens a single WLAN handle, enumerates wireless interfaces and queries
        /// the current connection for each, returning a map keyed by interface GUID.
        /// Returns an empty (non-null) map on any failure.
        /// </summary>
        private static Dictionary<Guid, WifiInfo> QueryAllWifi()
        {
            var map = new Dictionary<Guid, WifiInfo>();
            IntPtr handle = IntPtr.Zero;
            IntPtr ifaceList = IntPtr.Zero;
            bool opened = false;

            try
            {
                // Client version 2 = Vista+ Native Wifi.
                uint negotiated;
                if (WlanOpenHandle(2, IntPtr.Zero, out negotiated, out handle) != 0)
                    return map;
                opened = true;

                if (WlanEnumInterfaces(handle, IntPtr.Zero, out ifaceList) != 0)
                    return map;
                if (ifaceList == IntPtr.Zero)
                    return map;

                // Header: int dwNumberOfItems; int dwIndex; then the array.
                int numItems = Marshal.ReadInt32(ifaceList, 0);
                int structSize = Marshal.SizeOf(typeof(WLAN_INTERFACE_INFO)); // 532 bytes
                long basePtr = ifaceList.ToInt64() + 8; // skip the two header ints

                for (int i = 0; i < numItems; i++)
                {
                    IntPtr entryPtr = new IntPtr(basePtr + (long)i * structSize);
                    WLAN_INTERFACE_INFO info;
                    try
                    {
                        info = (WLAN_INTERFACE_INFO)Marshal.PtrToStructure(
                            entryPtr, typeof(WLAN_INTERFACE_INFO));
                    }
                    catch
                    {
                        continue;
                    }

                    WifiInfo wi;
                    if (TryQueryConnection(handle, info.InterfaceGuid, out wi))
                        map[info.InterfaceGuid] = wi;
                }
            }
            catch
            {
                // wlanapi unavailable (e.g. WLAN AutoConfig service stopped) — return what we have.
            }
            finally
            {
                if (ifaceList != IntPtr.Zero)
                {
                    try { WlanFreeMemory(ifaceList); } catch { }
                }
                if (opened && handle != IntPtr.Zero)
                {
                    try { WlanCloseHandle(handle, IntPtr.Zero); } catch { }
                }
            }

            return map;
        }

        private static bool TryQueryConnection(IntPtr handle, Guid iface, out WifiInfo wi)
        {
            wi = new WifiInfo { SignalPct = 0, RssiDbm = -100, Radio = "" };
            IntPtr pData = IntPtr.Zero;
            try
            {
                Guid g = iface;
                uint size;
                uint rc = WlanQueryInterface(handle, ref g,
                    WLAN_INTF_OPCODE.wlan_intf_opcode_current_connection,
                    IntPtr.Zero, out size, out pData, IntPtr.Zero);
                if (rc != 0 || pData == IntPtr.Zero)
                    return false;

                var attr = (WLAN_CONNECTION_ATTRIBUTES)Marshal.PtrToStructure(
                    pData, typeof(WLAN_CONNECTION_ATTRIBUTES));
                var a = attr.wlanAssociationAttributes;

                wi.Ssid = DecodeSsid(a.dot11Ssid);
                wi.Bssid = FormatBssid(a.dot11Bssid);

                int quality = (int)a.wlanSignalQuality;
                if (quality < 0) quality = 0;
                if (quality > 100) quality = 100;
                wi.SignalPct = quality;
                wi.RssiDbm = quality <= 0 ? -100 : quality >= 100 ? -50
                    : (int)Math.Round(-100.0 + quality / 2.0, MidpointRounding.AwayFromZero);

                wi.RxMbps = a.ulRxRate / 1000.0;
                wi.TxMbps = a.ulTxRate / 1000.0;
                wi.Radio = PhyTypeName(a.dot11PhyType);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (pData != IntPtr.Zero)
                {
                    try { WlanFreeMemory(pData); } catch { }
                }
            }

            // Channel is best-effort and must not fail the whole query.
            wi.Channel = TryQueryChannel(handle, iface);
            return true;
        }

        private static int TryQueryChannel(IntPtr handle, Guid iface)
        {
            IntPtr pData = IntPtr.Zero;
            try
            {
                Guid g = iface;
                uint size;
                uint rc = WlanQueryInterface(handle, ref g,
                    WLAN_INTF_OPCODE.wlan_intf_opcode_channel_number,
                    IntPtr.Zero, out size, out pData, IntPtr.Zero);
                if (rc != 0 || pData == IntPtr.Zero || size < 4)
                    return 0;
                return Marshal.ReadInt32(pData);
            }
            catch
            {
                return 0;
            }
            finally
            {
                if (pData != IntPtr.Zero)
                {
                    try { WlanFreeMemory(pData); } catch { }
                }
            }
        }

        private static string DecodeSsid(DOT11_SSID ssid)
        {
            try
            {
                if (ssid.ucSSID == null) return "";
                int len = (int)ssid.uSSIDLength;
                if (len < 0) len = 0;
                if (len > ssid.ucSSID.Length) len = ssid.ucSSID.Length;
                if (len == 0) return "";
                // SSIDs are octet strings; UTF-8 is the de-facto interpretation.
                return Encoding.UTF8.GetString(ssid.ucSSID, 0, len);
            }
            catch
            {
                return "";
            }
        }

        private static string FormatBssid(byte[] mac)
        {
            try
            {
                if (mac == null || mac.Length < 6) return "";
                var sb = new StringBuilder(17);
                for (int i = 0; i < 6; i++)
                {
                    if (i > 0) sb.Append(':');
                    sb.Append(mac[i].ToString("x2"));
                }
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        // DOT11_PHY_TYPE mapping to friendly 802.11 generation names.
        private static string PhyTypeName(int phy)
        {
            switch (phy)
            {
                case 4: return "802.11a";   // OFDM
                case 5: return "802.11b";   // HRDSSS
                case 6: return "802.11g";   // ERP
                case 7: return "802.11n";   // HT
                case 8: return "802.11ac";  // VHT
                case 9: return "802.11ax";  // HE
                default: return "";
            }
        }
    }
}
