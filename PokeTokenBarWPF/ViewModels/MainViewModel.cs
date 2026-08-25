using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeTokenBar.Core;

namespace PokeTokenBar.ViewModels;

public enum PopoverTab { Home, Pokedex, Shop, Settings }

public partial class MainViewModel : ObservableObject
{
    public UsageStore Store { get; }

    [ObservableProperty] private PopoverTab _currentTab = PopoverTab.Home;
    [ObservableProperty] private byte[]? _spriteBytes;
    [ObservableProperty] private bool _spriteLoading;
    [ObservableProperty] private string? _toastMessage;
    [ObservableProperty] private bool _toastVisible;

    private int _toastToken;

    /// <summary>
    /// Shows a themed, self-dismissing toast instead of a native MessageBox —
    /// keeps confirmations inside the app's own dark/gold styling. The text
    /// (<see cref="ToastMessage"/>) is left in place after hiding — only
    /// <see cref="ToastVisible"/> toggles — so the fade-out animates the
    /// existing message instead of flashing an empty box.
    /// </summary>
    public void ShowToast(string message)
    {
        ToastMessage = message;
        ToastVisible = true;
        var token = ++_toastToken;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            if (token == _toastToken)
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ToastVisible = false);
        });
    }

    public MainViewModel(UsageStore store)
    {
        Store = store;
        store.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(UsageStore.TodayUsage):
                    OnPropertyChanged(nameof(TodayTokens));
                    OnPropertyChanged(nameof(TodayCost));
                    // Every refresh cycle (including the very first one at startup)
                    // must re-hydrate the companion display — CompanionChanged only
                    // fires on hatch/evolve/graduate, not on plain token updates, so
                    // relying on it alone leaves the name/sprite/progress stuck at
                    // whatever they were when the app last transitioned state.
                    _ = RefreshCompanionDisplayAsync();
                    break;
                case nameof(UsageStore.WeekUsage):
                    OnPropertyChanged(nameof(WeekTokens));
                    break;
                case nameof(UsageStore.MonthUsage):
                    OnPropertyChanged(nameof(MonthTokens));
                    break;
                case nameof(UsageStore.ActiveBlock):
                    OnPropertyChanged(nameof(BurnRate));
                    break;
            }
        };
    }

    // ── Companion display hydration ──────────────────────────────────────────

    private async Task RefreshCompanionDisplayAsync()
    {
        await Store.Companion.RefreshDisplayNameAsync();
        await ReloadSpriteAsync();
        OnPropertyChanged(nameof(CompanionName));
        OnPropertyChanged(nameof(StageLabel));
        OnPropertyChanged(nameof(EggProgress));
        OnPropertyChanged(nameof(EggProgressValue));
        OnPropertyChanged(nameof(WalletDisplay));
    }

    // ── Tab navigation ────────────────────────────────────────────────────────

    [RelayCommand] private void GoHome()      => CurrentTab = PopoverTab.Home;
    [RelayCommand] private void GoPokedex()   => CurrentTab = PopoverTab.Pokedex;
    [RelayCommand] private void GoShop()      => CurrentTab = PopoverTab.Shop;
    [RelayCommand] private void GoSettings()  => CurrentTab = PopoverTab.Settings;

    [RelayCommand]
    private async Task RefreshAsync() => await Store.RefreshAsync();

    // ── Sprite ────────────────────────────────────────────────────────────────

    public async Task ReloadSpriteAsync()
    {
        var mon = Store.Companion.State.Active;
        if (mon is null) { SpriteBytes = null; return; }

        SpriteLoading = true;
        try
        {
            SpriteBytes = await Store.Companion.FetchSpriteAsync(mon.CurrentId, mon.IsShiny);
        }
        finally { SpriteLoading = false; }
    }

    // ── Shop actions ──────────────────────────────────────────────────────────

    [RelayCommand]
    private void UseRareCandy()
    {
        if (Store.Companion.UseRareCandy())
            _ = Store.RefreshAsync();
    }

    // ── Computed display props ────────────────────────────────────────────────

    public string TodayTokens   => Store.TodayUsage?.FormattedTokens ?? "—";
    public string TodayCost     => Store.TodayUsage?.FormattedCost   ?? "—";
    public string WeekTokens    => Store.WeekUsage is { } w  ? FormatTokens(w.TotalTokens)  : "—";
    public string MonthTokens   => Store.MonthUsage is { } m ? FormatTokens(m.TotalTokens)  : "—";
    public string BurnRate      => Store.ActiveBlock?.TokensPerMinute is { } tpm
                                      ? $"{tpm:N0} t/min"
                                      : "—";
    public string CompanionName => Store.Companion.CompanionDisplayName;
    public string StageLabel
    {
        get
        {
            var mon = Store.Companion.State.Active;
            if (mon is null) return "Egg";
            return mon.StageIndex >= mon.TotalForms - 1 ? "Final form" : $"Stage {mon.StageIndex + 1} of {mon.TotalForms}";
        }
    }
    public string EggProgress
    {
        get
        {
            var s = Store.Companion.State;
            if (s.Active is { } mon)
            {
                var threshold = PokemonBalance.PhaseThreshold(mon.Rarity, mon.TotalForms, mon.StageIndex);
                var pct = threshold > 0 ? mon.UsedAtStage * 100L / threshold : 100;
                return $"{Math.Min(100, pct)}%";
            }
            var eggPct = PokemonBalance.EggHatchThreshold > 0
                ? s.EggUsage * 100L / PokemonBalance.EggHatchThreshold : 0;
            return $"{Math.Min(100, eggPct)}%";
        }
    }

    public double EggProgressValue
    {
        get
        {
            var s = Store.Companion.State;
            if (s.Active is { } mon)
            {
                var threshold = PokemonBalance.PhaseThreshold(mon.Rarity, mon.TotalForms, mon.StageIndex);
                return threshold > 0 ? Math.Min(1.0, (double)mon.UsedAtStage / threshold) : 1.0;
            }
            return Math.Min(1.0, (double)s.EggUsage / PokemonBalance.EggHatchThreshold);
        }
    }

    public string WalletDisplay => FormatTokens(Store.Companion.State.Wallet);

    private static string FormatTokens(long t) => t switch {
        >= 1_000_000_000 => $"{t / 1_000_000_000.0:F1}B",
        >= 1_000_000     => $"{t / 1_000_000.0:F1}M",
        >= 1_000         => $"{t / 1_000.0:F1}K",
        _                => t.ToString("N0")
    };
}
