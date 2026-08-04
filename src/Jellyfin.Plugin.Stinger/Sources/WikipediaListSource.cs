using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Stinger.Sources;

/// <summary>
/// Cross-check against Wikipedia's "List of films with post-credits scenes".
/// The list is crawled via the MediaWiki API into a local index, refreshed weekly,
/// and kept on failure (graceful degradation — this source can only affirm, never deny).
/// </summary>
public partial class WikipediaListSource
{
    private const string ApiUrl =
        "https://en.wikipedia.org/w/api.php?action=parse&page=List_of_films_with_post-credits_scenes&prop=wikitext&format=json&formatversion=2";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WikipediaListSource> _logger;
    private readonly string _indexPath;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private WikipediaIndex? _index;

    public WikipediaListSource(
        IHttpClientFactory httpClientFactory,
        IApplicationPaths applicationPaths,
        ILogger<WikipediaListSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _indexPath = Path.Combine(applicationPaths.DataPath, "stinger", "wikipedia-index.json");
    }

    /// <summary>Returns true when the film is on the list; null otherwise (never false).</summary>
    public async Task<bool?> IsListedAsync(string title, int? year, CancellationToken cancellationToken)
    {
        var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        if (index is null || index.Titles.Count == 0)
        {
            return null;
        }

        if (!index.Titles.TryGetValue(Normalize(title), out var years))
        {
            return null;
        }

        if (year is null || years.Count == 0 || years.Any(y => Math.Abs(y - year.Value) <= 1))
        {
            return true;
        }

        return null;
    }

    public static string Normalize(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (var c in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>Extracts (title, year) pairs from the list page's wikitext tables.</summary>
    public static Dictionary<string, List<int>> ParseWikitext(string wikitext)
    {
        var titles = new Dictionary<string, List<int>>();

        // Table rows are separated by "|-" lines; the film title is the first wikilink
        // in the row and the year is the first standalone 4-digit number.
        foreach (var row in wikitext.Split("|-"))
        {
            var linkMatch = WikiLinkRegex().Match(row);
            if (!linkMatch.Success)
            {
                continue;
            }

            var title = linkMatch.Groups[1].Value;
            title = ParentheticalRegex().Replace(title, string.Empty).Trim();
            if (title.Length == 0 || title.Contains("List of", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = Normalize(title);
            if (key.Length == 0)
            {
                continue;
            }

            if (!titles.TryGetValue(key, out var years))
            {
                years = new List<int>();
                titles[key] = years;
            }

            var yearMatch = YearRegex().Match(row);
            if (yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out var year))
            {
                years.Add(year);
            }
        }

        return titles;
    }

    private async Task<WikipediaIndex?> GetIndexAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.EnableWikipediaSource != true)
        {
            return null;
        }

        if (_index is not null && DateTime.UtcNow - _index.FetchedAtUtc < RefreshInterval)
        {
            return _index;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_index is null && File.Exists(_indexPath))
            {
                try
                {
                    _index = JsonSerializer.Deserialize<WikipediaIndex>(await File.ReadAllTextAsync(_indexPath, cancellationToken).ConfigureAwait(false));
                }
                catch (JsonException)
                {
                }
            }

            if (_index is not null && DateTime.UtcNow - _index.FetchedAtUtc < RefreshInterval)
            {
                return _index;
            }

            var fresh = await FetchAsync(cancellationToken).ConfigureAwait(false);
            if (fresh is not null)
            {
                // Sanity check: a drastic shrink means the page layout changed and the
                // parser broke — keep the last good index rather than degrade silently.
                if (_index is not null && fresh.Titles.Count < _index.Titles.Count / 2)
                {
                    _logger.LogWarning(
                        "Wikipedia index shrank from {Old} to {New} entries; keeping previous index",
                        _index.Titles.Count,
                        fresh.Titles.Count);
                    _index.FetchedAtUtc = DateTime.UtcNow;
                }
                else
                {
                    _index = fresh;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
                await File.WriteAllTextAsync(_indexPath, JsonSerializer.Serialize(_index), cancellationToken).ConfigureAwait(false);
            }

            return _index;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<WikipediaIndex?> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client.GetAsync(ApiUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var wikitext = doc.RootElement.GetProperty("parse").GetProperty("wikitext").GetString();
            if (string.IsNullOrEmpty(wikitext))
            {
                return null;
            }

            var titles = ParseWikitext(wikitext);
            _logger.LogInformation("Wikipedia stinger index refreshed: {Count} titles", titles.Count);
            return new WikipediaIndex { FetchedAtUtc = DateTime.UtcNow, Titles = titles };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            _logger.LogWarning(ex, "Wikipedia list fetch failed; using cached index if available");
            return null;
        }
    }

    private sealed class WikipediaIndex
    {
        public DateTime FetchedAtUtc { get; set; }

        [JsonPropertyName("titles")]
        public Dictionary<string, List<int>> Titles { get; set; } = new();
    }

    [GeneratedRegex(@"\[\[([^\]|#]+)(?:\|[^\]]*)?\]\]")]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"\s*\((?:[0-9]{4} )?film\)$|\s*\([0-9]{4}\)$")]
    private static partial Regex ParentheticalRegex();

    [GeneratedRegex(@"\b(19[2-9][0-9]|20[0-9][0-9])\b")]
    private static partial Regex YearRegex();
}
