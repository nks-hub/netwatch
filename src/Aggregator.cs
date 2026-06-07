using System;
using System.Collections.Generic;
using System.Globalization;

namespace NetWatch
{
    /// <summary>
    /// Thread-safe rolling aggregation of ping/speed/trace/interface samples plus
    /// edge-triggered diagnostic event detection. A single lock guards all mutable
    /// state; every public method may be called concurrently from worker threads.
    /// Never throws out of public entry points.
    /// </summary>
    public sealed class Aggregator
    {
        private const int MaxSamplesPerTarget = 5000;
        private const int RecentEventsCap = 12;
        private const int DrainQueueCap = 256;
        private const int NoSignalSentinel = 101;

        // A target is only reported DOWN/"unreachable" after this many consecutive
        // failed samples, so one dropped probe (or bufferbloat during a speed test)
        // cannot raise a false alarm or flip the dashboard to DOWN.
        private const int UnreachableThreshold = 2;

        // The "high packet loss" event is only evaluated once the window holds at
        // least this many samples, so a tiny startup window cannot fire a false alarm.
        private const int MinLossSamples = 20;

        private readonly object _lock = new object();
        private readonly Config _cfg;
        private readonly int[] _windowsSec;
        private readonly long _maxWindowTicks;
        private readonly int _sparkLen;
        private readonly int _wifiWeakPct;
        private readonly double _highLatencyMs;
        private readonly double _highLossPct;

        private readonly Dictionary<string, TargetState> _targets =
            new Dictionary<string, TargetState>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _targetOrder = new List<string>();

        private readonly List<string> _recentEvents = new List<string>();
        private readonly List<string> _drainQueue = new List<string>();

        private InterfaceSample[] _interfaces = new InterfaceSample[0];
        private InterfaceSample _active;
        private VpnStatus _vpn;

        private SpeedResult _lastSpeed;
        private int _speedCount;
        private double _sumDown, _sumUp, _maxDown, _maxUp;

        private TraceResult _lastTrace;
        private int _traceChanges;

        private int _totalPings;
        private int _totalLost;
        private int _wifiMinSignal = NoSignalSentinel;

        // Edge-trigger memory.
        private bool _vpnPrevConnected;
        private bool _vpnPrevKnown;
        private string _prevActiveLabel;

        public Aggregator(Config cfg)
        {
            _cfg = cfg ?? Config.Default();

            var secs = new List<int>();
            foreach (var ts in _cfg.Windows())
            {
                int s = (int)Math.Round(ts.TotalSeconds);
                if (s > 0) secs.Add(s);
            }
            if (secs.Count == 0) secs.Add(60);
            _windowsSec = secs.ToArray();

            long maxTicks = 0;
            foreach (int s in _windowsSec)
            {
                long t = TimeSpan.FromSeconds(s).Ticks;
                if (t > maxTicks) maxTicks = t;
            }
            _maxWindowTicks = maxTicks > 0 ? maxTicks : TimeSpan.FromSeconds(60).Ticks;

            _sparkLen = _cfg.SparkLen > 0 ? _cfg.SparkLen : 40;
            _wifiWeakPct = _cfg.WifiWeakSignalPct;
            _highLatencyMs = _cfg.HighLatencyMs;
            _highLossPct = _cfg.HighLossPct;
        }

        public void AddPing(PingSample s)
        {
            if (s == null) return;
            lock (_lock)
            {
                try
                {
                    var key = s.Target ?? "";
                    if (!_targets.TryGetValue(key, out var st))
                    {
                        st = new TargetState(key);
                        _targets[key] = st;
                        _targetOrder.Add(key);
                    }

                    st.Addr = s.Addr;
                    st.Kind = s.Kind;
                    st.LastMs = s.Success ? s.RttMs : double.NaN;
                    st.HasLast = true;

                    var rec = new Sample
                    {
                        TimeTicks = s.Time.Ticks,
                        RttMs = s.RttMs,
                        Ok = s.Success
                    };
                    st.Samples.Add(rec);

                    _totalPings++;
                    if (!s.Success) _totalLost++;

                    st.SessCount++;
                    if (s.Success) { if (s.RttMs > st.SessMax) st.SessMax = s.RttMs; }
                    else st.SessLost++;

                    st.Spark.Add(s.Success ? s.RttMs : -1.0);
                    while (st.Spark.Count > _sparkLen) st.Spark.RemoveAt(0);

                    PruneTarget(st);
                    DetectPingEvents(st, s.Time);
                }
                catch
                {
                    // Never throw out of aggregation.
                }
            }
        }

