using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace NetWatch
{
    /// <summary>
    /// Internet throughput / latency probe against Cloudflare's open speed
    /// endpoints over plain outbound HTTPS (port 443). Works for standard
    /// non-admin users; no raw sockets. Synchronous and never throws.
    /// </summary>
    public static class SpeedTest
    {
        private const string DownUrl = "https://speed.cloudflare.com/__down?bytes=";
        private const string UpUrl = "https://speed.cloudflare.com/__up";

        // Latency probes.
        private const int LatencyProbes = 6;        // first is warm-up, discarded
        private const int LatencyProbeTimeoutMs = 3_000;  // a 20s "latency" sample is meaningless
        private const int LatencyPhaseCapMs = 8_000;      // overall wall-clock budget for latency

        // Download phase.
        private const long DownChunkBytes = 25_000_000;
        private const int DownStreams = 4;
        private const int DownPhaseCapMs = 15_000;

        // Upload phase.
        private const long UpChunkBytes = 10_000_000;
        private const int UpStreams = 3;
        private const int UpPhaseCapMs = 15_000;

        private const int RequestTimeoutMs = 20_000;
        private const int MeasuredPhaseRwTimeoutMs = 5_000; // stalled socket can't pin a stream for 20s
        private const int WarmupTimeoutMs = 5_000;          // warm-up only needs ~1MB
        private const int PhaseWaitGraceMs = 1_000;         // small WaitAll grace over the phase cap
        private const int ReadBufferSize = 81_920;

        /// <summary>Runs latency, download and upload measurements in sequence. Never throws.</summary>
        public static SpeedResult Run()
        {
            var result = new SpeedResult
            {
                Time = DateTime.UtcNow,
                DownMbps = 0,
                UpMbps = 0,
                LatencyMs = 0,
                JitterMs = 0,
                Server = null,
                Error = null
            };

            try
            {
                ServicePointManager.DefaultConnectionLimit = 64;
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                // Tls13 may not exist on every patch level; add it only if defined.
                try
                {
                    var tls13 = (SecurityProtocolType)12288;
                    if (Enum.IsDefined(typeof(SecurityProtocolType), tls13))
                        ServicePointManager.SecurityProtocol |= tls13;
                }
                catch { /* ignore: enum value unavailable on this runtime */ }
                ServicePointManager.Expect100Continue = false;
            }
            catch (Exception ex)
            {
                result.Error = "init: " + Short(ex);
                return result;
            }

            try
            {
                MeasureLatency(result);
            }
            catch (Exception ex)
            {
                AppendError(result, "latency: " + Short(ex));
            }

            try
            {
                MeasureDownload(result);
            }
            catch (Exception ex)
            {
                AppendError(result, "down: " + Short(ex));
            }

            try
            {
                MeasureUpload(result);
            }
            catch (Exception ex)
            {
                AppendError(result, "up: " + Short(ex));
            }

            return result;
        }

        /// <summary>
        /// Issues small round-trips to __down?bytes=0; min RTT is the unloaded
        /// latency, population stddev of the remaining samples is the jitter.
        /// Also captures the Cloudflare colo from response headers.
        /// </summary>
        private static void MeasureLatency(SpeedResult result)
        {
            var rtts = new double[LatencyProbes];
            int got = 0;

            // Overall wall-clock budget so a stalled link cannot pin the whole
            // latency phase near LatencyProbes x per-probe timeout.
            var phaseDeadline = DateTime.UtcNow.AddMilliseconds(LatencyPhaseCapMs);

            for (int i = 0; i < LatencyProbes; i++)
            {
                if (DateTime.UtcNow >= phaseDeadline) break;
                var req = NewRequest(DownUrl + "0", "GET",
                    LatencyProbeTimeoutMs, LatencyProbeTimeoutMs);
                var sw = Stopwatch.StartNew();
                try
                {
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        if (result.Server == null)
                            result.Server = ParseColo(resp);
                        using (var s = resp.GetResponseStream())
                        {
                            DrainAndDiscard(s);
                        }
                    }
                    sw.Stop();
                    if (i > 0)
                        rtts[got++] = sw.Elapsed.TotalMilliseconds;
                }
                catch
                {
                    sw.Stop();
                    // Skip failed probe; partial latency is still useful.
                }
            }

            if (got == 0)
            {
                AppendError(result, "latency: no successful probe");
                return;
            }

            double min = double.MaxValue;
            double sum = 0;
            for (int i = 0; i < got; i++)
            {
                double v = rtts[i];
                if (v < min) min = v;
                sum += v;
            }
            result.LatencyMs = min;

            // Population standard deviation across the measured (non-warm-up) probes.
            double mean = sum / got;
            double sq = 0;
            for (int i = 0; i < got; i++)
            {
                double d = rtts[i] - mean;
                sq += d * d;
            }
            result.JitterMs = got > 1 ? Math.Sqrt(sq / got) : 0;
        }

        /// <summary>
        /// Warms up one connection, then runs parallel GET streams reading
        /// bytes=DownChunkBytes each into a throwaway buffer while counting
        /// bytes. Throughput is total bits over the wall time of the streams;
        /// this wall time includes per-stream connection setup (TCP/TLS) for
        /// the cold connections beyond the single warmed one, so on
        /// high-latency links the figure is biased slightly low.
        /// </summary>
        private static void MeasureDownload(SpeedResult result)
        {
            // Warm-up (single small stream, not timed).
            try
            {
                var warm = NewRequest(DownUrl + "1000000", "GET", WarmupTimeoutMs, WarmupTimeoutMs);
                using (var resp = (HttpWebResponse)warm.GetResponse())
                {
                    if (result.Server == null)
                        result.Server = ParseColo(resp);
                    using (var s = resp.GetResponseStream())
                        DrainAndDiscard(s);
                }
            }
            catch
            {
                // Warm-up failure is non-fatal; measured pass below still attempts.
            }

            long[] counts = new long[DownStreams];
            var deadline = DateTime.UtcNow.AddMilliseconds(DownPhaseCapMs);

            var sw = Stopwatch.StartNew();
            var tasks = new Task[DownStreams];
            string downUrl = DownUrl + DownChunkBytes.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < DownStreams; i++)
            {
                int idx = i;
                tasks[i] = Task.Run(() =>
                {
                    counts[idx] = DownloadStream(downUrl, deadline, "dl" + idx.ToString(CultureInfo.InvariantCulture));
                });
            }

            try { Task.WaitAll(tasks, DownPhaseCapMs + PhaseWaitGraceMs); }
            catch (AggregateException) { /* individual streams swallow their own errors */ }
            sw.Stop();

            long total = 0;
            for (int i = 0; i < DownStreams; i++) total += counts[i];

            double seconds = sw.Elapsed.TotalSeconds;
            if (total > 0 && seconds > 0.0001)
                result.DownMbps = total * 8.0 / seconds / 1e6;
            else
                AppendError(result, "down: no bytes");
        }

        private static long DownloadStream(string url, DateTime deadline, string connectionGroup)
        {
            long bytes = 0;
            try
            {
                var req = NewRequest(url, "GET",
                    RequestTimeoutMs, MeasuredPhaseRwTimeoutMs, connectionGroup);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                {
                    var buf = new byte[ReadBufferSize];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        bytes += n;
                        if (DateTime.UtcNow >= deadline) break;
                    }
                }
            }
            catch
            {
                // Return whatever we managed to read before failure.
            }
            return bytes;
        }

        /// <summary>
        /// Runs parallel chunked POSTs to __up. Each stream times only its own
        /// body-write loop (excluding connection setup and the post-upload
        /// response round-trip); aggregate throughput is total flushed bits
        /// over the longest single stream's write time.
        /// </summary>
        private static void MeasureUpload(SpeedResult result)
        {
            long[] counts = new long[UpStreams];
            long[] elapsedTicks = new long[UpStreams];
            var deadline = DateTime.UtcNow.AddMilliseconds(UpPhaseCapMs);

            var tasks = new Task[UpStreams];
            for (int i = 0; i < UpStreams; i++)
            {
                int idx = i;
                tasks[i] = Task.Run(() =>
                {
                    counts[idx] = UploadStream(UpChunkBytes, deadline,
                        "ul" + idx.ToString(CultureInfo.InvariantCulture), out elapsedTicks[idx]);
                });
            }

            try { Task.WaitAll(tasks, UpPhaseCapMs + PhaseWaitGraceMs); }
            catch (AggregateException) { /* individual streams swallow their own errors */ }

            long total = 0;
            long maxTicks = 0;
            for (int i = 0; i < UpStreams; i++)
            {
                total += counts[i];
                if (counts[i] > 0 && elapsedTicks[i] > maxTicks) maxTicks = elapsedTicks[i];
            }

            double seconds = (double)maxTicks / Stopwatch.Frequency;
            if (total > 0 && seconds > 0.0001)
                result.UpMbps = total * 8.0 / seconds / 1e6;
            else
                AppendError(result, "up: no bytes");
        }

        private static long UploadStream(long size, DateTime deadline, string connectionGroup, out long elapsedTicks)
        {
            long sent = 0;
            elapsedTicks = 0;
            try
            {
                var req = NewRequest(UpUrl, "POST",
                    RequestTimeoutMs, MeasuredPhaseRwTimeoutMs, connectionGroup);
                req.ContentType = "application/octet-stream";
                // Chunked transfer: a partial body cut off at the deadline is a
                // valid, server-accepted upload, so we only ever count bytes the
                // socket actually flushed (no pre-declared ContentLength to violate).
                req.SendChunked = true;
                req.AllowWriteStreamBuffering = false;

                var buf = new byte[ReadBufferSize]; // zero-filled payload
                var sw = Stopwatch.StartNew();
                using (var s = req.GetRequestStream())
                {
                    long remaining = size;
                    while (remaining > 0)
                    {
                        int chunk = (int)Math.Min(buf.Length, remaining);
                        s.Write(buf, 0, chunk);
                        s.Flush();
                        sent += chunk;
                        remaining -= chunk;
                        if (DateTime.UtcNow >= deadline) break;
                    }
                }
                sw.Stop();
                elapsedTicks = sw.ElapsedTicks;
                // Read and discard the response so the connection can be reused.
                // Excluded from timing above (it is a full server round-trip).
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rs = resp.GetResponseStream())
                    DrainAndDiscard(rs);
            }
            catch
            {
                // A failed stream contributes no throughput sample.
                elapsedTicks = 0;
                return 0;
            }
            return sent;
        }

        private static HttpWebRequest NewRequest(string url, string method,
            int timeoutMs = RequestTimeoutMs, int readWriteTimeoutMs = RequestTimeoutMs,
            string connectionGroup = null)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = readWriteTimeoutMs;
            req.KeepAlive = true;
            req.Proxy = WebRequest.DefaultWebProxy; // honour system/corporate proxy
            req.UserAgent = "NetWatch/1.0";
            req.AutomaticDecompression = DecompressionMethods.None;
            // A distinct connection group forces a separate ServicePoint connection
            // pool, so N parallel streams use N distinct TCP connections.
            if (!string.IsNullOrEmpty(connectionGroup))
                req.ConnectionGroupName = connectionGroup;
            req.ServicePoint.ConnectionLimit = 64;
            return req;
        }

        /// <summary>
        /// Reads 'cf-ray' (format '&lt;hex&gt;-&lt;COLO&gt;') and returns the colo
        /// suffix; falls back to 'cf-meta-colo' when present.
        /// </summary>
        private static string ParseColo(HttpWebResponse resp)
        {
            try
            {
                var ray = resp.Headers["cf-ray"];
                if (!string.IsNullOrEmpty(ray))
                {
                    int dash = ray.LastIndexOf('-');
                    if (dash >= 0 && dash < ray.Length - 1)
                        return ray.Substring(dash + 1).Trim();
                    return ray.Trim();
                }
                var meta = resp.Headers["cf-meta-colo"];
                if (!string.IsNullOrEmpty(meta))
                    return meta.Trim();
            }
            catch { /* headers unavailable */ }
            return null;
        }

        private static void DrainAndDiscard(Stream s)
        {
            if (s == null) return;
            var buf = new byte[ReadBufferSize];
            while (s.Read(buf, 0, buf.Length) > 0) { }
        }

        private static void AppendError(SpeedResult r, string msg)
        {
            if (string.IsNullOrEmpty(r.Error)) r.Error = msg;
            else r.Error = r.Error + "; " + msg;
        }

        private static string Short(Exception ex)
        {
            var msg = ex.Message ?? ex.GetType().Name;
            if (msg.Length > 120) msg = msg.Substring(0, 120);
            return msg;
        }
    }
}
