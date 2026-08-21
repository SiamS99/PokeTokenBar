using System.Net.Http;
using System.Text.Json.Nodes;

namespace PokeTokenBar.Core;

/// <summary>
/// Lightweight PokéAPI wrapper.  Caches all responses to %APPDATA%\PokeTokenBar\cache\.
/// </summary>
public class PokeApiClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly string CacheDir = Path.Combine(AppSettings.AppDataDir, "cache");

    // ── Sprite URLs ───────────────────────────────────────────────────────────

    public string AnimatedSpriteUrl(int speciesId, bool shiny)
    {
        var variant = shiny ? "shiny" : "default";
        return $"https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/versions/generation-v/black-white/animated/{(shiny ? "shiny/" : "")}{speciesId}.gif";
    }

    public string StaticSpriteUrl(int speciesId, bool shiny)
    {
        var s = shiny ? "shiny/" : "";
        return $"https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/{s}{speciesId}.png";
    }

    // ── Sprite caching ────────────────────────────────────────────────────────

    public async Task<byte[]?> FetchSpriteAsync(int speciesId, bool shiny, bool animated = true)
    {
        var fname = $"{speciesId}{(shiny ? "_s" : "")}{(animated ? ".gif" : ".png")}";
        var cached = Path.Combine(CacheDir, "sprites", fname);

        if (File.Exists(cached))
        {
            try { return await File.ReadAllBytesAsync(cached); } catch { }
        }

        var url = animated ? AnimatedSpriteUrl(speciesId, shiny) : StaticSpriteUrl(speciesId, shiny);
        byte[]? data = null;
        try
        {
            data = await _http.GetByteArrayAsync(url);
        }
        catch
        {
            // Animated may not exist for all species — fall back to static.
            if (animated)
            {
                try { data = await _http.GetByteArrayAsync(StaticSpriteUrl(speciesId, shiny)); }
                catch { return null; }
            }
            else return null;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            await File.WriteAllBytesAsync(cached, data);
        }
        catch { }

        return data;
    }

    // ── Species info ──────────────────────────────────────────────────────────

    public async Task<(string name, Rarity rarity, int captureRate)?> FetchSpeciesInfoAsync(int speciesId)
    {
        var json = await FetchJsonAsync($"https://pokeapi.co/api/v2/pokemon-species/{speciesId}/");
        if (json is null) return null;

        var captureRate = (int)(json["capture_rate"]?.GetValue<int>() ?? 255);
        var isLegendary = json["is_legendary"]?.GetValue<bool>() ?? false;
        var isMythical  = json["is_mythical"]?.GetValue<bool>()  ?? false;
        var rarity      = RarityFrom(captureRate, isLegendary, isMythical);

        var names   = json["names"]?.AsArray();
        var engName = names?
            .Where(n => n?["language"]?["name"]?.GetValue<string>() == "en")
            .Select(n => n?["name"]?.GetValue<string>())
            .FirstOrDefault() ?? $"#{speciesId}";

        return (engName, rarity, captureRate);
    }

    public async Task<int[]?> FetchEvolutionChainAsync(int speciesId)
    {
        // Fetch species to get evolution chain URL.
        var species = await FetchJsonAsync($"https://pokeapi.co/api/v2/pokemon-species/{speciesId}/");
        if (species is null) return null;

        var chainUrl = species["evolution_chain"]?["url"]?.GetValue<string>();
        if (chainUrl is null) return null;

        var chain = await FetchJsonAsync(chainUrl);
        if (chain is null) return null;

        var ids = new List<int>();
        CollectChain(chain["chain"], ids);
        return [.. ids];
    }

    private static void CollectChain(JsonNode? node, List<int> ids)
    {
        if (node is null) return;
        var url = node["species"]?["url"]?.GetValue<string>();
        if (url is not null)
        {
            var parts = url.TrimEnd('/').Split('/');
            if (int.TryParse(parts[^1], out var id)) ids.Add(id);
        }
        foreach (var child in node["evolves_to"]?.AsArray() ?? [])
            CollectChain(child, ids);
    }

    // ── Random species picker ─────────────────────────────────────────────────

    // Pick a random Gen 1-5 species ID instantly, no network calls.
    // Rarity weighting happens at hatch time via FetchSpeciesInfoAsync.
    public Task<int?> PickRandomSpeciesAsync(Rarity? minRarity = null, Random? rng = null)
    {
        rng ??= Random.Shared;
        return Task.FromResult<int?>(rng.Next(1, 650));
    }

    // ── JSON fetch with disk cache ────────────────────────────────────────────

    private async Task<JsonNode?> FetchJsonAsync(string url)
    {
        var fname   = Uri.EscapeDataString(url).Replace("%2F", "_").Replace("%3A", "_");
        var maxLen  = Math.Min(fname.Length, 120);
        var cached  = Path.Combine(CacheDir, "api", fname[..maxLen] + ".json");

        if (File.Exists(cached))
        {
            try
            {
                var raw = await File.ReadAllTextAsync(cached);
                return JsonNode.Parse(raw);
            }
            catch { }
        }

        string? body;
        try { body = await _http.GetStringAsync(url); }
        catch { return null; }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            await File.WriteAllTextAsync(cached, body);
        }
        catch { }

        return JsonNode.Parse(body);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Rarity RarityFrom(int captureRate, bool isLegendary, bool isMythical)
    {
        if (isLegendary || isMythical) return Rarity.Legendary;
        if (captureRate <= 45)  return Rarity.Rare;
        if (captureRate <= 120) return Rarity.Uncommon;
        return Rarity.Common;
    }

    public void Dispose() => _http.Dispose();
}
