using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NetWatch.Report
{
    /// <summary>
    /// Builds a single self-contained HTML report (inline CSS + inline SVG, no
    /// external JS/CSS) from the daily CSV files NetWatch writes. For every metric
    /// it renders three side-by-side charts covering the last 5 minutes, last 60
    /// minutes and last 24 hours ending at <c>nowLocal</c>.
    ///
    /// The data is read via <see cref="CsvHistory"/> and rendered via
    /// <see cref="SvgChart"/>. Public entry points never throw.
    /// </summary>
    public static class HtmlReport
    {
        // ---- chart geometry / bucketing tuning ----
        private const int ChartWidth = 600;
        private const int ChartHeight = 200;
        private const int TargetPoints = 150; // aim ~120-180 points per chart

        // ---- the three time windows (seconds) ----
        private const int Win5Min = 5 * 60;
        private const int Win60Min = 60 * 60;
        private const int Win24H = 24 * 60 * 60;

        // ---- dark theme palette (matches the console dashboard accents) ----
        private const string BgColor = "#0f1117";
        private const string PanelColor = "#171a21";
        private const string PanelBorder = "#262b36";
        private const string TextColor = "#c9d1d9";
        private const string MutedColor = "#7d8590";
        private const string AccentColor = "#4ade80"; // green
        private const string HeadingColor = "#e6edf3";

        // Distinct line colours assigned to ping targets in order, then cycled.
        private static readonly string[] TargetColors =
        {
            "#4ade80", // green
            "#60a5fa", // blue
            "#f472b6", // pink
            "#fbbf24", // amber
            "#a78bfa", // violet
            "#34d399", // teal
            "#f87171", // red
            "#22d3ee", // cyan
            "#facc15", // yellow
            "#fb923c", // orange
        };

        private const string DownColor = "#60a5fa";  // download / recv
        private const string UpColor = "#4ade80";    // upload / send
        private const string LatencyColor = "#fbbf24";
        private const string SignalColor = "#22d3ee";

        /// <summary>
        /// Builds the full HTML page string for the last 24h of data ending at
        /// <paramref name="nowLocal"/>. Never throws; on failure returns a minimal
        /// HTML page describing the error.
        /// </summary>
        public static string Build(string logDir, DateTime nowLocal)
        {
            try
            {
                DateTime since = nowLocal.AddHours(-24);

                List<PingRecord> pings = SafeReadPings(logDir, since);
                List<SpeedRecord> speeds = SafeReadSpeed(logDir, since);
                List<IfaceRecord> ifaces = SafeReadIface(logDir, since);

                var sb = new StringBuilder(64 * 1024);
                WriteHead(sb, logDir, nowLocal);

                // LATENCY (ms) — one series per ping target, averaged per bucket.
                AppendMetricSection(sb, "Latency", "ms",
                    BuildPingSeriesFactory(pings, PingMetric.Latency), nowLocal,
                    "Round-trip time per target (averaged per bucket).");

                // PACKET LOSS (%) — failed/total per bucket per target.
                AppendMetricSection(sb, "Packet loss", "%",
                    BuildPingSeriesFactory(pings, PingMetric.Loss), nowLocal,
                    "Percentage of probes lost per bucket per target.", 0, 100);

                // JITTER (ms) — mean absolute consecutive RTT difference per bucket.
                AppendMetricSection(sb, "Jitter", "ms",
                    BuildPingSeriesFactory(pings, PingMetric.Jitter), nowLocal,
                    "Variability of round-trip time (mean abs. consecutive delta) per bucket.");

                // SPEED (Mbps) — download + upload from the speed test.
                AppendMetricSection(sb, "Speed", "Mbps",
                    BuildSpeedSeriesFactory(speeds, speedLatency: false), nowLocal,
                    "Speed-test download and upload throughput.");

                // SPEED-TEST LATENCY (ms) — small companion chart.
                AppendMetricSection(sb, "Speed-test latency", "ms",
                    BuildSpeedSeriesFactory(speeds, speedLatency: true), nowLocal,
                    "Idle latency reported by the speed test.");

                // THROUGHPUT (Mbps) — recv + send of the active adapter.
                AppendMetricSection(sb, "Adapter throughput", "Mbps",
                    BuildIfaceThroughputFactory(ifaces), nowLocal,
                    "Live receive / send throughput of the active network adapter.");

                // WI-FI SIGNAL (%) — only when there is Wi-Fi data.
                if (HasWifiData(ifaces))
                {
                    AppendMetricSection(sb, "Wi-Fi signal", "%",
                        BuildWifiSignalFactory(ifaces), nowLocal,
                        "Wi-Fi signal strength of the active wireless adapter.", 0, 100);
                }

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

                File.WriteAllText(full, html, new UTF8Encoding(false));
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

                // Per-target accumulators per bucket.
                int ti = 0;
                foreach (var target in order)
                {
                    var sums = new double[n];
                    var counts = new int[n];
                    var fails = new int[n];
                    // For jitter: keep the chronological RTT list per bucket.
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
                            // Latency / jitter only consider successful probes.
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
        // Section assembly
        // =====================================================================

        private static void AppendMetricSection(StringBuilder sb, string title, string yUnit,
            SeriesFactory factory, DateTime nowLocal, string description)
        {
            AppendMetricSection(sb, title, yUnit, factory, nowLocal, description, double.NaN, double.NaN);
        }

        private static void AppendMetricSection(StringBuilder sb, string title, string yUnit,
            SeriesFactory factory, DateTime nowLocal, string description, double yMin, double yMax)
        {
            sb.Append("<section class=\"metric\">");
            sb.Append("<h2>").Append(Esc(title));
            sb.Append(" <span class=\"unit\">(").Append(Esc(yUnit)).Append(")</span></h2>");
            if (!string.IsNullOrEmpty(description))
                sb.Append("<p class=\"desc\">").Append(Esc(description)).Append("</p>");

            sb.Append("<div class=\"charts\">");
            AppendWindowChart(sb, "5 min", title, yUnit, factory, nowLocal, Win5Min, yMin, yMax);
            AppendWindowChart(sb, "60 min", title, yUnit, factory, nowLocal, Win60Min, yMin, yMax);
            AppendWindowChart(sb, "24 h", title, yUnit, factory, nowLocal, Win24H, yMin, yMax);
            sb.Append("</div>");

            sb.Append("</section>");
        }

        private static void AppendWindowChart(StringBuilder sb, string windowLabel, string title, string yUnit,
            SeriesFactory factory, DateTime nowLocal, int windowSec, double yMin, double yMax)
        {
            DateTime from = nowLocal.AddSeconds(-windowSec);
            int bucketSec = BucketSec(windowSec);
            List<ChartSeries> series;
            try { series = factory(from, nowLocal, bucketSec) ?? new List<ChartSeries>(); }
            catch { series = new List<ChartSeries>(); }

            var spec = new ChartSpec
            {
                Title = title + " — " + windowLabel,
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

            sb.Append("<figure class=\"chart\">");
            sb.Append("<figcaption>").Append(Esc(windowLabel)).Append("</figcaption>");
            sb.Append("<div class=\"svgwrap\">").Append(svg).Append("</div>");
            AppendLegend(sb, series);
            sb.Append("</figure>");
        }

        private static void AppendLegend(StringBuilder sb, List<ChartSeries> series)
        {
            if (series == null || series.Count == 0) return;
            sb.Append("<div class=\"legend\">");
            foreach (var s in series)
            {
                if (s == null) continue;
                sb.Append("<span class=\"li\">");
                sb.Append("<i style=\"background:").Append(Esc(s.Color ?? "#888")).Append("\"></i>");
                sb.Append(Esc(s.Label ?? ""));
                sb.Append("</span>");
            }
            sb.Append("</div>");
        }

        // =====================================================================
        // HTML scaffolding
        // =====================================================================

        private static void WriteHead(StringBuilder sb, string logDir, DateTime nowLocal)
        {
            string machine;
            try { machine = Environment.MachineName; } catch { machine = "?"; }
            string generated = nowLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            string dir = string.IsNullOrEmpty(logDir) ? "(unknown)" : logDir;

            sb.Append("<!doctype html>");
            sb.Append("<html lang=\"en\"><head>");
            sb.Append("<meta charset=\"utf-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            // Auto-reload in the browser so an open tab shows fresh data while the app keeps
            // rewriting report.html in the background (matches the default report_interval_min).
            sb.Append("<meta http-equiv=\"refresh\" content=\"60\">");
            sb.Append("<title>NetWatch report — ").Append(Esc(machine)).Append("</title>");
            sb.Append("<style>").Append(Css()).Append("</style>");
            sb.Append("</head><body>");

            sb.Append("<header class=\"page-header\">");
            sb.Append("<h1>NetWatch report</h1>");
            sb.Append("<div class=\"meta\">");
            AppendMetaItem(sb, "Machine", machine);
            AppendMetaItem(sb, "Generated", generated);
            AppendMetaItem(sb, "Log directory", dir);
            sb.Append("</div>");
            sb.Append("<p class=\"sub\">Time-series over the last 5 minutes, 60 minutes and 24 hours.</p>");
            sb.Append("</header>");

            sb.Append("<main>");
        }

        private static void AppendMetaItem(StringBuilder sb, string label, string value)
        {
            sb.Append("<span class=\"meta-item\"><span class=\"k\">").Append(Esc(label))
              .Append("</span><span class=\"v\">").Append(Esc(value)).Append("</span></span>");
        }

        private static void WriteFoot(StringBuilder sb)
        {
            sb.Append("</main>");
            sb.Append("<footer class=\"page-footer\">Generated by NetWatch — self-contained offline report.</footer>");
            sb.Append("</body></html>");
        }

        private static string Css()
        {
            // Inline dark theme. Charts laid out three-up via flex, wrapping on
            // narrow viewports. No external fonts; system stack only.
            return
                "*{box-sizing:border-box}" +
                "body{margin:0;background:" + BgColor + ";color:" + TextColor + ";" +
                "font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;" +
                "font-size:14px;line-height:1.4}" +
                ".page-header{padding:24px 28px;border-bottom:1px solid " + PanelBorder + ";background:" + PanelColor + "}" +
                ".page-header h1{margin:0 0 10px;font-size:22px;color:" + HeadingColor + ";font-weight:600}" +
                ".page-header .sub{margin:10px 0 0;color:" + MutedColor + "}" +
                ".meta{display:flex;flex-wrap:wrap;gap:8px 22px}" +
                ".meta-item{display:flex;gap:6px;align-items:baseline}" +
                ".meta-item .k{color:" + MutedColor + ";font-size:12px;text-transform:uppercase;letter-spacing:.04em}" +
                ".meta-item .v{color:" + TextColor + ";font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace}" +
                "main{padding:20px 28px;display:flex;flex-direction:column;gap:22px}" +
                ".metric{background:" + PanelColor + ";border:1px solid " + PanelBorder + ";border-radius:10px;padding:16px 18px}" +
                ".metric h2{margin:0 0 2px;font-size:16px;color:" + HeadingColor + ";font-weight:600}" +
                ".metric h2 .unit{color:" + MutedColor + ";font-weight:400;font-size:13px}" +
                ".metric .desc{margin:0 0 14px;color:" + MutedColor + ";font-size:12.5px}" +
                ".charts{display:flex;flex-wrap:wrap;gap:16px}" +
                ".chart{flex:1 1 " + ChartWidth + "px;min-width:300px;max-width:100%;margin:0;" +
                "background:" + BgColor + ";border:1px solid " + PanelBorder + ";border-radius:8px;padding:10px}" +
                ".chart figcaption{color:" + AccentColor + ";font-size:12px;font-weight:600;" +
                "text-transform:uppercase;letter-spacing:.05em;margin-bottom:6px}" +
                ".svgwrap{width:100%;overflow:hidden}" +
                ".svgwrap svg{width:100%;height:auto;display:block}" +
                ".legend{display:flex;flex-wrap:wrap;gap:6px 14px;margin-top:8px}" +
                ".legend .li{display:flex;align-items:center;gap:5px;font-size:11.5px;color:" + MutedColor + "}" +
                ".legend .li i{width:11px;height:11px;border-radius:2px;display:inline-block}" +
                ".page-footer{padding:16px 28px;color:" + MutedColor + ";font-size:12px;" +
                "border-top:1px solid " + PanelBorder + "}";
        }

        private static string ErrorPage(Exception ex)
        {
            string msg = ex != null ? ex.Message : "unknown error";
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            sb.Append("<title>NetWatch report — error</title>");
            sb.Append("<style>body{background:").Append(BgColor).Append(";color:").Append(TextColor)
              .Append(";font-family:sans-serif;padding:24px}</style></head><body>");
            sb.Append("<h1>NetWatch report could not be generated</h1>");
            sb.Append("<p>").Append(Esc(msg)).Append("</p>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        // =====================================================================
        // Bucketing helpers
        // =====================================================================

        // Bucket width in seconds so a window yields ~TargetPoints buckets, with a
        // sensible floor so the 5-minute window doesn't ask for sub-second buckets.
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

        // Index of the bucket containing t, or -1 if t falls outside [from, from+n*bucket).
        private static int BucketIndex(DateTime t, DateTime from, int bucketSec, int n)
        {
            if (bucketSec <= 0) return -1;
            double offset = (t - from).TotalSeconds;
            if (offset < 0) return -1;
            int b = (int)(offset / bucketSec);
            if (b < 0 || b >= n) return -1;
            return b;
        }

        // Representative time of bucket i (its midpoint), used as the point's X.
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

        // Mean absolute difference between consecutive samples — a jitter proxy
        // that needs no second pass for the mean.
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
