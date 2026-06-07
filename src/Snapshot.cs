using System;

namespace NetWatch
{
    /// <summary>Aggregated ping statistics for one target over one rolling time window.</summary>
    public sealed class WindowStat
    {
        public int WindowSec { get; set; }
        public int Count { get; set; }
        public int Lost { get; set; }
        public double LossPct { get; set; }
        public double MinMs { get; set; }
        public double AvgMs { get; set; }
        public double MaxMs { get; set; }
        public double P95Ms { get; set; }
        /// <summary>Mean absolute difference between consecutive RTTs (jitter), ms.</summary>
        public double JitterMs { get; set; }
    }

    /// <summary>Per-target view at snapshot time.</summary>
    public sealed class TargetSnapshot
    {
        public string Target { get; set; }
        public string Addr { get; set; }
        public TargetKind Kind { get; set; }
        /// <summary>True when the most recent sample succeeded.</summary>
        public bool Up { get; set; }
        /// <summary>Most recent RTT in milliseconds (NaN when unknown).</summary>
        public double LastMs { get; set; }
        /// <summary>One entry per configured window, in config order.</summary>
        public WindowStat[] Windows { get; set; }
        /// <summary>Recent RTTs in ms for a sparkline (oldest..newest); -1 marks a lost probe.</summary>
        public double[] Spark { get; set; }

        /// <summary>Total probes sent to this target since the session started.</summary>
        public int SessionCount { get; set; }
        /// <summary>Total dropped (failed) probes to this target since the session started.</summary>
        public int SessionLost { get; set; }
        /// <summary>Worst (maximum) RTT in ms observed for this target this session (0 when none).</summary>
        public double SessionMaxMs { get; set; }
    }

    /// <summary>Immutable copy of all aggregated state, safe to hand to UI and log writer.</summary>
    public sealed class Snapshot
    {
        public DateTime Time { get; set; }
        public TargetSnapshot[] Targets { get; set; }

        /// <summary>Active default-route adapter (may be null before first sample).</summary>
        public InterfaceSample ActiveInterface { get; set; }
        /// <summary>All operationally-up adapters.</summary>
        public InterfaceSample[] Interfaces { get; set; }
        public VpnStatus Vpn { get; set; }

        /// <summary>Most recent speed-test result (Time == default when none yet).</summary>
        public SpeedResult LastSpeed { get; set; }
        public int SpeedCount { get; set; }
        public double AvgDownMbps { get; set; }
        public double AvgUpMbps { get; set; }
        public double MaxDownMbps { get; set; }
        public double MaxUpMbps { get; set; }

        /// <summary>Most recent traceroute (Time == default when none yet).</summary>
        public TraceResult LastTrace { get; set; }
        public int TraceChanges { get; set; }

        public int TotalPings { get; set; }
        public int TotalLost { get; set; }
        /// <summary>Most recent diagnostic events (newest last), for the dashboard.</summary>
        public string[] RecentEvents { get; set; }
        /// <summary>Lowest Wi-Fi signal quality observed this session (101 = none seen).</summary>
        public int WifiMinSignalPct { get; set; }
    }
}
