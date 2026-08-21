using System.Text.Json.Nodes;

namespace PokeTokenBar.Core;

/// <summary>
/// Reads Claude Code token usage directly from local JSONL logs.
/// Windows paths: %USERPROFILE%\.claude\projects\**\*.jsonl
/// </summary>
public class ClaudeCodeProvider
{
    private static readonly string[] _logRoots = BuildLogRoots();

    private static string[] BuildLogRoots()
    {
        var home  = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>();

        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            foreach (var part in configDir.Split(','))
            {
                var p = part.Trim();
                if (!string.IsNullOrEmpty(p))
                    roots.Add(Path.Combine(p, "projects"));
            }
        }

        roots.Add(Path.Combine(home, ".config", "claude", "projects"));
        roots.Add(Path.Combine(home, ".claude", "projects"));

        return [.. roots.Where(Directory.Exists).Distinct()];
    }

    // ── Public surface ────────────────────────────────────────────────────────

    public DailyUsage? FetchToday()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var all   = ScanEntries(DateTime.Now.Date.AddDays(-1)); // yesterday mtime cutoff is safe

        long input = 0, output = 0, cacheWrite = 0, cacheRead = 0;
        double cost = 0;
        var seen = new HashSet<string>();

        foreach (var e in all.Where(e => e.LocalDay == today))
        {
            if (!seen.Add(e.Id)) continue;
            input     += e.Input;
            output    += e.Output;
            cacheWrite += e.CacheWrite;
            cacheRead  += e.CacheRead;
            cost       += e.ExplicitCost > 0 ? e.ExplicitCost : 0;
        }

        long total = input + output + cacheWrite + cacheRead;
        return total > 0 ? new DailyUsage(today, input, output, cacheWrite, cacheRead, total, cost) : null;
    }

    public (DailyUsage? week, DailyUsage? month) FetchPeriods()
    {
        var now        = DateTime.Now;
        var weekStart  = now.AddDays(-(int)now.DayOfWeek).Date;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var scanFrom   = new[] { weekStart, monthStart }.Min();

        var all = ScanEntries(scanFrom.AddDays(-1));

        long wInput = 0, wOutput = 0, wCW = 0, wCR = 0;
        double wCost = 0;
        long mInput = 0, mOutput = 0, mCW = 0, mCR = 0;
        double mCost = 0;
        var seen = new HashSet<string>();

        foreach (var e in all)
        {
            if (!seen.Add(e.Id)) continue;

            if (e.Date.Date >= weekStart)
            {
                wInput  += e.Input;  wOutput += e.Output;
                wCW     += e.CacheWrite; wCR += e.CacheRead;
                wCost   += e.ExplicitCost > 0 ? e.ExplicitCost : 0;
            }
            if (e.Date.Date >= monthStart)
            {
                mInput  += e.Input;  mOutput += e.Output;
                mCW     += e.CacheWrite; mCR += e.CacheRead;
                mCost   += e.ExplicitCost > 0 ? e.ExplicitCost : 0;
            }
        }

        long wTotal = wInput + wOutput + wCW + wCR;
        long mTotal = mInput + mOutput + mCW + mCR;

        var week  = wTotal > 0 ? new DailyUsage(weekStart.ToString("yyyy-MM-dd"),  wInput, wOutput, wCW, wCR, wTotal, wCost)  : null;
        var month = mTotal > 0 ? new DailyUsage(monthStart.ToString("yyyy-MM-dd"), mInput, mOutput, mCW, mCR, mTotal, mCost) : null;
        return (week, month);
    }

    /// <summary>
    /// Sum of all usage in [fromInclusive, toInclusive] (local dates), recomputed
    /// fresh from the logs — used to reconcile days the app wasn't running to
    /// observe via <see cref="FetchToday"/> before they rolled over.
    /// </summary>
    public DailyUsage? FetchRange(DateTime fromInclusive, DateTime toInclusive)
    {
        var from = fromInclusive.Date;
        var to   = toInclusive.Date;
        var all  = ScanEntries(from.AddDays(-1));

        long input = 0, output = 0, cacheWrite = 0, cacheRead = 0;
        double cost = 0;
        var seen = new HashSet<string>();

        foreach (var e in all)
        {
            if (e.Date.Date < from || e.Date.Date > to) continue;
            if (!seen.Add(e.Id)) continue;
            input      += e.Input;
            output     += e.Output;
            cacheWrite += e.CacheWrite;
            cacheRead  += e.CacheRead;
            cost       += e.ExplicitCost > 0 ? e.ExplicitCost : 0;
        }

        long total = input + output + cacheWrite + cacheRead;
        return total > 0 ? new DailyUsage(from.ToString("yyyy-MM-dd"), input, output, cacheWrite, cacheRead, total, cost) : null;
    }

    public BlockUsage? FetchActiveBlock()
    {
        var blockStart = DateTime.UtcNow.AddHours(-5);
        var all = ScanEntries(blockStart.ToLocalTime().AddHours(-1));
        var recent = all
            .Where(e => e.Date.ToUniversalTime() >= blockStart)
            .OrderBy(e => e.Date)
            .ToList();

        if (recent.Count == 0) return null;

        long total = recent.Sum(e => e.Input + e.Output + e.CacheWrite + e.CacheRead);
        var first  = recent[0].Date;
        var minutes = Math.Max(1, (DateTime.UtcNow - first.ToUniversalTime()).TotalMinutes);
        var tpm    = total / minutes;

        return new BlockUsage(
            Id:          $"block-{(long)first.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds}",
            StartTime:   first,
            EndTime:     first.AddHours(5),
            IsActive:    true,
            TotalTokens: total,
            CostUsd:     recent.Sum(e => e.ExplicitCost),
            TokensPerMinute: tpm);
    }

    // ── Scanning ──────────────────────────────────────────────────────────────

    private IEnumerable<LogEntry> ScanEntries(DateTime modifiedSince)
    {
        var byId = new Dictionary<string, LogEntry>();

        foreach (var root in _logRoots)
        {
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var file in files)
            {
                try
                {
                    if (File.GetLastWriteTime(file) < modifiedSince) continue;
                }
                catch { continue; }

                foreach (var entry in ParseFile(file))
                {
                    if (!byId.TryGetValue(entry.Id, out var existing) || entry.Total > existing.Total)
                        byId[entry.Id] = entry;
                }
            }
        }

        return byId.Values;
    }

    private static IEnumerable<LogEntry> ParseFile(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { yield break; }

        var seen = new Dictionary<string, LogEntry>();

        foreach (var line in lines)
        {
            if (!line.Contains("\"usage\"") || !line.Contains("\"assistant\"")) continue;

            LogEntry? entry;
            try { entry = ParseLine(line); }
            catch { continue; }

            if (entry is null) continue;

            if (!seen.TryGetValue(entry.Id, out var existing) || entry.Total > existing.Total)
                seen[entry.Id] = entry;
        }

        foreach (var e in seen.Values) yield return e;
    }

    private static LogEntry? ParseLine(string line)
    {
        var obj = JsonNode.Parse(line);
        if (obj is null) return null;
        if (obj["type"]?.GetValue<string>() != "assistant") return null;

        var msg   = obj["message"]; if (msg is null) return null;
        var usage = msg["usage"];   if (usage is null) return null;

        var ts = obj["timestamp"]?.GetValue<string>();
        if (ts is null) return null;
        if (!DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dateUtc))
            return null;

        var dateLocal = dateUtc.ToLocalTime();
        var msgId     = msg["id"]?.GetValue<string>() ?? "";
        var reqId     = obj["requestId"]?.GetValue<string>() ?? "";
        var id        = $"{msgId}|{reqId}";
        var model     = msg["model"]?.GetValue<string>() ?? "unknown";
        var localDay  = dateLocal.ToString("yyyy-MM-dd");

        long input      = LongVal(usage["input_tokens"]);
        long output     = LongVal(usage["output_tokens"]);
        long cacheWrite = LongVal(usage["cache_creation_input_tokens"]);
        long cacheRead  = LongVal(usage["cache_read_input_tokens"]);
        double cost     = DoubleVal(obj["costUSD"]);

        return new LogEntry(id, dateLocal, localDay, model, input, output, cacheWrite, cacheRead, cost);
    }

    private static long LongVal(JsonNode? n)
    {
        if (n is null) return 0;
        try { return n.GetValue<long>(); }
        catch { try { return (long)n.GetValue<double>(); } catch { return 0; } }
    }

    private static double DoubleVal(JsonNode? n)
    {
        if (n is null) return 0;
        try { return n.GetValue<double>(); }
        catch { return 0; }
    }

    // ── Internal record ───────────────────────────────────────────────────────

    private record LogEntry(
        string Id, DateTime Date, string LocalDay, string Model,
        long Input, long Output, long CacheWrite, long CacheRead, double ExplicitCost)
    {
        public long Total => Input + Output + CacheWrite + CacheRead;
    }
}
