using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NetWatch.Report
{
    /// <summary>
    /// Builds a single self-contained HTML report (inline CSS + inline JS + inline SVG,
    /// no external JS/CSS/fonts/CDN) from the daily CSV files NetWatch writes. The page
    /// is a premium dark analytics dashboard: a sticky header, a global time-range
    /// segmented control (5 min / 60 min / 24 h), an "at a glance" row of summary stat
    /// cards, and a responsive grid of metric cards. For every metric the three windows
    /// (5 minutes / 60 minutes / 24 hours ending at <c>nowLocal</c>) are rendered, and a
    /// tiny inline script shows only the selected range, persisting the choice in
    /// localStorage so the 60-second meta-refresh keeps it.
    ///
    /// The data is read via <see cref="CsvHistory"/> and rendered via
    /// <see cref="SvgChart"/>. Public entry points never throw.
    /// </summary>
    public static class HtmlReport
    {
        // ---- chart geometry / bucketing tuning ----
        // Bigger viewBox so each chart is large and readable in the 2-per-row grid.
        private const int ChartWidth = 860;
        private const int ChartHeight = 320;
        private const int TargetPoints = 150; // aim ~120-180 points per chart

        // ---- the three time windows (seconds) ----
        private const int Win5Min = 5 * 60;
        private const int Win60Min = 60 * 60;
        private const int Win24H = 24 * 60 * 60;

        // ---- premium dark theme palette ----
        private const string BgColor = "#0b0e14";       // page background
        private const string PanelColor = "#151a23";    // card surface
        private const string PanelColor2 = "#11161f";   // inset / chart well
        private const string PanelBorder = "#222a37";   // subtle border
        private const string TextColor = "#e5e7eb";     // bright text
        private const string MutedColor = "#94a3b8";    // muted text
        private const string HeadingColor = "#e5e7eb";  // headings

        // ---- accents ----
        private const string AccentCyan = "#38bdf8";
        private const string AccentGreen = "#4ade80";
        private const string AccentAmber = "#fbbf24";
        private const string AccentRed = "#f87171";
        private const string AccentViolet = "#a78bfa";

        // Distinct line colours assigned to ping targets in order, then cycled.
        private static readonly string[] TargetColors =
        {
            "#38bdf8", // cyan
            "#4ade80", // green
            "#a78bfa", // violet
            "#fbbf24", // amber
            "#f472b6", // pink
            "#34d399", // teal
            "#60a5fa", // blue
            "#f87171", // red
            "#facc15", // yellow
            "#fb923c", // orange
        };

        private const string DownColor = "#38bdf8";  // download / recv
        private const string UpColor = "#4ade80";    // upload / send
        private const string LatencyColor = "#fbbf24";
        private const string SignalColor = "#22d3ee";

        /// <summary>
        /// Builds the full HTML page string for the last 24h of data ending at
        /// <paramref name="nowLocal"/>. Never throws; on failure returns a styled
        /// HTML error page.
        /// </summary>
        public static string Build(string logDir, DateTime nowLocal)
        {
            try
            {
                DateTime since = nowLocal.AddHours(-24);

                List<PingRecord> pings = SafeReadPings(logDir, since);
                List<SpeedRecord> speeds = SafeReadSpeed(logDir, since);
                List<IfaceRecord> ifaces = SafeReadIface(logDir, since);

                var sb = new StringBuilder(96 * 1024);
                WriteHead(sb, nowLocal);

                // Sticky header + global range control + summary cards.
                WriteHeader(sb, nowLocal);
                WriteSummary(sb, pings, speeds, ifaces, nowLocal);

                // The metric grid. Every chart for every range is rendered; the active
                // range is shown via the .range-* class set on #ranged by the inline JS.
                sb.Append("<main class=\"grid range-60m\" id=\"ranged\">");

                // LATENCY (ms) — one series per ping target, averaged per bucket.
                AppendMetricSection(sb, "Latency", "ms",
                    BuildPingSeriesFactory(pings, PingMetric.Latency), nowLocal,
                    "Round-trip time per target, averaged per bucket.");

                // PACKET LOSS (%) — failed/total per bucket per target.
                AppendMetricSection(sb, "Packet loss", "%",
                    BuildPingSeriesFactory(pings, PingMetric.Loss), nowLocal,
                    "Share of probes lost per bucket per target.", 0, 100);

                // JITTER (ms) — mean absolute consecutive RTT difference per bucket.
                AppendMetricSection(sb, "Jitter", "ms",
                    BuildPingSeriesFactory(pings, PingMetric.Jitter), nowLocal,
                    "Round-trip variability (mean abs. consecutive delta) per bucket.");

                // SPEED (Mbps) — download + upload from the speed test.
                AppendMetricSection(sb, "Speed", "Mbps",
                    BuildSpeedSeriesFactory(speeds, speedLatency: false), nowLocal,
                    "Speed-test download and upload throughput.");

                // SPEED-TEST LATENCY (ms) — small companion chart.
                AppendMetricSection(sb, "Speed latency", "ms",
                    BuildSpeedSeriesFactory(speeds, speedLatency: true), nowLocal,
                    "Idle latency reported by the speed test.");

                // THROUGHPUT (Mbps) — recv + send of the active adapter.
                AppendMetricSection(sb, "Throughput", "Mbps",
                    BuildIfaceThroughputFactory(ifaces), nowLocal,
                    "Live receive / send throughput of the active adapter.");

                // WI-FI SIGNAL (%) — only when there is Wi-Fi data.
                if (HasWifiData(ifaces))
                {
                    AppendMetricSection(sb, "Wi-Fi signal", "%",
                        BuildWifiSignalFactory(ifaces), nowLocal,
                        "Signal strength of the active wireless adapter.", 0, 100);
                }

                sb.Append("</main>");

                WriteFoot(sb);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ErrorPage(ex);
            }
        }

        /// <summary>
        /// Builds the report and writes it as UTF-8 to <paramref name="outPath"/>,
        /// creating the parent directory. Returns the absolute path on success, or
        /// <c>null</c> on failure. Never throws.
        /// </summary>
        public static string Write(string logDir, string outPath, DateTime nowLocal)
        {
            try
            {
                if (string.IsNullOrEmpty(outPath)) return null;
                string html = Build(logDir, nowLocal);

                string full = Path.GetFullPath(outPath);
                string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var enc = new UTF8Encoding(false);
                // Atomic write: render to a temp file then swap it into place, so a browser
                // auto-refreshing report.html can never read a half-written file (which would
                // blank/reset the charts). Falls back to a plain write if the swap fails.
                try
                {
                    string tmp = full + ".tmp";
                    File.WriteAllText(tmp, html, enc);
                    if (File.Exists(full)) File.Replace(tmp, full, null);
                    else File.Move(tmp, full);
                }
                catch
                {
                    File.WriteAllText(full, html, enc);
                }
                return full;
            }
            catch
            {
                return null;
            }
        }

        // =====================================================================
        // Series factories — given a window (from..to, bucketSec) produce the
        // ChartSeries list for one chart. They close over the parsed records so
        // the same data is re-bucketed cheaply for each of the three windows.
        // =====================================================================

        private delegate List<ChartSeries> SeriesFactory(DateTime from, DateTime to, int bucketSec);

        private enum PingMetric { Latency, Loss, Jitter }

        private static SeriesFactory BuildPingSeriesFactory(List<PingRecord> pings, PingMetric metric)
        {
            // Stable target ordering by first appearance so colours stay consistent
            // across all three windows and across the page.
            var order = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in pings)
            {
                string t = p.Target ?? "";
                if (seen.Add(t)) order.Add(t);
            }

            return (from, to, bucketSec) =>
            {
                var series = new List<ChartSeries>();
                if (order.Count == 0) return series;

                int n = BucketCount(from, to, bucketSec);
                if (n <= 0) return series;

                int ti = 0;
                foreach (var target in order)
                {
                    var sums = new double[n];
                    var counts = new int[n];
                    var fails = new int[n];
                    var rttLists = (metric == PingMetric.Jitter) ? new List<double>[n] : null;

                    foreach (var p in pings)
                    {
                        if (!string.Equals(p.Target ?? "", target, StringComparison.OrdinalIgnoreCase)) continue;
                        int b = BucketIndex(p.Time, from, bucketSec, n);
                        if (b < 0) continue;

                        if (metric == PingMetric.Loss)
                        {
                            counts[b]++;
                            if (!p.Success) fails[b]++;
                        }
                        else
                        {
                            if (!p.Success || double.IsNaN(p.RttMs)) continue;
                            if (metric == PingMetric.Latency)
                            {
                                sums[b] += p.RttMs;
                                counts[b]++;
                            }
                            else // Jitter
                            {
                                (rttLists[b] ?? (rttLists[b] = new List<double>())).Add(p.RttMs);
                            }
                        }
                    }

                    var points = new List<ChartPoint>(n);
                    for (int i = 0; i < n; i++)
                    {
                        DateTime t = BucketTime(from, bucketSec, i);
                        double value = 0;
                        bool missing;

                        if (metric == PingMetric.Loss)
                        {
                            if (counts[i] == 0) { missing = true; }
                            else { value = 100.0 * fails[i] / counts[i]; missing = false; }
                        }
                        else if (metric == PingMetric.Latency)
                        {
                            if (counts[i] == 0) { missing = true; }
                            else { value = sums[i] / counts[i]; missing = false; }
                        }
                        else // Jitter
                        {
                            var list = rttLists[i];
                            if (list == null || list.Count < 2) { missing = true; }
                            else { value = MeanAbsDelta(list); missing = false; }
                        }

                        points.Add(new ChartPoint { Time = t, Value = value, Missing = missing });
                    }

                    series.Add(new ChartSeries
                    {
                        Label = target.Length == 0 ? "(unnamed)" : target,
                        Color = TargetColors[ti % TargetColors.Length],
                        Points = points,
                    });
                    ti++;
                }

                return series;
            };
        }

        private static SeriesFactory BuildSpeedSeriesFactory(List<SpeedRecord> speeds, bool speedLatency)
        {
            return (from, to, bucketSec) =>
            {
                var series = new List<ChartSeries>();
                int n = BucketCount(from, to, bucketSec);
                if (n <= 0) return series;

                if (speedLatency)
                {
                    series.Add(BuildSpeedLine(speeds, from, bucketSec, n, "Latency", LatencyColor, r => r.LatencyMs));
                }
                else
                {
                    series.Add(BuildSpeedLine(speeds, from, bucketSec, n, "Download", DownColor, r => r.DownMbps));
                    series.Add(BuildSpeedLine(speeds, from, bucketSec, n, "Upload", UpColor, r => r.UpMbps));
                }
                return series;
            };
        }

        private static ChartSeries BuildSpeedLine(List<SpeedRecord> speeds, DateTime from, int bucketSec, int n,
            string label, string color, Func<SpeedRecord, double> selector)
        {
            var sums = new double[n];
            var counts = new int[n];
            foreach (var r in speeds)
            {
                int b = BucketIndex(r.Time, from, bucketSec, n);
                if (b < 0) continue;
                double v = selector(r);
                if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) continue;
                sums[b] += v;
                counts[b]++;
            }
            return new ChartSeries { Label = label, Color = color, Points = AvgPoints(sums, counts, from, bucketSec, n) };
        }

        private static SeriesFactory BuildIfaceThroughputFactory(List<IfaceRecord> ifaces)
        {
            return (from, to, bucketSec) =>
            {
                var series = new List<ChartSeries>();
                int n = BucketCount(from, to, bucketSec);
                if (n <= 0) return series;

                series.Add(BuildIfaceLine(ifaces, from, bucketSec, n, "Receive", DownColor, r => r.RecvMbps));
                series.Add(BuildIfaceLine(ifaces, from, bucketSec, n, "Send", UpColor, r => r.SendMbps));
                return series;
            };
        }

        private static ChartSeries BuildIfaceLine(List<IfaceRecord> ifaces, DateTime from, int bucketSec, int n,
            string label, string color, Func<IfaceRecord, double> selector)
        {
            var sums = new double[n];
            var counts = new int[n];
            foreach (var r in ifaces)
            {
                int b = BucketIndex(r.Time, from, bucketSec, n);
                if (b < 0) continue;
                double v = selector(r);
                if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) continue;
                sums[b] += v;
                counts[b]++;
            }
            return new ChartSeries { Label = label, Color = color, Points = AvgPoints(sums, counts, from, bucketSec, n) };
        }

        private static SeriesFactory BuildWifiSignalFactory(List<IfaceRecord> ifaces)
        {
            return (from, to, bucketSec) =>
            {
                var series = new List<ChartSeries>();
                int n = BucketCount(from, to, bucketSec);
                if (n <= 0) return series;

                var sums = new double[n];
                var counts = new int[n];
                foreach (var r in ifaces)
                {
                    if (!r.IsWifi) continue;
                    int b = BucketIndex(r.Time, from, bucketSec, n);
                    if (b < 0) continue;
                    int s = r.SignalPct;
                    if (s < 0) continue;
                    sums[b] += s;
                    counts[b]++;
                }
                series.Add(new ChartSeries
                {
                    Label = "Signal",
                    Color = SignalColor,
                    Points = AvgPoints(sums, counts, from, bucketSec, n),
                });
                return series;
            };
        }

        // =====================================================================
        // Metric section assembly (one card in the responsive grid)
        // =====================================================================

        private static void AppendMetricSection(StringBuilder sb, string title, string yUnit,
            SeriesFactory factory, DateTime nowLocal, string description)
        {
            AppendMetricSection(sb, title, yUnit, factory, nowLocal, description, double.NaN, double.NaN);
        }

        private static void AppendMetricSection(StringBuilder sb, string title, string yUnit,
            SeriesFactory factory, DateTime nowLocal, string description, double yMin, double yMax)
        {
            // Render the three windows up front; capture the series so legend/footer
            // can reflect the data actually plotted in each range.
            WindowRender w5 = RenderWindow(title, yUnit, factory, nowLocal, Win5Min, yMin, yMax);
            WindowRender w60 = RenderWindow(title, yUnit, factory, nowLocal, Win60Min, yMin, yMax);
            WindowRender w24 = RenderWindow(title, yUnit, factory, nowLocal, Win24H, yMin, yMax);

            sb.Append("<section class=\"card metric\">");

            sb.Append("<div class=\"card-head\">");
            sb.Append("<h2>").Append(Esc(title))
              .Append(" <span class=\"unit\">").Append(Esc(yUnit)).Append("</span></h2>");
            if (!string.IsNullOrEmpty(description))
                sb.Append("<p class=\"desc\">").Append(Esc(description)).Append("</p>");
            sb.Append("</div>");

            // The legend is identical across windows (same series), so show it once.
            AppendLegend(sb, w60.Series);

            // Three stacked chart wells; only the active range is visible via CSS.
            sb.Append("<div class=\"chart-stack\">");
            AppendChartWell(sb, "c5m", w5);
            AppendChartWell(sb, "c60m", w60);
            AppendChartWell(sb, "c24h", w24);
            sb.Append("</div>");

            // A per-range min / avg / max footer (only the active one is shown).
            sb.Append("<div class=\"foot-stack\">");
            AppendFooter(sb, "c5m", w5, yUnit);
            AppendFooter(sb, "c60m", w60, yUnit);
            AppendFooter(sb, "c24h", w24, yUnit);
            sb.Append("</div>");

            sb.Append("</section>");
        }

        // One rendered window: its SVG markup plus the series it was built from.
        private sealed class WindowRender
        {
            public string Svg;
            public List<ChartSeries> Series;
        }

        private static WindowRender RenderWindow(string title, string yUnit, SeriesFactory factory,
            DateTime nowLocal, int windowSec, double yMin, double yMax)
        {
            DateTime from = nowLocal.AddSeconds(-windowSec);
            int bucketSec = BucketSec(windowSec);
            List<ChartSeries> series;
            try { series = factory(from, nowLocal, bucketSec) ?? new List<ChartSeries>(); }
            catch { series = new List<ChartSeries>(); }

            var spec = new ChartSpec
            {
                Title = "", // header above the chart already carries the title
                YUnit = yUnit,
                Width = ChartWidth,
                Height = ChartHeight,
                FromTime = from,
                ToTime = nowLocal,
                YMin = yMin,
                YMax = yMax,
                Series = series,
            };

            string svg;
            try { svg = SvgChart.Render(spec) ?? ""; }
            catch { svg = ""; }

            return new WindowRender { Svg = svg, Series = series };
        }

        private static void AppendChartWell(StringBuilder sb, string cls, WindowRender w)
        {
            sb.Append("<div class=\"chart ").Append(cls).Append("\">");
            if (HasAnyData(w.Series))
                sb.Append("<div class=\"svgwrap\">").Append(w.Svg).Append("</div>");
            else
                sb.Append("<div class=\"nodata\">No data in this range</div>");
            sb.Append("</div>");
        }

        private static void AppendLegend(StringBuilder sb, List<ChartSeries> series)
        {
            if (series == null || series.Count == 0) return;
            sb.Append("<div class=\"legend\">");
            foreach (var s in series)
            {
                if (s == null) continue;
                // data-nw-toggle carries the series label so the JS can click-toggle /
                // hover-emphasise the matching g.nw-series across every chart in the card.
                sb.Append("<span class=\"li\" role=\"button\" tabindex=\"0\" data-nw-toggle=\"")
                  .Append(Esc(s.Label ?? "")).Append("\">");
                sb.Append("<i style=\"background:").Append(Esc(s.Color ?? "#888")).Append("\"></i>");
                sb.Append(Esc(s.Label ?? ""));
                sb.Append("</span>");
            }
            sb.Append("</div>");
        }

        // Per-range footer: min / avg / max (last for single-series adapters/speed).
        private static void AppendFooter(StringBuilder sb, string cls, WindowRender w, string unit)
        {
            sb.Append("<div class=\"foot ").Append(cls).Append("\">");

            bool any = false;
            if (w.Series != null)
            {
                foreach (var s in w.Series)
                {
                    Stats st = Aggregate(s);
                    if (!st.HasData) continue;
                    any = true;
                    sb.Append("<div class=\"foot-row\">");
                    sb.Append("<span class=\"fl\"><i style=\"background:")
                      .Append(Esc(s.Color ?? "#888")).Append("\"></i>")
                      .Append(Esc(s.Label ?? "")).Append("</span>");
                    sb.Append("<span class=\"fv\">")
                      .Append("<b>").Append(Esc(Fmt(st.Last))).Append("</b> now")
                      .Append("<span class=\"sep\">·</span>min ").Append(Esc(Fmt(st.Min)))
                      .Append("<span class=\"sep\">·</span>avg ").Append(Esc(Fmt(st.Avg)))
                      .Append("<span class=\"sep\">·</span>max ").Append(Esc(Fmt(st.Max)))
                      .Append(' ').Append(Esc(unit))
                      .Append("</span>");
                    sb.Append("</div>");
                }
            }
            if (!any)
                sb.Append("<div class=\"foot-row muted\">No samples in this range.</div>");

            sb.Append("</div>");
        }

        // =====================================================================
        // Summary stat cards ("at a glance" current state)
        // =====================================================================

        private static void WriteSummary(StringBuilder sb, List<PingRecord> pings,
            List<SpeedRecord> speeds, List<IfaceRecord> ifaces, DateTime nowLocal)
        {
            sb.Append("<section class=\"summary\">");

            // One card per ping target: latest + avg RTT and loss% over the last hour.
            foreach (var t in PingTargetsInOrder(pings))
                AppendTargetCard(sb, pings, t, nowLocal);

            AppendSpeedCard(sb, speeds);
            AppendLinkCard(sb, ifaces);

            sb.Append("</section>");
        }

        private static List<string> PingTargetsInOrder(List<PingRecord> pings)
        {
            var order = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in pings)
            {
                string t = p.Target ?? "";
                if (seen.Add(t)) order.Add(t);
            }
            return order;
        }

        // Health-coloured card for one ping target. Uses the last sample for the big
        // number and the last hour for avg + loss context.
        private static void AppendTargetCard(StringBuilder sb, List<PingRecord> pings, string target, DateTime nowLocal)
        {
            DateTime since = nowLocal.AddSeconds(-Win60Min);

            bool haveLast = false;
            double lastRtt = 0;
            bool lastSuccess = false;
            DateTime lastTime = DateTime.MinValue;

            double sum = 0; int okCount = 0; int total = 0; int fails = 0;
            foreach (var p in pings)
            {
                if (!string.Equals(p.Target ?? "", target, StringComparison.OrdinalIgnoreCase)) continue;

                // Latest sample overall (drives the "down" state and the big RTT).
                if (!haveLast || p.Time >= lastTime)
                {
                    haveLast = true; lastTime = p.Time; lastSuccess = p.Success; lastRtt = p.RttMs;
                }

                if (p.Time < since) continue;
                total++;
                if (p.Success) { sum += p.RttMs; okCount++; }
                else fails++;
            }

            double avg = okCount > 0 ? sum / okCount : double.NaN;
            double loss = total > 0 ? 100.0 * fails / total : double.NaN;

            // Health: red when last probe failed; amber on elevated RTT/loss; else green.
            string accent = AccentGreen;
            string state = "OK";
            if (!lastSuccess && haveLast) { accent = AccentRed; state = "DOWN"; }
            else if ((!double.IsNaN(loss) && loss >= 5) || (!double.IsNaN(avg) && avg >= 150))
            { accent = AccentAmber; state = "ELEVATED"; }

            string big = (haveLast && lastSuccess) ? Fmt(lastRtt) + " ms"
                        : (haveLast ? "—" : "n/a");

            sb.Append("<div class=\"stat\" style=\"--accent:").Append(accent).Append("\">");
            sb.Append("<div class=\"stat-top\"><span class=\"dot\"></span>")
              .Append("<span class=\"stat-label\">").Append(Esc(NiceTarget(target))).Append("</span>")
              .Append("<span class=\"badge\">").Append(Esc(state)).Append("</span></div>");
            sb.Append("<div class=\"stat-big\">").Append(Esc(big)).Append("</div>");
            sb.Append("<div class=\"stat-sub\">avg ")
              .Append(Esc(double.IsNaN(avg) ? "—" : Fmt(avg) + " ms"))
              .Append("<span class=\"sep\">·</span>loss ")
              .Append(Esc(double.IsNaN(loss) ? "—" : Fmt(loss) + "%"))
              .Append("</div>");
            sb.Append("</div>");
        }

        private static void AppendSpeedCard(StringBuilder sb, List<SpeedRecord> speeds)
        {
            SpeedRecord last = default(SpeedRecord);
            bool have = false;
            foreach (var r in speeds)
            {
                if (!have || r.Time >= last.Time) { last = r; have = true; }
            }

            string accent = AccentCyan;
            string down = "—", up = "—", lat = "—";
            if (have)
            {
                if (!double.IsNaN(last.DownMbps) && last.DownMbps >= 0) down = Fmt(last.DownMbps);
                if (!double.IsNaN(last.UpMbps) && last.UpMbps >= 0) up = Fmt(last.UpMbps);
                if (!double.IsNaN(last.LatencyMs) && last.LatencyMs >= 0) lat = Fmt(last.LatencyMs);
            }

            sb.Append("<div class=\"stat\" style=\"--accent:").Append(accent).Append("\">");
            sb.Append("<div class=\"stat-top\"><span class=\"dot\"></span>")
              .Append("<span class=\"stat-label\">Speed test</span>");
            if (have)
                sb.Append("<span class=\"badge\">")
                  .Append(Esc(last.Time.ToString("HH:mm", CultureInfo.InvariantCulture))).Append("</span>");
            sb.Append("</div>");
            sb.Append("<div class=\"stat-big\">")
              .Append("<span class=\"dl\">↓ ").Append(Esc(down)).Append("</span> ")
              .Append("<span class=\"ul\">↑ ").Append(Esc(up)).Append("</span>")
              .Append("</div>");
            sb.Append("<div class=\"stat-sub\">Mbps down / up")
              .Append("<span class=\"sep\">·</span>latency ")
              .Append(Esc(lat == "—" ? "—" : lat + " ms"))
              .Append("</div>");
            sb.Append("</div>");
        }

        private static void AppendLinkCard(StringBuilder sb, List<IfaceRecord> ifaces)
        {
            IfaceRecord last = default(IfaceRecord);
            bool have = false;
            foreach (var r in ifaces)
            {
                if (!have || r.Time >= last.Time) { last = r; have = true; }
            }

            string accent = AccentGreen;
            string name = have ? (string.IsNullOrEmpty(last.Name) ? "Adapter" : last.Name) : "—";
            string recv = "—", send = "—", extra = "";
            if (have)
            {
                if (!double.IsNaN(last.RecvMbps) && last.RecvMbps >= 0) recv = Fmt(last.RecvMbps);
                if (!double.IsNaN(last.SendMbps) && last.SendMbps >= 0) send = Fmt(last.SendMbps);
                if (last.IsWifi && last.SignalPct >= 0) extra = "Wi-Fi " + last.SignalPct + "%";
                else extra = last.IsWifi ? "Wi-Fi" : "Wired";
            }

            sb.Append("<div class=\"stat\" style=\"--accent:").Append(accent).Append("\">");
            sb.Append("<div class=\"stat-top\"><span class=\"dot\"></span>")
              .Append("<span class=\"stat-label\">Active link</span>");
            if (have)
                sb.Append("<span class=\"badge\">").Append(Esc(extra)).Append("</span>");
            sb.Append("</div>");
            sb.Append("<div class=\"stat-big stat-name\">").Append(Esc(name)).Append("</div>");
            sb.Append("<div class=\"stat-sub\">↓ ").Append(Esc(recv))
              .Append("<span class=\"sep\">·</span>↑ ").Append(Esc(send))
              .Append(" Mbps</div>");
            sb.Append("</div>");
        }

        // =====================================================================
        // HTML scaffolding
        // =====================================================================

        private static void WriteHead(StringBuilder sb, DateTime nowLocal)
        {
            string machine;
            try { machine = Environment.MachineName; } catch { machine = "?"; }

            sb.Append("<!doctype html>");
            sb.Append("<html lang=\"en\"><head>");
            sb.Append("<meta charset=\"utf-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append("<meta name=\"color-scheme\" content=\"dark\">");
            // Auto-reload so an open tab shows fresh data while the app keeps rewriting
            // report.html in the background (matches the default report interval).
            sb.Append("<meta http-equiv=\"refresh\" content=\"60\">");
            sb.Append("<title>NetWatch — ").Append(Esc(machine)).Append("</title>");
            sb.Append("<style>").Append(Css()).Append("</style>");
            sb.Append("</head><body>");
        }

        private static void WriteHeader(StringBuilder sb, DateTime nowLocal)
        {
            string machine;
            try { machine = Environment.MachineName; } catch { machine = "?"; }
            string generated = nowLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            sb.Append("<header class=\"topbar\">");

            // Left: wordmark + subtitle.
            sb.Append("<div class=\"brand\">");
            sb.Append("<div class=\"logo\">").Append(SvgLogo()).Append("</div>");
            sb.Append("<div class=\"brand-txt\"><div class=\"wordmark\">NetWatch</div>")
              .Append("<div class=\"tagline\">network &amp; VPN diagnostics report</div></div>");
            sb.Append("</div>");

            // Center: the global time-range segmented control.
            sb.Append("<nav class=\"segmented\" role=\"tablist\" aria-label=\"Time range\">");
            AppendRangeButton(sb, "5m", "5 min");
            AppendRangeButton(sb, "60m", "60 min");
            AppendRangeButton(sb, "24h", "24 h");
            sb.Append("</nav>");

            // Right: machine, generated-at, auto-refresh pill.
            sb.Append("<div class=\"head-meta\">");
            sb.Append("<span class=\"hm\"><span class=\"hm-k\">Machine</span><span class=\"hm-v\">")
              .Append(Esc(machine)).Append("</span></span>");
            sb.Append("<span class=\"hm\"><span class=\"hm-k\">Generated</span><span class=\"hm-v mono\">")
              .Append(Esc(generated)).Append("</span></span>");
            sb.Append("<span class=\"pill\"><span class=\"pulse\"></span>auto-refresh 60s</span>");
            sb.Append("</div>");

            sb.Append("</header>");
        }

        private static void AppendRangeButton(StringBuilder sb, string key, string label)
        {
            sb.Append("<button type=\"button\" class=\"seg\" data-range=\"").Append(key)
              .Append("\" role=\"tab\">").Append(Esc(label)).Append("</button>");
        }

        private static void WriteFoot(StringBuilder sb)
        {
            sb.Append("<footer class=\"page-footer\">Generated by NetWatch — self-contained offline report.</footer>");
            sb.Append("<script>").Append(Js()).Append("</script>");
            sb.Append("</body></html>");
        }

        // Tiny inline SVG mark for the wordmark (no external assets).
        private static string SvgLogo()
        {
            return
                "<svg viewBox=\"0 0 24 24\" width=\"22\" height=\"22\" fill=\"none\" " +
                "stroke=\"#38bdf8\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\">" +
                "<path d=\"M2 14 L7 9 L11 13 L16 6 L22 11\"/>" +
                "<circle cx=\"16\" cy=\"6\" r=\"1.6\" fill=\"#38bdf8\" stroke=\"none\"/></svg>";
        }

        // =====================================================================
        // Inline JS: global range toggle + localStorage persistence (restored on
        // every load so the 60s meta-refresh keeps the user's selected range).
        // =====================================================================

        private static string Js()
        {
            // One vanilla-JS IIFE, no external libraries. Two responsibilities:
            //   (1) the global time-range segmented control (+ localStorage), and
            //   (2) full chart interactivity driven entirely by the data-* contract
            //       that SvgChart emits (svg.nw-chart with data-l/t/w/h, data-xfrom/
            //       data-xto, data-ymin/data-ymax, data-unit; and g.nw-series with
            //       data-label/data-color/data-points="epochMs,value ...").
            // Re-runs cleanly on every load, so the 60s meta-refresh keeps it wired.
            return
                "(function(){" +

                // ---------- (1) global range toggle ----------
                "var KEY='netwatch.range';" +
                "var VALID={'5m':1,'60m':1,'24h':1};" +
                "var ranged=document.getElementById('ranged');" +
                "var segs=document.querySelectorAll('.seg');" +
                "function apply(r){" +
                "if(!VALID[r])r='60m';" +
                "if(ranged){ranged.className='grid range-'+r;}" +
                "for(var i=0;i<segs.length;i++){" +
                "var on=segs[i].getAttribute('data-range')===r;" +
                "segs[i].classList.toggle('active',on);" +
                "segs[i].setAttribute('aria-selected',on?'true':'false');}" +
                "}" +
                "function loadR(){var r='60m';try{var s=localStorage.getItem(KEY);if(s&&VALID[s])r=s;}catch(e){}return r;}" +
                "function saveR(r){try{localStorage.setItem(KEY,r);}catch(e){}}" +
                "for(var i=0;i<segs.length;i++){(function(b){" +
                "b.addEventListener('click',function(){var r=b.getAttribute('data-range');apply(r);saveR(r);});" +
                "})(segs[i]);}" +
                "apply(loadR());" +

                // ---------- (2) chart interactivity ----------
                "var SVGNS='http://www.w3.org/2000/svg';" +
                // Shared tooltip div: create once, reuse for every chart.
                "var tip=document.createElement('div');tip.className='nw-tip';" +
                "document.body.appendChild(tip);" +

                // Parse a series' data-points into a sorted [{t,v}] array (cached on the <g>).
                "function pts(g){" +
                "if(g._nw)return g._nw;" +
                "var raw=g.getAttribute('data-points')||'';var out=[];" +
                "var toks=raw.split(' ');" +
                "for(var i=0;i<toks.length;i++){var s=toks[i];if(!s)continue;" +
                "var c=s.indexOf(',');if(c<0)continue;" +
                "var t=parseFloat(s.substring(0,c));var v=parseFloat(s.substring(c+1));" +
                "if(t===t&&v===v)out.push({t:t,v:v});}" +  // t===t filters NaN
                "out.sort(function(a,b){return a.t-b.t;});" +
                "g._nw=out;return out;}" +

                // Nearest sample (by |t-cursorT|) in a sorted point array, or null.
                "function nearest(a,t){if(!a.length)return null;" +
                "var lo=0,hi=a.length-1;" +
                "while(lo<hi){var m=(lo+hi)>>1;if(a[m].t<t)lo=m+1;else hi=m;}" +
                "var best=a[lo];var bd=Math.abs(best.t-t);" +
                "if(lo>0){var d=Math.abs(a[lo-1].t-t);if(d<bd){best=a[lo-1];bd=d;}}" +
                "return best;}" +

                // Format a value with ~2-3 significant digits + the chart unit.
                "function fmtVal(v,unit){var a=Math.abs(v),s;" +
                "if(a>=1000)s=v.toFixed(0);" +
                "else if(a>=100)s=v.toFixed(0);" +
                "else if(a>=10)s=v.toFixed(1);" +
                "else s=v.toFixed(2);" +
                "return unit?(s+' '+unit):s;}" +

                // HH:mm:ss from epoch-ms using UTC getters (the encoded wall-clock).
                "function fmtTime(ms){var d=new Date(ms);" +
                "function p(n){return(n<10?'0':'')+n;}" +
                "return p(d.getUTCHours())+':'+p(d.getUTCMinutes())+':'+p(d.getUTCSeconds());}" +

                // Wire one svg.nw-chart: crosshair + dots + tooltip on hover.
                "function wireChart(svg){" +
                "if(svg._nwWired)return;svg._nwWired=1;" +
                "var L=parseFloat(svg.getAttribute('data-l'));" +
                "var T=parseFloat(svg.getAttribute('data-t'));" +
                "var W=parseFloat(svg.getAttribute('data-w'));" +
                "var H=parseFloat(svg.getAttribute('data-h'));" +
                "var XF=parseFloat(svg.getAttribute('data-xfrom'));" +
                "var XT=parseFloat(svg.getAttribute('data-xto'));" +
                "var YMIN=parseFloat(svg.getAttribute('data-ymin'));" +
                "var YMAX=parseFloat(svg.getAttribute('data-ymax'));" +
                "var unit=svg.getAttribute('data-unit')||'';" +
                "if(!(W>0)||!(H>0)||!(XT>XF)||!(YMAX>YMIN))return;" +
                // Map data -> svg user units exactly as the chart was drawn.
                "function xOf(t){return L+(t-XF)/(XT-XF)*W;}" +
                "function yOf(v){return T+(1-(v-YMIN)/(YMAX-YMIN))*H;}" +
                // Overlay group (crosshair line + dots) appended last so it sits on top.
                "var ov=document.createElementNS(SVGNS,'g');ov.setAttribute('pointer-events','none');" +
                "svg.appendChild(ov);" +
                "var cross=document.createElementNS(SVGNS,'line');cross.setAttribute('class','nw-cross');" +
                "cross.setAttribute('y1',T);cross.setAttribute('y2',T+H);" +
                "var dots=[];" +
                "function clear(){ov.style.display='none';tip.classList.remove('nw-show');}" +
                "function ensureDots(n){while(dots.length<n){" +
                "var d=document.createElementNS(SVGNS,'circle');d.setAttribute('class','nw-dot');" +
                "d.setAttribute('r','3.4');d.setAttribute('stroke','#0b0e14');d.setAttribute('stroke-width','1.5');" +
                "ov.appendChild(d);dots.push(d);}}" +
                "function move(ev){" +
                "var ctm=svg.getScreenCTM();if(!ctm)return;" +
                "var pt=svg.createSVGPoint();pt.x=ev.clientX;pt.y=ev.clientY;" +
                "var lp=pt.matrixTransform(ctm.inverse());" +
                // Clamp the crosshair to the plot area; derive cursor time from it.
                "var px=lp.x;if(px<L)px=L;else if(px>L+W)px=L+W;" +
                "var ct=XF+(px-L)/W*(XT-XF);" +
                // Crosshair line.
                "if(!cross.parentNode)ov.appendChild(cross);" +
                "cross.setAttribute('x1',px);cross.setAttribute('x2',px);" +
                // Build tooltip rows + hover dots for every VISIBLE series.
                "var ser=svg.querySelectorAll('g.nw-series');" +
                "var rows='';var di=0;ensureDots(ser.length);" +
                "for(var i=0;i<ser.length;i++){var g=ser[i];" +
                "if(g.style.display==='none'){continue;}" +
                "var col=g.getAttribute('data-color')||'#38bdf8';" +
                "var lab=g.getAttribute('data-label')||'';" +
                "var np=nearest(pts(g),ct);" +
                "var vtxt='—';" +
                "if(np){vtxt=fmtVal(np.v,unit);" +
                "var d=dots[di++];d.style.display='';d.setAttribute('fill',col);" +
                "d.setAttribute('cx',xOf(np.t));d.setAttribute('cy',yOf(np.v));}" +
                "rows+='<div class=\\\"nw-tip-row\\\"><span class=\\\"nw-sw\\\" style=\\\"background:'+col+'\\\"></span>'+" +
                "'<span class=\\\"nw-lab\\\">'+esc(lab)+'</span><span class=\\\"nw-val\\\">'+esc(vtxt)+'</span></div>';}" +
                // Hide any leftover dots beyond the visible-series count.
                "for(var k=di;k<dots.length;k++){dots[k].style.display='none';}" +
                "ov.style.display='';" +
                "tip.innerHTML='<div class=\\\"nw-tip-time\\\">'+esc(fmtTime(ct))+'</div>'+rows;" +
                "tip.style.left=ev.clientX+'px';tip.style.top=(ev.clientY-14)+'px';" +
                "tip.classList.add('nw-show');" +
                "}" +
                "svg.addEventListener('mousemove',move);" +
                "svg.addEventListener('mouseleave',clear);" +
                "clear();" +
                "}" +

                // Small HTML-escape for tooltip text built via innerHTML.
                "function esc(s){s=''+s;return s.replace(/&/g,'&amp;').replace(/</g,'&lt;')" +
                ".replace(/>/g,'&gt;').replace(/\"/g,'&quot;');}" +

                // Wire every chart on the page.
                "var charts=document.querySelectorAll('svg.nw-chart');" +
                "for(var i=0;i<charts.length;i++){wireChart(charts[i]);}" +

                // ---------- legend: per-card toggle (click) + emphasis (hover) ----------
                "var legs=document.querySelectorAll('[data-nw-toggle]');" +
                "function cardOf(el){var n=el;while(n&&n!==document.body){" +
                "if(n.classList&&n.classList.contains('metric'))return n;n=n.parentNode;}return document;}" +
                "function seriesIn(card,label){" +
                "var all=card.querySelectorAll('g.nw-series');var r=[];" +
                "for(var i=0;i<all.length;i++){if(all[i].getAttribute('data-label')===label)r.push(all[i]);}" +
                "return r;}" +
                "for(var i=0;i<legs.length;i++){(function(li){" +
                "var label=li.getAttribute('data-nw-toggle');var card=cardOf(li);" +
                // Click toggles this series' visibility across every chart in this card.
                "function toggle(){var off=li.classList.toggle('nw-off');" +
                "var gs=seriesIn(card,label);" +
                "for(var j=0;j<gs.length;j++){gs[j].style.display=off?'none':'';}}" +
                "li.addEventListener('click',toggle);" +
                "li.addEventListener('keydown',function(e){if(e.key==='Enter'||e.key===' '){e.preventDefault();toggle();}});" +
                // Hover emphasises this series and fades the others within the card.
                "li.addEventListener('mouseenter',function(){" +
                "var svgs=card.querySelectorAll('svg.nw-chart');" +
                "for(var s=0;s<svgs.length;s++){svgs[s].classList.add('nw-emph');}" +
                "var gs=seriesIn(card,label);for(var j=0;j<gs.length;j++){gs[j].classList.add('nw-hot');}});" +
                "li.addEventListener('mouseleave',function(){" +
                "var svgs=card.querySelectorAll('svg.nw-chart');" +
                "for(var s=0;s<svgs.length;s++){svgs[s].classList.remove('nw-emph');}" +
                "var gs=card.querySelectorAll('g.nw-series.nw-hot');" +
                "for(var j=0;j<gs.length;j++){gs[j].classList.remove('nw-hot');}});" +
                "})(legs[i]);}" +

                "})();";
        }

        // =====================================================================
        // Inline CSS — premium dark dashboard
        // =====================================================================

        private static string Css()
        {
            var c = new StringBuilder(8 * 1024);

            c.Append("*{box-sizing:border-box}");
            c.Append("html,body{margin:0}");
            c.Append("body{background:" + BgColor + ";color:" + TextColor + ";" +
                "font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;" +
                "font-size:14px;line-height:1.45;-webkit-font-smoothing:antialiased}");
            c.Append(".mono{font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,'Liberation Mono',monospace}");

            // ---- sticky header ----
            c.Append(".topbar{position:sticky;top:0;z-index:50;display:flex;align-items:center;gap:18px;" +
                "flex-wrap:wrap;padding:12px 22px;background:rgba(17,22,31,.86);" +
                "backdrop-filter:saturate(140%) blur(8px);-webkit-backdrop-filter:saturate(140%) blur(8px);" +
                "border-bottom:1px solid " + PanelBorder + "}");
            c.Append(".brand{display:flex;align-items:center;gap:11px;min-width:0}");
            c.Append(".logo{display:flex;width:38px;height:38px;align-items:center;justify-content:center;" +
                "border-radius:10px;background:#0e1520;border:1px solid " + PanelBorder + "}");
            c.Append(".wordmark{font-size:17px;font-weight:700;letter-spacing:.2px;color:" + TextColor + "}");
            c.Append(".tagline{font-size:11.5px;color:" + MutedColor + ";letter-spacing:.02em}");

            // segmented control
            c.Append(".segmented{display:inline-flex;padding:3px;gap:2px;border-radius:11px;" +
                "background:#0e131c;border:1px solid " + PanelBorder + "}");
            c.Append(".seg{appearance:none;border:0;cursor:pointer;font:inherit;font-size:13px;font-weight:600;" +
                "color:" + MutedColor + ";background:transparent;padding:6px 14px;border-radius:8px;" +
                "transition:background .15s,color .15s,box-shadow .15s}");
            c.Append(".seg:hover{color:" + TextColor + "}");
            c.Append(".seg.active{color:#06121d;background:" + AccentCyan + ";" +
                "box-shadow:0 1px 8px rgba(56,189,248,.35)}");
            c.Append(".seg:focus-visible{outline:2px solid " + AccentCyan + ";outline-offset:2px}");

            // header meta (right)
            c.Append(".head-meta{display:flex;align-items:center;gap:16px;margin-left:auto;flex-wrap:wrap}");
            c.Append(".hm{display:flex;flex-direction:column;line-height:1.25}");
            c.Append(".hm-k{font-size:10px;text-transform:uppercase;letter-spacing:.07em;color:" + MutedColor + "}");
            c.Append(".hm-v{font-size:13px;color:" + TextColor + "}");
            c.Append(".pill{display:inline-flex;align-items:center;gap:7px;font-size:12px;color:" + AccentGreen + ";" +
                "background:rgba(74,222,128,.10);border:1px solid rgba(74,222,128,.28);" +
                "padding:5px 11px;border-radius:999px;white-space:nowrap}");
            c.Append(".pulse{width:8px;height:8px;border-radius:50%;background:" + AccentGreen + ";" +
                "box-shadow:0 0 0 0 rgba(74,222,128,.6);animation:pulse 2s infinite}");
            c.Append("@keyframes pulse{0%{box-shadow:0 0 0 0 rgba(74,222,128,.55)}" +
                "70%{box-shadow:0 0 0 7px rgba(74,222,128,0)}100%{box-shadow:0 0 0 0 rgba(74,222,128,0)}}");
            c.Append("@media (prefers-reduced-motion:reduce){.pulse{animation:none}}");

            // ---- summary stat cards ----
            c.Append(".summary{display:grid;gap:14px;padding:20px 22px 4px;" +
                "grid-template-columns:repeat(auto-fill,minmax(190px,1fr))}");
            c.Append(".stat{position:relative;overflow:hidden;background:" + PanelColor + ";" +
                "border:1px solid " + PanelBorder + ";border-radius:13px;padding:14px 15px;" +
                "box-shadow:0 1px 2px rgba(0,0,0,.35)}");
            c.Append(".stat:before{content:'';position:absolute;left:0;top:0;bottom:0;width:3px;" +
                "background:var(--accent,#38bdf8)}");
            c.Append(".stat-top{display:flex;align-items:center;gap:8px}");
            c.Append(".stat .dot{width:8px;height:8px;border-radius:50%;background:var(--accent,#38bdf8);" +
                "box-shadow:0 0 8px var(--accent,#38bdf8);flex:0 0 auto}");
            c.Append(".stat-label{font-size:12.5px;font-weight:600;color:" + TextColor + ";" +
                "white-space:nowrap;overflow:hidden;text-overflow:ellipsis}");
            c.Append(".badge{margin-left:auto;font-size:10px;font-weight:700;letter-spacing:.05em;" +
                "color:var(--accent,#38bdf8);background:rgba(255,255,255,.04);" +
                "border:1px solid " + PanelBorder + ";padding:2px 7px;border-radius:999px;white-space:nowrap}");
            c.Append(".stat-big{margin-top:10px;font-size:26px;font-weight:700;color:" + TextColor + ";" +
                "letter-spacing:-.01em;line-height:1.1}");
            c.Append(".stat-big.stat-name{font-size:20px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}");
            c.Append(".stat-big .dl{color:" + DownColor + "}.stat-big .ul{color:" + UpColor + "}");
            c.Append(".stat-sub{margin-top:6px;font-size:12px;color:" + MutedColor + "}");
            c.Append(".sep{margin:0 7px;color:#3a4555}");

            // ---- metric grid ----
            // Default 2 columns on a normal desktop; collapse to 1 when narrow; allow
            // at most 3 on a very wide screen. Charts stay fluid (width:100%).
            c.Append(".grid{display:grid;gap:20px;padding:18px 22px 8px;" +
                "grid-template-columns:repeat(2,minmax(0,1fr))}");
            c.Append("@media (max-width:1024px){.grid{grid-template-columns:1fr}}");
            c.Append("@media (min-width:1700px){.grid{grid-template-columns:repeat(3,minmax(0,1fr))}}");
            c.Append(".card{background:" + PanelColor + ";border:1px solid " + PanelBorder + ";" +
                "border-radius:14px;padding:16px 18px;box-shadow:0 1px 3px rgba(0,0,0,.4)}");
            c.Append(".card-head h2{margin:0;font-size:16px;font-weight:650;color:" + HeadingColor + "}");
            c.Append(".card-head h2 .unit{font-size:12px;font-weight:600;color:" + MutedColor + ";" +
                "background:#0e131c;border:1px solid " + PanelBorder + ";padding:1px 7px;border-radius:6px;" +
                "margin-left:6px;vertical-align:middle}");
            c.Append(".card-head .desc{margin:5px 0 0;color:" + MutedColor + ";font-size:12.5px}");

            // legend — entries are clickable (toggle) + hoverable (emphasise)
            c.Append(".legend{display:flex;flex-wrap:wrap;gap:6px 14px;margin:12px 0 4px}");
            c.Append(".legend .li{display:flex;align-items:center;gap:6px;font-size:12px;color:" + MutedColor + ";" +
                "cursor:pointer;user-select:none;padding:2px 6px;border-radius:6px;" +
                "transition:background .12s,opacity .12s}");
            c.Append(".legend .li:hover{background:rgba(255,255,255,.05);color:" + TextColor + "}");
            c.Append(".legend .li:focus-visible{outline:2px solid " + AccentCyan + ";outline-offset:1px}");
            c.Append(".legend .li i{width:10px;height:10px;border-radius:3px;display:inline-block;flex:0 0 auto}");
            // Hidden (toggled-off) series: dim the legend entry and strike its label.
            c.Append(".legend .li.nw-off{opacity:.4;text-decoration:line-through}");

            // chart well — chart scales to card width; taller well for the bigger viewBox
            c.Append(".chart-stack{margin-top:6px}");
            c.Append(".chart{background:" + PanelColor2 + ";border:1px solid " + PanelBorder + ";" +
                "border-radius:10px;padding:10px}");
            c.Append(".svgwrap{position:relative;width:100%;overflow:hidden}");
            c.Append(".svgwrap svg{width:100%;height:auto;display:block}");
            c.Append(".nodata{display:flex;align-items:center;justify-content:center;height:200px;" +
                "color:" + MutedColor + ";font-size:13px}");

            // ---- chart interactivity (crosshair, hover dots, tooltip, emphasis) ----
            // Crosshair: thin muted dashed vertical line at the cursor's plot x.
            c.Append(".nw-cross{stroke:" + MutedColor + ";stroke-width:1;stroke-dasharray:3 3;" +
                "opacity:.55;pointer-events:none}");
            // Hover dot: a filled circle in the series colour at the nearest sample.
            c.Append(".nw-dot{pointer-events:none}");
            // Series emphasis: when hovering a legend entry, fade the other series.
            c.Append(".nw-chart.nw-emph .nw-series{opacity:.18;transition:opacity .12s}");
            c.Append(".nw-chart.nw-emph .nw-series.nw-hot{opacity:1}");
            // Floating tooltip panel (created once by the JS, reused, follows the cursor).
            c.Append(".nw-tip{position:fixed;z-index:9999;pointer-events:none;min-width:120px;" +
                "background:rgba(13,18,26,.96);border:1px solid " + PanelBorder + ";border-radius:9px;" +
                "padding:8px 10px;font-size:12px;color:" + TextColor + ";line-height:1.45;" +
                "box-shadow:0 8px 24px rgba(0,0,0,.55);opacity:0;transition:opacity .08s;" +
                "transform:translate(-50%,-100%)}");
            c.Append(".nw-tip.nw-show{opacity:1}");
            c.Append(".nw-tip .nw-tip-time{font-weight:700;margin-bottom:5px;color:" + TextColor + ";" +
                "font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace}");
            c.Append(".nw-tip .nw-tip-row{display:flex;align-items:center;gap:7px;white-space:nowrap}");
            c.Append(".nw-tip .nw-tip-row .nw-sw{width:9px;height:9px;border-radius:2px;flex:0 0 auto}");
            c.Append(".nw-tip .nw-tip-row .nw-lab{color:" + MutedColor + "}");
            c.Append(".nw-tip .nw-tip-row .nw-val{margin-left:auto;font-weight:600;" +
                "font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace}");

            // footer stats
            c.Append(".foot-stack{margin-top:10px}");
            c.Append(".foot-row{display:flex;align-items:baseline;justify-content:space-between;gap:10px;" +
                "padding:5px 0;border-top:1px dashed " + PanelBorder + ";font-size:12px;flex-wrap:wrap}");
            c.Append(".foot-row:first-child{border-top:0}");
            c.Append(".foot .fl{display:flex;align-items:center;gap:6px;color:" + TextColor + ";font-weight:600}");
            c.Append(".foot .fl i{width:9px;height:9px;border-radius:2px;display:inline-block}");
            c.Append(".foot .fv{color:" + MutedColor + "}.foot .fv b{color:" + TextColor + "}");
            c.Append(".foot-row.muted{color:" + MutedColor + ";justify-content:flex-start}");

            // ---- range visibility toggling ----
            // Hide all per-range chart wells + footers, then reveal the active one.
            c.Append(".c5m,.c60m,.c24h{display:none}");
            c.Append(".grid.range-5m .c5m{display:block}");
            c.Append(".grid.range-60m .c60m{display:block}");
            c.Append(".grid.range-24h .c24h{display:block}");
            // No-JS fallback: show the 60-min charts by default.
            c.Append(".grid:not(.range-5m):not(.range-60m):not(.range-24h) .c60m{display:block}");

            // footer
            c.Append(".page-footer{padding:18px 22px 28px;color:" + MutedColor + ";font-size:12px;" +
                "border-top:1px solid " + PanelBorder + ";margin-top:8px}");

            c.Append("@media (max-width:560px){.topbar{gap:12px}.segmented{order:3;width:100%}" +
                ".seg{flex:1}.head-meta{order:2}}");

            return c.ToString();
        }

        private static string ErrorPage(Exception ex)
        {
            string msg = ex != null ? ex.Message : "unknown error";
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append("<title>NetWatch — error</title>");
            sb.Append("<style>")
              .Append("body{margin:0;background:" + BgColor + ";color:" + TextColor + ";" +
                  "font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;" +
                  "display:flex;min-height:100vh;align-items:center;justify-content:center;padding:24px}")
              .Append(".box{max-width:560px;background:" + PanelColor + ";border:1px solid " + PanelBorder + ";" +
                  "border-radius:14px;padding:26px 28px;box-shadow:0 6px 24px rgba(0,0,0,.5)}")
              .Append(".box h1{margin:0 0 8px;font-size:18px;color:" + AccentRed + "}")
              .Append(".box p{margin:0;color:" + MutedColor + ";font-size:13px;" +
                  "font-family:ui-monospace,Consolas,monospace}")
              .Append("</style></head><body><div class=\"box\">");
            sb.Append("<h1>NetWatch report could not be generated</h1>");
            sb.Append("<p>").Append(Esc(msg)).Append("</p>");
            sb.Append("</div></body></html>");
            return sb.ToString();
        }

        // =====================================================================
        // Bucketing helpers
        // =====================================================================

        private static int BucketSec(int windowSec)
        {
            int b = windowSec / TargetPoints;
            if (b < 1) b = 1;
            return b;
        }

        private static int BucketCount(DateTime from, DateTime to, int bucketSec)
        {
            if (bucketSec <= 0) return 0;
            double secs = (to - from).TotalSeconds;
            if (secs <= 0) return 0;
            int n = (int)Math.Ceiling(secs / bucketSec);
            if (n < 1) n = 1;
            return n;
        }

        private static int BucketIndex(DateTime t, DateTime from, int bucketSec, int n)
        {
            if (bucketSec <= 0) return -1;
            double offset = (t - from).TotalSeconds;
            if (offset < 0) return -1;
            int b = (int)(offset / bucketSec);
            if (b < 0 || b >= n) return -1;
            return b;
        }

        private static DateTime BucketTime(DateTime from, int bucketSec, int i)
        {
            return from.AddSeconds((i + 0.5) * bucketSec);
        }

        private static List<ChartPoint> AvgPoints(double[] sums, int[] counts, DateTime from, int bucketSec, int n)
        {
            var points = new List<ChartPoint>(n);
            for (int i = 0; i < n; i++)
            {
                DateTime t = BucketTime(from, bucketSec, i);
                if (counts[i] == 0)
                    points.Add(new ChartPoint { Time = t, Value = 0, Missing = true });
                else
                    points.Add(new ChartPoint { Time = t, Value = sums[i] / counts[i], Missing = false });
            }
            return points;
        }

        private static double MeanAbsDelta(List<double> values)
        {
            if (values == null || values.Count < 2) return 0;
            double total = 0;
            for (int i = 1; i < values.Count; i++)
                total += Math.Abs(values[i] - values[i - 1]);
            return total / (values.Count - 1);
        }

        private static bool HasWifiData(List<IfaceRecord> ifaces)
        {
            foreach (var r in ifaces)
                if (r.IsWifi && r.SignalPct >= 0) return true;
            return false;
        }

        // =====================================================================
        // Series stats (for the per-card min / avg / max footer)
        // =====================================================================

        private struct Stats
        {
            public bool HasData;
            public double Min, Max, Avg, Last;
        }

        // Reduce one series' plotted (non-missing) points to min / avg / max / last.
        private static Stats Aggregate(ChartSeries s)
        {
            var st = new Stats { HasData = false, Min = 0, Max = 0, Avg = 0, Last = 0 };
            if (s == null || s.Points == null) return st;

            double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0;
            int count = 0;
            bool haveLast = false; double last = 0;
            foreach (var p in s.Points)
            {
                if (p.Missing || double.IsNaN(p.Value) || double.IsInfinity(p.Value)) continue;
                if (p.Value < min) min = p.Value;
                if (p.Value > max) max = p.Value;
                sum += p.Value; count++;
                last = p.Value; haveLast = true; // points are chronological
            }
            if (count == 0) return st;

            st.HasData = true;
            st.Min = min; st.Max = max; st.Avg = sum / count;
            st.Last = haveLast ? last : st.Avg;
            return st;
        }

        private static bool HasAnyData(List<ChartSeries> series)
        {
            if (series == null) return false;
            foreach (var s in series)
                if (Aggregate(s).HasData) return true;
            return false;
        }

        // =====================================================================
        // Formatting / display helpers
        // =====================================================================

        // Compact numeric formatting for cards/footers: integers when large.
        private static string Fmt(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "—";
            double a = Math.Abs(v);
            if (a >= 1000) return v.ToString("0", CultureInfo.InvariantCulture);
            if (a >= 100) return v.ToString("0", CultureInfo.InvariantCulture);
            if (a >= 10) return v.ToString("0.#", CultureInfo.InvariantCulture);
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        // Friendlier display of common probe target labels.
        private static string NiceTarget(string target)
        {
            if (string.IsNullOrEmpty(target)) return "(unnamed)";
            return target;
        }

        // =====================================================================
        // Safe CSV reads — defend against a missing/partial CsvHistory at runtime.
        // =====================================================================

        private static List<PingRecord> SafeReadPings(string logDir, DateTime since)
        {
            try { return CsvHistory.ReadPings(logDir, since) ?? new List<PingRecord>(); }
            catch { return new List<PingRecord>(); }
        }

        private static List<SpeedRecord> SafeReadSpeed(string logDir, DateTime since)
        {
            try { return CsvHistory.ReadSpeed(logDir, since) ?? new List<SpeedRecord>(); }
            catch { return new List<SpeedRecord>(); }
        }

        private static List<IfaceRecord> SafeReadIface(string logDir, DateTime since)
        {
            try { return CsvHistory.ReadIface(logDir, since) ?? new List<IfaceRecord>(); }
            catch { return new List<IfaceRecord>(); }
        }

        // =====================================================================
        // HTML text escaping
        // =====================================================================

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
