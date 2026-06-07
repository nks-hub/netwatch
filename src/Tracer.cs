using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;

namespace NetWatch
{
    /// <summary>
    /// TTL-limited traceroute built on <see cref="System.Net.NetworkInformation.Ping"/>.
    /// One probe per hop keeps runs quick. Works for non-administrator users.
    /// </summary>
    public static class Tracer
    {
        private const int PayloadSize = 32;
        private const int ReverseDnsTimeoutMs = 700;

        /// <summary>
        /// Traces the path to <paramref name="target"/> up to
        /// <paramref name="maxHops"/> hops. Builds <see cref="TraceResult.Hops"/>
        /// in TTL order and sets <see cref="TraceResult.Changed"/> when the hop
        /// address sequence differs from <paramref name="previousHops"/>. Never throws.
        /// </summary>
        public static TraceResult Run(string target, int maxHops, int timeoutMs, bool reverseDns, Hop[] previousHops)
        {
            var result = new TraceResult
            {
                Target = target,
                Addr = null,
                Time = DateTime.UtcNow,
                Hops = new Hop[0],
                Changed = false,
                Error = null
            };

            string destStr = Pinger.Resolve(target);
            if (destStr == null)
            {
                result.Error = "ResolveFailed";
                return result;
            }
            result.Addr = destStr;

            IPAddress dest;
            if (!IPAddress.TryParse(destStr, out dest))
            {
                result.Error = "BadAddress";
                return result;
            }

            if (maxHops < 1) maxHops = 1;
            // A non-positive timeout makes Ping.Send throw, which the inner catch
            // would silently turn into fake timeouts; clamp like maxHops above.
            if (timeoutMs < 1) timeoutMs = 1;

            var hops = new List<Hop>();
            byte[] buffer = new byte[PayloadSize];

            try
            {
                using (var ping = new System.Net.NetworkInformation.Ping())
                {
                    for (int ttl = 1; ttl <= maxHops; ttl++)
                    {
                        var hop = new Hop
                        {
                            Ttl = ttl,
                            Addr = "",
                            Host = null,
                            RttMs = 0,
                            Reached = false,
                            Timeout = false
                        };

                        var options = new PingOptions(ttl, true);
                        PingReply reply = null;

                        try
                        {
                            reply = ping.Send(dest, timeoutMs, buffer, options);
                        }
                        catch
                        {
                            hop.Timeout = true;
                            hops.Add(hop);
                            continue;
                        }

                        if (reply == null)
                        {
                            hop.Timeout = true;
                            hops.Add(hop);
                            continue;
                        }

                        // RoundtripTime is the OS-observed round-trip in ms (long);
                        // populated on both TtlExpired and Success replies. RttMs is
                        // only ever set from an actual reply, so timed-out/failed
                        // hops keep RttMs = 0 instead of carrying ~timeoutMs.
                        switch (reply.Status)
                        {
                            case IPStatus.TtlExpired:
                                hop.Addr = AddrString(reply.Address);
                                hop.RttMs = Convert.ToDouble(reply.RoundtripTime, CultureInfo.InvariantCulture);
                                hop.Reached = false;
                                break;

                            case IPStatus.Success:
                                string sa = AddrString(reply.Address);
                                // Some stacks return null/0.0.0.0 for the final echo
                                // reply; fall back to the known destination so the
                                // final hop key stays stable for PathChanged.
                                hop.Addr = sa.Length > 0 ? sa : destStr;
                                hop.RttMs = Convert.ToDouble(reply.RoundtripTime, CultureInfo.InvariantCulture);
                                hop.Reached = true;
                                break;

                            case IPStatus.TimedOut:
                                hop.Addr = "";
                                hop.Timeout = true;
                                break;

                            default:
                                string a = AddrString(reply.Address);
                                if (a.Length > 0)
                                    hop.Addr = a;
                                else
                                    hop.Timeout = true;
                                break;
                        }

                        if (reverseDns && hop.Addr.Length > 0)
                            hop.Host = TryReverseDns(hop.Addr);

                        hops.Add(hop);

                        if (hop.Reached) break;
                    }
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            result.Hops = hops.ToArray();
            result.Changed = PathChanged(result.Hops, previousHops);
            return result;
        }

        private static string AddrString(IPAddress addr)
        {
            if (addr == null) return "";
            string s = addr.ToString();
            if (s == "0.0.0.0") return "";
            return s;
        }

        /// <summary>
        /// Best-effort reverse DNS, bounded to ~700 ms wall-clock. The BCL Dns
        /// API has no per-call timeout, so a slow PTR resolver could otherwise
        /// stall the trace several seconds per hop. Returns null on failure or
        /// timeout; never throws.
        /// </summary>
        private static string TryReverseDns(string addr)
        {
            try
            {
                var t = System.Threading.Tasks.Task.Run(() => Dns.GetHostEntry(addr).HostName);
                if (t.Wait(ReverseDnsTimeoutMs))
                {
                    string host = t.Result;
                    if (!string.IsNullOrEmpty(host))
                        return host;
                }
            }
            catch
            {
                // Wait throws AggregateException on resolver failure; swallow to null.
                // No PTR record, timeout, etc. also land here.
            }
            return null;
        }

        /// <summary>
        /// Compares the ordered hop Addr sequence (timeouts treated as "*",
        /// Host/RTT ignored). Returns false when there is no previous run.
        /// </summary>
        private static bool PathChanged(Hop[] current, Hop[] previous)
        {
            if (previous == null || previous.Length == 0) return false;
            if (current == null) current = new Hop[0];
            if (current.Length != previous.Length) return true;

            for (int i = 0; i < current.Length; i++)
            {
                if (HopKey(current[i]) != HopKey(previous[i]))
                    return true;
            }
            return false;
        }

        private static string HopKey(Hop h)
        {
            if (h == null) return "*";
            if (h.Timeout || string.IsNullOrEmpty(h.Addr)) return "*";
            return h.Addr;
        }
    }
}
