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

            // data-unit lets the export filenames / CSV header use the metric unit;
            // data-metric carries the title so PNG/CSV downloads are named per metric.
            sb.Append("<section class=\"card metric\" data-metric=\"").Append(Esc(title))
              .Append("\" data-unit=\"").Append(Esc(yUnit)).Append("\">");

            sb.Append("<div class=\"card-head\">");
            sb.Append("<h2>").Append(Esc(title))
              .Append(" <span class=\"unit\">").Append(Esc(yUnit)).Append("</span></h2>");
            if (!string.IsNullOrEmpty(description))
                sb.Append("<p class=\"desc\">").Append(Esc(description)).Append("</p>");
            // Per-chart toolbar (top-right, appears on hover; keyboard focusable).
            AppendToolbar(sb);
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

        // Per-chart toolbar: a row of focusable icon buttons in the card's top-right.
        // Each button carries a data-act the inline JS dispatches on; titles + aria
        // labels keep it accessible. Glyphs are plain Unicode so it stays offline.
        private static void AppendToolbar(StringBuilder sb)
        {
            sb.Append("<div class=\"nw-tools\" role=\"toolbar\" aria-label=\"Chart tools\">");
            ToolBtn(sb, "reset", "↺", "Reset zoom");
            ToolBtn(sb, "png", "⬇︎", "Download PNG");
            ToolBtn(sb, "csv", "CSV", "Download CSV");
            ToolBtn(sb, "full", "⛶", "Fullscreen");
            ToolBtn(sb, "area", "▧", "Toggle area fill");
            ToolBtn(sb, "smooth", "∿", "Toggle smoothing");
            ToolBtn(sb, "log", "log", "Toggle log / linear Y scale");
            sb.Append("</div>");
        }

        private static void ToolBtn(StringBuilder sb, string act, string glyph, string label)
        {
            sb.Append("<button type=\"button\" class=\"nw-tb nw-tb-").Append(act)
              .Append("\" data-act=\"").Append(act)
              .Append("\" title=\"").Append(Esc(label))
              .Append("\" aria-label=\"").Append(Esc(label)).Append("\">")
              .Append(glyph).Append("</button>");
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
            // NOTE: auto-reload is driven by a JS setTimeout(location.reload, 60s) wired
            // in Js() so the Pause control can cancel it (a static <meta refresh> cannot
            // be paused). The generated time is published to the body via data-gen so the
            // "updated <ago>" hint can tick without a server round-trip.
            sb.Append("<title>NetWatch — ").Append(Esc(machine)).Append("</title>");
            sb.Append("<style>").Append(Css()).Append("</style>");
            // Publish the generation wall-clock as epoch-ms (no TZ offset, matching the
            // SvgChart encoding) so the JS "updated <ago>" hint and refresh timer work
            // entirely offline.
            sb.Append("</head><body data-gen=\"").Append(GenEpochMs(nowLocal)).Append("\">");
        }

        // Generation time as integer epoch-ms of the LOCAL wall-clock (no timezone
        // offset), the same convention SvgChart uses for data-xfrom/xto.
        private static readonly DateTime UnixEpochUnspecified =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

        private static string GenEpochMs(DateTime nowLocal)
        {
            double ms = (nowLocal - UnixEpochUnspecified).TotalMilliseconds;
            if (double.IsNaN(ms) || double.IsInfinity(ms)) return "0";
            return Math.Round(ms).ToString("0", CultureInfo.InvariantCulture);
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
            // Pause auto-refresh control: a clickable pill. The inline JS owns the
            // 60s reload timer (no <meta refresh>), toggles paused state + persists it,
            // and updates the "updated <ago>" hint. aria-pressed reflects paused state.
            sb.Append("<button type=\"button\" class=\"pill nw-refresh\" id=\"nw-refresh\" ")
              .Append("aria-pressed=\"false\" aria-label=\"Pause auto-refresh\" ")
              .Append("title=\"Pause / resume the 60s auto-refresh\">")
              .Append("<span class=\"pulse\"></span>")
              .Append("<span class=\"nw-refresh-txt\">auto-refresh 60s</span></button>");
            sb.Append("<span class=\"nw-updated\" id=\"nw-updated\" aria-live=\"polite\"></span>");
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

                // Small HTML-escape for tooltip text built via innerHTML.
                "function esc(s){s=''+s;return s.replace(/&/g,'&amp;').replace(/</g,'&lt;')" +
                ".replace(/>/g,'&gt;').replace(/\"/g,'&quot;');}" +

                // Build an SVG element with attributes in one call.
                "function mk(tag,at){var e=document.createElementNS(SVGNS,tag);" +
                "if(at){for(var k in at){if(at.hasOwnProperty(k))e.setAttribute(k,at[k]);}}return e;}" +

                // \"Nice\" axis step for a span/target-tick-count (1/2/2.5/5 * 10^n).
                "function niceStep(span,want){if(!(span>0))return 1;" +
                "var raw=span/(want>0?want:4);var mag=Math.pow(10,Math.floor(Math.log(raw)/Math.LN10));" +
                "var n=raw/mag;var s;if(n<1.5)s=1;else if(n<3)s=2;else if(n<7)s=5;else s=10;return s*mag;}" +

                // Visible (legend-not-hidden) series <g> nodes of a chart, in order.
                "function visSeries(svg){var all=svg.querySelectorAll('g.nw-series');var r=[];" +
                "for(var i=0;i<all.length;i++){if(all[i].style.display!=='none')r.push(all[i]);}return r;}" +

                // Registry of every wired chart controller (for synced crosshair etc.).
                "var CHARTS=[];" +

                // Wire one svg.nw-chart into a full interactive controller:
                // zoom/brush/wheel/pan, redraw-from-data-points with Y auto-rescale,
                // crosshair + dots + tooltip (using the CURRENT domain/scale), and
                // toggles (area/smoothing/log). Returns the controller (also stored on
                // svg._nwc and pushed to CHARTS).
                "function wireChart(svg){" +
                "if(svg._nwc)return svg._nwc;" +
                "var L=parseFloat(svg.getAttribute('data-l'));" +
                "var T=parseFloat(svg.getAttribute('data-t'));" +
                "var W=parseFloat(svg.getAttribute('data-w'));" +
                "var H=parseFloat(svg.getAttribute('data-h'));" +
                "var XF=parseFloat(svg.getAttribute('data-xfrom'));" +
                "var XT=parseFloat(svg.getAttribute('data-xto'));" +
                "var YMIN0=parseFloat(svg.getAttribute('data-ymin'));" +
                "var YMAX0=parseFloat(svg.getAttribute('data-ymax'));" +
                "var unit=svg.getAttribute('data-unit')||'';" +
                "if(!(W>0)||!(H>0)||!(XT>XF)||!(YMAX0>YMIN0))return null;" +
                // Per-chart state: current X domain + current Y scale + flags.
                "var st={x0:XF,x1:XT,y0:YMIN0,y1:YMAX0,log:false,area:true,smooth:false};" +
                // The empty server-emitted g.nw-dynamic (or create one as a fallback).
                "var dyn=svg.querySelector('g.nw-dynamic');" +
                "if(!dyn){dyn=mk('g',{'class':'nw-dynamic'});svg.appendChild(dyn);}" +
                // Brush + crosshair overlay group, appended AFTER nw-dynamic so it is on top.
                "var ov=mk('g',{'pointer-events':'none'});svg.appendChild(ov);" +
                "var brush=mk('rect',{'class':'nw-brush','y':T,'height':H});brush.style.display='none';ov.appendChild(brush);" +
                "var cross=mk('line',{'class':'nw-cross','y1':T,'y2':T+H});" +
                "var ghost=mk('line',{'class':'nw-cross','y1':T,'y2':T+H});ghost.style.display='none';ov.appendChild(ghost);" +
                "var dots=[];" +
                // Current-domain mappers (read live state so crosshair stays aligned).
                "function ymap(v){if(st.log){var lo=Math.log10(Math.max(st.y0,1e-9)),hi=Math.log10(Math.max(st.y1,1e-9));" +
                "if(!(hi>lo))hi=lo+1;return T+(1-(Math.log10(Math.max(v,1e-9))-lo)/(hi-lo))*H;}" +
                "return T+(1-(v-st.y0)/((st.y1-st.y0)||1))*H;}" +
                "function xmap(t){return L+(t-st.x0)/((st.x1-st.x0)||1)*W;}" +
                "function tAt(px){return st.x0+(px-L)/W*(st.x1-st.x0);}" +
                "if(!Math.log10)Math.log10=function(x){return Math.log(x)/Math.LN10;};" +
                // Local->client px helpers via the live CTM (correct under width:100%).
                "function toLocalX(clientX,clientY){var ctm=svg.getScreenCTM();if(!ctm)return null;" +
                "var pt=svg.createSVGPoint();pt.x=clientX;pt.y=clientY;return pt.matrixTransform(ctm.inverse());}" +
                "function ensureDots(n){while(dots.length<n){" +
                "var d=mk('circle',{'class':'nw-dot','r':'3.4','stroke':'#0b0e14','stroke-width':'1.5'});" +
                "ov.appendChild(d);dots.push(d);}}" +

                // Samples of one series within the current domain, plus the immediate
                // neighbours so the drawn line reaches both plot edges.
                "function clip(arr){var a=arr,n=a.length,r=[];if(!n)return r;" +
                "var i=0;for(;i<n;i++){if(a[i].t>=st.x0)break;}" +
                "if(i>0)r.push(a[i-1]);" +
                "for(;i<n;i++){r.push(a[i]);if(a[i].t>st.x1){break;}}" +
                "return r;}" +

                // Recompute the visible Y range from clipped samples of visible series.
                "function autoY(){var lo=Infinity,hi=-Infinity,ser=visSeries(svg);" +
                "for(var i=0;i<ser.length;i++){var c=clip(pts(ser[i]));" +
                "for(var j=0;j<c.length;j++){var v=c[j].v;if(v<lo)lo=v;if(v>hi)hi=v;}}" +
                "if(lo===Infinity){st.y0=YMIN0;st.y1=YMAX0;return;}" +
                "if(st.log){if(lo<=0)lo=1e-9;}" +
                "var pad=(hi-lo)*0.08;if(!(pad>0))pad=Math.abs(hi)>1e-9?Math.abs(hi)*0.1:1;" +
                "st.y0=lo-pad;st.y1=hi+pad;if(YMIN0>=0&&st.y0<0)st.y0=0;" +
                "if(st.log&&st.y0<=0)st.y0=Math.max(lo*0.5,1e-9);" +
                "if(!(st.y1>st.y0))st.y1=st.y0+1;}" +

                // SVG path 'd' for a clipped point list using current mappers.
                "function lineD(c){var d='';for(var i=0;i<c.length;i++){var x=xmap(c[i].t),y=ymap(c[i].v);" +
                "if(x<L)x=L;else if(x>L+W)x=L+W;d+=(i?'L':'M')+x.toFixed(2)+' '+y.toFixed(2);}return d;}" +
                "function areaD(c){if(c.length<2)return '';var d='';var base=(T+H).toFixed(2);" +
                "var x0=Math.min(Math.max(xmap(c[0].t),L),L+W);d+='M'+x0.toFixed(2)+' '+base;" +
                "for(var i=0;i<c.length;i++){var x=xmap(c[i].t),y=ymap(c[i].v);" +
                "if(x<L)x=L;else if(x>L+W)x=L+W;d+='L'+x.toFixed(2)+' '+y.toFixed(2);}" +
                "var xl=Math.min(Math.max(xmap(c[c.length-1].t),L),L+W);" +
                "d+='L'+xl.toFixed(2)+' '+base+'Z';return d;}" +
                // Centred moving average (window ~ smoothing overlay) over clipped pts.
                "function smoothD(c){if(c.length<3)return '';var w=Math.max(2,Math.round(c.length/12));" +
                "var d='';for(var i=0;i<c.length;i++){var s=0,n=0;" +
                "for(var k=-w;k<=w;k++){var j=i+k;if(j>=0&&j<c.length){s+=c[j].v;n++;}}" +
                "var x=xmap(c[i].t),y=ymap(s/n);if(x<L)x=L;else if(x>L+W)x=L+W;" +
                "d+=(i?'L':'M')+x.toFixed(2)+' '+y.toFixed(2);}return d;}" +

                // Full extent? (whole data domain, linear Y) -> show static server SVG.
                "function isFull(){return st.x0<=XF+0.5&&st.x1>=XT-0.5;}" +

                // Rebuild g.nw-dynamic for the current domain/scale (zoomed view) or
                // clear it and reveal the static server-rendered content at full extent.
                "function redraw(){while(dyn.firstChild)dyn.removeChild(dyn.firstChild);" +
                "var zoomed=!isFull()||st.log||st.smooth;" +
                "if(!zoomed){svg.classList.remove('nw-zoomed');st.y0=YMIN0;st.y1=YMAX0;return;}" +
                "svg.classList.add('nw-zoomed');autoY();" +
                // Y gridlines + labels (nice ticks).
                "var step=niceStep(st.y1-st.y0,4);var start=Math.ceil(st.y0/step)*step;" +
                "for(var gv=start;gv<=st.y1+1e-9;gv+=step){var gy=ymap(gv);" +
                "dyn.appendChild(mk('line',{'class':'nw-dyn-grid','x1':L,'y1':gy.toFixed(2),'x2':L+W,'y2':gy.toFixed(2)}));" +
                "var tl=mk('text',{'class':'nw-dyn-txt','x':(L-8).toFixed(2),'y':(gy+3).toFixed(2),'text-anchor':'end'});" +
                "tl.textContent=fmtAxis(gv)+(gv===start&&unit?' '+unit:'');dyn.appendChild(tl);}" +
                // X labels (HH:mm or HH:mm:ss when span<15min).
                "var span=st.x1-st.x0;var shortS=span<15*60000;" +
                "for(var xi=0;xi<=5;xi++){var fr=xi/5;var xx=L+fr*W;var tt=st.x0+fr*span;" +
                "var an=xi===0?'start':(xi===5?'end':'middle');" +
                "var xt=mk('text',{'class':'nw-dyn-txt','x':xx.toFixed(2),'y':(T+H+16).toFixed(2),'text-anchor':an});" +
                "xt.textContent=shortS?fmtTime(tt):fmtHM(tt);dyn.appendChild(xt);}" +
                // One area + line (+ optional smoothing) per visible series.
                "var ser=visSeries(svg);" +
                "for(var i=0;i<ser.length;i++){var g=ser[i];var col=g.getAttribute('data-color')||'#38bdf8';" +
                "var c=clip(pts(g));if(c.length<1)continue;" +
                "if(st.area&&c.length>1){var ad=areaD(c);if(ad)dyn.appendChild(mk('path',{'class':'nw-dyn-area','d':ad,'fill':col,'opacity':'0.16'}));}" +
                "var ld=lineD(c);if(ld)dyn.appendChild(mk('path',{'class':'nw-dyn-line','d':ld,'stroke':col}));" +
                "if(st.smooth){var sd=smoothD(c);if(sd)dyn.appendChild(mk('path',{'class':'nw-smooth-line','d':sd,'stroke':col}));}}" +
                "}" +
                // Axis number format (compact, like the server FormatValue).
                "function fmtAxis(v){var a=Math.abs(v);if(a>=1000){var k=v/1000;return (a>=100000?k.toFixed(0):k.toFixed(1))+'k';}" +
                "if(a>=100)return v.toFixed(0);if(a>=10)return v.toFixed(1);return v.toFixed(2);}" +
                "function fmtHM(ms){var d=new Date(ms);function p(n){return(n<10?'0':'')+n;}" +
                "return p(d.getUTCHours())+':'+p(d.getUTCMinutes());}" +

                // Crosshair + dots + tooltip at a cursor time, using the CURRENT scale.
                // owner=true draws the full tooltip; owner=false is the synced ghost.
                "function showAt(ct,clientX,clientY,owner){" +
                "if(ct<st.x0||ct>st.x1){if(owner)hide();else{ghost.style.display='none';}return;}" +
                "var px=xmap(ct);" +
                "if(owner){if(!cross.parentNode)ov.appendChild(cross);cross.style.display='';" +
                "cross.setAttribute('x1',px);cross.setAttribute('x2',px);" +
                "var ser=visSeries(svg);var di=0;ensureDots(ser.length);" +
                "for(var i=0;i<ser.length;i++){var g=ser[i];var col=g.getAttribute('data-color')||'#38bdf8';" +
                "var np=nearest(pts(g),ct);" +
                "if(np&&np.t>=st.x0-1&&np.t<=st.x1+1){var d=dots[di++];" +
                "d.style.display='';d.setAttribute('fill',col);d.setAttribute('cx',xmap(np.t));d.setAttribute('cy',ymap(np.v));}}" +
                "for(var k=di;k<dots.length;k++){dots[k].style.display='none';}" +
                "ov.style.display='';" +
                "tip.innerHTML='<div class=\\\"nw-tip-time\\\">'+esc(fmtTime(ct))+'</div>'+rows0(ct);" +
                "tip.style.left=(clientX!=null?clientX:0)+'px';tip.style.top=((clientY!=null?clientY:0)-14)+'px';" +
                "tip.classList.add('nw-show');}" +
                "else{ghost.style.display='';ghost.setAttribute('x1',px);ghost.setAttribute('x2',px);ov.style.display='';}}" +
                // Tooltip rows for visible series at time ct (current scale).
                "function rows0(ct){var ser=visSeries(svg);var rows='';" +
                "for(var i=0;i<ser.length;i++){var g=ser[i];var col=g.getAttribute('data-color')||'#38bdf8';" +
                "var lab=g.getAttribute('data-label')||'';var np=nearest(pts(g),ct);var vtxt='—';" +
                "if(np&&np.t>=st.x0-1&&np.t<=st.x1+1)vtxt=fmtVal(np.v,unit);" +
                "rows+='<div class=\\\"nw-tip-row\\\"><span class=\\\"nw-sw\\\" style=\\\"background:'+col+'\\\"></span>'+" +
                "'<span class=\\\"nw-lab\\\">'+esc(lab)+'</span><span class=\\\"nw-val\\\">'+esc(vtxt)+'</span></div>';}" +
                "return rows;}" +
                "function hide(){cross.style.display='none';for(var k=0;k<dots.length;k++)dots[k].style.display='none';" +
                "tip.classList.remove('nw-show');}" +
                "function hideGhost(){ghost.style.display='none';}" +

                // ----- pointer interactions: brush-zoom / wheel-zoom / pan / dblclick -----
                "var drag=null;" + // {mode:'brush'|'pan',startX,startT,...}
                "function onDown(ev){if(ev.button===1||ev.shiftKey){var lp=toLocalX(ev.clientX,ev.clientY);if(!lp)return;" +
                "drag={mode:'pan',cx:ev.clientX,x0:st.x0,x1:st.x1};svg.classList.add('nw-panning');ev.preventDefault();return;}" +
                "if(ev.button!==0)return;var lp=toLocalX(ev.clientX,ev.clientY);if(!lp)return;" +
                "var px=Math.min(Math.max(lp.x,L),L+W);drag={mode:'brush',sx:px,cx:ev.clientX};}" +
                "function onMove(ev){" +
                "if(drag&&drag.mode==='pan'){var ctm=svg.getScreenCTM();if(ctm){" +
                "var dpx=(ev.clientX-drag.cx)/ctm.a;var dt=-dpx/W*(drag.x1-drag.x0);" +
                "var nx0=drag.x0+dt,nx1=drag.x1+dt,wsp=drag.x1-drag.x0;" +
                "if(nx0<XF){nx0=XF;nx1=XF+wsp;}if(nx1>XT){nx1=XT;nx0=XT-wsp;}" +
                "st.x0=nx0;st.x1=nx1;redraw();sync(null);}hide();return;}" +
                "if(drag&&drag.mode==='brush'){var lp=toLocalX(ev.clientX,ev.clientY);if(!lp)return;" +
                "var px=Math.min(Math.max(lp.x,L),L+W);var a=Math.min(drag.sx,px),b=Math.max(drag.sx,px);" +
                "brush.style.display='';brush.setAttribute('x',a);brush.setAttribute('width',Math.max(0,b-a));ov.style.display='';hide();return;}" +
                // No drag: crosshair + synced ghosts on other charts. onMove is a window
                // listener (so a drag keeps tracking outside the svg), so gate the hover on the
                // pointer actually being over THIS svg — otherwise every chart sharing this X
                // column would also become owner and overwrite the shared tooltip.
                "var rb=svg.getBoundingClientRect();" +
                "if(ev.clientX<rb.left||ev.clientX>rb.right||ev.clientY<rb.top||ev.clientY>rb.bottom)return;" +
                "var lp=toLocalX(ev.clientX,ev.clientY);if(!lp)return;" +
                "var px=Math.min(Math.max(lp.x,L),L+W);var ct=tAt(px);" +
                "showAt(ct,ev.clientX,ev.clientY,true);sync(ct);}" +
                "function onUp(ev){svg.classList.remove('nw-panning');" +
                "if(drag&&drag.mode==='brush'){brush.style.display='none';" +
                "var lp=toLocalX(ev.clientX,ev.clientY);" +
                "if(lp){var px=Math.min(Math.max(lp.x,L),L+W);" +
                "if(Math.abs(px-drag.sx)>=6){var a=Math.min(drag.sx,px),b=Math.max(drag.sx,px);" +
                "var t0=tAt(a),t1=tAt(b);if(t1>t0){st.x0=t0;st.x1=t1;redraw();sync(null);}}}}" +
                "drag=null;}" +
                "function onWheel(ev){ev.preventDefault();var lp=toLocalX(ev.clientX,ev.clientY);if(!lp)return;" +
                "var px=Math.min(Math.max(lp.x,L),L+W);var ct=tAt(px);" +
                "var f=ev.deltaY<0?0.8:1.25;var nw=(st.x1-st.x0)*f;" +
                "var minw=Math.min(2000,(XT-XF));if(nw<minw)nw=minw;if(nw>(XT-XF))nw=XT-XF;" +
                "var left=ct-(ct-st.x0)*(nw/(st.x1-st.x0||1));var right=left+nw;" +
                "if(left<XF){left=XF;right=XF+nw;}if(right>XT){right=XT;left=XT-nw;}" +
                "st.x0=left;st.x1=right;redraw();showAt(ct,ev.clientX,ev.clientY,true);sync(ct);}" +
                "function reset(){st.x0=XF;st.x1=XT;st.log=false;st.smooth=false;st.area=true;" +
                "svg.classList.remove('nw-noarea');redraw();" +
                "svg.classList.remove('nw-zoomed');sync(null);}" +
                "svg.addEventListener('mousedown',onDown);" +
                "window.addEventListener('mousemove',onMove);" +
                "window.addEventListener('mouseup',onUp);" +
                "svg.addEventListener('wheel',onWheel,{passive:false});" +
                "svg.addEventListener('dblclick',function(e){e.preventDefault();reset();});" +
                "svg.addEventListener('mouseleave',function(){if(!drag){hide();syncClear();}});" +
                "svg.addEventListener('contextmenu',function(e){if(drag)e.preventDefault();});" +

                // Controller object exposed for toolbar / keyboard / synced crosshair.
                "var ctrl={svg:svg,st:st,redraw:redraw,reset:reset," +
                "ghost:function(ct){if(ct==null){hideGhost();return;}showAt(ct,null,null,false);}," +
                "hideAll:function(){hide();hideGhost();}," +
                "panBy:function(frac){var sp=st.x1-st.x0;var dt=sp*frac;var nx0=st.x0+dt,nx1=st.x1+dt;" +
                "if(nx0<XF){nx0=XF;nx1=XF+sp;}if(nx1>XT){nx1=XT;nx0=XT-sp;}st.x0=nx0;st.x1=nx1;redraw();}," +
                "zoomBy:function(f){var ct=(st.x0+st.x1)/2;var nw=(st.x1-st.x0)*f;var mw=Math.min(2000,XT-XF);" +
                "if(nw<mw)nw=mw;if(nw>XT-XF)nw=XT-XF;var l=ct-nw/2,r=ct+nw/2;" +
                "if(l<XF){l=XF;r=XF+nw;}if(r>XT){r=XT;l=XT-nw;}st.x0=l;st.x1=r;redraw();}," +
                "setLog:function(on){st.log=on;redraw();}," +
                "setArea:function(on){st.area=on;svg.classList.toggle('nw-noarea',!on);redraw();}," +
                "setSmooth:function(on){st.smooth=on;redraw();}," +
                "isFull:isFull,XF:XF,XT:XT};" +
                "svg._nwc=ctrl;CHARTS.push(ctrl);redraw();return ctrl;}" +

                // ---------- synced crosshair across all charts (by time) ----------
                // Show a ghost crosshair at time ct on every OTHER chart that covers it.
                "function sync(ct){for(var i=0;i<CHARTS.length;i++){var c=CHARTS[i];" +
                "if(ct==null){c.ghost(null);continue;}" +
                "if(c.st===undefined)continue;" +
                "if(ct>=c.st.x0&&ct<=c.st.x1)c.ghost(ct);else c.ghost(null);}}" +
                "function syncClear(){for(var i=0;i<CHARTS.length;i++){CHARTS[i].ghost(null);}}" +
                // Make sync skip the owning chart's own ghost when its real crosshair is up:
                // showAt(owner) already drew the real crosshair, and ghost()/showAt(false)
                // for the same chart just overlaps harmlessly (same x), so no special case.

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
                // Also re-run any zoomed chart's redraw so its dynamic layer reflects it.
                "function toggle(){var off=li.classList.toggle('nw-off');" +
                "var gs=seriesIn(card,label);" +
                "for(var j=0;j<gs.length;j++){gs[j].style.display=off?'none':'';}" +
                "var cs=card.querySelectorAll('svg.nw-chart');" +
                "for(var s=0;s<cs.length;s++){if(cs[s]._nwc)cs[s]._nwc.redraw();}}" +
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

                // ---------- which chart is currently active in a card (by range) ----------
                // Only one of the 3 stacked wells (c5m/c60m/c24h) is visible at a time.
                "function activeSvg(card){var ws=card.querySelectorAll('.chart');" +
                "for(var i=0;i<ws.length;i++){if(ws[i].offsetParent!==null){var s=ws[i].querySelector('svg.nw-chart');if(s)return s;}}" +
                "var any=card.querySelector('svg.nw-chart');return any;}" +

                // ---------- PNG export: rasterise the active chart's current SVG view ----------
                "function downloadDataUrl(url,name){var a=document.createElement('a');a.href=url;a.download=name;" +
                "document.body.appendChild(a);a.click();document.body.removeChild(a);}" +
                "function exportPNG(card){var svg=activeSvg(card);if(!svg)return;" +
                "var vb=svg.getAttribute('viewBox').split(/\\s+/);var w=parseFloat(vb[2])||860,h=parseFloat(vb[3])||320;" +
                "var clone=svg.cloneNode(true);clone.setAttribute('width',w);clone.setAttribute('height',h);" +
                // Strip the transient overlay (crosshair/brush) so the export is clean.
                "var ovs=clone.querySelectorAll('.nw-cross,.nw-brush,.nw-dot');for(var i=0;i<ovs.length;i++)ovs[i].parentNode&&ovs[i].parentNode.removeChild(ovs[i]);" +
                "clone.style.background='#11161f';" +
                // Inline the chart CSS into the standalone clone so a rasterised SVG matches
                // the on-screen view: dynamic line/grid/label/area styling, the zoomed-state
                // rule that hides the static server layer, and the area-off rule. Without this
                // a zoomed export would show black-filled blobs + the reappearing static layer.
                "var ec='.nw-dyn-line,.nw-smooth-line{fill:none;stroke-linejoin:round;stroke-linecap:round}'+" +
                "'.nw-dyn-line{stroke-width:1.75}.nw-smooth-line{stroke-width:1.4;stroke-dasharray:5 3;opacity:.85}'+" +
                "'.nw-dyn-grid{stroke:#1f2733;stroke-width:1}.nw-dyn-txt{fill:#94a3b8;font-size:10px}.nw-dyn-area{stroke:none}'+" +
                "'.nw-chart.nw-zoomed .nw-xaxis,.nw-chart.nw-zoomed .nw-yaxis,.nw-chart.nw-zoomed .nw-grid,.nw-chart.nw-zoomed .nw-series{display:none}'+" +
                "'.nw-chart.nw-noarea .nw-dyn-area{display:none}';" +
                "var stl=document.createElementNS(SVGNS,'style');stl.textContent=ec;clone.insertBefore(stl,clone.firstChild);" +
                // When area is toggled off, also drop the static gradient-filled area paths from
                // the clone (the on-page rule that hides them is a CSS attribute selector we omit).
                "if(svg.classList.contains('nw-noarea')){var aps=clone.querySelectorAll('.nw-series path');" +
                "for(var j=0;j<aps.length;j++){var ff=aps[j].getAttribute('fill');" +
                "if(ff&&ff.indexOf('url(')===0&&aps[j].parentNode)aps[j].parentNode.removeChild(aps[j]);}}" +
                "var xml=new XMLSerializer().serializeToString(clone);" +
                "var src='data:image/svg+xml;base64,'+btoa(unescape(encodeURIComponent(xml)));" +
                "var img=new Image();var scale=2;" +
                "img.onload=function(){var cv=document.createElement('canvas');cv.width=w*scale;cv.height=h*scale;" +
                "var cx=cv.getContext('2d');cx.fillStyle='#11161f';cx.fillRect(0,0,cv.width,cv.height);" +
                "cx.scale(scale,scale);cx.drawImage(img,0,0,w,h);" +
                "try{downloadDataUrl(cv.toDataURL('image/png'),fname(card,'png'));}catch(e){}};" +
                "img.onerror=function(){};img.src=src;}" +

                // ---------- CSV export: visible series within the current domain ----------
                "function exportCSV(card){var svg=activeSvg(card);if(!svg)return;var c=svg._nwc;" +
                "var x0=c?c.st.x0:parseFloat(svg.getAttribute('data-xfrom'));" +
                "var x1=c?c.st.x1:parseFloat(svg.getAttribute('data-xto'));" +
                "var ser=visSeries(svg);if(!ser.length)return;" +
                // Union of all sample times (in-domain), sorted, then one column per series.
                "var labels=[],maps=[];for(var i=0;i<ser.length;i++){labels.push(ser[i].getAttribute('data-label')||('s'+i));" +
                "var m={};var p=pts(ser[i]);for(var j=0;j<p.length;j++){if(p[j].t>=x0&&p[j].t<=x1)m[p[j].t]=p[j].v;}maps.push(m);}" +
                "var tset={};for(var i=0;i<maps.length;i++)for(var t in maps[i])if(maps[i].hasOwnProperty(t))tset[t]=1;" +
                "var times=[];for(var t in tset)if(tset.hasOwnProperty(t))times.push(parseFloat(t));times.sort(function(a,b){return a-b;});" +
                "var out='time';for(var i=0;i<labels.length;i++)out+=','+csvCell(labels[i]);out+='\\n';" +
                "for(var r=0;r<times.length;r++){out+=fmtTime(times[r]);" +
                "for(var i=0;i<maps.length;i++){var v=maps[i][times[r]];out+=','+(v===undefined?'':v);}out+='\\n';}" +
                "var url='data:text/csv;charset=utf-8,'+encodeURIComponent(out);downloadDataUrl(url,fname(card,'csv'));}" +
                "function csvCell(s){s=''+s;if(/[\",\\n]/.test(s))s='\"'+s.replace(/\"/g,'\"\"')+'\"';return s;}" +
                // Build \"<metric>-<range>.<ext>\" filename from card data + active range.
                "function fname(card,ext){var m=(card.getAttribute('data-metric')||'chart').toLowerCase().replace(/[^a-z0-9]+/g,'-').replace(/^-|-$/g,'');" +
                "var r=ranged?(ranged.className.replace('grid','').replace('range-','').trim()||'60m'):'60m';" +
                "return m+'-'+r+'.'+ext;}" +

                // ---------- fullscreen overlay (offline-safe; CSS class, not the FS API) ----------
                "var fsBackdrop=null,fsCard=null;" +
                "function openFS(card){closeFS();fsBackdrop=document.createElement('div');fsBackdrop.className='nw-fs-backdrop';" +
                "fsBackdrop.addEventListener('click',closeFS);document.body.appendChild(fsBackdrop);" +
                "card.classList.add('nw-fs');document.body.classList.add('nw-fs-open');fsCard=card;}" +
                "function closeFS(){if(fsCard){fsCard.classList.remove('nw-fs');fsCard=null;}" +
                "if(fsBackdrop){fsBackdrop.parentNode&&fsBackdrop.parentNode.removeChild(fsBackdrop);fsBackdrop=null;}" +
                "document.body.classList.remove('nw-fs-open');}" +
                "document.addEventListener('keydown',function(e){if(e.key==='Escape')closeFS();});" +

                // ---------- per-chart toolbar dispatch ----------
                "function cardCtrls(card){var cs=card.querySelectorAll('svg.nw-chart');var r=[];" +
                "for(var i=0;i<cs.length;i++){if(cs[i]._nwc)r.push(cs[i]._nwc);}return r;}" +
                "var tbs=document.querySelectorAll('.nw-tb');" +
                "for(var i=0;i<tbs.length;i++){(function(b){" +
                "var card=cardOf(b);var act=b.getAttribute('data-act');" +
                // Reflect default latched state: area on.
                "if(act==='area')b.classList.add('nw-on');" +
                "b.addEventListener('click',function(){" +
                "if(act==='reset'){var cc=cardCtrls(card);for(var j=0;j<cc.length;j++)cc[j].reset();" +
                "var ab=card.querySelector('.nw-tb-area');if(ab)ab.classList.add('nw-on');" +
                "var sb=card.querySelector('.nw-tb-smooth');if(sb)sb.classList.remove('nw-on');" +
                "var lb=card.querySelector('.nw-tb-log');if(lb)lb.classList.remove('nw-on');}" +
                "else if(act==='png'){exportPNG(card);}" +
                "else if(act==='csv'){exportCSV(card);}" +
                "else if(act==='full'){if(card.classList.contains('nw-fs'))closeFS();else openFS(card);}" +
                "else if(act==='area'){var on=!b.classList.contains('nw-on');b.classList.toggle('nw-on',on);" +
                "var cc=cardCtrls(card);for(var j=0;j<cc.length;j++)cc[j].setArea(on);}" +
                "else if(act==='smooth'){var on=!b.classList.contains('nw-on');b.classList.toggle('nw-on',on);" +
                "var cc=cardCtrls(card);for(var j=0;j<cc.length;j++)cc[j].setSmooth(on);}" +
                "else if(act==='log'){var on=!b.classList.contains('nw-on');b.classList.toggle('nw-on',on);" +
                "var cc=cardCtrls(card);for(var j=0;j<cc.length;j++)cc[j].setLog(on);}" +
                "});})(tbs[i]);}" +

                // ---------- keyboard nav on a focused chart (tabindex=0) ----------
                // Make the active well focusable; Left/Right pan, +/- zoom, 0 resets.
                "var wraps=document.querySelectorAll('.svgwrap');" +
                "for(var i=0;i<wraps.length;i++){(function(wrap){" +
                "wrap.setAttribute('tabindex','0');" +
                "wrap.addEventListener('keydown',function(e){var svg=wrap.querySelector('svg.nw-chart');" +
                "if(!svg||!svg._nwc)return;var c=svg._nwc;var k=e.key;" +
                "if(k==='ArrowLeft'){c.panBy(-0.2);e.preventDefault();}" +
                "else if(k==='ArrowRight'){c.panBy(0.2);e.preventDefault();}" +
                "else if(k==='+'||k==='='){c.zoomBy(0.8);e.preventDefault();}" +
                "else if(k==='-'||k==='_'){c.zoomBy(1.25);e.preventDefault();}" +
                "else if(k==='0'){c.reset();e.preventDefault();}" +
                "});})(wraps[i]);}" +

                // ---------- pause auto-refresh + 'updated <ago>' hint ----------
                "var RKEY='netwatch.paused';var gen=parseFloat(document.body.getAttribute('data-gen'))||0;" +
                "var rbtn=document.getElementById('nw-refresh');var rupd=document.getElementById('nw-updated');" +
                "var reloadTimer=null;var paused=false;" +
                "try{paused=localStorage.getItem(RKEY)==='1';}catch(e){}" +
                "function scheduleReload(){if(reloadTimer){clearTimeout(reloadTimer);reloadTimer=null;}" +
                "if(!paused){reloadTimer=setTimeout(function(){location.reload();},60000);}}" +
                // Age from the encoded wall-clock: now-as-wall-clock (epoch-ms minus the
                // local TZ offset, matching the server's no-offset encoding) minus gen.
                "function agoTxt(){if(!gen)return '';" +
                "var nowWall=Date.now()-new Date().getTimezoneOffset()*60000;" +
                "var s=Math.max(0,Math.round((nowWall-gen)/1000));" +
                "if(s<60)return 'updated '+s+'s ago';var m=Math.round(s/60);return 'updated '+m+'m ago';}" +
                "function paintRefresh(){if(rbtn){rbtn.classList.toggle('nw-paused',paused);" +
                "rbtn.setAttribute('aria-pressed',paused?'true':'false');" +
                "var tx=rbtn.querySelector('.nw-refresh-txt');if(tx)tx.textContent=paused?'paused':'auto-refresh 60s';}" +
                "if(rupd)rupd.textContent=agoTxt();}" +
                "if(rbtn){rbtn.addEventListener('click',function(){paused=!paused;" +
                "try{localStorage.setItem(RKEY,paused?'1':'0');}catch(e){}paintRefresh();scheduleReload();});}" +
                "paintRefresh();scheduleReload();setInterval(paintRefresh,5000);" +

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

            // ---- per-chart toolbar (top-right; appears/solidifies on hover) ----
            c.Append(".metric{position:relative}");
            c.Append(".card-head{position:relative}");
            c.Append(".nw-tools{position:absolute;top:0;right:0;display:flex;gap:4px;" +
                "opacity:0;transform:translateY(-2px);transition:opacity .14s,transform .14s;" +
                "z-index:6}");
            c.Append(".metric:hover .nw-tools,.metric:focus-within .nw-tools{opacity:1;transform:none}");
            c.Append(".nw-tb{appearance:none;cursor:pointer;font:inherit;font-size:12px;line-height:1;" +
                "min-width:26px;height:26px;padding:0 6px;display:inline-flex;align-items:center;" +
                "justify-content:center;color:" + MutedColor + ";background:#0e131c;" +
                "border:1px solid " + PanelBorder + ";border-radius:7px;" +
                "transition:background .12s,color .12s,border-color .12s}");
            c.Append(".nw-tb:hover{color:" + TextColor + ";background:#172030;border-color:#2c3849}");
            c.Append(".nw-tb:focus-visible{outline:2px solid " + AccentCyan + ";outline-offset:1px}");
            c.Append(".nw-tb-csv,.nw-tb-log{font-size:10px;font-weight:700;letter-spacing:.03em}");
            // Active (latched) toggles: area on by default, smoothing/log off.
            c.Append(".nw-tb.nw-on{color:#06121d;background:" + AccentCyan + ";border-color:" + AccentCyan + "}");

            // ---- brush selection band (drag-to-zoom) ----
            c.Append(".nw-brush{fill:rgba(56,189,248,.16);stroke:rgba(56,189,248,.6);" +
                "stroke-width:1;pointer-events:none}");
            // Plot cursor affordances while interacting.
            c.Append(".nw-chart{cursor:crosshair}");
            c.Append(".nw-chart.nw-panning{cursor:grabbing}");

            // ---- dynamic (zoomed) redraw layer ----
            // The JS draws zoomed lines/areas/axes here; keep it above the static series
            // but below the crosshair overlay (which the JS appends afterwards).
            c.Append(".nw-dynamic{pointer-events:none}");
            c.Append(".nw-dyn-line{fill:none;stroke-width:1.75;stroke-linejoin:round;stroke-linecap:round}");
            c.Append(".nw-dyn-grid{stroke:" + "#1f2733" + ";stroke-width:1}");
            c.Append(".nw-dyn-txt{fill:" + MutedColor + ";font-size:10px}");
            // When a chart is zoomed, the server-static axes/grid/series are hidden by JS
            // via this class so only the dynamic layer shows.
            c.Append(".nw-chart.nw-zoomed .nw-xaxis,.nw-chart.nw-zoomed .nw-yaxis," +
                ".nw-chart.nw-zoomed .nw-grid,.nw-chart.nw-zoomed .nw-series{display:none}");
            // Area-fill toggle: hide gradient area paths (kept the stroke line) when off.
            c.Append(".nw-chart.nw-noarea .nw-series path[fill^=\"url(\"],");
            c.Append(".nw-chart.nw-noarea .nw-dyn-area{display:none}");
            c.Append(".nw-dyn-area{stroke:none}");
            // Smoothing overlay line (dashed, slightly transparent).
            c.Append(".nw-smooth-line{fill:none;stroke-width:1.4;stroke-dasharray:5 3;opacity:.85;" +
                "stroke-linejoin:round;stroke-linecap:round;pointer-events:none}");

            // ---- focus ring on a focused chart (keyboard nav target) ----
            c.Append(".chart:focus-within{outline:none}");
            c.Append(".svgwrap:focus-visible,.nw-chart:focus-visible{outline:2px solid " + AccentCyan + ";" +
                "outline-offset:2px;border-radius:8px}");

            // ---- pause auto-refresh pill (button variant of .pill) ----
            c.Append(".nw-refresh{appearance:none;cursor:pointer;font:inherit}");
            c.Append(".nw-refresh:focus-visible{outline:2px solid " + AccentGreen + ";outline-offset:2px}");
            // Paused state: amber treatment + halted pulse.
            c.Append(".pill.nw-paused{color:" + AccentAmber + ";background:rgba(251,191,36,.10);" +
                "border-color:rgba(251,191,36,.30)}");
            c.Append(".pill.nw-paused .pulse{background:" + AccentAmber + ";animation:none;box-shadow:none}");
            c.Append(".nw-updated{font-size:11px;color:" + MutedColor + ";white-space:nowrap;" +
                "align-self:center}");

            // ---- fullscreen overlay (pure CSS/JS, offline-safe; not the FS API) ----
            c.Append(".nw-fs-backdrop{position:fixed;inset:0;z-index:200;background:rgba(5,8,13,.86);" +
                "backdrop-filter:blur(3px);-webkit-backdrop-filter:blur(3px)}");
            c.Append(".metric.nw-fs{position:fixed;inset:18px;z-index:201;margin:0;overflow:auto;" +
                "display:flex;flex-direction:column;box-shadow:0 24px 80px rgba(0,0,0,.7)}");
            c.Append(".metric.nw-fs .chart-stack{flex:1 1 auto;display:flex;flex-direction:column;justify-content:center}");
            c.Append("body.nw-fs-open{overflow:hidden}");

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
