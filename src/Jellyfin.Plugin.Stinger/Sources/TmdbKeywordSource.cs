using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Stinger.Sources;

public sealed record TmdbStingerFlags(bool During, bool After);

/// <summary>
/// Cross-check via TMDB's community keywords:
/// 179431 = duringcreditsstinger, 179430 = aftercreditsstinger.
/// Returns null when the source is unavailable (no API key, network error) — never a hard "no".
/// </summary>
public class TmdbKeywordSource
{
    private const int AfterCreditsKeywordId = 179430;
    private const int DuringCreditsKeywordId = 179431;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbKeywordSource> _logger;

    public TmdbKeywordSource(IHttpClientFactory httpClientFactory, ILogger<TmdbKeywordSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<TmdbStingerFlags?> GetFlagsAsync(string tmdbId, CancellationToken cancellationToken)
    {
        var apiKey = Plugin.Instance?.Configuration.TmdbApiKey;
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(tmdbId))
        {
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            var url = $"https://api.themoviedb.org/3/movie/{Uri.EscapeDataString(tmdbId)}/keywords?api_key={Uri.EscapeDataString(apiKey)}";
            var response = await client.GetFromJsonAsync<KeywordsResponse>(url, cancellationToken).ConfigureAwait(false);
            if (response?.Keywords is null)
            {
                return null;
            }

            return new TmdbStingerFlags(
                During: response.Keywords.Any(k => k.Id == DuringCreditsKeywordId),
                After: response.Keywords.Any(k => k.Id == AfterCreditsKeywordId));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _logger.LogDebug(ex, "TMDB keyword lookup failed for {TmdbId}", tmdbId);
            return null;
        }
    }

    private sealed class KeywordsResponse
    {
        [JsonPropertyName("keywords")]
        public List<Keyword>? Keywords { get; set; }
    }

    private sealed class Keyword
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
    }
}
