using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace NetWatch
{
    /// <summary>
    /// Console entry point. Wires the diagnostic modules together into either a
    /// single one-shot pass (-once) or a continuous live-monitoring session.
    /// All loop bodies are guarded so a transient failure never kills monitoring.
    /// </summary>
    public static class Program
    {
        private const string Version = BuildInfo.Version;
        private const string Repo = BuildInfo.Repo;
        private const string Author = BuildInfo.Author;

        /// <summary>
        /// True while a speed test is saturating the link. The ping and traceroute
        /// loops skip their cycles during this window (and a short drain margin after)
        /// so the self-inflicted bufferbloat is not recorded as packet loss / high RTT.
        /// </summary>
        private static volatile bool _speedtestActive;

        /// <summary>One monitored ping target. The address may be updated at runtime.</summary>
        private sealed class Target
        {
            public string Label;
            public string Host;        // original host/IP (for re-resolution)
            public string Addr;        // resolved IPv4
            public TargetKind Kind;
            public int Fails;          // consecutive failures, drives re-resolution
        }

        public static int Main(string[] args)
        {
            // ---- argument parsing ----
            string exeDir = ExeDir();
            string configPath = Path.Combine(exeDir, "netwatch.conf");
            string logDirOverride = null;
            bool once = false;
            bool noUi = false;
            bool frame = false;
            bool report = false;
            string reportPath = null; // optional explicit output path for -report
            int frameWidth = 100; // width for -frame rendering (optional arg overrides)

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a.ToLowerInvariant())
                {
                    case "-config":
                    case "--config":
                        if (i + 1 >= args.Length)
                        {
                            Console.Error.WriteLine("option " + a + " requires a value");
                            PrintUsage();
                            return 2;
                        }
                        configPath = args[++i];
                        break;
                    case "-logdir":
                    case "--logdir":
                        if (i + 1 >= args.Length)
                        {
                            Console.Error.WriteLine("option " + a + " requires a value");
                            PrintUsage();
                            return 2;
                        }
                        logDirOverride = args[++i];
                        break;
                    case "-once":
                    case "--once":
                        once = true;
                        break;
                    case "-no-ui":
                    case "--no-ui":
                    case "-noui":
                        noUi = true;
                        break;
                    case "-frame":
                    case "--frame":
                    case "-status":
                        frame = true;
                        // Optional width argument: consume the next token only when it
                        // parses as a positive integer (e.g. "-frame 160"); otherwise keep
                        // the default so "-frame" alone still works.
                        if (i + 1 < args.Length &&
                            int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fw) &&
                            fw > 0)
                        {
                            frameWidth = fw;
                            i++;
                        }
                        break;
                    case "-report":
                    case "--report":
                        report = true;
                        // Optional output path argument: consume the next token only when it
                        // is not another option (does not start with '-'); otherwise default
                        // to report.html in the log directory later on.
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                            reportPath = args[++i];
                        break;
                    case "-version":
                    case "--version":
                    case "-v":
                        Console.WriteLine("NetWatch " + Version);
                        Console.WriteLine(Repo);
                        Console.WriteLine(Author);
                        return 0;
                    case "-h":
                    case "-help":
                    case "--help":
                    case "/?":
                        PrintUsage();
                        return 0;
                    default:
                        Console.Error.WriteLine("Unknown argument: " + a);
                        PrintUsage();
                        return 2;
                }
            }

            // ---- configuration ----
            Config cfg;
            bool created = false;
            try
            {
                cfg = Config.LoadOrCreate(configPath, out created);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Failed to load config: " + ex.Message);
                cfg = Config.Default();
            }

            // ---- resolve log directory ----
            string logDir = ResolveLogDir(exeDir, logDirOverride, cfg.LogDir);

            // ---- one-shot HTML report (charts) then exit, no monitoring ----
            if (report)
            {
                string outPath = reportPath ?? Path.Combine(logDir, "report.html");
                try
                {
                    string written = NetWatch.Report.HtmlReport.Write(logDir, outPath, DateTime.Now);
                    if (string.IsNullOrEmpty(written))
                    {
                        Console.Error.WriteLine("Failed to write HTML report to " + outPath);
                        return 1;
                    }
                    Console.WriteLine(written);
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Failed to write HTML report: " + ex.Message);
                    return 1;
                }
            }

            // ---- interface sample for startup target building ----
            // Take two samples ~1s apart so throughput deltas are populated; a single
            // sample has no delta, so Active()'s traffic-based scoring would be blind and
            // could pick a non-default adapter (and thus the wrong Gateway/DNS to monitor).
            var thState = new ThroughputState();
            InterfaceSample[] firstScan;
            try
            {
                NetInfo.Sample(thState);
                Thread.Sleep(1000);
                firstScan = NetInfo.Sample(thState);
            }
            catch { firstScan = new InterfaceSample[0]; }

            InterfaceSample firstActive = null;
            VpnStatus firstVpn = null;
            try { firstActive = NetInfo.Active(firstScan); } catch { }
            try { firstVpn = NetInfo.DetectVpn(firstScan); } catch { }

            // ---- build the monitored target list ----
            var targets = new List<Target>();
            BuildTargets(cfg, firstActive, firstVpn, targets);

            // ---- create aggregator + log writer ----
            var agg = new Aggregator(cfg);
            LogWriter log;
            try { log = new LogWriter(logDir); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Failed to open log directory '" + logDir + "': " + ex.Message);
                return 1;
            }

            // ---- startup banner ----
            WriteBanner(log, cfg, firstActive, firstVpn, targets, configPath, logDir);
            if (created)
                log.Event("default config generated at " + configPath + " (edit targets/VPN settings and restart)");

            try
            {
                if (frame)
                    return RunFrame(cfg, agg, log, thState, targets, frameWidth);
                if (once)
                    return RunOnce(cfg, agg, log, thState, targets);
                return RunContinuous(cfg, agg, log, thState, targets, noUi);
            }
            finally
            {
                try { log.Dispose(); } catch { }
            }
        }

        // =====================================================================
        //  One-shot diagnostic pass
        // =====================================================================

        private static int RunOnce(Config cfg, Aggregator agg, LogWriter log,
            ThroughputState thState, List<Target> targets)
        {
            const int PingsPerTarget = 4;
            var report = new System.Text.StringBuilder();
            report.AppendLine("NetWatch one-shot diagnostic  " +
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            report.AppendLine(new string('-', 60));

            // Interfaces (two samples a second apart to obtain throughput deltas).
            InterfaceSample[] scan;
            InterfaceSample active = null;
            VpnStatus vpn = null;
            try
            {
                NetInfo.Sample(thState);
                Thread.Sleep(1000);
                scan = NetInfo.Sample(thState);
                active = NetInfo.Active(scan);
                vpn = NetInfo.DetectVpn(scan);
                agg.AddInterfaces(scan, active, vpn);
                if (active != null) log.Interface(active);
            }
            catch (Exception ex)
            {
                report.AppendLine("Interface scan error: " + ex.Message);
                scan = new InterfaceSample[0];
            }

            report.AppendLine(DescribeActive(active));
            report.AppendLine(DescribeVpn(vpn));
            report.AppendLine();

            // Ping each target N times and report min/avg/max/drops/loss.
            report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-22}{1,-18}{2,7}{3,7}{4,7}{5,7}{6,8}", "TARGET", "ADDR", "MIN", "AVG", "MAX", "DROPS", "LOSS%"));
            foreach (var t in targets)
            {
                int ok = 0;
                double min = double.MaxValue, sum = 0, max = 0;
                for (int i = 0; i < PingsPerTarget; i++)
                {
                    PingSample s;
                    try { s = Pinger.Ping(t.Label, t.Addr, t.Kind, cfg.PingTimeoutMs); }
                    catch (Exception ex) { s = FailedSample(t, ex.Message); }
                    agg.AddPing(s);
                    log.Ping(s);
                    if (s.Success) { ok++; sum += s.RttMs; if (s.RttMs < min) min = s.RttMs; if (s.RttMs > max) max = s.RttMs; }
                    Thread.Sleep(200);
                }
                int drops = PingsPerTarget - ok;
                double loss = 100.0 * drops / PingsPerTarget;
                string minS = ok > 0 ? min.ToString("0.##", CultureInfo.InvariantCulture) : "-";
                string avgS = ok > 0 ? (sum / ok).ToString("0.##", CultureInfo.InvariantCulture) : "-";
                string maxS = ok > 0 ? max.ToString("0.##", CultureInfo.InvariantCulture) : "-";
                report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-22}{1,-18}{2,7}{3,7}{4,7}{5,7}{6,8}",
                    Trunc(LabelOf(t), 21), Trunc(t.Addr ?? "-", 17), minS, avgS, maxS,
                    drops.ToString(CultureInfo.InvariantCulture),
                    loss.ToString("0.#", CultureInfo.InvariantCulture)));
            }
            report.AppendLine();

            // One traceroute to the configured trace target.
            try
            {
                var tr = Tracer.Run(cfg.TraceTarget, cfg.TraceMaxHops, cfg.PingTimeoutMs,
                    cfg.TraceReverseDns, null);
                agg.AddTrace(tr);
                log.Trace(tr);
                report.AppendLine(DescribeTrace(tr));
            }
            catch (Exception ex)
            {
                report.AppendLine("Traceroute error: " + ex.Message);
            }
            report.AppendLine();

            // One speed test, if enabled.
            if (cfg.SpeedtestEnabled)
            {
                report.AppendLine("Speedtest (this may take ~30s)...");
                try
                {
                    var sp = SpeedTest.Run();
                    agg.AddSpeed(sp);
                    log.Speed(sp);
                    report.AppendLine(DescribeSpeed(sp));
                }
                catch (Exception ex)
                {
                    report.AppendLine("Speedtest error: " + ex.Message);
                }
            }
            else
            {
                report.AppendLine("Speedtest: disabled");
            }

            string text = report.ToString();
            Console.WriteLine();
            Console.WriteLine(text);

            // Persist the same report to the human log and a session summary/CSVs.
            log.Event("one-shot diagnostic pass:");
            foreach (var line in text.Split('\n'))
                log.Event("  " + line.TrimEnd('\r'));
            log.Summary(agg.Snapshot());
            FlushEvents(agg, log);
            return 0;
        }

        // =====================================================================
        //  Single status frame (renders one dashboard frame to stdout, then exits)
        // =====================================================================

        private static int RunFrame(Config cfg, Aggregator agg, LogWriter log,
            ThroughputState thState, List<Target> targets, int width)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

            // Sample interfaces twice (1s apart) for throughput deltas + VPN detection.
            try
            {
                NetInfo.Sample(thState);
                Thread.Sleep(1000);
                var scan = NetInfo.Sample(thState);
                var active = NetInfo.Active(scan);
                var vpn = NetInfo.DetectVpn(scan);
                agg.AddInterfaces(scan, active, vpn);
                if (active != null) log.Interface(active);
            }
            catch { }

            // A few pings per target to populate the live stats (no speed test here).
            for (int round = 0; round < 4; round++)
            {
                foreach (var t in targets)
                {
                    PingSample s;
                    try { s = Pinger.Ping(t.Label, t.Addr, t.Kind, cfg.PingTimeoutMs); }
                    catch (Exception ex) { s = FailedSample(t, ex.Message); }
                    agg.AddPing(s);
                    log.Ping(s);
                }
                Thread.Sleep(200);
            }

            // One speed test so the snapshot is complete (status note goes to stderr so it
            // does not pollute the rendered frame on stdout).
            if (cfg.SpeedtestEnabled)
            {
                try
                {
                    Console.Error.WriteLine("measuring internet speed (~30s)...");
                    var sr = SpeedTest.Run();
                    agg.AddSpeed(sr);
                    log.Speed(sr);
                }
                catch { }
            }

            Console.Write(Dashboard.RenderToText(agg.Snapshot(), cfg, DateTime.UtcNow, width > 0 ? width : 100));
            FlushEvents(agg, log);
            return 0;
        }

        // =====================================================================
        //  Continuous monitoring
        // =====================================================================

        private static int RunContinuous(Config cfg, Aggregator agg, LogWriter log,
            ThroughputState thState, List<Target> targets, bool noUi)
        {
            string logDir = log.Dir;
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            var targetsLock = new object();
            DateTime startedUtc = DateTime.UtcNow;

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true; // we shut down gracefully ourselves
                try { cts.Cancel(); } catch { }
            };

            bool uiActive = !noUi && Dashboard.Available;
            if (uiActive)
            {
                try { Dashboard.Init(); } catch { uiActive = false; }
            }

            var loops = new List<Task>();
            var loopsLock = new object();

            // --- ping loops, one per target, staggered to spread bursts ---
            int pingInterval = cfg.PingIntervalMs > 0 ? cfg.PingIntervalMs : 1000;
            Target[] snapshotTargets;
            lock (targetsLock) snapshotTargets = targets.ToArray();
            for (int i = 0; i < snapshotTargets.Length; i++)
            {
                Target t = snapshotTargets[i];
                int stagger = snapshotTargets.Length > 0
                    ? (int)((long)pingInterval * i / snapshotTargets.Length)
                    : 0;
                loops.Add(Task.Run(() => PingLoop(t, cfg, agg, log, pingInterval, stagger, token), token));
            }

            // --- interface loop (may add ping loops at runtime for late-appearing
            //     gateway/VPN/DNS targets, so it needs the loop list + lock) ---
            loops.Add(Task.Run(() => InterfaceLoop(cfg, agg, log, thState, targets, targetsLock,
                loops, loopsLock, pingInterval, token), token));

            // --- trace loop ---
            loops.Add(Task.Run(() => TraceLoop(cfg, agg, log, token), token));

            // --- speed loop ---
            loops.Add(Task.Run(() => SpeedLoop(cfg, agg, log, token), token));

            // --- event drain loop ---
            loops.Add(Task.Run(() => EventLoop(agg, log, token), token));

            // --- summary loop ---
            loops.Add(Task.Run(() => SummaryLoop(cfg, agg, log, token), token));

            // --- HTML report loop (writes a fresh report.html next to the logs) ---
            if (cfg.ReportIntervalMin > 0)
                loops.Add(Task.Run(() => ReportLoop(cfg, log, logDir, token), token));

            // --- UI loop (foreground-ish; we just block Main on it) ---
            if (uiActive)
                UiLoop(cfg, agg, startedUtc, token);
            else
                WaitNoUi(agg, log, token);

            // ---- graceful shutdown ----
            try { cts.Cancel(); } catch { }
            Task[] allLoops;
            lock (loopsLock) allLoops = loops.ToArray();
            try { Task.WaitAll(allLoops, 5000); } catch { }

            FlushEvents(agg, log);
            log.Event("stopped");
            log.Summary(agg.Snapshot());
            try { Console.CursorVisible = true; } catch { }
            try { Console.ResetColor(); } catch { }
            return 0;
        }

        // ---- individual loops ----

        private static void PingLoop(Target t, Config cfg, Aggregator agg, LogWriter log,
            int intervalMs, int staggerMs, CancellationToken token)
        {
            if (Sleep(staggerMs, token)) return;
            while (!token.IsCancellationRequested)
            {
                // Skip this cycle while a speed test saturates the link, otherwise the
                // self-inflicted bufferbloat would be recorded as loss / elevated RTT.
                if (_speedtestActive)
                {
                    if (Sleep(intervalMs, token)) return;
                    continue;
                }
                try
                {
                    string addr;
                    lock (t) addr = t.Addr;

                    PingSample s;
                    if (string.IsNullOrEmpty(addr))
                        s = FailedSample(t, "NoAddress");
                    else
                        s = Pinger.Ping(t.Label, addr, t.Kind, cfg.PingTimeoutMs);

                    agg.AddPing(s);
                    log.Ping(s);

                    // Re-resolve hostname targets after repeated failures.
                    if (!s.Success)
                    {
                        int fails = Interlocked.Increment(ref t.Fails);
                        if (fails >= 5 && !IsLiteralIp(t.Host))
                        {
                            string re = TryResolve(t.Host);
                            if (!string.IsNullOrEmpty(re) && re != addr)
                            {
                                lock (t) t.Addr = re;
                                log.Event(t.Label + " re-resolved to " + re);
                            }
                            t.Fails = 0;
                        }
                    }
                    else
                    {
                        t.Fails = 0;
                    }
                }
                catch (Exception ex)
                {
                    SafeEvent(log, "ping loop error (" + t.Label + "): " + ex.Message);
                }
                if (Sleep(intervalMs, token)) return;
            }
        }

        private static void InterfaceLoop(Config cfg, Aggregator agg, LogWriter log,
            ThroughputState thState, List<Target> targets, object targetsLock,
            List<Task> loops, object loopsLock, int pingInterval, CancellationToken token)
        {
            int intervalMs = (cfg.InterfaceIntervalSec > 0 ? cfg.InterfaceIntervalSec : 2) * 1000;
            string lastGw = null;
            string lastVpnGw = null;
            string lastDns = null;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var scan = NetInfo.Sample(thState);
                    var active = NetInfo.Active(scan);
                    var vpn = NetInfo.DetectVpn(scan);
                    agg.AddInterfaces(scan, active, vpn);
                    if (active != null) log.Interface(active);

                    // Track gateway changes; update or, if absent at startup, create+start.
                    string gw = active != null ? active.Gateway : null;
                    if (cfg.MonitorGateway && !string.IsNullOrEmpty(gw) && gw != lastGw)
                    {
                        ReconcileTarget(targets, targetsLock, loops, loopsLock, cfg, agg, log,
                            pingInterval, token, TargetKind.Gateway, "Gateway", gw, "gateway");
                        lastGw = gw;
                    }

                    // Track VPN gateway changes; update or create+start.
                    string vgw = (vpn != null && vpn.Present) ? vpn.Gateway : null;
                    if (cfg.MonitorVpn && !string.IsNullOrEmpty(vgw) && vgw != lastVpnGw)
                    {
                        ReconcileTarget(targets, targetsLock, loops, loopsLock, cfg, agg, log,
                            pingInterval, token, TargetKind.Vpn, "VPN-GW", vgw, "VPN gateway");
                        lastVpnGw = vgw;
                    }

                    // Track the active adapter's first DNS server (never updated before),
                    // so a DNS built for the wrong adapter at startup self-corrects.
                    string dns = (active != null && active.DnsServers != null && active.DnsServers.Length > 0)
                        ? active.DnsServers[0] : null;
                    if (!string.IsNullOrEmpty(dns) && dns != lastDns)
                    {
                        ReconcileTarget(targets, targetsLock, loops, loopsLock, cfg, agg, log,
                            pingInterval, token, TargetKind.Dns, "DNS", dns, "DNS server");
                        lastDns = dns;
                    }
                }
                catch (Exception ex)
                {
                    SafeEvent(log, "interface loop error: " + ex.Message);
                }
                if (Sleep(intervalMs, token)) return;
            }
        }

        private static void TraceLoop(Config cfg, Aggregator agg, LogWriter log, CancellationToken token)
        {
            if (Sleep(5000, token)) return; // first run ~5s after start
            int intervalMs = (cfg.TraceIntervalSec > 0 ? cfg.TraceIntervalSec : 300) * 1000;
            Hop[] prev = null;

            while (!token.IsCancellationRequested)
            {
                // Skip this cycle while a speed test saturates the link (same rationale
                // as the ping loop: avoid recording self-inflicted loss / high RTT).
                if (_speedtestActive)
                {
                    if (Sleep(intervalMs, token)) return;
                    continue;
                }
                try
                {
                    var tr = Tracer.Run(cfg.TraceTarget, cfg.TraceMaxHops, cfg.PingTimeoutMs,
                        cfg.TraceReverseDns, prev);
                    agg.AddTrace(tr);
                    log.Trace(tr);
                    if (tr.Hops != null && tr.Hops.Length > 0) prev = tr.Hops;
                }
                catch (Exception ex)
                {
                    SafeEvent(log, "trace loop error: " + ex.Message);
                }
                if (Sleep(intervalMs, token)) return;
            }
        }

        private static void SpeedLoop(Config cfg, Aggregator agg, LogWriter log, CancellationToken token)
        {
            if (!cfg.SpeedtestEnabled) return;

            if (cfg.SpeedtestOnStart)
            {
                if (Sleep(10000, token)) return; // ~10s after start
                RunSpeed(agg, log, token);
            }

            int intervalMs = (cfg.SpeedtestIntervalMin > 0 ? cfg.SpeedtestIntervalMin : 30) * 60 * 1000;
            while (!token.IsCancellationRequested)
            {
                if (Sleep(intervalMs, token)) return;
                if (!cfg.SpeedtestEnabled) continue;
                RunSpeed(agg, log, token);
            }
        }

        private static void RunSpeed(Aggregator agg, LogWriter log, CancellationToken token)
        {
            if (token.IsCancellationRequested) return;
            // Pause the ping/trace loops while the test saturates the link, then keep
            // them paused for a short drain margin so buffers empty before sampling
            // resumes. Runs on the dedicated speed thread, so the sleep is harmless.
            _speedtestActive = true;
            try
            {
                var sp = SpeedTest.Run();
                agg.AddSpeed(sp);
                log.Speed(sp);
            }
            catch (Exception ex)
            {
                SafeEvent(log, "speedtest error: " + ex.Message);
            }
            finally
            {
                try { Thread.Sleep(1500); } catch { }
                _speedtestActive = false;
            }
        }

        private static void EventLoop(Aggregator agg, LogWriter log, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { FlushEvents(agg, log); }
                catch { }
                if (Sleep(1000, token)) return;
            }
        }

        private static void SummaryLoop(Config cfg, Aggregator agg, LogWriter log, CancellationToken token)
        {
            int intervalMs = (cfg.SummaryIntervalSec > 0 ? cfg.SummaryIntervalSec : 60) * 1000;
            while (!token.IsCancellationRequested)
            {
                if (Sleep(intervalMs, token)) return;
                try { log.Summary(agg.Snapshot()); }
                catch (Exception ex) { SafeEvent(log, "summary loop error: " + ex.Message); }
            }
        }

        private static void ReportLoop(Config cfg, LogWriter log, string logDir, CancellationToken token)
        {
            int intervalMs = (cfg.ReportIntervalMin > 0 ? cfg.ReportIntervalMin : 1) * 60 * 1000;
            string outPath = Path.Combine(logDir, "report.html");
            while (!token.IsCancellationRequested)
            {
                try { NetWatch.Report.HtmlReport.Write(logDir, outPath, DateTime.Now); }
                catch (Exception ex) { SafeEvent(log, "report loop error: " + ex.Message); }
                if (Sleep(intervalMs, token)) return;
            }
        }

        private static void UiLoop(Config cfg, Aggregator agg, DateTime startedUtc, CancellationToken token)
        {
            int intervalMs = cfg.ConsoleRefreshMs > 0 ? cfg.ConsoleRefreshMs : 1000;
            while (!token.IsCancellationRequested)
            {
                try { Dashboard.Render(agg.Snapshot(), cfg, startedUtc); }
                catch { }
                if (Sleep(intervalMs, token)) return;
            }
        }

        private static void WaitNoUi(Aggregator agg, LogWriter log, CancellationToken token)
        {
            Console.WriteLine("NetWatch " + Version + " running (no UI). Press Ctrl+C to stop. Logs: " + log.Dir);
            while (!token.IsCancellationRequested)
            {
                if (Sleep(1000, token)) return;
            }
        }

        // =====================================================================
        //  Target construction
        // =====================================================================

        private static void BuildTargets(Config cfg, InterfaceSample active, VpnStatus vpn, List<Target> targets)
        {
            // Internet targets.
            foreach (var host in cfg.InternetTargets ?? new string[0])
                AddResolved(targets, host, host, TargetKind.Internet, null);

            // Gateway (router).
            if (cfg.MonitorGateway && active != null && !string.IsNullOrEmpty(active.Gateway))
                AddTarget(targets, "Gateway", active.Gateway, active.Gateway, TargetKind.Gateway);

            // VPN tunnel gateway.
            if (cfg.MonitorVpn && vpn != null && vpn.Present && !string.IsNullOrEmpty(vpn.Gateway))
                AddTarget(targets, "VPN-GW", vpn.Gateway, vpn.Gateway, TargetKind.Vpn);

            // VPN public server endpoint.
            if (!string.IsNullOrWhiteSpace(cfg.VpnServer))
                AddResolved(targets, "VPN-Server", cfg.VpnServer, TargetKind.Vpn, "VPN-Server");

            // VPN-only hosts.
            foreach (var host in cfg.VpnTargets ?? new string[0])
                AddResolved(targets, "VPN " + host, host, TargetKind.Vpn, null);

            // Active adapter's first DNS server.
            if (active != null && active.DnsServers != null && active.DnsServers.Length > 0)
            {
                string dns = active.DnsServers[0];
                if (!string.IsNullOrEmpty(dns))
                    AddTarget(targets, "DNS", dns, dns, TargetKind.Dns);
            }
        }

        private static void AddResolved(List<Target> targets, string label, string host,
            TargetKind kind, string forcedLabel)
        {
            string addr = TryResolve(host);
            if (string.IsNullOrEmpty(addr))
            {
                Console.Error.WriteLine("warning: could not resolve '" + host + "' (skipped)");
                return;
            }
            AddTarget(targets, forcedLabel ?? label, host, addr, kind);
        }

        private static void AddTarget(List<Target> targets, string label, string host,
            string addr, TargetKind kind)
        {
            // Avoid duplicate labels / addresses cluttering the dashboard.
            foreach (var t in targets)
                if (string.Equals(t.Label, label, StringComparison.OrdinalIgnoreCase)) return;
            targets.Add(new Target { Label = label, Host = host, Addr = addr, Kind = kind, Fails = 0 });
        }

        /// <summary>Outcome of reconciling a runtime-detected target's address.</summary>
        private enum TargetSync { Unchanged, Updated, Created }

        /// <summary>
        /// Updates the matching target's address, or creates it under the lock when no
        /// match exists. Returns whether nothing changed, the address was updated, or a
        /// new target was created (in which case the caller must start its ping loop).
        /// The created target is returned via <paramref name="created"/>.
        /// </summary>
        private static TargetSync UpdateTargetAddr(List<Target> targets, object targetsLock,
            TargetKind kind, string label, string newAddr, out Target created)
        {
            created = null;
            lock (targetsLock)
            {
                foreach (var t in targets)
                {
                    if (t.Kind == kind && string.Equals(t.Label, label, StringComparison.OrdinalIgnoreCase))
                    {
                        string old;
                        lock (t) old = t.Addr;
                        if (old == newAddr) return TargetSync.Unchanged;
                        lock (t) { t.Addr = newAddr; t.Host = newAddr; }
                        return TargetSync.Updated;
                    }
                }
                created = new Target { Label = label, Host = newAddr, Addr = newAddr, Kind = kind, Fails = 0 };
                targets.Add(created);
            }
            return TargetSync.Created;
        }

        /// <summary>
        /// Reconciles a runtime-detected gateway/VPN/DNS target: updates an existing
        /// target's address, or creates one and launches its ping loop when it was absent
        /// at startup (e.g. a VPN that connects after launch). Logs the change.
        /// </summary>
        private static void ReconcileTarget(List<Target> targets, object targetsLock,
            List<Task> loops, object loopsLock, Config cfg, Aggregator agg, LogWriter log,
            int pingInterval, CancellationToken token,
            TargetKind kind, string label, string newAddr, string what)
        {
            Target created;
            TargetSync r = UpdateTargetAddr(targets, targetsLock, kind, label, newAddr, out created);
            switch (r)
            {
                case TargetSync.Updated:
                    SafeEvent(log, what + " changed to " + newAddr);
                    break;
                case TargetSync.Created:
                    SafeEvent(log, what + " detected, now monitoring " + label + " -> " + newAddr);
                    var task = Task.Run(() => PingLoop(created, cfg, agg, log, pingInterval, 0, token), token);
                    lock (loopsLock) loops.Add(task);
                    break;
            }
        }

        private static string TryResolve(string host)
        {
            try { return Pinger.Resolve(host); }
            catch { return null; }
        }

        // =====================================================================
        //  Reporting / banner helpers
        // =====================================================================

        private static void WriteBanner(LogWriter log, Config cfg, InterfaceSample active,
            VpnStatus vpn, List<Target> targets, string configPath, string logDir)
        {
            log.Event("===== NetWatch " + Version + " starting =====");
            log.Event(Repo + "  " + Author);
            log.Event("machine=" + SafeMachine() + " user=" + SafeUser() +
                      " os=" + SafeOs() + " clr=" + Environment.Version);
            log.Event("config=" + configPath);
            log.Event("logs=" + logDir);
            log.Event(DescribeActive(active));
            log.Event(DescribeVpn(vpn));
            log.Event("monitored targets (" + targets.Count + "):");
            foreach (var t in targets)
                log.Event("  " + (t.Label ?? "").PadRight(12) + " -> " + (t.Addr ?? "?") + "  [" + t.Kind + "]");
        }

        private static string DescribeActive(InterfaceSample a)
        {
            if (a == null) return "active link: (none detected)";
            var sb = new System.Text.StringBuilder();
            string kindWord = Display.KindWord(a.Kind);
            string name = a.Name ?? "";
            sb.Append("active link: ").Append(kindWord);
            if (name.Length > 0 && name.IndexOf(kindWord, StringComparison.OrdinalIgnoreCase) < 0)
                sb.Append(" \"").Append(name).Append('"');
            if (!string.IsNullOrEmpty(a.IpAddress)) sb.Append(" ip=").Append(a.IpAddress);
            if (!string.IsNullOrEmpty(a.Gateway)) sb.Append(" gw=").Append(a.Gateway);
            if (a.IsWifi)
                sb.Append(" SSID=").Append(string.IsNullOrEmpty(a.Ssid) ? "?" : a.Ssid)
                  .Append(" signal=").Append(a.SignalPct.ToString(CultureInfo.InvariantCulture)).Append('%');
            if (a.LinkSpeedBps > 0)
                sb.Append(" link=").Append((a.LinkSpeedBps / 1e6).ToString("0.##", CultureInfo.InvariantCulture)).Append("Mbps");
            return sb.ToString();
        }

        private static string DescribeVpn(VpnStatus v)
        {
            if (v == null || !v.Present) return "VPN: none detected";
            var sb = new System.Text.StringBuilder();
            sb.Append("VPN: ").Append(string.IsNullOrEmpty(v.Vendor) ? "VPN" : v.Vendor);
            sb.Append(v.Connected ? " connected" : " disconnected");
            if (!string.IsNullOrEmpty(v.IpAddress)) sb.Append(" ip=").Append(v.IpAddress);
            if (!string.IsNullOrEmpty(v.Gateway)) sb.Append(" gw=").Append(v.Gateway);
            return sb.ToString();
        }

        private static string DescribeTrace(TraceResult t)
        {
            if (t == null) return "trace: (none)";
            var sb = new System.Text.StringBuilder();
            int n = t.Hops != null ? t.Hops.Length : 0;
            sb.Append("trace to ").Append(t.Target ?? "?");
            if (!string.IsNullOrEmpty(t.Addr)) sb.Append(" (").Append(t.Addr).Append(')');
            sb.Append(": ").Append(n).Append(" hops");
            if (!string.IsNullOrEmpty(t.Error)) sb.Append(" error=").Append(t.Error);
            sb.AppendLine();
            if (t.Hops != null)
            {
                foreach (var h in t.Hops)
                {
                    if (h == null) continue;
                    string addr = h.Timeout || string.IsNullOrEmpty(h.Addr) ? "*" : h.Addr;
                    string rtt = h.Timeout ? "*" : h.RttMs.ToString("0.#", CultureInfo.InvariantCulture) + "ms";
                    sb.Append("  ").Append(h.Ttl.ToString(CultureInfo.InvariantCulture).PadLeft(2))
                      .Append("  ").Append(addr.PadRight(16)).Append("  ").Append(rtt);
                    if (!string.IsNullOrEmpty(h.Host)) sb.Append("  ").Append(h.Host);
                    sb.AppendLine();
                }
            }
            return sb.ToString().TrimEnd();
        }

        private static string DescribeSpeed(SpeedResult sp)
        {
            if (sp == null) return "speedtest: (none)";
            if (!string.IsNullOrEmpty(sp.Error) && sp.DownMbps <= 0 && sp.UpMbps <= 0)
                return "speedtest: error " + sp.Error;
            var sb = new System.Text.StringBuilder();
            sb.Append("speedtest: down ").Append(sp.DownMbps.ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append(" / up ").Append(sp.UpMbps.ToString("0.##", CultureInfo.InvariantCulture)).Append(" Mbps");
            sb.Append(", latency ").Append(sp.LatencyMs.ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append(" +/- ").Append(sp.JitterMs.ToString("0.##", CultureInfo.InvariantCulture)).Append(" ms");
            if (!string.IsNullOrEmpty(sp.Server)) sb.Append(", server ").Append(sp.Server);
            if (!string.IsNullOrEmpty(sp.Error)) sb.Append(" (partial: ").Append(sp.Error).Append(')');
            return sb.ToString();
        }

        // =====================================================================
        //  Small utilities
        // =====================================================================

        private static void FlushEvents(Aggregator agg, LogWriter log)
        {
            string[] events = agg.DrainEvents();
            foreach (var e in events) log.Event(e);
        }

        private static void SafeEvent(LogWriter log, string msg)
        {
            try { log.Event(msg); } catch { }
        }

        private static PingSample FailedSample(Target t, string error)
        {
            return new PingSample
            {
                Target = t.Label,
                Addr = t.Addr,
                Kind = t.Kind,
                Time = DateTime.UtcNow,
                Success = false,
                Error = error
            };
        }

        private static string LabelOf(Target t)
        {
            // Labels are self-descriptive at creation (Gateway / DNS / VPN-GW / VPN <host> / host).
            return t.Label ?? "";
        }

        private static bool IsLiteralIp(string host)
        {
            return !string.IsNullOrEmpty(host) &&
                   System.Net.IPAddress.TryParse(host, out _);
        }

        /// <summary>Sleeps in a cancellation-aware way. Returns true when cancelled.</summary>
        private static bool Sleep(int ms, CancellationToken token)
        {
            if (ms <= 0) return token.IsCancellationRequested;
            try { return token.WaitHandle.WaitOne(ms); }
            catch { return token.IsCancellationRequested; }
        }

        private static string Trunc(string s, int max)
        {
            s = s ?? "";
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private static string ResolveLogDir(string exeDir, string overridePath, string cfgLogDir)
        {
            string dir = !string.IsNullOrEmpty(overridePath) ? overridePath
                       : (!string.IsNullOrEmpty(cfgLogDir) ? cfgLogDir : "logs");
            if (!Path.IsPathRooted(dir))
                dir = Path.Combine(exeDir, dir);
            return dir;
        }

        private static string ExeDir()
        {
            try
            {
                var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                string loc = asm.Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    string d = Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(d)) return d;
                }
            }
            catch { }
            try { return Directory.GetCurrentDirectory(); }
            catch { return "."; }
        }

        private static string SafeMachine() { try { return Environment.MachineName; } catch { return "?"; } }
        private static string SafeUser() { try { return Environment.UserName; } catch { return "?"; } }

        /// <summary>
        /// Reports the real OS from the registry. On net48 without an OS-compat
        /// manifest, Environment.OSVersion misreports Win10/11 as 6.2.9200.0, so
        /// read HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion directly.
        /// Falls back to Environment.OSVersion on any error.
        /// </summary>
        private static string SafeOs()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key != null)
                    {
                        string product = key.GetValue("ProductName") as string ?? "Windows";
                        string display = key.GetValue("DisplayVersion") as string
                                         ?? key.GetValue("ReleaseId") as string;
                        string buildStr = key.GetValue("CurrentBuildNumber") as string
                                          ?? key.GetValue("CurrentBuild") as string;

                        int build = 0;
                        if (!string.IsNullOrEmpty(buildStr))
                            int.TryParse(buildStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out build);

                        // ProductName still says "Windows 10" on Windows 11 (build >= 22000);
                        // correct it so the diagnostic report is accurate.
                        if (build >= 22000 && product.IndexOf("Windows 10", StringComparison.OrdinalIgnoreCase) >= 0)
                            product = product.Replace("Windows 10", "Windows 11");

                        var sb = new System.Text.StringBuilder(product);
                        if (!string.IsNullOrEmpty(display)) sb.Append(' ').Append(display);
                        if (build > 0) sb.Append(" (build ").Append(build.ToString(CultureInfo.InvariantCulture)).Append(')');
                        return sb.ToString();
                    }
                }
            }
            catch { }
            try { return Environment.OSVersion.ToString(); } catch { return "?"; }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("NetWatch " + Version + " - employee network & VPN latency diagnostics");
            Console.WriteLine();
            Console.WriteLine("Usage: netwatch.exe [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -config <path>   Use the given configuration file");
            Console.WriteLine("                   (default: netwatch.conf next to the EXE)");
            Console.WriteLine("  -logdir <path>   Override the log/CSV output directory");
            Console.WriteLine("  -once            Run a single diagnostic pass, print a report, exit");
            Console.WriteLine("  -frame [width]   Print one live dashboard frame to stdout and exit");
            Console.WriteLine("                   (optional render width, e.g. -frame 160; default 100)");
            Console.WriteLine("  -no-ui           Continuous monitoring without the live dashboard");
            Console.WriteLine("  -report [path]   Write an HTML metrics report (charts) and exit");
            Console.WriteLine("                   (default: report.html in the log directory)");
            Console.WriteLine("  -version         Print version and exit");
            Console.WriteLine("  -h, -help        Show this help and exit");
            Console.WriteLine();
            Console.WriteLine("No administrator rights required. Runs on stock Windows 10/11");
            Console.WriteLine("(.NET Framework 4.8 in-box). Press Ctrl+C to stop monitoring.");
            Console.WriteLine();
            Console.WriteLine(Repo + "  " + Author);
        }
    }
}
