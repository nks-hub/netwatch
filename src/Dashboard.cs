using System;
using System.Globalization;
using System.Runtime.InteropServices;
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
        private const string Repo = BuildInfo.Repo;
        private const string Author = BuildInfo.Author;

        // Cards span the window up to this many interior columns so a wide console is
        // used, while a hard cap keeps the layout readable on ultra-wide terminals.
        private const int MaxCardWidth = 120;

        // Shared responsive card interior width: fills the window (width-2 for the two
        // border columns) up to MaxCardWidth, clamped to >= 0. Every card and the header
        // box derive their geometry from this so borders/content stay aligned.
        private static int CardInner(int width) => Math.Max(0, Math.Min(width - 2, MaxCardWidth));

        private const int STD_OUTPUT_HANDLE = -11;
        private const uint ENABLE_WRAP_AT_EOL_OUTPUT = 0x0002;
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(IntPtr h, uint mode);

        private static IntPtr _stdOut = IntPtr.Zero;
        private static uint _origMode;
        private static bool _wrapDisabled;

        public static void Init()
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            try { Console.CursorVisible = false; } catch { }

            // Disable console line-wrap: a line wider than the window then clips at the
            // right edge instead of wrapping and scrolling the buffer (which would shift
            // every row and shatter the in-place SetCursorPosition(0,0) redraw). Restore
            // the original mode on exit so the user's shell behaves normally afterwards.
            try
            {
                _stdOut = GetStdHandle(STD_OUTPUT_HANDLE);
                if (_stdOut != IntPtr.Zero && _stdOut != new IntPtr(-1) &&
                    GetConsoleMode(_stdOut, out _origMode) &&
                    (_origMode & ENABLE_WRAP_AT_EOL_OUTPUT) != 0)
                {
                    if (SetConsoleMode(_stdOut, _origMode & ~ENABLE_WRAP_AT_EOL_OUTPUT))
                    {
                        _wrapDisabled = true;
                        AppDomain.CurrentDomain.ProcessExit += (s, e) => RestoreConsole();
                    }
                }
            }
            catch { }
        }

        /// <summary>Restores the console output mode (line-wrap) changed by <see cref="Init"/>.</summary>
        public static void RestoreConsole()
        {
            if (!_wrapDisabled) return;
            try { SetConsoleMode(_stdOut, _origMode); } catch { }
            _wrapDisabled = false;
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

        // Last rendered console size, so a resize can trigger one full clear before
        // the in-place redraw (otherwise a wider/taller previous frame leaves artifacts).
        private static int _lastWidth = -1;
        private static int _lastHeight = -1;

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
                // A 0- or 1-wide/tall window leaves no usable area; skip this frame.
                if (width <= 1 || height <= 1) return;

                // Detect a resize between frames: one full clear wipes any stale wider/
                // taller frame, then re-hide the cursor (Clear can make it reappear).
                if (width != _lastWidth || height != _lastHeight)
                {
                    try { Console.Clear(); } catch { }
                    try { Console.CursorVisible = false; } catch { }
                    _lastWidth = width;
                    _lastHeight = height;
                }

                // Reserve the final column so a full-width line never wraps/scrolls
                // the buffer (which would destroy the SetCursorPosition(0,0) redraw).
                // WriteLinePadded enforces the same (width-1) cap when writing.
                int buildWidth = Math.Max(1, width - 1);

                // Build the frame to the reduced width so the box geometry
                // (inner = buildWidth-2) leaves the reserved column free and the right
                // border is never the char that gets truncated at write time.
                var lines = Build(snap, cfg, startedUtc, buildWidth);

                ConsoleColor baseColor;
                try { baseColor = Console.ForegroundColor; } catch { baseColor = ConsoleColor.Gray; }

                // Never write more rows than fit; the last row stays clear so the final
                // line write can't wrap into a scroll either.
                int max = Math.Max(0, height - 1);
                int count = Math.Min(lines.Count, max);

                // Position the cursor explicitly at the start of EACH row before writing it.
                // With console line-wrap disabled (Init) and each line capped at width-1, the
                // cursor never auto-advances to the next row, so row advancement must NOT rely
                // on auto-wrap — set it per row instead. SetCursorPosition is guarded because the
                // window may be resized/closed mid-draw.
                for (int i = 0; i < count; i++)
                {
                    try { Console.SetCursorPosition(0, i); } catch { }
                    var ln = lines[i];
                    WriteLinePadded(ln.Text, ln.Color, width, baseColor);
                }
                // Clear any leftover rows from a taller previous frame, each at its own row.
                for (int i = count; i < max; i++)
                {
                    try { Console.SetCursorPosition(0, i); } catch { }
                    WriteLinePadded("", baseColor, width, baseColor);
                }

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
            int inner = CardInner(width);
            outp.Add(new Line("╭" + new string('─', inner) + "╮", ConsoleColor.DarkCyan));
            outp.Add(new Line("│ " + Fit("NetWatch v1.0    network & VPN latency diagnostics", inner - 2) + " │", ConsoleColor.Cyan));
            outp.Add(new Line("│ " + Fit("by " + Author + "  ·  " + Repo, inner - 2) + " │", ConsoleColor.DarkGray));
            outp.Add(new Line("╰" + new string('─', inner) + "╯", ConsoleColor.DarkCyan));
            outp.Add(new Line(" up " + upStr + "    " + now + "    log: " + (cfg.LogDir ?? "logs"), def));
            outp.Add(new Line("", def));

            // LINK card (active adapter + Wi-Fi + VPN, merged)
            BuildLinkCard(outp, snap, cfg, width);
            outp.Add(new Line("", def));

            // TARGETS card
            BuildTargetsCard(outp, snap, cfg, width);
            outp.Add(new Line("", def));

            // SPEEDTEST card
            BuildSpeed(outp, snap, cfg, def, width);
            outp.Add(new Line("", def));

            // TRACE / EVENTS card
            BuildTraceEventsCard(outp, snap, width);

            return outp;
        }

        // LINK card — active adapter (+ Wi-Fi signal + throughput) and VPN status, merged.
        private static void BuildLinkCard(System.Collections.Generic.List<Line> outp, Snapshot snap, Config cfg, int width)
        {
            int inner = CardInner(width);
            int cw = Math.Max(0, inner - 2);
            var a = snap.ActiveInterface;
            var v = snap.Vpn;
            string kindWord = a != null ? Display.KindWord(a.Kind) : "—";

            outp.Add(new Line(TitledBorder('╭', '╮', " LINK ", inner), ConsoleColor.DarkCyan));

            if (a == null)
            {
                outp.Add(new Line(Card("(no active link / detecting…)", cw), ConsoleColor.Yellow));
            }
            else
            {
                var r1 = new StringBuilder();
                r1.Append(kindWord);
                string aname = a.Name ?? "";
                if (aname.Length > 0 && aname.IndexOf(kindWord, StringComparison.OrdinalIgnoreCase) < 0)
                    r1.Append(' ').Append(aname);
                if (!string.IsNullOrEmpty(a.IpAddress)) r1.Append("   ip ").Append(a.IpAddress);
                if (a.LinkSpeedBps > 0) r1.Append("   link ").Append(Num(a.LinkSpeedBps / 1e6)).Append(" Mbps");
                outp.Add(new Line(Card(r1.ToString(), cw), ConsoleColor.Cyan));

                if (a.IsWifi)
                {
                    var w = new StringBuilder();
                    w.Append("Wi-Fi  ").Append(string.IsNullOrEmpty(a.Ssid) ? "?" : a.Ssid);
                    w.Append("  [").Append(SignalBar(a.SignalPct)).Append("] ")
                     .Append(a.SignalPct.ToString(CultureInfo.InvariantCulture)).Append('%');
                    w.Append(" (").Append(a.RssiDbm.ToString(CultureInfo.InvariantCulture)).Append(" dBm)");
                    if (a.WifiRxMbps > 0 || a.WifiTxMbps > 0)
                        w.Append("  rx/tx ").Append(Num(a.WifiRxMbps)).Append('/').Append(Num(a.WifiTxMbps));
                    outp.Add(new Line(Card(w.ToString(), cw),
                        a.SignalPct < cfg.WifiWeakSignalPct ? ConsoleColor.Yellow : ConsoleColor.Gray));
                }

                var r2 = new StringBuilder();
                r2.Append("↓ recv ").Append(Num(a.RecvMbps)).Append("   ↑ send ").Append(Num(a.SendMbps)).Append(" Mbps");
                if (a.InErrors > 0 || a.OutErrors > 0)
                    r2.Append("   err in/out ").Append(a.InErrors.ToString(CultureInfo.InvariantCulture))
                      .Append('/').Append(a.OutErrors.ToString(CultureInfo.InvariantCulture));
                outp.Add(new Line(Card(r2.ToString(), cw),
                    (a.InErrors > 0 || a.OutErrors > 0) ? ConsoleColor.Yellow : ConsoleColor.Gray));
            }

            if (v != null && v.Present)
            {
                var vr = new StringBuilder();
                vr.Append("VPN  ").Append(string.IsNullOrEmpty(v.Vendor) ? "VPN" : v.Vendor).Append("  ");
                vr.Append(v.Connected ? "CONNECTED" : "DISCONNECTED");
                if (!string.IsNullOrEmpty(v.IpAddress)) vr.Append("  ip ").Append(v.IpAddress);
                if (!string.IsNullOrEmpty(v.Gateway)) vr.Append("  gw ").Append(v.Gateway);
                else if (v.Connected) vr.Append("  (no tunnel gw - set vpn_targets)");
                outp.Add(new Line(Card(vr.ToString(), cw), v.Connected ? ConsoleColor.Green : ConsoleColor.Red));
            }
            else
            {
                outp.Add(new Line(Card("VPN  none detected", cw), ConsoleColor.DarkGray));
            }

            string status = a != null ? kindWord + " · up" : "no link";
            outp.Add(new Line(TitledBorder('╰', '╯', " " + status + " ", inner), ConsoleColor.DarkCyan));
        }

        // TARGETS card — the aligned ping table wrapped in a titled box.
        private static void BuildTargetsCard(System.Collections.Generic.List<Line> outp, Snapshot snap, Config cfg, int width)
        {
            int inner = CardInner(width);
            int cw = Math.Max(0, inner - 2);

            int w0 = (cfg.WindowsSec != null && cfg.WindowsSec.Length > 0) ? cfg.WindowsSec[0] : 60;
            string avgLabel = w0 == 60 ? "Avg1m" : (w0 % 60 == 0 ? "Avg" + (w0 / 60) + "m" : "Avg" + w0 + "s");

            // Numeric columns stay fixed; the TARGET name column and the sparkline grow with
            // cw so long hostnames show in full and more latency history fits on wide windows.
            const int cLast = 6, cAvg = 6, cMax = 6, cLoss = 5, cDrops = 6, cJit = 5, cP95 = 5;
            const int cNumeric = cLast + cAvg + cMax + cLoss + cDrops + cJit + cP95; // 39
            const int desiredSpark = 16; // sparkline cells we aim to leave when there is room
            int cTarget = Clamp(cw - cNumeric - desiredSpark, 18, 40);
            int sparkRoom = Math.Max(0, cw - cNumeric - cTarget);

            outp.Add(new Line(TitledBorder('╭', '╮', " TARGETS ", inner), ConsoleColor.DarkCyan));

            string header = Pad("TARGET", cTarget) + Pad("Last", cLast) + Pad(avgLabel, cAvg) + Pad("Max", cMax) +
                            Pad("Loss", cLoss) + Pad("Drops", cDrops) + Pad("Jit", cJit) + Pad("P95", cP95) + "spark";
            outp.Add(new Line(Card(header, cw), ConsoleColor.White));

            var targets = snap.Targets ?? new TargetSnapshot[0];
            string worst = null;
            double worstAvg = -1;
            foreach (var ts in targets)
            {
                if (ts == null) continue;
                WindowStat first = ts.First();
                string label = ts.Target ?? "?";
                string last = double.IsNaN(ts.LastMs) ? "-" : Num(ts.LastMs);
                string avg = first != null ? Num(first.AvgMs) : "-";
                string max = ts.SessionMaxMs > 0 ? Num(ts.SessionMaxMs) : "-";
                string loss = first != null ? Num(first.LossPct) : "-";
                string drops = ts.SessionLost.ToString(CultureInfo.InvariantCulture);
                string jit = first != null ? Num(first.JitterMs) : "-";
                string p95 = first != null ? Num(first.P95Ms) : "-";

                var sb = new StringBuilder();
                sb.Append(Pad(label, cTarget)).Append(Pad(last, cLast)).Append(Pad(avg, cAvg)).Append(Pad(max, cMax));
                sb.Append(Pad(loss, cLoss)).Append(Pad(drops, cDrops)).Append(Pad(jit, cJit)).Append(Pad(p95, cP95));
                string spark = Spark(ts.Spark);
                if (spark.Length > sparkRoom) spark = spark.Substring(spark.Length - sparkRoom);
                sb.Append(spark);

                ConsoleColor color = ConsoleColor.Green;
                double lossPct = first != null ? first.LossPct : 0;
                double avgMs = first != null ? first.AvgMs : 0;
                if (!ts.Up || lossPct > cfg.HighLossPct) color = ConsoleColor.Red;
                else if (lossPct > 0 || avgMs > cfg.HighLatencyMs) color = ConsoleColor.Yellow;
                bool emphasize = ts.Kind == TargetKind.Gateway || ts.Kind == TargetKind.Vpn;
                if (emphasize && color == ConsoleColor.Green) color = ConsoleColor.Cyan;
                outp.Add(new Line(Card(sb.ToString(), cw), color));

                if (avgMs > worstAvg) { worstAvg = avgMs; worst = label; }
            }

            string status = targets.Length + (targets.Length == 1 ? " target" : " targets");
            if (worst != null) status += " · worst " + worst;
            outp.Add(new Line(TitledBorder('╰', '╯', " " + status + " ", inner), ConsoleColor.DarkCyan));
        }

        private static void BuildSpeed(System.Collections.Generic.List<Line> outp, Snapshot snap, Config cfg, ConsoleColor def, int width)
        {
            var sp = snap.LastSpeed;
            int inner = CardInner(width); // same responsive geometry as every other card
            int cw = Math.Max(0, inner - 2);                   // content width inside "│ " ... " │"

            if (sp == null || sp.Time == default(DateTime))
            {
                outp.Add(new Line(TitledBorder('╭', '╮', " SPEEDTEST ", inner), ConsoleColor.DarkCyan));
                outp.Add(new Line(Card("not run yet", cw), ConsoleColor.DarkGray));
                outp.Add(new Line(TitledBorder('╰', '╯', " idle ", inner), ConsoleColor.DarkCyan));
                return;
            }
            if (!string.IsNullOrEmpty(sp.Error))
            {
                outp.Add(new Line(TitledBorder('╭', '╮', " SPEEDTEST ", inner), ConsoleColor.DarkCyan));
                outp.Add(new Line(Card("error: " + sp.Error, cw), ConsoleColor.Red));
                outp.Add(new Line(TitledBorder('╰', '╯',
                    " " + sp.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " ", inner),
                    ConsoleColor.DarkCyan));
                return;
            }

            double down = (!double.IsNaN(sp.DownMbps) && sp.DownMbps > 0) ? sp.DownMbps : 0;
            double up = (!double.IsNaN(sp.UpMbps) && sp.UpMbps > 0) ? sp.UpMbps : 0;
            double maxDown = (!double.IsNaN(snap.MaxDownMbps) && snap.MaxDownMbps > 0) ? snap.MaxDownMbps : 0;
            double maxUp = (!double.IsNaN(snap.MaxUpMbps) && snap.MaxUpMbps > 0) ? snap.MaxUpMbps : 0;
            double avgDown = double.IsNaN(snap.AvgDownMbps) ? 0 : snap.AvgDownMbps;
            double avgUp = double.IsNaN(snap.AvgUpMbps) ? 0 : snap.AvgUpMbps;
            double peak = Math.Max(Math.Max(down, up), Math.Max(maxDown, maxUp));
            double scaleMax = NiceCeil(peak);

            string hhmmss = sp.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            // Top border carries the title; bottom border carries the test time.
            outp.Add(new Line(TitledBorder('╭', '╮', " SPEEDTEST ", inner), ConsoleColor.DarkCyan));

            // Down / up rows: label, right-aligned value, then a gauge on the shared scale.
            const int labelW = 8, valW = 12, gap = 2;
            int barCells = Math.Max(4, cw - labelW - valW - gap);
            outp.Add(new Line(Card(Fit("↓ DOWN", labelW) + PadLeft(Num(down) + " Mbps", valW) + "  " +
                                    Bar(down, maxDown, scaleMax, barCells), cw), ConsoleColor.Cyan));
            outp.Add(new Line(Card(Fit("↑ UP", labelW) + PadLeft(Num(up) + " Mbps", valW) + "  " +
                                    Bar(up, maxUp, scaleMax, barCells), cw), ConsoleColor.Green));

            // Dotted-leader scale line (replaces the crude "scale 0───200").
            string scaleLabel = "scale 0 ─ " + Num(scaleMax) + " Mbps";
            int dots = Math.Max(0, cw - scaleLabel.Length - 1);
            outp.Add(new Line(Card(new string('·', dots) + " " + scaleLabel, cw), ConsoleColor.DarkGray));

            // Latency / jitter / server (the whole row recolours when latency is high).
            double lat = double.IsNaN(sp.LatencyMs) ? 0 : sp.LatencyMs;
            double jit = double.IsNaN(sp.JitterMs) ? 0 : sp.JitterMs;
            ConsoleColor latColor = lat >= cfg.HighLatencyMs ? ConsoleColor.Red
                                  : lat >= cfg.HighLatencyMs * 0.4 ? ConsoleColor.Yellow : def;
            var ljs = new StringBuilder();
            ljs.Append("latency  ").Append(Num(lat)).Append(" ms");
            ljs.Append("    jitter  ±").Append(Num(jit)).Append(" ms");
            if (!string.IsNullOrEmpty(sp.Server)) ljs.Append("    server  ").Append(sp.Server);
            outp.Add(new Line(Card(ljs.ToString(), cw), latColor));

            // Session averages, test count and next-test countdown.
            var ag = new StringBuilder();
            ag.Append("avg ↓ ").Append(Num(avgDown)).Append(" / ↑ ").Append(Num(avgUp)).Append(" Mbps");
            if (snap.SpeedCount > 0)
                ag.Append("    ").Append(snap.SpeedCount.ToString(CultureInfo.InvariantCulture))
                  .Append(snap.SpeedCount == 1 ? " test" : " tests");
            if (cfg.SpeedtestEnabled && cfg.SpeedtestIntervalMin > 0)
            {
                DateTime nextUtc = ToUtc(sp.Time).AddMinutes(cfg.SpeedtestIntervalMin);
                TimeSpan rem = nextUtc - DateTime.UtcNow;
                if (rem < TimeSpan.Zero) rem = TimeSpan.Zero;
                ag.Append(" · next in ").Append(((int)rem.TotalMinutes).ToString(CultureInfo.InvariantCulture)).Append('m');
            }
            outp.Add(new Line(Card(ag.ToString(), cw), ConsoleColor.DarkGray));

            outp.Add(new Line(TitledBorder('╰', '╯', " " + hhmmss + " ", inner), ConsoleColor.DarkCyan));
        }

        // A content row inside a card box: "│ " + content fitted to exact width + " │".
        private static string Card(string content, int contentWidth)
        {
            return "│ " + Fit(content, contentWidth) + " │";
        }

        // A box border carrying an inline title, e.g. "╭─ SPEEDTEST ──────╮", total width
        // = inner + 2. Falls back to a plain border when the console is too narrow.
        private static string TitledBorder(char left, char right, string title, int inner)
        {
            if (inner < title.Length + 3)
                return left + new string('─', Math.Max(0, inner)) + right;
            int dashes = inner - 1 - title.Length;
            if (dashes < 0) dashes = 0;
            return left + "─" + title + new string('─', dashes) + right;
        }

        // Right-justify s within width (keep the rightmost chars when longer).
        private static string PadLeft(string s, int width)
        {
            s = s ?? "";
            if (s.Length >= width) return s.Length > width ? s.Substring(s.Length - width) : s;
            return new string(' ', width - s.Length) + s;
        }

        // A gauge of <cells> █/░ cells with a '┊' tick at the session-max position, shown
        // only when the session peak meaningfully exceeds the current value.
        private static string Bar(double value, double sessionMax, double scaleMax, int cells)
        {
            if (cells < 1) return "";
            if (scaleMax <= 0) scaleMax = 1;
            if (value < 0) value = 0;
            int filled = (int)Math.Round(value / scaleMax * cells);
            if (filled < 0) filled = 0; else if (filled > cells) filled = cells;
            int markPos = -1;
            if (sessionMax > value * 1.02 && sessionMax > value + 0.5)
            {
                markPos = (int)Math.Round(sessionMax / scaleMax * cells);
                if (markPos > cells - 1) markPos = cells - 1;
                if (markPos < 0) markPos = 0;
            }
            var bar = new StringBuilder(cells);
            for (int i = 0; i < cells; i++)
            {
                if (i < filled) bar.Append('█');
                else if (i == markPos) bar.Append('┊');
                else bar.Append('░');
            }
            return bar.ToString();
        }

        // Round a positive value up to a readable 1/2/5×10^n ceiling so the shared bar
        // scale stays clean even though the real link capacity is unknown.
        private static double NiceCeil(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) return 1;
            double exp = Math.Floor(Math.Log10(v));
            double pow = Math.Pow(10, exp);
            double frac = v / pow;
            double nice = frac <= 1 ? 1 : frac <= 2 ? 2 : frac <= 5 ? 5 : 10;
            return nice * pow;
        }

        // TRACE / EVENTS card — last traceroute summary plus the most recent diagnostic events.
        private static void BuildTraceEventsCard(System.Collections.Generic.List<Line> outp, Snapshot snap, int width)
        {
            int inner = CardInner(width);
            int cw = Math.Max(0, inner - 2);

            outp.Add(new Line(TitledBorder('╭', '╮', " TRACE / ALERTS ", inner), ConsoleColor.DarkCyan));

            var t = snap.LastTrace;
            string status = "—";
            if (t == null || t.Time == default(DateTime))
            {
                outp.Add(new Line(Card("trace  none yet", cw), ConsoleColor.DarkGray));
            }
            else
            {
                int hops = t.Hops != null ? t.Hops.Length : 0;
                var tr = new StringBuilder();
                tr.Append("trace  ").Append(t.Target ?? "?").Append("  ").Append(hops.ToString(CultureInfo.InvariantCulture)).Append(" hops");
                tr.Append("  ").Append(t.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                tr.Append("  (path ").Append(t.Changed ? "CHANGED" : "stable").Append(')');
                outp.Add(new Line(Card(tr.ToString(), cw), t.Changed ? ConsoleColor.Yellow : ConsoleColor.Gray));
                status = (t.Target ?? "?") + " · " + (t.Changed ? "CHANGED" : "stable");
            }

            var ev = snap.RecentEvents ?? new string[0];
            if (ev.Length == 0)
            {
                outp.Add(new Line(Card("no alerts", cw), ConsoleColor.DarkGray));
            }
            else
            {
                int start = Math.Max(0, ev.Length - 3); // newest 3, never wrap the card
                for (int i = start; i < ev.Length; i++)
                    outp.Add(new Line(Card("! " + ev[i], cw), AlertColor(ev[i])));
            }

            outp.Add(new Line(TitledBorder('╰', '╯', " " + status + " ", inner), ConsoleColor.DarkCyan));
        }

        // Colour an alert line by severity inferred from its text.
        private static ConsoleColor AlertColor(string s)
        {
            if (string.IsNullOrEmpty(s)) return ConsoleColor.DarkGray;
            string t = s.ToLowerInvariant();
            if (t.Contains("unreachable") || t.Contains("packet loss") || t.Contains("disconnected"))
                return ConsoleColor.Red;
            if (t.Contains("weak") || t.Contains("high") || t.Contains("changed") || t.Contains("switch"))
                return ConsoleColor.Yellow;
            if (t.Contains("recovered") || t.Contains("connected"))
                return ConsoleColor.Green;
            return ConsoleColor.Gray;
        }

        // ---- rendering helpers ----

        private static void WriteLinePadded(string text, ConsoleColor color, int width, ConsoleColor baseColor)
        {
            // Pad/truncate to (width-1), never the full width: a line that exactly fills
            // the console width moves the cursor to the next row and SCROLLS the buffer,
            // which destroys the in-place SetCursorPosition(0,0) redraw (most visible while
            // resizing). Leaving the final column empty keeps every write on its own row.
            if (width <= 1) return;
            int target = width - 1;
            text = text ?? "";
            // Truncate to target and pad with spaces so stale chars are cleared.
            if (text.Length > target)
            {
                int cut = target;
                // Don't split a surrogate pair (would render a broken glyph).
                if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
                text = text.Substring(0, cut);
            }
            if (text.Length < target) text = text + new string(' ', target - text.Length);
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

        private static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "-";
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        // Clamp v into [lo, hi] (net48 has no Math.Clamp). Assumes lo <= hi.
        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
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
