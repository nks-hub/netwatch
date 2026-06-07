using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NetWatch.Report
{
    /// <summary>
    /// Renders a <see cref="ChartSpec"/> as a single, self-contained inline SVG
    /// area/line chart for the premium dark dashboard: responsive (viewBox + 100%
    /// width, no fixed pixel width), a soft transparent gradient area fill under
    /// each series with a crisp stroke on top, thin muted gridlines (no heavy
    /// frame), right-padded Y value labels (unit on the top tick) and "HH:mm"
    /// (or "HH:mm:ss" for short spans) X time labels, one path per series broken
    /// at gaps, and a legend for multi-series charts. Pure and side-effect free:
    /// no JavaScript, no external CSS or font refs. Never throws; returns "" for
    /// a null spec or one with no usable plotting area.
    /// </summary>
    public static class SvgChart
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Dashboard palette (kept inline so the SVG opens identically offline).
        private const string GridColor = "#1f2733";  // subtle gridlines
        private const string TextColor = "#cbd5e1";   // legend / labels
        private const string MutedColor = "#94a3b8";  // axis ticks
        private const string TitleColor = "#e5e7eb";  // bright text
        private const string DefaultSeries = "#38bdf8"; // accent cyan fallback

        // Plot-area insets inside the overall Width/Height.
        private const int PadLeft = 48;
        private const int PadRight = 14;
        private const int PadTop = 16;
        private const int PadBottom = 26;

        /// <summary>
        /// Build the inline SVG markup for the chart. Returns "" for a null spec
        /// or one with no usable plotting area; never throws.
        /// </summary>
        public static string Render(ChartSpec spec)
        {
            try
            {
                return RenderCore(spec);
            }
            catch
            {
                // Never throw: a malformed spec yields an empty string, not a crash.
                return "";
            }
        }

        private static string RenderCore(ChartSpec spec)
        {
            if (spec == null) return "";

            int width = spec.Width > 0 ? spec.Width : 640;
            int height = spec.Height > 0 ? spec.Height : 200;

            int plotW = width - PadLeft - PadRight;
            int plotH = height - PadTop - PadBottom;
            if (plotW <= 0 || plotH <= 0) return "";

            // X domain (time). Guard a zero/inverted span so mapping stays finite.
            DateTime from = spec.FromTime;
            DateTime to = spec.ToTime;
            double spanTicks = (to - from).Ticks;
            if (spanTicks <= 0) spanTicks = 1;

            var series = spec.Series ?? new List<ChartSeries>();

            // Y domain: honour fixed YMin/YMax when given, else auto from data.
            double yMin, yMax;
            ComputeYRange(spec, series, out yMin, out yMax);

            var left = (double)PadLeft;
            var top = (double)PadTop;
            var right = left + plotW;
            var bottom = top + plotH;

            // Per-render salt keeps gradient/clip ids unique across the several
            // SVGs embedded in one HTML page (ids are document-global in the DOM).
            string uid = MakeUid(spec, width, height);

            var sb = new StringBuilder(4096);

            // Epoch-millisecond X domain for the interactivity contract: encode the
            // LOCAL wall-clock directly (no timezone offset) so the report JS can map
            // a cursor time back to the same X positions used to draw the lines.
            double xFromMs = EpochMs(from);
            double xToMs = EpochMs(to);

            // Y unit string (also published to the root via data-unit for the tooltip).
            string unit = string.IsNullOrEmpty(spec.YUnit) ? "" : spec.YUnit;

            // Responsive root: viewBox + width:100%, no fixed pixel width attribute,
            // preserveAspectRatio so it scales cleanly inside its card. The class +
            // data-* attributes describe the PLOT RECTANGLE (viewBox user units) and
            // the data scales actually used below, so the report's inline JS can attach
            // a crosshair / tooltip / hover-dots and legend toggling without any
            // external library. See the contract in the surrounding feature notes.
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" class=\"nw-chart\" viewBox=\"0 0 ")
              .Append(I(width)).Append(' ').Append(I(height))
              .Append("\" width=\"100%\" preserveAspectRatio=\"xMidYMid meet\" role=\"img\"")
              .Append(" style=\"display:block;width:100%;height:auto\"")
              .Append(" data-l=\"").Append(N(left)).Append("\" data-t=\"").Append(N(top))
              .Append("\" data-w=\"").Append(N(plotW)).Append("\" data-h=\"").Append(N(plotH))
              .Append("\" data-xfrom=\"").Append(Ms(xFromMs)).Append("\" data-xto=\"").Append(Ms(xToMs))
              .Append("\" data-ymin=\"").Append(Num(yMin)).Append("\" data-ymax=\"").Append(Num(yMax))
              .Append("\" data-unit=\"").Append(Esc(unit)).Append('"')
              .Append(" font-family=\"-apple-system,&#39;Segoe UI&#39;,Roboto,Helvetica,Arial,sans-serif\">");

            // ---- defs: one soft gradient + one clip per series ----
            sb.Append("<defs>");
            sb.Append("<clipPath id=\"clip").Append(uid).Append("\">")
              .Append("<rect x=\"").Append(N(left)).Append("\" y=\"").Append(N(top))
              .Append("\" width=\"").Append(N(plotW)).Append("\" height=\"").Append(N(plotH))
              .Append("\"/></clipPath>");

            int gi = 0;
            foreach (var s in series)
            {
                if (s == null) continue;
                string color = SeriesColor(s);
                // Vertical gradient from the series colour (soft) to transparent.
                sb.Append("<linearGradient id=\"g").Append(uid).Append('_').Append(I(gi))
                  .Append("\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">")
                  .Append("<stop offset=\"0\" stop-color=\"").Append(color).Append("\" stop-opacity=\"0.28\"/>")
                  .Append("<stop offset=\"1\" stop-color=\"").Append(color).Append("\" stop-opacity=\"0\"/>")
                  .Append("</linearGradient>");
                gi++;
            }
            sb.Append("</defs>");

            // ---- Y axis: horizontal gridlines + value labels (thin, muted; no heavy
            // frame). Wrapped in g.nw-yaxis so the report JS can hide/replace the
            // server-rendered ticks + gridlines when the Y domain is rescaled. ----
            sb.Append("<g class=\"nw-yaxis\">");
            int ySteps = 4;
            for (int i = 0; i <= ySteps; i++)
            {
                double frac = (double)i / ySteps;
                double val = yMax - frac * (yMax - yMin);
                double y = top + frac * plotH;

                sb.Append("<line x1=\"").Append(N(left)).Append("\" y1=\"").Append(N(y))
                  .Append("\" x2=\"").Append(N(right)).Append("\" y2=\"").Append(N(y))
                  .Append("\" stroke=\"").Append(GridColor)
                  .Append("\" stroke-width=\"1\" shape-rendering=\"crispEdges\"/>");

                string label = FormatValue(val);
                if (i == 0 && unit.Length > 0) label += " " + unit;
                sb.Append("<text x=\"").Append(N(left - 8)).Append("\" y=\"").Append(N(y + 3))
                  .Append("\" fill=\"").Append(MutedColor)
                  .Append("\" font-size=\"10\" text-anchor=\"end\">")
                  .Append(Esc(label)).Append("</text>");
            }
            sb.Append("</g>");

            // ---- X axis: faint vertical gridlines (g.nw-grid) + time labels
            // (g.nw-xaxis). Split into two groups so the report JS can independently
            // hide/replace the vertical gridlines and the X tick texts on zoom/pan. ----
            bool shortSpan = (to - from) < TimeSpan.FromMinutes(15);
            string timeFmt = shortSpan ? "HH:mm:ss" : "HH:mm";
            int xSteps = 5;

            // Vertical gridlines first (kept below series/labels, identical to before).
            sb.Append("<g class=\"nw-grid\">");
            for (int i = 1; i <= xSteps; i++)
            {
                double frac = (double)i / xSteps;
                double x = left + frac * plotW;

                // Skip the leftmost vertical (i==0) so it doesn't double the Y baseline.
                sb.Append("<line x1=\"").Append(N(x)).Append("\" y1=\"").Append(N(top))
                  .Append("\" x2=\"").Append(N(x)).Append("\" y2=\"").Append(N(bottom))
                  .Append("\" stroke=\"").Append(GridColor)
                  .Append("\" stroke-width=\"1\" shape-rendering=\"crispEdges\"/>");
            }
            sb.Append("</g>");

            // X tick texts.
            sb.Append("<g class=\"nw-xaxis\">");
            for (int i = 0; i <= xSteps; i++)
            {
                double frac = (double)i / xSteps;
                double x = left + frac * plotW;
                DateTime t = from.AddTicks((long)(frac * spanTicks));

                string anchor = i == 0 ? "start" : (i == xSteps ? "end" : "middle");
                sb.Append("<text x=\"").Append(N(x)).Append("\" y=\"").Append(N(bottom + 16))
                  .Append("\" fill=\"").Append(MutedColor)
                  .Append("\" font-size=\"10\" text-anchor=\"").Append(anchor).Append("\">")
                  .Append(Esc(t.ToString(timeFmt, Inv))).Append("</text>");
            }
            sb.Append("</g>");

            // ---- Data series: area fill (clipped) + crisp stroke, broken at gaps ----
            double yRange = yMax - yMin;
            if (yRange <= 0) yRange = 1; // guarded again so mapping is finite

            gi = 0;
            foreach (var s in series)
            {
                if (s == null) { continue; }

                string color = SeriesColor(s);
                string fillId = "url(#g" + uid + "_" + I(gi) + ")";

                // Build the present-sample list ("epochMs,value" space-separated,
                // invariant culture, '.' decimal) the contract publishes on the
                // group so the report JS can drive crosshair/tooltip/hover-dots.
                // Missing / NaN / Infinity samples are simply omitted.
                string dataPoints = BuildPointsAttr(s, from);

                // Wrap EVERY visual element of this series (its gradient area path(s),
                // its stroke line path(s) and any dot circle(s)) in one group so a
                // single style.display toggle hides/shows the whole series. The group
                // is emitted even for an empty series so the legend can map to it.
                sb.Append("<g class=\"nw-series\" data-label=\"").Append(Esc(s.Label ?? ""))
                  .Append("\" data-color=\"").Append(Esc(color))
                  .Append("\" data-points=\"").Append(dataPoints).Append("\">");

                if (s.Points != null && s.Points.Count > 0)
                {
                    // Accumulate contiguous (non-missing) runs as point lists, then
                    // emit an area path and a stroke path per run. The clip keeps the
                    // soft fill inside the plot box even after the round-cap stroke.
                    var run = new List<double[]>();
                    foreach (var p in s.Points)
                    {
                        if (p.Missing || double.IsNaN(p.Value) || double.IsInfinity(p.Value))
                        {
                            FlushRun(sb, run, color, fillId, top, bottom, uid);
                            continue;
                        }

                        double xf = (p.Time - from).Ticks / spanTicks;
                        if (xf < 0) xf = 0; else if (xf > 1) xf = 1;
                        double x = left + xf * plotW;

                        double vf = (p.Value - yMin) / yRange;
                        if (vf < 0) vf = 0; else if (vf > 1) vf = 1;
                        double y = top + (1 - vf) * plotH;

                        run.Add(new[] { x, y });
                    }
                    FlushRun(sb, run, color, fillId, top, bottom, uid);
                }

                sb.Append("</g>");
                gi++;
            }

            // ---- Legend (only when more than one series) ----
            if (series.Count > 1)
            {
                double lx = left + 2;
                double ly = top + 2;
                foreach (var s in series)
                {
                    if (s == null) continue;
                    string color = SeriesColor(s);
                    string label = s.Label ?? "";

                    // Wrap each legend entry so the report JS can click-toggle the
                    // matching nw-series (by data-label) and hover-emphasise it.
                    sb.Append("<g class=\"nw-legend-item\" data-label=\"").Append(Esc(label))
                      .Append("\" data-color=\"").Append(Esc(color))
                      .Append("\" style=\"cursor:pointer\">");
                    sb.Append("<rect x=\"").Append(N(lx)).Append("\" y=\"").Append(N(ly - 8))
                      .Append("\" width=\"10\" height=\"10\" rx=\"2\" fill=\"")
                      .Append(color).Append("\"/>");
                    sb.Append("<text x=\"").Append(N(lx + 14)).Append("\" y=\"").Append(N(ly))
                      .Append("\" fill=\"").Append(TextColor).Append("\" font-size=\"10\">")
                      .Append(Esc(label)).Append("</text>");
                    sb.Append("</g>");

                    lx += 18 + label.Length * 6.2 + 14;
                }
            }

            // ---- Empty dynamic overlay layer ----
            // LAST child of the root <svg>: drawn on top of the series (and below the
            // JS crosshair overlay the report injects). The report JS draws zoomed
            // lines/areas/axes into this group; it is intentionally empty at render.
            sb.Append("<g class=\"nw-dynamic\"></g>");

            sb.Append("</svg>");
            return sb.ToString();
        }

        // Emit the soft area + crisp line for one contiguous run, then clear it.
        // A single point is drawn as a small dot so isolated samples stay visible.
        private static void FlushRun(StringBuilder sb, List<double[]> run, string color, string fillId,
            double top, double bottom, string uid)
        {
            if (run == null || run.Count == 0) return;

            if (run.Count == 1)
            {
                // Exactly one sample -> a dot (still inside the clip region).
                double cx = run[0][0];
                double cy = run[0][1];
                sb.Append("<circle cx=\"").Append(N(cx)).Append("\" cy=\"").Append(N(cy))
                  .Append("\" r=\"1.8\" fill=\"").Append(color).Append("\"/>");
                run.Clear();
                return;
            }

            // Baseline for the area fill is the plot bottom.
            double baseY = bottom;

            // Area path: down to baseline at the first x, across the line, back to
            // baseline at the last x, closed. Clipped to the plot box.
            var area = new StringBuilder(run.Count * 12 + 32);
            area.Append('M').Append(N(run[0][0])).Append(' ').Append(N(baseY));
            for (int i = 0; i < run.Count; i++)
                area.Append('L').Append(N(run[i][0])).Append(' ').Append(N(run[i][1]));
            area.Append('L').Append(N(run[run.Count - 1][0])).Append(' ').Append(N(baseY)).Append('Z');

            sb.Append("<path d=\"").Append(area).Append("\" fill=\"").Append(fillId)
              .Append("\" stroke=\"none\" clip-path=\"url(#clip").Append(uid).Append(")\"/>");

            // Stroke line on top: round join/cap, crisp colour.
            var line = new StringBuilder(run.Count * 12 + 8);
            line.Append('M').Append(N(run[0][0])).Append(' ').Append(N(run[0][1]));
            for (int i = 1; i < run.Count; i++)
                line.Append('L').Append(N(run[i][0])).Append(' ').Append(N(run[i][1]));

            sb.Append("<path d=\"").Append(line).Append("\" fill=\"none\" stroke=\"").Append(color)
              .Append("\" stroke-width=\"1.75\" stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");

            run.Clear();
        }

        // Resolve a series' line colour with a sensible accent fallback.
        private static string SeriesColor(ChartSeries s)
        {
            return (s == null || string.IsNullOrEmpty(s.Color)) ? DefaultSeries : s.Color;
        }

        // Resolve the vertical range from fixed bounds or the data, with padding.
        private static void ComputeYRange(ChartSpec spec, List<ChartSeries> series, out double yMin, out double yMax)
        {
            bool hasMin = !double.IsNaN(spec.YMin);
            bool hasMax = !double.IsNaN(spec.YMax);

            double dataMin = double.PositiveInfinity;
            double dataMax = double.NegativeInfinity;
            bool any = false;
            foreach (var s in series)
            {
                if (s == null || s.Points == null) continue;
                foreach (var p in s.Points)
                {
                    if (p.Missing || double.IsNaN(p.Value) || double.IsInfinity(p.Value)) continue;
                    if (p.Value < dataMin) dataMin = p.Value;
                    if (p.Value > dataMax) dataMax = p.Value;
                    any = true;
                }
            }

            if (!any)
            {
                // Empty/all-missing: fall back to fixed bounds or a neutral 0..1.
                yMin = hasMin ? spec.YMin : 0;
                yMax = hasMax ? spec.YMax : (yMin + 1);
                if (yMax <= yMin) yMax = yMin + 1;
                return;
            }

            yMin = hasMin ? spec.YMin : dataMin;
            yMax = hasMax ? spec.YMax : dataMax;

            // Add a little headroom only on auto-resolved edges.
            if (!hasMin || !hasMax)
            {
                double range = yMax - yMin;
                if (range <= 0)
                {
                    // Flat series: open a small symmetric window around the value.
                    double pad = Math.Abs(yMax) > 1e-9 ? Math.Abs(yMax) * 0.1 : 1;
                    if (!hasMin) yMin -= pad;
                    if (!hasMax) yMax += pad;
                }
                else
                {
                    double pad = range * 0.08;
                    if (!hasMin) yMin -= pad;
                    if (!hasMax) yMax += pad;
                    // Don't drag a non-negative metric below zero.
                    if (!hasMin && dataMin >= 0 && yMin < 0) yMin = 0;
                }
            }

            if (yMax <= yMin) yMax = yMin + 1;
        }

        // Compact value label (e.g. 0, 12.5, 1.2k) for axis ticks.
        private static string FormatValue(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "";
            double a = Math.Abs(v);
            if (a >= 1000)
            {
                double k = v / 1000.0;
                return k.ToString(a >= 100000 ? "0" : "0.#", Inv) + "k";
            }
            if (a >= 100) return v.ToString("0", Inv);
            if (a >= 10) return v.ToString("0.#", Inv);
            return v.ToString("0.##", Inv);
        }

        // A short, stable, DOM-safe id salt derived from the spec so gradient and
        // clip ids never collide between the multiple SVGs on one page.
        private static string MakeUid(ChartSpec spec, int width, int height)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (spec.Title ?? "").GetHashCode();
                h = h * 31 + (spec.YUnit ?? "").GetHashCode();
                h = h * 31 + spec.FromTime.Ticks.GetHashCode();
                h = h * 31 + spec.ToTime.Ticks.GetHashCode();
                h = h * 31 + width;
                h = h * 31 + height;
                if (spec.Series != null) h = h * 31 + spec.Series.Count;
                // Base-36, always positive, prefixed so it's a valid id start.
                uint u = (uint)h;
                return "n" + u.ToString("x", Inv);
            }
        }

        private static string I(int v)
        {
            return v.ToString(Inv);
        }

        // Epoch milliseconds for the interactivity contract. The LOCAL DateTime is
        // treated as the wall-clock to display: subtract the Unspecified-kind Unix
        // epoch so NO timezone offset is applied. The report JS formats this back
        // with getUTC* on new Date(ms), reproducing the same clock for any viewer.
        private static readonly DateTime UnixEpoch =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

        private static double EpochMs(DateTime dt)
        {
            return (dt - UnixEpoch).TotalMilliseconds;
        }

        // Integer-ms attribute value (no decimals, invariant), used for data-xfrom/xto.
        private static string Ms(double ms)
        {
            if (double.IsNaN(ms) || double.IsInfinity(ms)) return "0";
            return Math.Round(ms).ToString("0", Inv);
        }

        // Plain numeric attribute value (invariant, '.' decimal) for data-ymin/ymax
        // and for each sample's value inside data-points.
        private static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            return v.ToString("R", Inv);
        }

        // Compose the space-separated "epochMs,value" list of PRESENT samples for a
        // series (missing / NaN / Infinity omitted). Invariant culture, '.' decimal.
        // No XML-unsafe characters are produced (digits, '.', ',', '-', ' ' only).
        // The list is AUTHORITATIVE for the report JS (it redraws from it on zoom),
        // so it is exact, complete and SORTED ASCENDING by time.
        private static string BuildPointsAttr(ChartSeries s, DateTime from)
        {
            if (s == null || s.Points == null || s.Points.Count == 0) return "";

            // Collect present samples (epoch-ms key + original value) so we can emit
            // them in strictly ascending time order regardless of source ordering.
            var pts = new List<KeyValuePair<long, double>>(s.Points.Count);
            foreach (var p in s.Points)
            {
                if (p.Missing || double.IsNaN(p.Value) || double.IsInfinity(p.Value)) continue;
                long key = (long)Math.Round(EpochMs(p.Time));
                pts.Add(new KeyValuePair<long, double>(key, p.Value));
            }
            if (pts.Count == 0) return "";

            // Stable ascending sort by time (OrderBy is stable in System.Linq, but we
            // avoid Linq: List.Sort is fine here since equal times keep relative order
            // only loosely — duplicates at the same ms are rare bucket boundaries).
            pts.Sort((a, b) => a.Key.CompareTo(b.Key));

            var pb = new StringBuilder(pts.Count * 16);
            bool first = true;
            foreach (var kv in pts)
            {
                if (!first) pb.Append(' ');
                pb.Append(kv.Key.ToString("0", Inv)).Append(',').Append(Num(kv.Value));
                first = false;
            }
            return pb.ToString();
        }

        // Round coordinates to keep the markup small and finite.
        private static string N(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            return Math.Round(v, 2).ToString("0.##", Inv);
        }

        // Minimal XML text escaping for labels/titles.
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
