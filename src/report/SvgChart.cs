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

            // Responsive root: viewBox + width:100%, no fixed pixel width attribute,
            // preserveAspectRatio so it scales cleanly inside its card.
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ")
              .Append(I(width)).Append(' ').Append(I(height))
              .Append("\" width=\"100%\" preserveAspectRatio=\"xMidYMid meet\" role=\"img\"")
              .Append(" style=\"display:block;width:100%;height:auto\"")
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

            // ---- Y gridlines + value labels (thin, muted; no heavy frame) ----
            int ySteps = 4;
            string unit = string.IsNullOrEmpty(spec.YUnit) ? "" : spec.YUnit;
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

            // ---- X axis time labels + faint verticals ----
            bool shortSpan = (to - from) < TimeSpan.FromMinutes(15);
            string timeFmt = shortSpan ? "HH:mm:ss" : "HH:mm";
            int xSteps = 5;
            for (int i = 0; i <= xSteps; i++)
            {
                double frac = (double)i / xSteps;
                double x = left + frac * plotW;
                DateTime t = from.AddTicks((long)(frac * spanTicks));

                // Skip the leftmost vertical so it doesn't double the Y baseline.
                if (i > 0)
                {
                    sb.Append("<line x1=\"").Append(N(x)).Append("\" y1=\"").Append(N(top))
                      .Append("\" x2=\"").Append(N(x)).Append("\" y2=\"").Append(N(bottom))
                      .Append("\" stroke=\"").Append(GridColor)
                      .Append("\" stroke-width=\"1\" shape-rendering=\"crispEdges\"/>");
                }

                string anchor = i == 0 ? "start" : (i == xSteps ? "end" : "middle");
                sb.Append("<text x=\"").Append(N(x)).Append("\" y=\"").Append(N(bottom + 16))
                  .Append("\" fill=\"").Append(MutedColor)
                  .Append("\" font-size=\"10\" text-anchor=\"").Append(anchor).Append("\">")
                  .Append(Esc(t.ToString(timeFmt, Inv))).Append("</text>");
            }

            // ---- Data series: area fill (clipped) + crisp stroke, broken at gaps ----
            double yRange = yMax - yMin;
            if (yRange <= 0) yRange = 1; // guarded again so mapping is finite

            gi = 0;
            foreach (var s in series)
            {
                if (s == null) { continue; }
                if (s.Points == null || s.Points.Count == 0) { gi++; continue; }

                string color = SeriesColor(s);
                string fillId = "url(#g" + uid + "_" + I(gi) + ")";

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

                    sb.Append("<rect x=\"").Append(N(lx)).Append("\" y=\"").Append(N(ly - 8))
                      .Append("\" width=\"10\" height=\"10\" rx=\"2\" fill=\"")
                      .Append(color).Append("\"/>");
                    sb.Append("<text x=\"").Append(N(lx + 14)).Append("\" y=\"").Append(N(ly))
                      .Append("\" fill=\"").Append(TextColor).Append("\" font-size=\"10\">")
                      .Append(Esc(label)).Append("</text>");

                    lx += 18 + label.Length * 6.2 + 14;
                }
            }

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
