using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NetWatch
{
    /// <summary>
    /// Application configuration. Stored as a simple, locale-independent
    /// <c>key = value</c> text file (comments start with '#', lists are
    /// comma-separated) so IT staff can edit it without JSON tooling.
    /// </summary>
    public sealed class Config
    {
        public string[] InternetTargets { get; set; }
        public bool MonitorGateway { get; set; }
        public bool MonitorVpn { get; set; }
        public string[] VpnTargets { get; set; }
        public string VpnServer { get; set; }

        public int PingIntervalMs { get; set; }
        public int PingTimeoutMs { get; set; }

        public string TraceTarget { get; set; }
        public int TraceIntervalSec { get; set; }
        public int TraceMaxHops { get; set; }
        public bool TraceReverseDns { get; set; }

        public bool SpeedtestEnabled { get; set; }
        public int SpeedtestIntervalMin { get; set; }
        public bool SpeedtestOnStart { get; set; }

        public int InterfaceIntervalSec { get; set; }
        public int SummaryIntervalSec { get; set; }
        public int ConsoleRefreshMs { get; set; }
        public int ReportIntervalMin { get; set; }

        public string LogDir { get; set; }
        public int[] WindowsSec { get; set; }
        public int SparkLen { get; set; }

        public int WifiWeakSignalPct { get; set; }
        public double HighLatencyMs { get; set; }
        public double HighLossPct { get; set; }

        /// <summary>Returns a configuration populated with sensible defaults.</summary>
        public static Config Default()
        {
            return new Config
            {
                InternetTargets = new[] { "1.1.1.1", "8.8.8.8", "google.com" },
                MonitorGateway = true,
                MonitorVpn = true,
                VpnTargets = new string[0],
                VpnServer = "",
                PingIntervalMs = 1000,
                PingTimeoutMs = 2000,
                TraceTarget = "8.8.8.8",
                TraceIntervalSec = 300,
                TraceMaxHops = 30,
                TraceReverseDns = true,
                SpeedtestEnabled = true,
                SpeedtestIntervalMin = 30,
                SpeedtestOnStart = true,
                InterfaceIntervalSec = 2,
                SummaryIntervalSec = 60,
                ConsoleRefreshMs = 1000,
                ReportIntervalMin = 1,
                LogDir = "logs",
                WindowsSec = new[] { 60, 300, 900, 3600 },
                SparkLen = 120,
                WifiWeakSignalPct = 40,
                HighLatencyMs = 150,
                HighLossPct = 5
            };
        }

        /// <summary>Time windows expressed as <see cref="TimeSpan"/> values.</summary>
        public IEnumerable<TimeSpan> Windows()
        {
            foreach (int s in WindowsSec ?? new int[0])
                yield return TimeSpan.FromSeconds(s);
        }

        /// <summary>
        /// Loads the config from <paramref name="path"/>. When the file does
        /// not exist, a commented default file is written and
        /// <paramref name="created"/> is set to true.
        /// </summary>
        public static Config LoadOrCreate(string path, out bool created)
        {
            created = false;
            if (!File.Exists(path))
            {
                var def = Default();
                def.Save(path);
                created = true;
                return def;
            }
            var cfg = Default();
            cfg.MergeFrom(File.ReadAllLines(path));
            return cfg;
        }

        private void MergeFrom(string[] lines)
        {
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                var val = line.Substring(eq + 1).Trim();
                Apply(key, val);
            }
        }

        private void Apply(string key, string val)
        {
            switch (key)
            {
                case "internet_targets": InternetTargets = SplitList(val); break;
                case "monitor_gateway": MonitorGateway = ParseBool(val, MonitorGateway); break;
                case "monitor_vpn": MonitorVpn = ParseBool(val, MonitorVpn); break;
                case "vpn_targets": VpnTargets = SplitList(val); break;
                case "vpn_server": VpnServer = val; break;
                case "ping_interval_ms": PingIntervalMs = ParseInt(val, PingIntervalMs); break;
                case "ping_timeout_ms": PingTimeoutMs = ParseInt(val, PingTimeoutMs); break;
                case "trace_target": TraceTarget = val; break;
                case "trace_interval_sec": TraceIntervalSec = ParseInt(val, TraceIntervalSec); break;
                case "trace_max_hops": TraceMaxHops = ParseInt(val, TraceMaxHops); break;
                case "trace_reverse_dns": TraceReverseDns = ParseBool(val, TraceReverseDns); break;
                case "speedtest_enabled": SpeedtestEnabled = ParseBool(val, SpeedtestEnabled); break;
                case "speedtest_interval_min": SpeedtestIntervalMin = ParseInt(val, SpeedtestIntervalMin); break;
                case "speedtest_on_start": SpeedtestOnStart = ParseBool(val, SpeedtestOnStart); break;
                case "interface_interval_sec": InterfaceIntervalSec = ParseInt(val, InterfaceIntervalSec); break;
                case "summary_interval_sec": SummaryIntervalSec = ParseInt(val, SummaryIntervalSec); break;
                case "console_refresh_ms": ConsoleRefreshMs = ParseInt(val, ConsoleRefreshMs); break;
                case "report_interval_min": ReportIntervalMin = ParseInt(val, ReportIntervalMin); break;
                case "log_dir": LogDir = val; break;
                case "windows_sec": WindowsSec = SplitIntList(val, WindowsSec); break;
                case "spark_len": SparkLen = ParseInt(val, SparkLen); break;
                case "wifi_weak_signal_pct": WifiWeakSignalPct = ParseInt(val, WifiWeakSignalPct); break;
                case "high_latency_ms": HighLatencyMs = ParseDouble(val, HighLatencyMs); break;
                case "high_loss_pct": HighLossPct = ParseDouble(val, HighLossPct); break;
            }
        }

        /// <summary>Writes a commented configuration file reflecting the current values.</summary>
        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine("# NetWatch configuration");
            sb.AppendLine("# Edit values and restart the application. Lists are comma-separated.");
            sb.AppendLine();
            sb.AppendLine("# --- Targets -----------------------------------------------------------");
            sb.AppendLine("# Public hosts/IPs pinged continuously to gauge internet reachability.");
            sb.AppendLine("internet_targets = " + JoinList(InternetTargets));
            sb.AppendLine("# Auto-detect the default gateway (router) and ping it (LAN health).");
            sb.AppendLine("monitor_gateway = " + Bool(MonitorGateway));
            sb.AppendLine("# Auto-detect a VPN adapter and ping its tunnel gateway.");
            sb.AppendLine("monitor_vpn = " + Bool(MonitorVpn));
            sb.AppendLine("# Hosts reachable only through the VPN (e.g. internal app servers).");
            sb.AppendLine("vpn_targets = " + JoinList(VpnTargets));
            sb.AppendLine("# Optional public address of the company VPN endpoint (also traced).");
            sb.AppendLine("vpn_server = " + (VpnServer ?? ""));
            sb.AppendLine();
            sb.AppendLine("# --- Ping --------------------------------------------------------------");
            sb.AppendLine("ping_interval_ms = " + PingIntervalMs.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("ping_timeout_ms = " + PingTimeoutMs.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("# --- Traceroute --------------------------------------------------------");
            sb.AppendLine("trace_target = " + (TraceTarget ?? ""));
            sb.AppendLine("trace_interval_sec = " + TraceIntervalSec.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("trace_max_hops = " + TraceMaxHops.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("trace_reverse_dns = " + Bool(TraceReverseDns));
            sb.AppendLine();
            sb.AppendLine("# --- Speed test (Cloudflare) -------------------------------------------");
            sb.AppendLine("speedtest_enabled = " + Bool(SpeedtestEnabled));
            sb.AppendLine("speedtest_interval_min = " + SpeedtestIntervalMin.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("speedtest_on_start = " + Bool(SpeedtestOnStart));
            sb.AppendLine();
            sb.AppendLine("# --- Interface / Wi-Fi sampling ----------------------------------------");
            sb.AppendLine("interface_interval_sec = " + InterfaceIntervalSec.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("# --- Logging & display -------------------------------------------------");
            sb.AppendLine("summary_interval_sec = " + SummaryIntervalSec.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("console_refresh_ms = " + ConsoleRefreshMs.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("# Minutes between automatic HTML report writes (report.html next to logs); 0 disables.");
            sb.AppendLine("report_interval_min = " + ReportIntervalMin.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("log_dir = " + (LogDir ?? "logs"));
            sb.AppendLine("windows_sec = " + JoinIntList(WindowsSec));
            sb.AppendLine("spark_len = " + SparkLen.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("# --- Alert thresholds (logged as events when crossed) ------------------");
            sb.AppendLine("wifi_weak_signal_pct = " + WifiWeakSignalPct.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("high_latency_ms = " + HighLatencyMs.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("high_loss_pct = " + HighLossPct.ToString(CultureInfo.InvariantCulture));

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static string[] SplitList(string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return new string[0];
            var parts = val.Split(',');
            var list = new List<string>();
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list.ToArray();
        }

        private static int[] SplitIntList(string val, int[] fallback)
        {
            var s = SplitList(val);
            if (s.Length == 0) return fallback;
            var list = new List<int>();
            foreach (var p in s)
                if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) list.Add(n);
            return list.Count > 0 ? list.ToArray() : fallback;
        }

        private static bool ParseBool(string v, bool fallback)
        {
            v = (v ?? "").Trim().ToLowerInvariant();
            if (v == "true" || v == "1" || v == "yes" || v == "on") return true;
            if (v == "false" || v == "0" || v == "no" || v == "off") return false;
            return fallback;
        }

        private static int ParseInt(string v, int fallback)
        {
            return int.TryParse((v ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback;
        }

        private static double ParseDouble(string v, double fallback)
        {
            return double.TryParse((v ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : fallback;
        }

        private static string JoinList(string[] v) { return v == null ? "" : string.Join(", ", v); }
        private static string JoinIntList(int[] v)
        {
            if (v == null) return "";
            var parts = new string[v.Length];
            for (int i = 0; i < v.Length; i++) parts[i] = v[i].ToString(CultureInfo.InvariantCulture);
            return string.Join(", ", parts);
        }
        private static string Bool(bool b) { return b ? "true" : "false"; }
    }
}
