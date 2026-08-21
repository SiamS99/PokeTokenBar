using System.Text.Json.Serialization;

namespace PokeTokenBar.Core;

public record DailyUsage(
    string Date,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    long TotalTokens,
    double TotalCost)
{
    public string FormattedTokens => TotalTokens switch {
        >= 1_000_000 => $"{TotalTokens / 1_000_000.0:F1}M",
        >= 1_000     => $"{TotalTokens / 1_000.0:F1}K",
        _            => TotalTokens.ToString("N0")
    };
    public string FormattedCost => $"${TotalCost:F4}";
}

public record BlockUsage(
    string Id,
    DateTime StartTime,
    DateTime EndTime,
    bool IsActive,
    long TotalTokens,
    double CostUsd,
    double? TokensPerMinute);

public record PeriodUsage(
    string Period,
    long TotalTokens,
    double TotalCost);

public record LimitStatus(
    double? FiveHourPct,
    DateTime? FiveHourResetsAt,
    double? SevenDayPct,
    DateTime? SevenDayResetsAt,
    string? PlanDisplay);

public record ProviderSnapshot(
    string ProviderId,
    string DisplayName,
    DailyUsage? Today,
    BlockUsage? ActiveBlock,
    PeriodUsage? WeekTotal,
    PeriodUsage? MonthTotal,
    LimitStatus? Limits,
    DateTime FetchedAt,
    bool ReportsCost = true)
{
    public long TodayTotalTokens => Today?.TotalTokens ?? 0;
}

// ─── Companion / Pokémon models ───────────────────────────────────────────────

public enum Rarity { Common, Uncommon, Rare, Legendary }

public enum CompanionDisplayState { Egg, Idle, Working, Focus, Tired, Sleep, LevelUp }

public enum PokemonNature
{
    Hardy, Lonely, Brave, Adamant, Naughty,
    Bold, Docile, Relaxed, Impish, Lax,
    Timid, Hasty, Serious, Jolly, Naive,
    Modest, Mild, Quiet, Bashful, Rash,
    Calm, Gentle, Sassy, Careful, Quirky
}

public static class PokemonBalance
{
    public const long EggHatchThreshold = 5_000_000;

    public static long GraduationTotal(Rarity r) => r switch {
        Rarity.Common    =>    750_000_000L,
        Rarity.Uncommon  =>  1_875_000_000L,
        Rarity.Rare      =>  3_000_000_000L,
        Rarity.Legendary =>  6_000_000_000L,
        _                =>    750_000_000L
    };

    public static long PhaseThreshold(Rarity rarity, int totalForms, int stageIndex)
    {
        int k   = Math.Max(1, totalForms);
        int i   = stageIndex + 1;
        double total = GraduationTotal(rarity);
        double denom = k * (k + 1) / 2.0;
        return (long)Math.Round(total * i / denom);
    }
}

public record MonState(
    int BaseId,
    int[] PathIds,
    int StageIndex,
    long UsedAtStage,
    Rarity Rarity,
    int TotalForms,
    bool IsShiny,
    PokemonNature? Nature)
{
    public int CurrentId => PathIds.Length > 0
        ? PathIds[Math.Min(StageIndex, PathIds.Length - 1)]
        : BaseId;
}

public record DexEntry(
    string Id,
    int BaseId,
    int FinalId,
    int[] ChainOrder,
    Rarity Rarity,
    DateTime? CaughtAt,
    bool IsShiny,
    PokemonNature? Nature);

public class CompanionState
{
    public bool InstallBaselineSet { get; set; }
    public long UsedSinceInstall { get; set; }
    public long SpentTokens { get; set; }
    public long EggUsage { get; set; }
    public Rarity? EggTier { get; set; }
    public int? PendingHatchId { get; set; }
    public Dictionary<string, long> ClaimedTodayTokensByProvider { get; set; } = [];
    public string LastDate { get; set; } = "";
    public MonState? Active { get; set; }
    public List<DexEntry> Dex { get; set; } = [];
    public HashSet<string> CollectedFinals { get; set; } = [];
    public string Language { get; set; } = "en";
    public Dictionary<string, int> Inventory { get; set; } = [];
    public Dictionary<string, int> CandyGrantTier { get; set; } = [];
    public bool CandyFeatureSeeded { get; set; }

    public long Wallet => Math.Max(0, UsedSinceInstall - SpentTokens);
}