        public void AddSpeed(SpeedResult r)
        {
            if (r == null) return;
            lock (_lock)
            {
                try
                {
                    _lastSpeed = r;
                    if (string.IsNullOrEmpty(r.Error))
                    {
                        _speedCount++;
                        _sumDown += r.DownMbps;
                        _sumUp += r.UpMbps;
                        if (r.DownMbps > _maxDown) _maxDown = r.DownMbps;
                        if (r.UpMbps > _maxUp) _maxUp = r.UpMbps;
                    }
                }
                catch { }
            }
        }

        public void AddTrace(TraceResult t)
        {
            if (t == null) return;
            lock (_lock)
            {
                try
                {
                    _lastTrace = t;
                    if (t.Changed)
                    {
                        _traceChanges++;
                        Emit("route to " + (t.Target ?? "target") + " changed");
                    }
                }
                catch { }
            }
        }

        public void AddInterfaces(InterfaceSample[] all, InterfaceSample active, VpnStatus vpn)
        {
            lock (_lock)
            {
                try
                {
                    // Clone the array so the sampler thread can keep mutating
                    // its own array without aliasing our backing store.
                    _interfaces = all == null
                        ? new InterfaceSample[0]
                        : (InterfaceSample[])all.Clone();
                    _active = active;
                    _vpn = vpn;

                    DetectInterfaceEvents(all, active, vpn);
                }
                catch { }
            }
        }

        public Snapshot Snapshot()
        {
            lock (_lock)
            {
                try
                {
                    long now = DateTime.UtcNow.Ticks;

                    var targets = new TargetSnapshot[_targetOrder.Count];
                    for (int i = 0; i < _targetOrder.Count; i++)
                    {
                        var st = _targets[_targetOrder[i]];
                        targets[i] = BuildTargetSnapshot(st, now);
                    }

                    var snap = new Snapshot
                    {
                        Time = DateTime.UtcNow,
                        Targets = targets,
                        ActiveInterface = _active,
                        Interfaces = (InterfaceSample[])_interfaces.Clone(),
                        Vpn = _vpn,
                        LastSpeed = _lastSpeed,
                        SpeedCount = _speedCount,
                        AvgDownMbps = _speedCount > 0 ? _sumDown / _speedCount : 0.0,
                        AvgUpMbps = _speedCount > 0 ? _sumUp / _speedCount : 0.0,
                        MaxDownMbps = _maxDown,
                        MaxUpMbps = _maxUp,
                        LastTrace = _lastTrace,
                        TraceChanges = _traceChanges,
                        TotalPings = _totalPings,
                        TotalLost = _totalLost,
                        RecentEvents = _recentEvents.ToArray(),
                        WifiMinSignalPct = _wifiMinSignal
                    };
                    return snap;
                }
                catch
                {
                    return new Snapshot
                    {
                        Time = DateTime.UtcNow,
                        Targets = new TargetSnapshot[0],
                        Interfaces = new InterfaceSample[0],
                        RecentEvents = new string[0],
                        WifiMinSignalPct = _wifiMinSignal
                    };
                }
            }
        }

        public string[] DrainEvents()
        {
            lock (_lock)
            {
                if (_drainQueue.Count == 0) return new string[0];
                var arr = _drainQueue.ToArray();
                _drainQueue.Clear();
                return arr;
            }
        }

        // --- Snapshot helpers --------------------------------------------------

        private TargetSnapshot BuildTargetSnapshot(TargetState st, long now)
        {
            var windows = new WindowStat[_windowsSec.Length];
            for (int w = 0; w < _windowsSec.Length; w++)
                windows[w] = ComputeWindow(st, _windowsSec[w], now);

            // Reflect debounced reachability so the dashboard does not flash DOWN on
            // a single miss: down only once UnreachableThreshold consecutive fails are
            // reached. Before the first sample (no data yet) report Up = false.
            bool up = st.HasLast && st.ConsecutiveFails < UnreachableThreshold;

            return new TargetSnapshot
            {
                Target = st.Key,
                Addr = st.Addr,
                Kind = st.Kind,
                Up = up,
                LastMs = st.HasLast ? st.LastMs : double.NaN,
                Windows = windows,
                Spark = st.Spark.ToArray(),
                SessionCount = st.SessCount,
                SessionLost = st.SessLost,
                SessionMaxMs = st.SessMax
            };
        }

