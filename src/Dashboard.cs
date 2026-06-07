using System;
using System.Globalization;
using System.Text;

namespace NetWatch
{
    /// <summary>
    /// Live console dashboard. Console-API based (SetCursorPosition + colours)
    /// so it is locale- and ANSI-independent. Never throws out of Render.
    /// </summary>
    public static class Dashboard
    {
        private static readonly char[] Blocks = { '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };
        private const string Repo = "https://github.com/nks-hub/netwatch";
        private const string Author = "LuRy <lury@lury.cz>";

        public static void Init()
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            try { Console.CursorVisible = false; } catch { }
        }

        public static bool Available
        {
            get
            {
                try
                {
                    if (Console.IsOutputRedirected) return false;
                    return Console.WindowWidth > 0 && Console.WindowHeight > 0;
                }
                catch { return false; }
            }
        }

        public static void Render(Snapshot snap, Config cfg, DateTime startedUtc)
        {
            if (snap == null || cfg == null) return;
            try
            {
                int width, height;
                try
                {
                    width = Console.WindowWidth;
                    height = Console.WindowHeight;
                }
                catch { return; }
                if (width <= 0 || height <= 0) return;

                var lines = Build(snap, cfg, startedUtc, width);

                try { Console.SetCursorPosition(0, 0); } catch { }

                ConsoleColor baseColor;
                try { baseColor = Console.ForegroundColor; } catch { baseColor = ConsoleColor.Gray; }

                int max = Math.Max(0, height - 1);
                int count = Math.Min(lines.Count, max);
                for (int i = 0; i < count; i++)
                {
                    var ln = lines[i];
                    WriteLinePadded(ln.Text, ln.Color, width, baseColor);
                }
                // Clear any leftover rows from a taller previous frame.
                for (int i = count; i < max; i++)
                    WriteLinePadded("", baseColor, width, baseColor);

                try { Console.ForegroundColor = baseColor; } catch { }
            }
            catch { /* window may have been resized/closed mid-draw */ }
        }

        /// <summary>
        /// Renders one dashboard frame to a plain text string (no cursor control),
        /// so a single status snapshot can be printed to stdout, piped, or logged
        /// even when there is no interactive console.
        /// </summary>
        public static string RenderToText(Snapshot snap, Config cfg, DateTime startedUtc, int width)
        {
            if (snap == null || cfg == null) return "";
            if (width <= 0) width = 100;
            var sb = new StringBuilder();
            foreach (var ln in Build(snap, cfg, startedUtc, width))
                sb.AppendLine(ln.Text);
            return sb.ToString();
        }

        // ---- frame construction ----

        private sealed class Line
        {
            public string Text;
            public ConsoleColor Color;
            public Line(string t, ConsoleColor c) { Text = t; Color = c; }
        }

