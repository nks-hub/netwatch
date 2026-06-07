using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace NetWatch.Report
{
    /// <summary>
    /// Reads NetWatch's daily CSV log files back into typed records for reporting.
    /// Files rotate by LOCAL date and timestamps are LOCAL "yyyy-MM-dd HH:mm:ss".
    /// Every reader spans the local dates in [sinceLocal.Date .. today], parses the
    /// per-record CSVs that exist, keeps rows at or after sinceLocal, and returns the
    /// list sorted ascending by Time. Quoted/escaped fields are honoured. Never throws:
    /// any file or row error is skipped and reading continues.
    /// </summary>
    public static class CsvHistory
    {
        private const string TsFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>Read ping samples from ping_&lt;date&gt;.csv files at or after sinceLocal.</summary>
        public static List<PingRecord> ReadPings(string logDir, DateTime sinceLocal)
        {
            var list = new List<PingRecord>();
            foreach (var path in DailyFiles(logDir, "ping_", sinceLocal))
            {
                foreach (var fields in ReadRows(path))
                {
                    try
                    {
                        // time,target,addr,kind,success,rtt_ms
                        if (fields.Count < 5) continue;
                        DateTime time;
                        if (!ParseTime(fields[0], out time)) continue;
                        if (time < sinceLocal) continue;

                        bool success = fields[4] == "1";
                        double rtt = 0.0;
                        if (success && fields.Count > 5)
                        {
                            // Blank rtt with success=1 is malformed; treat as 0.
                            if (!ParseDouble(fields[5], out rtt)) rtt = 0.0;
                        }

                        var rec = new PingRecord
                        {
                            Time = time,
                            Target = fields[1] ?? "",
                            Success = success,
                            RttMs = success ? rtt : 0.0
                        };
                        list.Add(rec);
                    }
                    catch { /* skip malformed row */ }
                }
            }
            list.Sort(ByTimePing);
            return list;
        }

        /// <summary>Read speedtest samples from speed_&lt;date&gt;.csv files at or after sinceLocal.</summary>
        public static List<SpeedRecord> ReadSpeed(string logDir, DateTime sinceLocal)
        {
            var list = new List<SpeedRecord>();
            foreach (var path in DailyFiles(logDir, "speed_", sinceLocal))
            {
                foreach (var fields in ReadRows(path))
                {
                    try
                    {
                        // time,down_mbps,up_mbps,latency_ms,jitter_ms,server,error
                        if (fields.Count < 5) continue;
                        DateTime time;
                        if (!ParseTime(fields[0], out time)) continue;
                        if (time < sinceLocal) continue;

                        var rec = new SpeedRecord
                        {
                            Time = time,
                            DownMbps = ParseDoubleOr(fields[1], double.NaN),
                            UpMbps = ParseDoubleOr(fields[2], double.NaN),
                            LatencyMs = ParseDoubleOr(fields[3], double.NaN),
                            JitterMs = ParseDoubleOr(fields[4], double.NaN)
                        };
                        list.Add(rec);
                    }
                    catch { /* skip malformed row */ }
                }
            }
            list.Sort(ByTimeSpeed);
            return list;
        }

        /// <summary>Read interface samples from iface_&lt;date&gt;.csv files at or after sinceLocal.</summary>
        public static List<IfaceRecord> ReadIface(string logDir, DateTime sinceLocal)
        {
            var list = new List<IfaceRecord>();
            foreach (var path in DailyFiles(logDir, "iface_", sinceLocal))
            {
                foreach (var fields in ReadRows(path))
                {
                    try
                    {
                        // time,name,kind,active,up,link_mbps,ip,gateway,recv_mbps,send_mbps,
                        // in_err,out_err,wifi,ssid,signal_pct,rssi_dbm,wifi_rx_mbps,wifi_tx_mbps
                        if (fields.Count < 15) continue;
                        DateTime time;
                        if (!ParseTime(fields[0], out time)) continue;
                        if (time < sinceLocal) continue;

                        var rec = new IfaceRecord
                        {
                            Time = time,
                            Name = fields[1] ?? "",
                            IsWifi = fields[12] == "1",
                            RecvMbps = ParseDoubleOr(fields[8], double.NaN),
                            SendMbps = ParseDoubleOr(fields[9], double.NaN),
                            SignalPct = ParseIntOr(fields[14], 0)
                        };
                        list.Add(rec);
                    }
                    catch { /* skip malformed row */ }
                }
            }
            list.Sort(ByTimeIface);
            return list;
        }

        // ---- file enumeration ----

        /// <summary>
        /// Yield the paths of &lt;prefix&gt;&lt;yyyy-MM-dd&gt;.csv files for each local date in
        /// [sinceLocal.Date .. today] that actually exist in logDir.
        /// </summary>
        private static IEnumerable<string> DailyFiles(string logDir, string prefix, DateTime sinceLocal)
        {
            string dir = string.IsNullOrEmpty(logDir) ? "." : logDir;
            DateTime from = sinceLocal.Date;
            DateTime to = DateTime.Now.Date;
            if (from > to) yield break;

            for (DateTime d = from; d <= to; d = d.AddDays(1))
            {
                string path;
                try
                {
                    string file = prefix + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv";
                    path = Path.Combine(dir, file);
                    if (!File.Exists(path)) continue;
                }
                catch { continue; }
                yield return path;
            }
        }

        /// <summary>
        /// Stream a CSV file row by row, skipping the header line, returning each data row
        /// as a list of unescaped fields. Quoted fields (the writer wraps fields containing
        /// commas/quotes/newlines in double quotes with "" escaping) are joined across lines.
        /// </summary>
        private static IEnumerable<List<string>> ReadRows(string path)
        {
            StreamReader reader = null;
            try
            {
                reader = new StreamReader(path);
            }
            catch
            {
                if (reader != null) { try { reader.Dispose(); } catch { } }
                yield break;
            }

            using (reader)
            {
                bool headerSkipped = false;
                while (true)
                {
                    List<string> fields;
                    bool ok;
                    try
                    {
                        ok = TryReadRecord(reader, out fields);
                    }
                    catch
                    {
                        // Unexpected read error: stop reading this file gracefully.
                        yield break;
                    }
                    if (!ok) yield break;
                    if (fields == null) continue;

                    if (!headerSkipped)
                    {
                        headerSkipped = true;
                        continue; // drop header line
                    }
                    yield return fields;
                }
            }
        }

        // ---- CSV record parsing (RFC-4180 style, matching LogWriter's Q() escaping) ----

        /// <summary>
        /// Read one logical CSV record (which may span multiple physical lines when a quoted
        /// field contains a newline). Returns false at end of file with no more data.
        /// fields is null for a record that should be ignored (e.g. trailing empty line).
        /// </summary>
        private static bool TryReadRecord(StreamReader reader, out List<string> fields)
        {
            fields = null;

            string line = reader.ReadLine();
            if (line == null) return false;

            var result = new List<string>();
            var field = new System.Text.StringBuilder();
            bool inQuotes = false;
            int i = 0;

            while (true)
            {
                if (i >= line.Length)
                {
                    if (inQuotes)
                    {
                        // Quoted field spans to the next physical line; restore the newline.
                        string next = reader.ReadLine();
                        if (next == null)
                        {
                            // Unterminated quote at EOF: accept what we have.
                            break;
                        }
                        field.Append('\n');
                        line = next;
                        i = 0;
                        continue;
                    }
                    break;
                }

                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            field.Append('"'); // escaped quote
                            i += 2;
                            continue;
                        }
                        inQuotes = false;
                        i++;
                        continue;
                    }
                    field.Append(c);
                    i++;
                }
                else
                {
                    if (c == '"')
                    {
                        inQuotes = true;
                        i++;
                    }
                    else if (c == ',')
                    {
                        result.Add(field.ToString());
                        field.Length = 0;
                        i++;
                    }
                    else
                    {
                        field.Append(c);
                        i++;
                    }
                }
            }

            result.Add(field.ToString());

            // Ignore a completely empty line (single empty field, no quotes consumed).
            if (result.Count == 1 && result[0].Length == 0)
            {
                fields = null;
                return true;
            }

            fields = result;
            return true;
        }

        // ---- value parsing ----

        private static bool ParseTime(string s, out DateTime time)
        {
            time = default(DateTime);
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (DateTime.TryParseExact(s, TsFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out time))
                return true;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out time))
                return true;
            return false;
        }

        private static bool ParseDouble(string s, out double value)
        {
            value = 0.0;
            if (string.IsNullOrEmpty(s)) return false;
            return double.TryParse(s.Trim(), NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out value);
        }

        private static double ParseDoubleOr(string s, double fallback)
        {
            double v;
            return ParseDouble(s, out v) ? v : fallback;
        }

        private static int ParseIntOr(string s, int fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            int v;
            if (int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                return v;
            // Tolerate a decimal in an int column (e.g. "57.0").
            double d;
            if (ParseDouble(s, out d)) return (int)Math.Round(d);
            return fallback;
        }

        // ---- sort comparers (ascending by Time) ----

        private static int ByTimePing(PingRecord a, PingRecord b)
        {
            return a.Time.CompareTo(b.Time);
        }

        private static int ByTimeSpeed(SpeedRecord a, SpeedRecord b)
        {
            return a.Time.CompareTo(b.Time);
        }

        private static int ByTimeIface(IfaceRecord a, IfaceRecord b)
        {
            return a.Time.CompareTo(b.Time);
        }
    }
}
