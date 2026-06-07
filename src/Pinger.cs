using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;

namespace NetWatch
{
    /// <summary>
    /// ICMP echo helper built on <see cref="System.Net.NetworkInformation.Ping"/>,
    /// which uses the OS ICMP helper (IcmpSendEcho) and works for standard,
    /// non-administrator users without raw sockets.
    /// </summary>
    public static class Pinger
    {
        private const int PayloadSize = 32;
        private const int DefaultTtl = 128;

        /// <summary>
        /// Resolves a host to its first IPv4 address string. Returns the input
        /// unchanged when it already parses as an IP address. Returns null on
        /// any failure; never throws.
        /// </summary>
        public static string Resolve(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return null;
            host = host.Trim();

            if (IPAddress.TryParse(host, out IPAddress literal))
            {
                if (literal.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    return literal.ToString();
                // Non-IPv4 literal (e.g. IPv6): unsupported for this tool.
                return null;
            }

            try
            {
                IPAddress[] addrs = Dns.GetHostAddresses(host);
                if (addrs != null)
                {
                    foreach (IPAddress a in addrs)
                    {
                        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            return a.ToString();
                    }
                }
            }
            catch
            {
                // Swallow all DNS failures and report as null.
            }
            return null;
        }

        /// <summary>
        /// Sends a single ICMP echo to <paramref name="addr"/> and returns a
        /// populated <see cref="PingSample"/>. RTT is taken from
        /// <see cref="PingReply.RoundtripTime"/> (the round-trip the OS ICMP
        /// layer observed), not wall-clock, to avoid inflating latency. Never throws.
        /// </summary>
        public static PingSample Ping(string target, string addr, TargetKind kind, int timeoutMs)
        {
            var sample = new PingSample
            {
                Target = target,
                Addr = addr,
                Kind = kind,
                Time = DateTime.UtcNow,
                Success = false,
                RttMs = 0,
                Error = null
            };

            if (string.IsNullOrWhiteSpace(addr))
            {
                sample.Error = "NoAddress";
                return sample;
            }

            IPAddress dest;
            if (!IPAddress.TryParse(addr, out dest))
            {
                sample.Error = "BadAddress";
                return sample;
            }

            // A non-positive timeout makes Ping.Send throw; clamp to degrade
            // gracefully rather than reporting a fabricated error.
            if (timeoutMs < 1) timeoutMs = 1;

            try
            {
                byte[] buffer = new byte[PayloadSize];
                var options = new PingOptions(DefaultTtl, true);

                using (var ping = new System.Net.NetworkInformation.Ping())
                {
                    var sw = Stopwatch.StartNew();
                    PingReply reply = ping.Send(dest, timeoutMs, buffer, options);
                    sw.Stop();

                    sample.Time = DateTime.UtcNow;

                    if (reply != null && reply.Status == IPStatus.Success)
                    {
                        sample.Success = true;
                        // RoundtripTime is the OS-observed round-trip in whole ms (long).
                        // It is accurate and overhead-free for >= 1 ms (internet/VPN). For a
                        // sub-millisecond LAN reply it truncates to 0, so fall back to the
                        // measured wall-clock there to show the fractional millisecond
                        // (e.g. a router at 0.3 ms instead of a meaningless 0).
                        double rt = Convert.ToDouble(reply.RoundtripTime, CultureInfo.InvariantCulture);
                        if (rt < 1.0)
                        {
                            double measured = sw.Elapsed.TotalMilliseconds;
                            rt = measured < 1.0 ? measured : 0.99; // keep it clearly sub-ms
                        }
                        sample.RttMs = rt;
                    }
                    else
                    {
                        sample.Success = false;
                        sample.Error = reply != null ? reply.Status.ToString() : "NullReply";
                    }
                }
            }
            catch (Exception ex)
            {
                sample.Success = false;
                sample.Error = ex.Message;
            }

            return sample;
        }
    }
}