        private static System.Collections.Generic.List<Line> Build(Snapshot snap, Config cfg, DateTime startedUtc, int width)
        {
            var outp = new System.Collections.Generic.List<Line>();
            var def = ConsoleColor.Gray;

            // Title
            TimeSpan up = DateTime.UtcNow - startedUtc;
            if (up < TimeSpan.Zero) up = TimeSpan.Zero;
            string upStr = ((int)up.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":" +
                           up.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" +
                           up.Seconds.ToString("00", CultureInfo.InvariantCulture);
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            int inner = Math.Min(66, Math.Max(40, width - 2));
            outp.Add(new Line("╭" + new string('─', inner) + "╮", ConsoleColor.DarkCyan));
            outp.Add(new Line("│ " + Fit("NetWatch v1.0    network & VPN latency diagnostics", inner - 2) + " │", ConsoleColor.Cyan));
            outp.Add(new Line("│ " + Fit("by " + Author + "  ·  " + Repo, inner - 2) + " │", ConsoleColor.DarkGray));
            outp.Add(new Line("╰" + new string('─', inner) + "╯", ConsoleColor.DarkCyan));
            outp.Add(new Line(" up " + upStr + "    " + now + "    log: " + (cfg.LogDir ?? "logs"), def));
            outp.Add(new Line("", def));

            // Active link
            BuildActiveLink(outp, snap.ActiveInterface, cfg, def);

            // VPN
            BuildVpn(outp, snap.Vpn, def);

            outp.Add(new Line("", def));

            // Targets table
            BuildTargets(outp, snap, cfg, def);

            outp.Add(new Line("", def));

            // Speedtest
            BuildSpeed(outp, snap, cfg, def);

            // Trace
            BuildTrace(outp, snap, def);

            // Events
            outp.Add(new Line("", def));
            outp.Add(new Line("EVENTS", ConsoleColor.White));
            var ev = snap.RecentEvents ?? new string[0];
            int start = Math.Max(0, ev.Length - 6);
            for (int i = start; i < ev.Length; i++)
                outp.Add(new Line("  " + ev[i], ConsoleColor.DarkGray));

            return outp;
        }

        private static void BuildActiveLink(System.Collections.Generic.List<Line> outp, InterfaceSample a, Config cfg, ConsoleColor def)
        {
            if (a == null)
            {
                outp.Add(new Line("ACTIVE LINK  (none / detecting...)", ConsoleColor.Yellow));
                return;
            }

            var sb = new StringBuilder();
            string kindWord = a.Kind == LinkKind.WiFi ? "Wi-Fi" : a.Kind == LinkKind.Vpn ? "VPN" : a.Kind.ToString();
            string aname = a.Name ?? "";
            sb.Append("ACTIVE LINK  ").Append(kindWord);
            if (aname.Length > 0 && aname.IndexOf(kindWord, StringComparison.OrdinalIgnoreCase) < 0)
                sb.Append("  ").Append(aname);
            if (!string.IsNullOrEmpty(a.IpAddress)) sb.Append("  ip ").Append(a.IpAddress);

            ConsoleColor color = ConsoleColor.Green;

            if (a.IsWifi)
            {
                sb.Append("  SSID ").Append(string.IsNullOrEmpty(a.Ssid) ? "?" : a.Ssid);
                sb.Append("  [").Append(SignalBar(a.SignalPct)).Append("] ");
                sb.Append(a.SignalPct.ToString(CultureInfo.InvariantCulture)).Append("% ");
                sb.Append('(').Append(a.RssiDbm.ToString(CultureInfo.InvariantCulture)).Append(" dBm)");
                if (a.WifiRxMbps > 0 || a.WifiTxMbps > 0)
                    sb.Append("  rx/tx ").Append(Num(a.WifiRxMbps)).Append('/').Append(Num(a.WifiTxMbps)).Append(" Mbps");
                if (a.SignalPct < cfg.WifiWeakSignalPct) color = ConsoleColor.Yellow;
            }
            outp.Add(new Line(sb.ToString(), color));

            var sb2 = new StringBuilder();
            sb2.Append("             ");
            if (a.LinkSpeedBps > 0) sb2.Append("link ").Append(Num(a.LinkSpeedBps / 1e6)).Append(" Mbps  ");
            sb2.Append("recv ").Append(Num(a.RecvMbps)).Append(" / send ").Append(Num(a.SendMbps)).Append(" Mbps");
            if (a.InErrors > 0 || a.OutErrors > 0)
                sb2.Append("   err in/out ").Append(a.InErrors.ToString(CultureInfo.InvariantCulture))
                   .Append('/').Append(a.OutErrors.ToString(CultureInfo.InvariantCulture));
            outp.Add(new Line(sb2.ToString(), (a.InErrors > 0 || a.OutErrors > 0) ? ConsoleColor.Yellow : def));
        }

        private static void BuildVpn(System.Collections.Generic.List<Line> outp, VpnStatus v, ConsoleColor def)
        {
            if (v == null || !v.Present)
            {
                outp.Add(new Line("VPN          none detected", ConsoleColor.DarkGray));
                return;
            }
            var sb = new StringBuilder();
            sb.Append("VPN          ").Append(string.IsNullOrEmpty(v.Vendor) ? "VPN" : v.Vendor).Append("  ");
            sb.Append(v.Connected ? "CONNECTED" : "DISCONNECTED");
            if (!string.IsNullOrEmpty(v.IpAddress)) sb.Append("  ip ").Append(v.IpAddress);
            if (!string.IsNullOrEmpty(v.Gateway)) sb.Append("  gw ").Append(v.Gateway);
            else if (v.Connected) sb.Append("  (no tunnel gateway exposed - set vpn_targets)");
            outp.Add(new Line(sb.ToString(), v.Connected ? ConsoleColor.Green : ConsoleColor.Red));
        }

        private static void BuildTargets(System.Collections.Generic.List<Line> outp, Snapshot snap, Config cfg, ConsoleColor def)
        {
            outp.Add(new Line(
                Pad("TARGET", 22) + Pad("Last", 7) + Pad("Avg1m", 7) + Pad("Max", 7) +
                Pad("Loss", 6) + Pad("Drops", 7) + Pad("Jit", 6) + Pad("P95", 7) + "spark",
                ConsoleColor.White));

            var targets = snap.Targets ?? new TargetSnapshot[0];
            foreach (var ts in targets)
            {
                if (ts == null) continue;
                WindowStat first = FirstWindow(ts);

                string label = ts.Target ?? "?";

                string last = double.IsNaN(ts.LastMs) ? "-" : Num(ts.LastMs);
                string avg = first != null ? Num(first.AvgMs) : "-";
                string max = ts.SessionMaxMs > 0 ? Num(ts.SessionMaxMs) : "-";
                string loss = first != null ? Num(first.LossPct) : "-";
                string drops = ts.SessionLost.ToString(CultureInfo.InvariantCulture);
                string jit = first != null ? Num(first.JitterMs) : "-";
                string p95 = first != null ? Num(first.P95Ms) : "-";

                var sb = new StringBuilder();
                sb.Append(Pad(label, 22));
                sb.Append(Pad(last, 7));
                sb.Append(Pad(avg, 7));
                sb.Append(Pad(max, 7));
                sb.Append(Pad(loss, 6));
                sb.Append(Pad(drops, 7));
                sb.Append(Pad(jit, 6));
                sb.Append(Pad(p95, 7));
                sb.Append(Spark(ts.Spark));

                ConsoleColor color = ConsoleColor.Green;
                double lossPct = first != null ? first.LossPct : 0;
                double avgMs = first != null ? first.AvgMs : 0;
                if (!ts.Up || lossPct > cfg.HighLossPct) color = ConsoleColor.Red;
                else if (lossPct > 0 || avgMs > cfg.HighLatencyMs) color = ConsoleColor.Yellow;
                bool emphasize = ts.Kind == TargetKind.Gateway || ts.Kind == TargetKind.Vpn;
                if (emphasize && color == ConsoleColor.Green) color = ConsoleColor.Cyan;

                outp.Add(new Line(sb.ToString(), color));
            }
        }

        private static void BuildSpeed(System.Collections.Generic.List<Line> outp, Snapshot snap, Config cfg, ConsoleColor def)
        {
            var sp = snap.LastSpeed;
            if (sp == null || sp.Time == default(DateTime))
            {
                outp.Add(new Line("SPEEDTEST    none yet", ConsoleColor.DarkGray));
                return;
            }
            if (!string.IsNullOrEmpty(sp.Error))
            {
                outp.Add(new Line("SPEEDTEST    error " + sp.Error + "  (" + Local(sp.Time) + ")", ConsoleColor.Red));
                return;
            }
            var sb = new StringBuilder();
            sb.Append("SPEEDTEST    down ").Append(Num(sp.DownMbps)).Append(" / up ").Append(Num(sp.UpMbps)).Append(" Mbps");
            sb.Append("  lat ").Append(Num(sp.LatencyMs)).Append(" +/- ").Append(Num(sp.JitterMs)).Append(" ms");
            if (!string.IsNullOrEmpty(sp.Server)) sb.Append("  ").Append(sp.Server);
            sb.Append("  (").Append(Local(sp.Time)).Append(')');

            if (cfg.SpeedtestEnabled && cfg.SpeedtestIntervalMin > 0)
            {
                DateTime nextUtc = ToUtc(sp.Time).AddMinutes(cfg.SpeedtestIntervalMin);
                TimeSpan rem = nextUtc - DateTime.UtcNow;
                if (rem < TimeSpan.Zero) rem = TimeSpan.Zero;
                sb.Append("  next in ").Append(((int)rem.TotalMinutes).ToString(CultureInfo.InvariantCulture)).Append('m');
            }
            outp.Add(new Line(sb.ToString(), def));
        }

        private static void BuildTrace(System.Collections.Generic.List<Line> outp, Snapshot snap, ConsoleColor def)
        {
            var t = snap.LastTrace;
            if (t == null || t.Time == default(DateTime))
            {
                outp.Add(new Line("TRACE        none yet", ConsoleColor.DarkGray));
                return;
            }
            int hops = t.Hops != null ? t.Hops.Length : 0;
            var sb = new StringBuilder();
            sb.Append("TRACE        ").Append(t.Target ?? "?").Append("  ").Append(hops.ToString(CultureInfo.InvariantCulture)).Append(" hops");
            sb.Append("  ").Append(Local(t.Time));
            sb.Append("  (path ").Append(t.Changed ? "CHANGED" : "stable").Append(')');
            outp.Add(new Line(sb.ToString(), t.Changed ? ConsoleColor.Yellow : def));
        }

        // ---- rendering helpers ----

        private static void WriteLinePadded(string text, ConsoleColor color, int width, ConsoleColor baseColor)
        {
            text = text ?? "";
            // Truncate to width and pad with spaces so stale chars are cleared.
            if (text.Length > width)
            {
                int cut = width;
                // Don't split a surrogate pair (would render a broken glyph).
                if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
                text = text.Substring(0, cut);
            }
            if (text.Length < width) text = text + new string(' ', width - text.Length);
            try { Console.ForegroundColor = color; } catch { }
            try { Console.Write(text); } catch { }
            try { Console.ForegroundColor = baseColor; } catch { }
        }

        private static string SignalBar(int pct)
        {
            if (pct < 0) pct = 0; else if (pct > 100) pct = 100;
            int filled = (int)Math.Round(pct / 20.0); // 5 cells
            if (filled > 5) filled = 5;
            var sb = new StringBuilder(5);
            for (int i = 0; i < 5; i++) sb.Append(i < filled ? '█' : '░');
            return sb.ToString();
        }

        private static string Spark(double[] data)
        {
            if (data == null || data.Length == 0) return "";
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            foreach (var d in data)
            {
                if (d < 0) continue; // lost probe marker
                if (d < min) min = d;
                if (d > max) max = d;
            }
            bool haveRange = !double.IsInfinity(min) && !double.IsInfinity(max);
            double span = haveRange ? (max - min) : 0;

            var sb = new StringBuilder(data.Length);
            foreach (var d in data)
            {
                if (d < 0) { sb.Append('_'); continue; } // lost
                int idx;
                if (!haveRange || span <= 0) idx = 0;
                else idx = (int)Math.Round((d - min) / span * (Blocks.Length - 1));
                if (idx < 0) idx = 0; else if (idx >= Blocks.Length) idx = Blocks.Length - 1;
                sb.Append(Blocks[idx]);
            }
            return sb.ToString();
        }

        private static WindowStat FirstWindow(TargetSnapshot ts)
        {
            var ws = ts.Windows;
            return (ws != null && ws.Length > 0) ? ws[0] : null;
        }

        private static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "-";
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Pad(string s, int width)
        {
            s = s ?? "";
            if (s.Length >= width)
            {
                int cut = Math.Max(0, width - 1);
                // Don't split a surrogate pair (would render a broken glyph).
                if (cut > 0 && cut < s.Length && char.IsHighSurrogate(s[cut - 1])) cut--;
                return s.Substring(0, cut).PadRight(width - 1) + " ";
            }
            return s + new string(' ', width - s.Length);
        }

        // Truncate or right-pad to exactly width characters (for fixed-width box content).
        private static string Fit(string s, int width)
        {
            s = s ?? "";
            if (width <= 0) return "";
            if (s.Length > width)
            {
                int cut = width;
                if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
                return s.Substring(0, cut).PadRight(width);
            }
            return s + new string(' ', width - s.Length);
        }

        private static DateTime ToUtc(DateTime t)
        {
            if (t.Kind == DateTimeKind.Utc) return t;
            if (t.Kind == DateTimeKind.Local) return t.ToUniversalTime();
            return DateTime.SpecifyKind(t, DateTimeKind.Utc);
        }

        private static string Local(DateTime utc)
        {
            DateTime local;
            try
            {
                if (utc.Kind == DateTimeKind.Utc) local = utc.ToLocalTime();
                else if (utc.Kind == DateTimeKind.Local) local = utc;
                else local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
            }
            catch { local = utc; }
            return local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }
}
