using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NetWatch.Report
{
    /// <summary>
    /// Renders a <see cref="ChartSpec"/> as a single, self-contained inline SVG
    /// line chart: dark panel, Y-axis value gridlines with labels and unit, an
    /// X-axis with "HH:mm" (or "HH:mm:ss" for short spans) time labels, one
    /// polyline per series broken at gaps, and a legend for multi-series charts.
    /// Pure and side-effect free: no JavaScript, no external CSS or font refs.
    /// </summary>
    public static class SvgChart
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Panel palette (kept inline so the SVG opens identically offline).
        private const string PanelBg = "#0f172a";   // slate-900
        private const string GridColor = "#1e293b"; // slate-800
        private const string AxisColor = "#334155"; // slate-700
        private const string TextColor = "#cbd5e1"; // slate-300
        private const string MutedColor = "#64748b";// slate-500
        private const string TitleColor = "#f1f5f9";// slate-100

        // Plot-area insets inside the overall Width/Height.
        private const int PadLeft = 56;
        private const int PadRight = 16;
        private const int PadTop = 34;
        private const int PadBottom = 30;

        /// <summary>
        /// Build the inline SVG markup for the chart. Returns "" for a null spec
        /// or one with no usable plotting area; never throws.
        /// </summary>
        public static string Render(ChartSpec spec)
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

            var sb = new StringBuilder(2048);
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"")
              .Append(I(width)).Append("\" height=\"").Append(I(height))
              .Append("\" viewBox=\"0 0 ").Append(I(width)).Append(' ').Append(I(height))
              .Append("\" role=\"img\" font-family=\"Segoe UI, Arial, sans-serif\">");

            // Panel background.
            sb.Append("<rect x=\"0\" y=\"0\" width=\"").Append(I(width))
              .Append("\" height=\"").Append(I(height))
              .Append("\" rx=\"6\" fill=\"").Append(PanelBg).Append("\"/>");

            // Title.
            if (!string.IsNullOrEmpty(spec.Title))
            {
                sb.Append("<text x=\"").Append(N(left)).Append("\" y=\"20\" fill=\"")
                  .Append(TitleColor).Append("\" font-size=\"13\" font-weight=\"600\">")
                  .Append(Esc(spec.Title)).Append("</text>");
            }

            // ---- Y gridlines + value labels ----
            int ySteps = 4;
            string unit = string.IsNullOrEmpty(spec.YUnit) ? "" : spec.YUnit;
            for (int i = 0; i <= ySteps; i++)
            {
                double frac = (double)i / ySteps;
                double val = yMax - frac * (yMax - yMin);
                double y = top + frac * plotH;

                sb.Append("<line x1=\"").Append(N(left)).Append("\" y1=\"").Append(N(y))
                  .Append("\" x2=\"").Append(N(right)).Append("\" y2=\"").Append(N(y))
                  .Append("\" stroke=\"").Append(GridColor).Append("\" stroke-width=\"1\"/>");

                string label = FormatValue(val);
                if (i == 0 && unit.Length > 0) label += " " + unit;
                sb.Append("<text x=\"").Append(N(left - 6)).Append("\" y=\"").Append(N(y + 3))
                  .Append("\" fill=\"").Append(MutedColor)
                  .Append("\" font-size=\"10\" text-anchor=\"end\">")
                  .Append(Esc(label)).Append("</text>");
            }

            // ---- X axis time labels + verticals ----
            bool shortSpan = (to - from) < TimeSpan.FromMinutes(15);
            string timeFmt = shortSpan ? "HH:mm:ss" : "HH:mm";
            int xSteps = 5;
            for (int i = 0; i <= xSteps; i++)
            {
                double frac = (double)i / xSteps;
                double x = left + frac * plotW;
                DateTime t = from.AddTicks((long)(frac * spanTicks));

                sb.Append("<line x1=\"").Append(N(x)).Append("\" y1=\"").Append(N(top))
                  .Append("\" x2=\"").Append(N(x)).Append("\" y2=\"").Append(N(bottom))
                  .Append("\" stroke=\"").Append(GridColor).Append("\" stroke-width=\"1\"/>");

                string anchor = i == 0 ? "start" : (i == xSteps ? "end" : "middle");
                sb.Append("<text x=\"").Append(N(x)).Append("\" y=\"").Append(N(bottom + 14))
                  .Append("\" fill=\"").Append(MutedColor)
                  .Append("\" font-size=\"10\" text-anchor=\"").Append(anchor).Append("\">")
                  .Append(Esc(t.ToString(timeFmt, Inv))).Append("</text>");
            }

            // Axis frame (left + bottom).
            sb.Append("<line x1=\"").Append(N(left)).Append("\" y1=\"").Append(N(top))
              .Append("\" x2=\"").Append(N(left)).Append("\" y2=\"").Append(N(bottom))
              .Append("\" stroke=\"").Append(AxisColor).Append("\" stroke-width=\"1\"/>");
            sb.Append("<line x1=\"").Append(N(left)).Append("\" y1=\"").Append(N(bottom))
              .Append("\" x2=\"").Append(N(right)).Append("\" y2=\"").Append(N(bottom))
              .Append("\" stroke=\"").Append(AxisColor).Append("\" stroke-width=\"1\"/>");

            // ---- Data polylines (broken at Missing points) ----
            double yRange = yMax - yMin;
            if (yRange <= 0) yRange = 1; // guarded again so mapping is finite

            foreach (var s in series)
            {
                if (s == null || s.Points == null || s.Points.Count == 0) continue;
                string color = string.IsNullOrEmpty(s.Color) ? "#4ade80" : s.Color;

                var seg = new StringBuilder();
                int segCount = 0;
                foreach (var p in s.Points)
                {
                    if (p.Missing || double.IsNaN(p.Value) || double.IsInfinity(p.Value))
                    {
                        FlushSegment(sb, seg, color, ref segCount);
                        continue;
                    }

                    double xf = (p.Time - from).Ticks / spanTicks;
                    if (xf < 0) xf = 0; else if (xf > 1) xf = 1;
                    double x = left + xf * plotW;

                    double vf = (p.Value - yMin) / yRange;
                    if (vf < 0) vf = 0; else if (vf > 1) vf = 1;
                    double y = top + (1 - vf) * plotH;

                    if (seg.Length > 0) seg.Append(' ');
                    seg.Append(N(x)).Append(',').Append(N(y));
                }
                FlushSegment(sb, seg, color, ref segCount);
            }

            // ---- Legend (only when more than one series) ----
            if (series.Count > 1)
            {
                double lx = left + 4;
                double ly = top + 12;
                foreach (var s in series)
                {
                    if (s == null) continue;
                    string color = string.IsNullOrEmpty(s.Color) ? "#4ade80" : s.Color;
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

        // Emit one polyline for the points accumulated so far, then reset.
        // A single point is drawn as a tiny dot so isolated samples stay visible.
        private static void FlushSegment(StringBuilder sb, StringBuilder seg, string color, ref int segCount)
        {
            if (seg.Length == 0) return;
            string pts = seg.ToString();
            seg.Length = 0;
            segCount++;

            if (pts.IndexOf(' ') < 0)
            {
                // Exactly one coordinate pair -> draw a dot.
                int comma = pts.IndexOf(',');
                if (comma > 0)
                {
                    string cx = pts.Substring(0, comma);
                    string cy = pts.Substring(comma + 1);
                    sb.Append("<circle cx=\"").Append(cx).Append("\" cy=\"").Append(cy)
                      .Append("\" r=\"1.5\" fill=\"").Append(color).Append("\"/>");
                }
                return;
            }

            sb.Append("<polyline fill=\"none\" stroke=\"").Append(color)
              .Append("\" stroke-width=\"1.5\" stroke-linejoin=\"round\" stroke-linecap=\"round\" points=\"")
              .Append(pts).Append("\"/>");
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
