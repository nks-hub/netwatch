using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace NetWatch
{
    /// <summary>
    /// Thread-safe, append-only writer for human log and per-record CSV files.
    /// Files rotate by local date; every write is locked and flushed so data
    /// survives an abrupt process kill. Never throws out of public methods.
    /// </summary>
    public sealed class LogWriter : IDisposable
    {
        private const string TsFormat = "yyyy-MM-dd HH:mm:ss";

        private readonly object _gate = new object();
        private readonly string _dir;

        private string _date;             // local date currently open ("yyyy-MM-dd")
        private StreamWriter _log;
        private StreamWriter _ping;
        private StreamWriter _speed;
        private StreamWriter _trace;
        private StreamWriter _iface;
        private bool _disposed;

        public LogWriter(string dir)
        {
            _dir = string.IsNullOrEmpty(dir) ? "." : dir;
            try { Directory.CreateDirectory(_dir); }
            catch { /* keep running; individual writes will retry/fail quietly */ }
        }

        public string Dir { get { return _dir; } }

        public void Event(string msg)
        {
            lock (_gate)
            {
                if (_disposed) return;
                try
                {
                    var w = Log();
                    if (w == null) return;
                    w.Write(Now());
                    w.Write("  ");
                    w.WriteLine(msg ?? "");
                    w.Flush();
                }
                catch { }
            }
        }

        public void Ping(PingSample s)
        {
            if (s == null) return;
            lock (_gate)
            {
                if (_disposed) return;
                try
                {
                    var w = Ping();
                    if (w == null) return;
                    // time,target,addr,kind,success,rtt_ms
                    w.Write(Local(s.Time)); w.Write(',');
                    w.Write(Q(s.Target)); w.Write(',');
                    w.Write(Q(s.Addr)); w.Write(',');
                    w.Write(s.Kind.ToString()); w.Write(',');
                    w.Write(s.Success ? "1" : "0"); w.Write(',');
                    w.Write(s.Success ? Num(s.RttMs) : "");
                    w.WriteLine();
                    w.Flush();
                }
                catch { }
            }
        }

        public void Speed(SpeedResult r)
        {
            if (r == null) return;
            lock (_gate)
            {
                if (_disposed) return;
                try
                {
                    var w = Speed();
                    if (w == null) return;
                    // time,down_mbps,up_mbps,latency_ms,jitter_ms,server,error
                    w.Write(Local(r.Time)); w.Write(',');
                    w.Write(Num(r.DownMbps)); w.Write(',');
                    w.Write(Num(r.UpMbps)); w.Write(',');
                    w.Write(Num(r.LatencyMs)); w.Write(',');
                    w.Write(Num(r.JitterMs)); w.Write(',');
                    w.Write(Q(r.Server)); w.Write(',');
                    w.Write(Q(r.Error));
                    w.WriteLine();
                    w.Flush();
                }
                catch { }
            }
        }

        public void Trace(TraceResult t)
        {
            if (t == null) return;
            lock (_gate)
            {
                if (_disposed) return;
                try
                {
                    var w = Trace();
                    if (w != null)
                    {
                        var hops = t.Hops ?? new Hop[0];
                        var stamp = Local(t.Time);
                        // time,target,addr,ttl,hop_addr,hop_host,rtt_ms,reached,timeout
                        foreach (var h in hops)
                        {
                            if (h == null) continue;
                            w.Write(stamp); w.Write(',');
                            w.Write(Q(t.Target)); w.Write(',');
                            w.Write(Q(t.Addr)); w.Write(',');
                            w.Write(h.Ttl.ToString(CultureInfo.InvariantCulture)); w.Write(',');
                            w.Write(Q(h.Addr)); w.Write(',');
                            w.Write(Q(h.Host)); w.Write(',');
                            w.Write(h.Timeout ? "" : Num(h.RttMs)); w.Write(',');
                            w.Write(h.Reached ? "1" : "0"); w.Write(',');
                            w.Write(h.Timeout ? "1" : "0");
                            w.WriteLine();
                        }
                        w.Flush();

                        int n = hops.Length;
                        string path = t.Changed ? "CHANGED" : "stable";
                        string summary = "trace to " + (t.Target ?? "?") + ": " + n.ToString(CultureInfo.InvariantCulture) + " hops, path " + path;
                        if (!string.IsNullOrEmpty(t.Error)) summary += " (error: " + t.Error + ")";
                        EventLocked(summary);
                    }
                }
                catch { }
            }
        }

        public void Interface(InterfaceSample s)
        {
            if (s == null) return;
            lock (_gate)
            {
                if (_disposed) return;
                try
                {
                    var w = Iface();
                    if (w == null) return;
                    // time,name,kind,active,up,link_mbps,ip,gateway,recv_mbps,send_mbps,
                    // in_err,out_err,wifi,ssid,signal_pct,rssi_dbm,wifi_rx_mbps,wifi_tx_mbps
                    w.Write(Local(s.Time)); w.Write(',');
                    w.Write(Q(s.Name)); w.Write(',');
                    w.Write(s.Kind.ToString()); w.Write(',');
                    w.Write(s.IsActive ? "1" : "0"); w.Write(',');
                    w.Write(s.Up ? "1" : "0"); w.Write(',');
                    w.Write(Num(s.LinkSpeedBps / 1e6)); w.Write(',');
                    w.Write(Q(s.IpAddress)); w.Write(',');
                    w.Write(Q(s.Gateway)); w.Write(',');
                    w.Write(Num(s.RecvMbps)); w.Write(',');
                    w.Write(Num(s.SendMbps)); w.Write(',');
                    w.Write(s.InErrors.ToString(CultureInfo.InvariantCulture)); w.Write(',');
                    w.Write(s.OutErrors.ToString(CultureInfo.InvariantCulture)); w.Write(',');
                    w.Write(s.IsWifi ? "1" : "0"); w.Write(',');
                    w.Write(Q(s.Ssid)); w.Write(',');
                    w.Write(s.SignalPct.ToString(CultureInfo.InvariantCulture)); w.Write(',');
                    w.Write(s.RssiDbm.ToString(CultureInfo.InvariantCulture)); w.Write(',');
                    w.Write(Num(s.WifiRxMbps)); w.Write(',');
                    w.Write(Num(s.WifiTxMbps));
                    w.WriteLine();
                    w.Flush();
                }
                catch { }
            }
        }

        public void Summary(Snapshot snap)
        {
            if (snap == null) return;
            lock (_gate)
            {
                if (_disposed) return;
                try
                {
                    var w = Log();
                    if (w == null) return;

                    w.WriteLine();
                    w.WriteLine("===== SUMMARY " + Now() + " =====");

                    var act = snap.ActiveInterface;
                    if (act != null)
                    {
                        var sb = new StringBuilder();
                        sb.Append("  Active link: ").Append(LinkLabel(act));
                        if (act.IsWifi)
                        {
                            sb.Append(" [SSID ").Append(string.IsNullOrEmpty(act.Ssid) ? "?" : act.Ssid);
                            sb.Append(", signal ").Append(act.SignalPct.ToString(CultureInfo.InvariantCulture)).Append('%');
                            sb.Append(", ").Append(act.RssiDbm.ToString(CultureInfo.InvariantCulture)).Append(" dBm]");
                        }
                        if (!string.IsNullOrEmpty(act.IpAddress)) sb.Append(" ip=").Append(act.IpAddress);
                        if (!string.IsNullOrEmpty(act.Gateway)) sb.Append(" gw=").Append(act.Gateway);
                        if (act.LinkSpeedBps > 0) sb.Append(" link=").Append(Num(act.LinkSpeedBps / 1e6)).Append(" Mbps");
                        w.WriteLine(sb.ToString());
                    }
                    else
                    {
                        w.WriteLine("  Active link: (none)");
                    }

                    var vpn = snap.Vpn;
                    if (vpn != null && vpn.Present)
                    {
                        var sb = new StringBuilder();
                        sb.Append("  VPN: ").Append(string.IsNullOrEmpty(vpn.Vendor) ? "VPN" : vpn.Vendor);
                        sb.Append(vpn.Connected ? " CONNECTED" : " DISCONNECTED");
                        if (!string.IsNullOrEmpty(vpn.IpAddress)) sb.Append(" ip=").Append(vpn.IpAddress);
                        if (!string.IsNullOrEmpty(vpn.Gateway)) sb.Append(" gw=").Append(vpn.Gateway);
                        w.WriteLine(sb.ToString());
                    }
                    else
                    {
                        w.WriteLine("  VPN: none detected");
                    }

                    var targets = snap.Targets ?? new TargetSnapshot[0];
                    foreach (var ts in targets)
                    {
                        if (ts == null) continue;
                        WindowStat first = FirstWindow(ts);
                        WindowStat longest = LongestWindow(ts);
                        var sb = new StringBuilder();
                        sb.Append("  ").Append(PadRight(ts.Target ?? "?", 22));
                        sb.Append(ts.Up ? "UP  " : "DOWN ");
                        if (first != null)
                        {
                            sb.Append("1m: loss ").Append(Num(first.LossPct)).Append('%');
                            sb.Append(" avg ").Append(Num(first.AvgMs)).Append("ms");
                            sb.Append(" p95 ").Append(Num(first.P95Ms)).Append("ms");
                        }
                        if (longest != null && (first == null || longest.WindowSec != first.WindowSec))
                        {
                            sb.Append("  | ").Append(longest.WindowSec.ToString(CultureInfo.InvariantCulture)).Append("s: loss ").Append(Num(longest.LossPct)).Append('%');
                            sb.Append(" avg ").Append(Num(longest.AvgMs)).Append("ms");
                            sb.Append(" p95 ").Append(Num(longest.P95Ms)).Append("ms");
                        }
                        sb.Append("  | session: max ").Append(Num(ts.SessionMaxMs)).Append("ms drops ")
                          .Append(ts.SessionLost.ToString(CultureInfo.InvariantCulture)).Append('/')
                          .Append(ts.SessionCount.ToString(CultureInfo.InvariantCulture));
                        w.WriteLine(sb.ToString());
                    }

                    var sp = snap.LastSpeed;
                    if (sp != null && sp.Time != default(DateTime))
                    {
                        if (!string.IsNullOrEmpty(sp.Error))
                        {
                            w.WriteLine("  Speedtest: error " + sp.Error + " (" + Local(sp.Time) + ")");
                        }
                        else
                        {
                            w.WriteLine("  Speedtest: down " + Num(sp.DownMbps) + " Mbps, up " + Num(sp.UpMbps) +
                                        " Mbps, latency " + Num(sp.LatencyMs) + "ms +/- " + Num(sp.JitterMs) +
                                        "ms, server " + (sp.Server ?? "?") + " (" + Local(sp.Time) + ")");
                        }
                    }
                    else
                    {
                        w.WriteLine("  Speedtest: none yet");
                    }

                    w.Flush();
                }
                catch { }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                CloseAll();
            }
        }

        // ---- internal: rotation + lazy open (caller holds _gate) ----

        private void Rotate()
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (today == _date) return;
            CloseAll();
            _date = today;
        }

        private StreamWriter Log()
        {
            Rotate();
            return _log ?? (_log = Open("log_" + _date + ".log", null));
        }

        private StreamWriter Ping()
        {
            Rotate();
            return _ping ?? (_ping = Open("ping_" + _date + ".csv",
                "time,target,addr,kind,success,rtt_ms"));
        }

        private StreamWriter Speed()
        {
            Rotate();
            return _speed ?? (_speed = Open("speed_" + _date + ".csv",
                "time,down_mbps,up_mbps,latency_ms,jitter_ms,server,error"));
        }

        private StreamWriter Trace()
        {
            Rotate();
            return _trace ?? (_trace = Open("trace_" + _date + ".csv",
                "time,target,addr,ttl,hop_addr,hop_host,rtt_ms,reached,timeout"));
        }

        private StreamWriter Iface()
        {
            Rotate();
            return _iface ?? (_iface = Open("iface_" + _date + ".csv",
                "time,name,kind,active,up,link_mbps,ip,gateway,recv_mbps,send_mbps," +
                "in_err,out_err,wifi,ssid,signal_pct,rssi_dbm,wifi_rx_mbps,wifi_tx_mbps"));
        }

        private StreamWriter Open(string file, string header)
        {
            try
            {
                var path = Path.Combine(_dir, file);
                bool fresh = !File.Exists(path) || new FileInfo(path).Length == 0;
                var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var w = new StreamWriter(fs, new UTF8Encoding(false));
                w.AutoFlush = false;
                if (fresh && header != null)
                {
                    w.WriteLine(header);
                    w.Flush();
                }
                return w;
            }
            catch
            {
                return null;
            }
        }

        private void CloseAll()
        {
            Close(ref _log);
            Close(ref _ping);
            Close(ref _speed);
            Close(ref _trace);
            Close(ref _iface);
        }

        private static void Close(ref StreamWriter w)
        {
            if (w == null) return;
            try { w.Flush(); w.Dispose(); } catch { }
            w = null;
        }

        private void EventLocked(string msg)
        {
            var w = Log();
            if (w == null) return;
            w.Write(Now());
            w.Write("  ");
            w.WriteLine(msg ?? "");
            w.Flush();
        }

        // ---- helpers ----

        private static string Now()
        {
            return DateTime.Now.ToString(TsFormat, CultureInfo.InvariantCulture);
        }

        private static string Local(DateTime utc)
        {
            // Sample times are recorded in UTC; render in local time for readability.
            DateTime local;
            try
            {
                if (utc.Kind == DateTimeKind.Utc) local = utc.ToLocalTime();
                else if (utc.Kind == DateTimeKind.Local) local = utc;
                else local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
            }
            catch { local = utc; }
            return local.ToString(TsFormat, CultureInfo.InvariantCulture);
        }

        private static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "";
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Q(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            bool needs = field.IndexOf(',') >= 0 || field.IndexOf('"') >= 0 ||
                         field.IndexOf('\n') >= 0 || field.IndexOf('\r') >= 0;
            if (!needs) return field;
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        private static string PadRight(string s, int width)
        {
            s = s ?? "";
            return s.Length >= width ? s : s + new string(' ', width - s.Length);
        }

        // Human label for an adapter without doubling its kind (e.g. an adapter
        // literally named "Ethernet" prints once, not "Ethernet Ethernet").
        private static string LinkLabel(InterfaceSample a)
        {
            string kind = a.Kind == LinkKind.WiFi ? "Wi-Fi" : a.Kind == LinkKind.Vpn ? "VPN" : a.Kind.ToString();
            string name = a.Name ?? "";
            if (name.Length == 0 || name.IndexOf(kind, StringComparison.OrdinalIgnoreCase) >= 0) return kind;
            return kind + " \"" + name + "\"";
        }

        private static WindowStat FirstWindow(TargetSnapshot ts)
        {
            var ws = ts.Windows;
            return (ws != null && ws.Length > 0) ? ws[0] : null;
        }

        private static WindowStat LongestWindow(TargetSnapshot ts)
        {
            var ws = ts.Windows;
            if (ws == null || ws.Length == 0) return null;
            WindowStat best = null;
            foreach (var w in ws)
            {
                if (w == null) continue;
                if (best == null || w.WindowSec > best.WindowSec) best = w;
            }
            return best;
        }
    }
}
