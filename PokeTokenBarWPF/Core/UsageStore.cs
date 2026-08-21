using System.Globalization;
using System.Timers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PokeTokenBar.Core;

/// <summary>
/// Main data store.  Polls Claude Code logs on a timer and exposes
/// observable properties for the UI.
/// </summary>
public partial class UsageStore : ObservableObject, IDisposable
{
    private readonly ClaudeCodeProvider _claude = new();
    private readonly CodexProvider      _codex  = new();
    private readonly CompanionStore     _companion;
    private readonly OhMyPoshExporter   _ompExporter = new();
    private readonly AppSettings        _settings;
    private System.Timers.Timer?        _timer;

    // ── Observable properties (UI binds to these) ─────────────────────────────

    [ObservableProperty] private DailyUsage?     _todayUsage;
    [ObservableProperty] private PeriodUsage?    _weekUsage;
    [ObservableProperty] private PeriodUsage?    _monthUsage;
    [ObservableProperty] private BlockUsage?     _activeBlock;
    [ObservableProperty] private LimitStatus?    _limits;
    [ObservableProperty] private DateTime        _lastFetched;
    [ObservableProperty] private bool            _isFetching;
    [ObservableProperty] private string          _trayLabel = "...";
    [ObservableProperty] private bool            _companionChanged;

    public CompanionStore Companion => _companion;
    public AppSettings Settings => _settings;

    public UsageStore(AppSettings settings, CompanionStore companion)
    {
        _settings  = settings;
        _companion = companion;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start()
    {
        _ = RefreshAsync();
        ScheduleTimer();
    }

    public void ApplyRefreshInterval()
    {
        ScheduleTimer();
    }

    private void ScheduleTimer()
    {
        _timer?.Stop();
        _timer?.Dispose();

        if (_settings.RefreshIntervalMinutes <= 0) return;

        _timer = new System.Timers.Timer(TimeSpan.FromMinutes(_settings.RefreshIntervalMinutes).TotalMilliseconds);
        _timer.Elapsed += async (_, _) => await RefreshAsync();
        _timer.AutoReset = true;
        _timer.Start();
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    public async Task RefreshAsync()
    {
        if (IsFetching) return;

        try
        {
            IsFetching = true;

            // Fetch token data from all providers in parallel.
            var claudeTask = Task.Run(_claude.FetchToday);
            var codexTask  = Task.Run(_codex.FetchToday);
            var claudePeriodsTask = Task.Run(_claude.FetchPeriods);
            var codexPeriodsTask  = Task.Run(_codex.FetchPeriods);
            var blockTask  = Task.Run(_claude.FetchActiveBlock);

            await Task.WhenAll(claudeTask, codexTask, claudePeriodsTask, codexPeriodsTask, blockTask);

            var today = Merge(claudeTask.Result, codexTask.Result);
            var (claudeWeek, claudeMonth) = claudePeriodsTask.Result;
            var (codexWeek,  codexMonth)  = codexPeriodsTask.Result;
            var week  = Merge(claudeWeek,  codexWeek);
            var month = Merge(claudeMonth, codexMonth);

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                TodayUsage   = today;
                WeekUsage    = week  is null ? null : new PeriodUsage("week",  week.TotalTokens,  week.TotalCost);
                MonthUsage   = month is null ? null : new PeriodUsage("month", month.TotalTokens, month.TotalCost);
                ActiveBlock  = blockTask.Result;
                LastFetched  = DateTime.Now;

                UpdateTrayLabel();
            });

            // Feed companion with combined total, reconciling any days the app
            // wasn't running to observe before they rolled over.
            var gapCredit = await ComputeGapCreditAsync();
            var changed = await _companion.UpdateAsync(today?.TotalTokens ?? 0, "combined", gapCredit);
            if (changed)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => CompanionChanged = true);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500);
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => CompanionChanged = false);
                });
            }

            // OhMyPosh export — pass each provider separately so the segment can pick which count to show.
            if (_settings.OhMyPoshExportEnabled)
                _ompExporter.Export(claudeTask.Result, codexTask.Result, today, _companion.State, _companion.CompanionDisplayName);
        }
        finally
        {
            IsFetching = false;
        }
    }

    /// <summary>
    /// The companion's wallet/egg/evolution progress is driven by diffing
    /// today's token count against a per-day baseline that resets at
    /// midnight. That only credits usage the app was actually running to
    /// observe — if it was closed across a day boundary (or closed before
    /// midnight and reopened the next day), any tokens generated in that gap
    /// were written to the Claude/Codex logs but never seen, and silently
    /// vanish from progress once the baseline resets. Detect that gap here
    /// and re-read the missed date range directly from the logs so it can be
    /// credited instead of dropped.
    /// </summary>
    private async Task<long> ComputeGapCreditAsync()
    {
        var lastDateStr = _companion.State.LastDate;
        if (string.IsNullOrEmpty(lastDateStr)) return 0;
        if (!DateTime.TryParseExact(lastDateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var lastDate))
            return 0;

        var today = DateTime.Now.Date;
        if (lastDate.Date >= today) return 0; // no rollover since the last check-in

        // [lastDate, yesterday] — re-read in full; whatever was already
        // claimed for lastDate before the app closed is subtracted below so
        // it isn't double-credited.
        var rangeEnd = today.AddDays(-1);
        var claudeTask = Task.Run(() => _claude.FetchRange(lastDate, rangeEnd));
        var codexTask  = Task.Run(() => _codex.FetchRange(lastDate, rangeEnd));
        await Task.WhenAll(claudeTask, codexTask);

        var actualTotal = (claudeTask.Result?.TotalTokens ?? 0) + (codexTask.Result?.TotalTokens ?? 0);
        var alreadyClaimed = _companion.State.ClaimedTodayTokensByProvider.GetValueOrDefault("combined");
        return Math.Max(0, actualTotal - alreadyClaimed);
    }

    private void UpdateTrayLabel()
    {
        var parts = new List<string>();

        if (_settings.ShowTokensInTray && TodayUsage is not null)
            parts.Add(TodayUsage.FormattedTokens);

        if (_settings.ShowCostInTray && TodayUsage is not null)
            parts.Add(TodayUsage.FormattedCost);

        TrayLabel = parts.Count > 0 ? string.Join(" | ", parts) : "";
    }

    // Combine two DailyUsage snapshots (different providers, same day) into one.
    private static DailyUsage? Merge(DailyUsage? a, DailyUsage? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return new DailyUsage(
            a.Date,
            a.InputTokens          + b.InputTokens,
            a.OutputTokens         + b.OutputTokens,
            a.CacheCreationTokens  + b.CacheCreationTokens,
            a.CacheReadTokens      + b.CacheReadTokens,
            a.TotalTokens          + b.TotalTokens,
            a.TotalCost            + b.TotalCost);
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer?.Dispose();
    }
}
