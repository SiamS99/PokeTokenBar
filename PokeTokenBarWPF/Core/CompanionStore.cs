using System.Text.Json;

namespace PokeTokenBar.Core;

/// <summary>
/// Manages the Pokémon companion — hatching, evolution, graduation, items.
/// Persists state to %APPDATA%\PokeTokenBar\companion-state.json.
/// </summary>
public class CompanionStore
{
    private static readonly string StatePath = Path.Combine(AppSettings.AppDataDir, "companion-state.json");
    private static readonly JsonSerializerOptions _jsonOpts =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private CompanionState _state = new();
    private readonly PokeApiClient _api = new();

    public CompanionState State => _state;

    // Cached display name of active Pokémon (fetched async).
    public string CompanionDisplayName { get; private set; } = "Egg";

    // ── Persistence ───────────────────────────────────────────────────────────

    public void Load()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var json = File.ReadAllText(StatePath);
                _state = JsonSerializer.Deserialize<CompanionState>(json, _jsonOpts) ?? new CompanionState();
            }
        }
        catch { _state = new CompanionState(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.AppDataDir);
            var json = JsonSerializer.Serialize(_state, _jsonOpts);
            File.WriteAllText(StatePath, json);
        }
        catch { }
    }

    // ── Token feed (called by UsageStore on each refresh) ────────────────────

    /// <summary>
    /// Feed today's token count.  Returns true when the companion changed state
    /// (hatch, evolution, graduation) and the UI should animate.
    /// </summary>
    /// <param name="gapCredit">
    /// Extra tokens to credit that a day-boundary reconciliation found were
    /// missed (see <see cref="UsageStore.RefreshAsync"/>) — the app wasn't
    /// running to observe them via <paramref name="todayTokens"/> before the
    /// day they belonged to rolled over and its baseline was cleared.
    /// </param>
    public async Task<bool> UpdateAsync(long todayTokens, string providerId, long gapCredit = 0)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");

        // ── Day rollover (or first run) ──────────────────────────────────────
        // Reset the provider baseline to 0 so today's full token count is credited.
        if (_state.LastDate != today)
        {
            _state.ClaimedTodayTokensByProvider.Remove(providerId);
            _state.LastDate = today;
            _state.InstallBaselineSet = true; // keep field valid for old saves
        }

        // ── Delta since last check ───────────────────────────────────────────
        _state.ClaimedTodayTokensByProvider.TryGetValue(providerId, out var baseline);
        long delta = Math.Max(0, todayTokens - baseline) + Math.Max(0, gapCredit);
        _state.ClaimedTodayTokensByProvider[providerId] = todayTokens;

        if (delta == 0) { Save(); return false; }

        var changed = await ApplyDeltaAsync(delta);
        Save();
        return changed;
    }

    private async Task<bool> ApplyDeltaAsync(long delta)
    {
        var changed = false;
        _state.UsedSinceInstall += delta;

        // ── Egg incubation ───────────────────────────────────────────────────
        if (_state.Active is null)
        {
            _state.EggUsage += delta;

            if (_state.EggUsage >= PokemonBalance.EggHatchThreshold)
            {
                // Pre-fetch species if not already done.
                if (!_state.PendingHatchId.HasValue)
                    _state.PendingHatchId = await PickSpeciesAsync();

                if (_state.PendingHatchId.HasValue)
                {
                    await HatchAsync(_state.PendingHatchId.Value);
                    changed = true;
                }
            }
            else if (!_state.PendingHatchId.HasValue)
            {
                // Pre-roll species in background so hatch is instant.
                _ = PreRollAsync();
            }

            return changed;
        }

        // ── Active Pokémon growth ────────────────────────────────────────────
        var mon = _state.Active;
        _state.Active = mon with { UsedAtStage = mon.UsedAtStage + (int)Math.Min(delta, int.MaxValue) };
        mon = _state.Active;

        var threshold = PokemonBalance.PhaseThreshold(mon.Rarity, mon.TotalForms, mon.StageIndex);

        if (mon.UsedAtStage >= threshold)
        {
            if (mon.StageIndex < mon.TotalForms - 1)
            {
                // Evolve
                _state.Active = mon with { StageIndex = mon.StageIndex + 1, UsedAtStage = 0 };
                changed = true;
            }
            else
            {
                // Graduate to Pokedex
                Graduate(mon);
                _state.Active = null;
                _state.EggUsage = 0;
                _state.PendingHatchId = null;
                changed = true;
            }
        }

        await RefreshDisplayNameAsync();
        return changed;
    }

    // ── Hatching ─────────────────────────────────────────────────────────────

    private async Task HatchAsync(int speciesId)
    {
        var chain = await _api.FetchEvolutionChainAsync(speciesId);
        var path  = chain ?? [speciesId];

        var info = await _api.FetchSpeciesInfoAsync(speciesId);
        var rarity = info?.rarity ?? Rarity.Common;

        bool isShiny = (Random.Shared.NextInt64() % (long)PokemonOdds.ShinyDenominator) == 0;

        var nature = (PokemonNature)(Random.Shared.Next(25));

        _state.Active = new MonState(
            BaseId:     speciesId,
            PathIds:    path,
            StageIndex: 0,
            UsedAtStage: (int)Math.Max(0, _state.EggUsage - PokemonBalance.EggHatchThreshold),
            Rarity:     rarity,
            TotalForms: path.Length,
            IsShiny:    isShiny,
            Nature:     nature);

        _state.EggUsage    = 0;
        _state.PendingHatchId = null;
        _state.EggTier     = null;

        await RefreshDisplayNameAsync();
    }

    private async Task PreRollAsync()
    {
        var id = await PickSpeciesAsync();
        if (id.HasValue) _state.PendingHatchId = id;
    }

    private async Task<int?> PickSpeciesAsync()
    {
        try { return await _api.PickRandomSpeciesAsync(_state.EggTier); }
        catch { return Random.Shared.Next(1, 150); } // fallback Gen 1 range
    }

    // ── Graduation ────────────────────────────────────────────────────────────

    private void Graduate(MonState mon)
    {
        var entry = new DexEntry(
            Id:         Guid.NewGuid().ToString(),
            BaseId:     mon.BaseId,
            FinalId:    mon.CurrentId,
            ChainOrder: mon.PathIds,
            Rarity:     mon.Rarity,
            CaughtAt:   DateTime.UtcNow,
            IsShiny:    mon.IsShiny,
            Nature:     mon.Nature);

        _state.Dex.Add(entry);
        _state.CollectedFinals.Add($"{mon.BaseId}/{mon.CurrentId}");
    }

    // ── Items ─────────────────────────────────────────────────────────────────

    public bool UseRareCandy()
    {
        if (_state.Active is null) return false;
        if (!_state.Inventory.TryGetValue("rareCandy", out var count) || count <= 0) return false;

        _state.Inventory["rareCandy"] = count - 1;
        var mon = _state.Active;
        _state.Active = mon with { UsedAtStage = mon.UsedAtStage + RareCandyBalance.Xp };
        Save();
        return true;
    }

    public bool BuyItem(string itemKey, long price)
    {
        if (_state.Wallet < price) return false;
        _state.SpentTokens += price;
        _state.Inventory.TryGetValue(itemKey, out var cur);
        _state.Inventory[itemKey] = cur + 1;
        Save();
        return true;
    }

    // ── Display helpers ───────────────────────────────────────────────────────

    public CompanionDisplayState GetDisplayState(double? burnRate)
    {
        if (_state.Active is null) return CompanionDisplayState.Egg;
        if (burnRate is null or 0) return CompanionDisplayState.Idle;
        if (burnRate > 5000) return CompanionDisplayState.Focus;
        if (burnRate > 1000) return CompanionDisplayState.Working;
        return CompanionDisplayState.Idle;
    }

    public async Task RefreshDisplayNameAsync()
    {
        if (_state.Active is null) { CompanionDisplayName = "Egg"; return; }
        try
        {
            var info = await _api.FetchSpeciesInfoAsync(_state.Active.CurrentId);
            CompanionDisplayName = info?.name ?? $"#{_state.Active.CurrentId}";
        }
        catch { CompanionDisplayName = $"#{_state.Active?.CurrentId}"; }
    }

    public Task<byte[]?> FetchSpriteAsync(int speciesId, bool shiny)
        => _api.FetchSpriteAsync(speciesId, shiny);
}

// ── Constants (mirrors Swift balance enums) ───────────────────────────────────

file static class PokemonOdds
{
    public const ulong ShinyDenominator = 64;
}

file static class RareCandyBalance
{
    public const long Xp = 100_000_000;
    public const long Price = 500_000_000;
}
