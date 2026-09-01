using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace PokeTokenBar.Core;

/// <summary>
/// Reads OpenCode token usage from its local SQLite database (preferred) and
/// legacy per-message JSON files (fallback for older installs).
/// Default root: %USERPROFILE%\.local\share\opencode — OpenCode uses this
/// literal XDG-style path on Windows too, not a native AppData folder.
/// Mirrors the macOS build's LocalOpenCodeProvider (Core/LocalAdditionalUsageProvider.swift).
/// </summary>
public class OpenCodeProvider
{
    private static readonly string[] _roots = BuildRoots();

    private static string[] BuildRoots()
    {
        var roots = new List<string>();

        var dataDir = Environment.GetEnvironmentVariable("OPENCODE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            foreach (var part in dataDir.Split(','))
            {
                var p = part.Trim();
                if (!string.IsNullOrEmpty(p)) roots.Add(p);
            }
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(Path.Combine(home, ".local", "share", "opencode"));
        }

        return [.. roots.Where(Directory.Exists).Distinct()];
    }

    // ── Public surface ────────────────────────────────────────────────────────

    public DailyUsage? FetchToday()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var all   = ScanEntries(DateTime.Now.Date.AddDays(-1));

        long input = 0, output = 0, cacheWrite = 0, cacheRead = 0;
        double cost = 0;
        var seen = new HashSet<string>();

        foreach (var e in all.Where(e => e.LocalDay == today))
        {
            if (!seen.Add(e.Id)) continue;
            input      += e.Input;
            output     += e.Output;
            cacheWrite += e.CacheWrite;
            cacheRead  += e.CacheRead;
            cost       += e.ExplicitCost;
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
                wInput += e.Input; wOutput += e.Output;
                wCW    += e.CacheWrite; wCR += e.CacheRead;
                wCost  += e.ExplicitCost;
            }
            if (e.Date.Date >= monthStart)
            {
                mInput += e.Input; mOutput += e.Output;
                mCW    += e.CacheWrite; mCR += e.CacheRead;
                mCost  += e.ExplicitCost;
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
            cost       += e.ExplicitCost;
        }