        private WindowStat ComputeWindow(TargetState st, int windowSec, long now)
        {
            long cutoff = now - TimeSpan.FromSeconds(windowSec).Ticks;
            var ws = new WindowStat { WindowSec = windowSec };

            int count = 0, lost = 0;
            double min = double.MaxValue, max = double.MinValue, sum = 0.0;
            double prevOk = double.NaN;
            double jitterSum = 0.0;
            int jitterCount = 0;
            var successes = new List<double>();

            var samples = st.Samples;
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                if (s.TimeTicks < cutoff) continue;
                count++;
                if (!s.Ok)
                {
                    lost++;
                    prevOk = double.NaN; // gap breaks the consecutive-success jitter chain
                    continue;
                }
                double r = s.RttMs;
                successes.Add(r);
                sum += r;
                if (r < min) min = r;
                if (r > max) max = r;
                if (!double.IsNaN(prevOk))
                {
                    jitterSum += Math.Abs(r - prevOk);
                    jitterCount++;
                }
                prevOk = r;
            }

            ws.Count = count;
            ws.Lost = lost;
            ws.LossPct = count > 0 ? 100.0 * lost / count : 0.0;

            int okN = successes.Count;
            if (okN > 0)
            {
                ws.MinMs = min;
                ws.MaxMs = max;
                ws.AvgMs = sum / okN;
                successes.Sort();
                int idx = (int)Math.Ceiling(0.95 * okN) - 1;
                if (idx < 0) idx = 0;
                if (idx >= okN) idx = okN - 1;
                ws.P95Ms = successes[idx];
            }
            else
            {
                ws.MinMs = 0.0;
                ws.MaxMs = 0.0;
                ws.AvgMs = 0.0;
                ws.P95Ms = 0.0;
            }

            ws.JitterMs = jitterCount > 0 ? jitterSum / jitterCount : 0.0;
            return ws;
        }

        // --- Event detection ---------------------------------------------------

        private void DetectPingEvents(TargetState st, DateTime time)
        {
            bool sampleOk = !double.IsNaN(st.LastMs);

            // Debounced reachability: track consecutive failures so a single dropped
            // packet (or self-inflicted bufferbloat during a speed test) cannot flip
            // the target to DOWN or emit a false "is unreachable" event. The target
            // is only considered DOWN after >= UnreachableThreshold consecutive misses.
            if (sampleOk) st.ConsecutiveFails = 0;
            else st.ConsecutiveFails++;

            bool down = st.ConsecutiveFails >= UnreachableThreshold;
            bool debouncedUp = !down;

            if (!st.ReachKnown)
            {
                st.ReachKnown = true;
                st.WasUp = debouncedUp;
            }
            else if (debouncedUp != st.WasUp)
            {
                st.WasUp = debouncedUp;
                // "recovered" only fires after the target had actually reached the
                // down state and then a success arrives.
                Emit(debouncedUp
                    ? (st.Key + " recovered")
                    : (st.Key + " is unreachable"));
            }

            // 1-minute window stats for threshold crossings.
            var oneMin = ComputeWindow(st, 60, DateTime.UtcNow.Ticks);

            // Loss threshold (edge-triggered). Gated on a minimum sample size so a
            // tiny startup window (e.g. 1 loss out of 14 = 7%) cannot fire a false
            // high-loss event; the numeric LossPct in stats is still computed as-is.
            bool lossHigh = oneMin.Count >= MinLossSamples && oneMin.LossPct > _highLossPct;
            if (lossHigh && !st.LossHigh)
                Emit("high packet loss to " + st.Key + ": " +
                     oneMin.LossPct.ToString("0", CultureInfo.InvariantCulture) + "%");
            st.LossHigh = lossHigh;

            // Latency threshold (edge-triggered). Highlight VPN/Gateway kinds.
            bool latHigh = oneMin.Count > 0 && oneMin.AvgMs > _highLatencyMs;
            if (latHigh && !st.LatHigh)
            {
                string ms = oneMin.AvgMs.ToString("0", CultureInfo.InvariantCulture);
                if (st.Kind == TargetKind.Vpn)
                    Emit("VPN latency high: " + ms + " ms (1m avg)");
                else if (st.Kind == TargetKind.Gateway)
                    Emit("gateway latency high: " + ms + " ms (1m avg)");
                else
                    Emit("high latency to " + st.Key + ": " + ms + " ms (1m avg)");
            }
            st.LatHigh = latHigh;
        }

