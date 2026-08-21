using System.Text.Json.Nodes;

namespace PokeTokenBar.Core;

/// <summary>
/// Reads OpenAI Codex token usage from local rollout JSONL logs.
/// Windows paths: %USERPROFILE%\.codex\sessions\**\rollout-*.jsonl and
/// %USERPROFILE%\.codex\archived_sessions\**\rollout-*.jsonl — Codex moves old
/// rollouts into the archived root without deleting them, so scanning only the
/// active root silently drops usage the moment a session gets archived.
/// Each relevant line contains a token_count event with last_token_usage (turn delta).
/// </summary>
public class CodexProvider
{
    private static readonly string[] ScanRoots = BuildScanRoots();

    private static string[] BuildScanRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            Path.Combine(home, ".codex", "sessions"),
            Path.Combine(home, ".codex", "archived_sessions"),
        ];
    }

    // ── Public surface ────────────────────────────────────────────────────────

    public DailyUsage? FetchToday()
    {
        var today   = DateTime.Now.ToString("yyyy-MM-dd");
        var entries = ScanEntries(DateTime.Now.Date.AddDays(-1));

        long input = 0, output = 0, cacheRead = 0;
        var seen = new HashSet<string>();

        foreach (var e in entries.Where(e => e.LocalDay == today))
        {
            if (!seen.Add(e.Id)) continue;
            input     += e.Input;
            output    += e.Output;
            cacheRead += e.CacheRead;
        }

        long total = input + output + cacheRead;
        return total > 0 ? new DailyUsage(today, input, output, 0, cacheRead, total, 0) : null;
    }

    public (DailyUsage? week, DailyUsage? month) FetchPeriods()
    {
        var now        = DateTime.Now;
        var weekStart  = now.AddDays(-(int)now.DayOfWeek).Date;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var scanFrom   = new[] { weekStart, monthStart }.Min();

        var all = ScanEntries(scanFrom.AddDays(-1));

        long wInput = 0, wOutput = 0, wCR = 0;
        long mInput = 0, mOutput = 0, mCR = 0;
        var seen = new HashSet<string>();

        foreach (var e in all)
        {
            if (!seen.Add(e.Id)) continue;
            if (e.Date.Date >= weekStart)  { wInput += e.Input; wOutput += e.Output; wCR += e.CacheRead; }
            if (e.Date.Date >= monthStart) { mInput += e.Input; mOutput += e.Output; mCR += e.CacheRead; }
        }

        long wTotal = wInput + wOutput + wCR;
        long mTotal = mInput + mOutput + mCR;

        var week  = wTotal > 0 ? new DailyUsage(weekStart.ToString("yyyy-MM-dd"),  wInput, wOutput, 0, wCR, wTotal, 0) : null;
        var month = mTotal > 0 ? new DailyUsage(monthStart.ToString("yyyy-MM-dd"), mInput, mOutput, 0, mCR, mTotal, 0) : null;
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

        long input = 0, output = 0, cacheRead = 0;
        var seen = new HashSet<string>();

        foreach (var e in all)
        {
            if (e.Date.Date < from || e.Date.Date > to) continue;
            if (!seen.Add(e.Id)) continue;
            input     += e.Input;
            output    += e.Output;
            cacheRead += e.CacheRead;
        }

        long total = input + output + cacheRead;
        return total > 0 ? new DailyUsage(from.ToString("yyyy-MM-dd"), input, output, 0, cacheRead, total, 0) : null;
    }

    // ── Scanning ──────────────────────────────────────────────────────────────

    private static IEnumerable<LogEntry> ScanEntries(DateTime modifiedSince)
    {
        var byId = new Dictionary<string, LogEntry>();

        foreach (var root in ScanRoots)
        {
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories);
            }
            catch { continue; }

            foreach (var file in files)
            {
                try { if (File.GetLastWriteTime(file) < modifiedSince) continue; }
                catch { continue; }

                foreach (var entry in ParseFile(file, Path.GetFileName(file)))
                {
                    if (!byId.ContainsKey(entry.Id))
                        byId[entry.Id] = entry;
                }
            }
        }

        return byId.Values;
    }

    private static IEnumerable<LogEntry> ParseFile(string path, string fileName)
    {
        // Stream line-by-line instead of loading the whole rollout into memory —
        // Codex rollouts can grow very large, and ReadAllLines peaks at the full
        // file size plus the resulting array. File.ReadLines opens/reads lazily,
        // so the try/catch has to wrap the whole enumeration, not just the call.
        var result = new List<LogEntry>();
        try
        {
            int turn = 0;
            foreach (var line in File.ReadLines(path))
            {
                var lineTurn = turn++;
                if (!line.Contains("token_count")) continue;

                LogEntry? entry;
                try { entry = ParseLine(line, fileName, lineTurn); }
                catch { continue; }

                if (entry is not null) result.Add(entry);
            }
        }
        catch { }
        return result;
    }

    private static LogEntry? ParseLine(string line, string fileName, int turn)
    {
        var obj = JsonNode.Parse(line);
        if (obj is null) return null;

        var payload = obj["payload"];
        if (payload is null) return null;
        if (payload["type"]?.GetValue<string>() != "token_count") return null;

        var info = payload["info"];
        if (info is null) return null;

        var last = info["last_token_usage"];
        if (last is null) return null;

        var ts = obj["timestamp"]?.GetValue<string>();
        if (ts is null) return null;
        if (!DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dateUtc))
            return null;

        var dateLocal = dateUtc.ToLocalTime();
        var localDay  = dateLocal.ToString("yyyy-MM-dd");

        long inputTotal = LongVal(last["input_tokens"]);
        long cached     = LongVal(last["cached_input_tokens"]);
        long output     = LongVal(last["output_tokens"]);
        long input      = Math.Max(0, inputTotal - cached);

        if (input + output + cached == 0) return null;

        return new LogEntry(
            Id:       $"codex|{fileName}|{turn}",
            Date:     dateLocal,
            LocalDay: localDay,
            Input:    input,
            Output:   output,
            CacheRead: cached);
    }

    private static long LongVal(JsonNode? n)
    {
        if (n is null) return 0;
        try { return n.GetValue<long>(); }
        catch { try { return (long)n.GetValue<double>(); } catch { return 0; } }
    }

    private record LogEntry(string Id, DateTime Date, string LocalDay, long Input, long Output, long CacheRead);
}