        long total = input + output + cacheWrite + cacheRead;
        return total > 0 ? new DailyUsage(from.ToString("yyyy-MM-dd"), input, output, cacheWrite, cacheRead, total, cost) : null;
    }

    // ── Scanning ──────────────────────────────────────────────────────────────

    private IEnumerable<LogEntry> ScanEntries(DateTime modifiedSince)
    {
        var byId = new Dictionary<string, LogEntry>();

        void Keep(LogEntry entry)
        {
            if (!byId.TryGetValue(entry.Id, out var existing) || entry.Total > existing.Total)
                byId[entry.Id] = entry;
        }

        foreach (var root in _roots)
        {
            var db = FindDatabase(root);
            if (db is not null)
            {
                foreach (var entry in DatabaseEntries(db, modifiedSince)) Keep(entry);
            }

            var legacyRoot = Path.Combine(root, "storage", "message");
            if (!Directory.Exists(legacyRoot)) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(legacyRoot, "*.json", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var file in files)
            {
                try { if (File.GetLastWriteTime(file) < modifiedSince) continue; }
                catch { continue; }

                LogEntry? entry;
                try
                {
                    var obj = JsonNode.Parse(File.ReadAllText(file));
                    entry = obj is null ? null : ParseMessage(obj, Path.GetFileNameWithoutExtension(file));
                }
                catch { entry = null; }

                if (entry is not null) Keep(entry);
            }
        }

        return byId.Values.Where(e => e.Date >= modifiedSince);
    }

    // ── Database (preferred source) ──────────────────────────────────────────

    /// <summary>opencode.db, or a channel build like opencode-nightly.db.</summary>
    private static string? FindDatabase(string root)
    {
        var standard = Path.Combine(root, "opencode.db");
        if (File.Exists(standard)) return standard;

        try
        {
            return Directory.EnumerateFiles(root, "opencode-*.db")
                .Where(f =>
                {
                    var channel = Path.GetFileNameWithoutExtension(f)["opencode-".Length..];
                    return channel.Length > 0 && channel.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');
                })
                .OrderBy(f => f, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static List<LogEntry> DatabaseEntries(string dbPath, DateTime modifiedSince)
    {
        var result = new List<LogEntry>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
            conn.Open();

            var cutoffMs = new DateTimeOffset(modifiedSince.ToUniversalTime()).ToUnixTimeMilliseconds();
            if (!TryQuery(conn, "SELECT id, data FROM message WHERE time_created >= $cutoff", cutoffMs, result))
                TryQuery(conn, "SELECT id, data FROM message", null, result);
        }
        catch { }
        return result;
    }

    /// <summary>Older OpenCode databases don't expose time_created — caller retries unfiltered.</summary>
    private static bool TryQuery(SqliteConnection conn, string sql, long? cutoffMs, List<LogEntry> result)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            if (cutoffMs.HasValue) cmd.Parameters.AddWithValue("$cutoff", cutoffMs.Value);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id   = reader.IsDBNull(0) ? null : reader.GetString(0);
                var data = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (id is null || data is null) continue;

                LogEntry? entry;
                try
                {
                    var obj = JsonNode.Parse(data);
                    entry = obj is null ? null : ParseMessage(obj, id);
                }
                catch { entry = null; }

                if (entry is not null) result.Add(entry);
            }
            return true;
        }
        catch (SqliteException) { return false; }
    }

    // ── Message parsing (shared by DB rows and legacy JSON files) ────────────

    private static LogEntry? ParseMessage(JsonNode obj, string fallbackId)
    {
        var tokens = obj["tokens"];
        if (tokens is null) return null;

        var date = DateFromRaw(obj["time"]?["created"]);
        if (date is null) return null;

        if (string.IsNullOrWhiteSpace(obj["modelID"]?.GetValue<string>())) return null;
        if (obj["providerID"] is null) return null;

        var cache = tokens["cache"];
        long input      = LongVal(tokens["input"]);
        long output     = LongVal(tokens["output"]);
        long cacheWrite = LongVal(cache?["write"]);
        long cacheRead  = LongVal(cache?["read"]);
        long total      = LongVal(tokens["total"]);

        // If the granular fields don't add up to the reported total, fold the
        // gap into output rather than drop it or double-count — mirrors the
        // macOS reader's reconciliation for the same source.
        var parts = input + output + cacheWrite + cacheRead;
        if (total > parts) output += total - parts;
        if (input + output + cacheWrite + cacheRead <= 0) return null;

        var id = obj["id"]?.GetValue<string>();
        var entryId = "opencode|" + (string.IsNullOrEmpty(id) ? fallbackId : id);

        return new LogEntry(
            entryId, date.Value, date.Value.ToString("yyyy-MM-dd"),
            input, output, cacheWrite, cacheRead, DoubleVal(obj["cost"]));
    }

    private static DateTime? DateFromRaw(JsonNode? value)
    {
        var raw = DoubleValOrNull(value);
        if (raw is null || !double.IsFinite(raw.Value) || raw <= 0) return null;
        var seconds = raw.Value >= 100_000_000_000 ? raw.Value / 1000.0 : raw.Value;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).ToLocalTime().DateTime;
    }

    private static long LongVal(JsonNode? n)
    {
        if (n is null) return 0;
        try { return Math.Max(0, n.GetValue<long>()); }
        catch { }
        try { return Math.Max(0, (long)n.GetValue<double>()); }
        catch { }
        try { return long.TryParse(n.GetValue<string>().Trim(), out var v) ? Math.Max(0, v) : 0; }
        catch { return 0; }
    }

    private static double DoubleVal(JsonNode? n) => DoubleValOrNull(n) ?? 0;

    private static double? DoubleValOrNull(JsonNode? n)
    {
        if (n is null) return null;
        try { return n.GetValue<double>(); }
        catch { }
        try { return double.TryParse(n.GetValue<string>().Trim(), out var v) ? v : null; }
        catch { return null; }
    }

    // ── Internal record ───────────────────────────────────────────────────────

    private record LogEntry(
        string Id, DateTime Date, string LocalDay,
        long Input, long Output, long CacheWrite, long CacheRead, double ExplicitCost)
    {
        public long Total => Input + Output + CacheWrite + CacheRead;
    }
}
