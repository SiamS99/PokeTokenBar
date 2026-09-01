using System.Text.Json;

namespace PokeTokenBar.Core;

/// <summary>
/// Writes token usage data to %APPDATA%\PokeTokenBar\omp.json.
/// Each provider's totals are written separately so OhMyPosh segments
/// can choose exactly which source to display.
///
/// Example OhMyPosh segment showing Codex tokens only:
/// {
///   "type": "command",
///   "style": "plain",
///   "foreground": "#FFD700",
///   "template": "{{ .Output }}",
///   "properties": {
///     "shell": "pwsh",
///     "command": "& { $d=(Get-Content -Raw \"$env:APPDATA\\PokeTokenBar\\omp.json\"|ConvertFrom-Json); $d.codex.tokens_today_fmt }"
///   }
/// }
/// </summary>
public class OhMyPoshExporter
{
    private static readonly string OutputPath = Path.Combine(AppSettings.AppDataDir, "omp.json");

    public void Export(
        DailyUsage? claudeToday,
        DailyUsage? codexToday,
        DailyUsage? ompToday,
        DailyUsage? opencodeToday,
        DailyUsage? combinedToday,
        CompanionState state,
        string companionName)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.AppDataDir);

            var mon = state.Active;

            var payload = new
            {
                // Per-provider sections — use these in your OhMyPosh segment.
                claude   = ProviderSection(claudeToday),
                codex    = ProviderSection(codexToday),
                omp      = ProviderSection(ompToday),
                opencode = ProviderSection(opencodeToday),

                // Combined total — use this if you want everything summed.
                combined = ProviderSection(combinedToday),

                // Companion / gamification fields.
                companion        = companionName,
                companion_id     = mon?.CurrentId,
                companion_stage  = mon is not null ? mon.StageIndex + 1 : 0,
                companion_rarity = mon?.Rarity.ToString().ToLowerInvariant() ?? "egg",
                is_shiny         = mon?.IsShiny ?? false,
                egg_progress_pct = mon is null
                    ? (int)Math.Min(100, state.EggUsage * 100 / PokemonBalance.EggHatchThreshold)
                    : 100,
                wallet           = state.Wallet,
                updated_at       = DateTime.Now.ToString("o")
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(OutputPath, json);
        }
        catch { /* non-critical */ }
    }

    private static object ProviderSection(DailyUsage? d) => new
    {
        tokens_today     = d?.TotalTokens     ?? 0,
        tokens_today_fmt = d?.FormattedTokens ?? "0",
        cost_today       = d?.TotalCost       ?? 0,
        cost_today_fmt   = d?.FormattedCost   ?? "$0.0000"
    };
}