        private void DetectInterfaceEvents(InterfaceSample[] all, InterfaceSample active, VpnStatus vpn)
        {
            // Wi-Fi signal tracking + weak-signal edge trigger.
            if (all != null)
            {
                foreach (var ifc in all)
                {
                    if (ifc == null || !ifc.IsWifi) continue;
                    int sig = ifc.SignalPct;
                    if (sig > 0 && sig < _wifiMinSignal) _wifiMinSignal = sig;

                    // Edge-trigger per adapter so a strong adapter cannot clear a
                    // weak adapter's state (docking stations / USB Wi-Fi dongles).
                    string key = ifc.Name ?? ifc.Description ?? ifc.Bssid ?? "wifi";
                    bool weak = sig > 0 && sig < _wifiWeakPct;
                    bool prevWeak;
                    if (!_wifiWeakActive.TryGetValue(key, out prevWeak)) prevWeak = false;
                    if (weak && !prevWeak)
                        Emit("Wi-Fi signal weak: " + sig.ToString(CultureInfo.InvariantCulture) + "%");
                    if (sig > 0) _wifiWeakActive[key] = weak;
                }
            }

            // Active interface change.
            string activeLabel = active != null ? (active.Name ?? active.Description ?? "?") : null;
            if (_prevActiveLabel == null)
            {
                _prevActiveLabel = activeLabel;
            }
            else if (!string.Equals(_prevActiveLabel, activeLabel, StringComparison.Ordinal)
                     && activeLabel != null)
            {
                Emit("active link changed: " + (_prevActiveLabel ?? "none") + " -> " + activeLabel);
                _prevActiveLabel = activeLabel;
            }

            // VPN connect/disconnect.
            bool connected = vpn != null && vpn.Present && vpn.Connected;
            if (!_vpnPrevKnown)
            {
                _vpnPrevKnown = true;
                _vpnPrevConnected = connected;
            }
            else if (connected != _vpnPrevConnected)
            {
                _vpnPrevConnected = connected;
                if (connected)
                {
                    string vendor = vpn != null && !string.IsNullOrEmpty(vpn.Vendor)
                        ? vpn.Vendor
                        : (vpn != null && !string.IsNullOrEmpty(vpn.Name) ? vpn.Name : "VPN");
                    Emit("VPN connected: " + vendor);
                }
                else
                {
                    Emit("VPN disconnected");
                }
            }
        }

        // Per-adapter weak-Wi-Fi edge state, keyed by stable adapter identifier.
        private readonly Dictionary<string, bool> _wifiWeakActive =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private void Emit(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            _recentEvents.Add(msg);
            while (_recentEvents.Count > RecentEventsCap) _recentEvents.RemoveAt(0);
            // Bound the drain queue too: if the consumer stops draining (crash,
            // dead log thread, sustained outage flipping many targets), drop the
            // oldest events instead of leaking memory unboundedly.
            _drainQueue.Add(msg);
            while (_drainQueue.Count > DrainQueueCap) _drainQueue.RemoveAt(0);
        }

        private void PruneTarget(TargetState st)
        {
            // Drop samples older than the largest configured window (time bound).
            long cutoff = DateTime.UtcNow.Ticks - _maxWindowTicks;
            int drop = 0;
            var samples = st.Samples;
            while (drop < samples.Count && samples[drop].TimeTicks < cutoff) drop++;
            if (drop > 0) samples.RemoveRange(0, drop);

            // Bound by count to cap memory regardless of sample rate.
            int over = samples.Count - MaxSamplesPerTarget;
            if (over > 0) samples.RemoveRange(0, over);
        }

        // --- Internal state ----------------------------------------------------

        private sealed class Sample
        {
            public long TimeTicks;
            public double RttMs;
            public bool Ok;
        }

        private sealed class TargetState
        {
            public readonly string Key;
            public string Addr;
            public TargetKind Kind;
            public double LastMs;
            public bool HasLast;

            public readonly List<Sample> Samples = new List<Sample>();
            public readonly List<double> Spark = new List<double>();

            // Edge-trigger memory.
            public bool ReachKnown;
            public bool WasUp;
            public bool LossHigh;
            public bool LatHigh;

            // Consecutive failed samples, for debounced reachability.
            public int ConsecutiveFails;

            // Session-cumulative counters (since process start), for the status display.
            public int SessCount;
            public int SessLost;
            public double SessMax;

            public TargetState(string key)
            {
                Key = key;
                LastMs = double.NaN;
            }
        }
    }
}
