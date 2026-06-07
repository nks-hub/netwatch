using System;
using System.Collections.Generic;

namespace NetWatch.Report
{
    /// <summary>One parsed row of the daily ping CSV (timestamps are LOCAL time).</summary>
    public struct PingRecord
    {
        public DateTime Time;
        public string Target;
        public bool Success;
        public double RttMs; // valid only when Success
    }

    /// <summary>One parsed row of the daily speed CSV (LOCAL time).</summary>
    public struct SpeedRecord
    {
        public DateTime Time;
        public double DownMbps;
        public double UpMbps;
        public double LatencyMs;
        public double JitterMs;
    }

    /// <summary>One parsed row of the daily interface CSV (LOCAL time).</summary>
    public struct IfaceRecord
    {
        public DateTime Time;
        public string Name;
        public bool IsWifi;
        public double RecvMbps;
        public double SendMbps;
        public int SignalPct;
    }

    /// <summary>A single point of a chart line.</summary>
    public struct ChartPoint
    {
        public DateTime Time;
        public double Value;
        public bool Missing; // true = gap / lost sample; the line should break here
    }

    /// <summary>One named line within a chart.</summary>
    public sealed class ChartSeries
    {
        public string Label;
        public string Color; // CSS colour, e.g. "#4ade80"
        public List<ChartPoint> Points;
    }

    /// <summary>A time-series line chart to be rendered as self-contained inline SVG.</summary>
    public sealed class ChartSpec
    {
        public string Title;
        public string YUnit;      // "ms" / "Mbps" / "%" / ""
        public int Width;         // px
        public int Height;        // px
        public DateTime FromTime; // x-axis start (LOCAL)
        public DateTime ToTime;   // x-axis end (LOCAL)
        public double YMin = double.NaN; // fixed min, or NaN for auto
        public double YMax = double.NaN; // fixed max, or NaN for auto
        public List<ChartSeries> Series;
    }
}
