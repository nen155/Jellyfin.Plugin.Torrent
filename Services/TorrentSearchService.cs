using System.Collections.Concurrent;
using System.Diagnostics;
using Jellyfin.Plugin.Torrent.Configuration;
using Jellyfin.Plugin.Torrent.Models;
using Jellyfin.Plugin.Torrent.Parsing;
using Jellyfin.Plugin.Torrent.Scraping;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Services;

/// <summary>
/// Agrega las búsquedas de todas las fuentes activas, deduplica por info-hash,
/// filtra, puntúa y ordena los resultados.
/// </summary>
public sealed class TorrentSearchService
{
    private const int MaxParallelSources = 4;
    private const int MaxParallelDetails = 3;

    private readonly IReadOnlyList<ISourceScraper> _scrapers;
    private readonly DomainResolverService _domains;
    private readonly ILogger<TorrentSearchService> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TorrentSearchService"/> class.
    /// </summary>
    /// <param name="scrapers">Extractores disponibles.</param>
    /// <param name="domains">Resolutor de dominios.</param>
    /// <param name="logger">Logger.</param>
    public TorrentSearchService(
        IEnumerable<ISourceScraper> scrapers,
        DomainResolverService domains,
        ILogger<TorrentSearchService> logger)
    {
        _scrapers = scrapers.ToList();
        _domains = domains;
        _logger = logger;
    }

    /// <summary>
    /// Ejecuta una búsqueda agregada.
    /// </summary>
    /// <param name="request">Petición.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Respuesta con resultados y estado de cada fuente.</returns>
    public async Task<SearchResponse> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var query = (request.Query ?? string.Empty).Trim();
        var response = new SearchResponse { Query = query };

        if (!config.Enabled || query.Length < 2)
        {
            return response;
        }

        var sources = config.Sources
            .Where(s => s.Enabled)
            .Where(s => request.SourceIds.Count == 0 || request.SourceIds.Contains(s.Id))
            .ToList();

        if (sources.Count == 0)
        {
            return response;
        }

        var cacheKey = BuildCacheKey(query, sources);
        if (config.CacheMinutes > 0
            && _cache.TryGetValue(cacheKey, out var entry)
            && entry.Expires > DateTimeOffset.UtcNow)
        {
            response.Results = ApplyFiltersAndSort(entry.Results, request, config);
            response.Sources = entry.Outcomes.Select(CloneOutcome).ToList();
            response.TotalBeforeFilters = entry.Results.Count;
            response.Cached = true;
            response.DurationMs = stopwatch.ElapsedMilliseconds;
            return response;
        }

        var gate = new SemaphoreSlim(MaxParallelSources);
        var tasks = sources.Select(async source =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await SearchSourceAsync(source, query, config, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        var perSource = await Task.WhenAll(tasks).ConfigureAwait(false);

        var merged = new List<TorrentResult>();
        var outcomes = new List<SourceOutcome>();
        foreach (var (outcome, results) in perSource)
        {
            outcomes.Add(outcome);
            merged.AddRange(results);
        }

        var deduped = Deduplicate(merged);
        response.TotalBeforeFilters = deduped.Count;
        response.Results = ApplyFiltersAndSort(deduped, request, config);
        response.Sources = outcomes;
        response.DurationMs = stopwatch.ElapsedMilliseconds;

        if (config.CacheMinutes > 0)
        {
            _cache[cacheKey] = new CacheEntry
            {
                Expires = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(config.CacheMinutes, 1, 720)),
                Results = deduped,
                Outcomes = outcomes
            };

            PruneCache();
        }

        return response;
    }

    /// <summary>
    /// Consulta una única fuente (para el botón "Probar" de la configuración).
    /// </summary>
    /// <param name="sourceId">Id de la fuente.</param>
    /// <param name="query">Texto de prueba.</param>
    /// <param name="fetchDetails">Si se visitan las fichas de detalle.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Resultados y diagnóstico.</returns>
    public async Task<SearchResponse> TestSourceAsync(
        Guid sourceId,
        string query,
        bool fetchDetails,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var source = config.Sources.FirstOrDefault(s => s.Id == sourceId);
        var response = new SearchResponse { Query = query };

        if (source is null)
        {
            response.Sources.Add(new SourceOutcome { Error = "La fuente no existe." });
            return response;
        }

        var probe = source.Clone();
        probe.FetchDetails = fetchDetails;
        var (outcome, results) = await SearchSourceAsync(
            probe, query, config, cancellationToken, detailLimit: 3).ConfigureAwait(false);
        response.Sources.Add(outcome);
        response.Results = results;
        response.TotalBeforeFilters = results.Count;
        response.DurationMs = stopwatch.ElapsedMilliseconds;
        return response;
    }

    private async Task<(SourceOutcome Outcome, List<TorrentResult> Results)> SearchSourceAsync(
        TorrentSourceConfig source,
        string query,
        PluginConfiguration config,
        CancellationToken cancellationToken,
        int? detailLimit = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = new SourceOutcome { SourceId = source.Id, Name = source.Name };

        try
        {
            var baseUrl = await _domains.ResolveBaseUrlAsync(source, cancellationToken)
                .ConfigureAwait(false);
            outcome.BaseUrl = baseUrl;

            var scraper = _scrapers.FirstOrDefault(s => s.Kind == source.Kind)
                ?? throw new InvalidOperationException(
                    $"No hay extractor para el tipo de fuente {source.Kind}.");

            var search = await scraper.SearchAsync(source, baseUrl, query, cancellationToken)
                .ConfigureAwait(false);

            outcome.Error = search.Error;
            outcome.Diagnostic = search.Diagnostic;
            outcome.Engine = search.Engine;

            var items = search.Results
                .Take(Math.Max(1, config.MaxResultsPerSource))
                .ToList();

            var limit = detailLimit ?? config.DetailFetchLimit;
            if (limit <= 0)
            {
                limit = items.Count;
            }

            limit = Math.Min(limit, items.Count);
            var enriched = await EnrichAllAsync(scraper, source, baseUrl, items, limit, cancellationToken)
                .ConfigureAwait(false);

            outcome.Count = enriched.Count;
            return (outcome, enriched);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Torrent: fallo consultando la fuente {Name}", source.Name);
            outcome.Error = ex.Message;
            return (outcome, new List<TorrentResult>());
        }
        finally
        {
            outcome.DurationMs = stopwatch.ElapsedMilliseconds;
        }
    }

    private static async Task<List<TorrentResult>> EnrichAllAsync(
        ISourceScraper scraper,
        TorrentSourceConfig source,
        string baseUrl,
        List<TorrentResult> items,
        int limit,
        CancellationToken cancellationToken)
    {
        var gate = new SemaphoreSlim(MaxParallelDetails);
        var tasks = items.Select(async (item, index) =>
        {
            if (index >= limit)
            {
                return (IReadOnlyList<TorrentResult>)new[] { item };
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await scraper.EnrichAsync(source, baseUrl, item, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return new[] { item };
            }
            finally
            {
                gate.Release();
            }
        });

        var groups = await Task.WhenAll(tasks).ConfigureAwait(false);
        var results = new List<TorrentResult>();
        foreach (var group in groups)
        {
            results.AddRange(group);
        }

        return results;
    }

    private static List<TorrentResult> Deduplicate(IEnumerable<TorrentResult> results)
    {
        var map = new Dictionary<string, TorrentResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            var key = BuildResultKey(result);
            if (!map.TryGetValue(key, out var existing))
            {
                map[key] = result;
                continue;
            }

            var existingSeeds = existing.Seeds ?? 0;
            var newSeeds = result.Seeds ?? 0;
            if (newSeeds > existingSeeds || (newSeeds == existingSeeds && !existing.HasLink && result.HasLink))
            {
                result.Magnet ??= existing.Magnet;
                result.TorrentUrl ??= existing.TorrentUrl;
                result.PosterUrl ??= existing.PosterUrl;
                result.Description ??= existing.Description;
                map[key] = result;
            }
            else
            {
                existing.Magnet ??= result.Magnet;
                existing.TorrentUrl ??= result.TorrentUrl;
                existing.PosterUrl ??= result.PosterUrl;
                existing.Description ??= result.Description;
            }
        }

        return map.Values.ToList();
    }

    private static string BuildResultKey(TorrentResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.InfoHash))
        {
            return "h:" + result.InfoHash;
        }

        if (!string.IsNullOrWhiteSpace(result.DetailUrl))
        {
            return "u:" + result.DetailUrl;
        }

        return "t:" + TextUtil.Normalize(result.ReleaseName ?? result.Title)
            + "|" + (result.SizeBytes ?? 0);
    }

    private static List<TorrentResult> ApplyFiltersAndSort(
        List<TorrentResult> results,
        SearchRequest request,
        PluginConfiguration config)
    {
        var minSeeds = request.MinSeeds > 0 ? request.MinSeeds : config.MinSeeds;
        var blocked = config.BlockedKeywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToList();

        var filtered = new List<TorrentResult>(results.Count);
        foreach (var result in results)
        {
            if (minSeeds > 0 && (result.Seeds ?? 0) < minSeeds)
            {
                continue;
            }

            var haystack = TextUtil.Normalize($"{result.ReleaseName} {result.Title}");
            if (blocked.Any(k => haystack.Contains(TextUtil.Normalize(k), StringComparison.Ordinal)))
            {
                continue;
            }

            if (request.MediaType.Equals("movie", StringComparison.OrdinalIgnoreCase)
                && result.Kind == MediaKind.Series)
            {
                continue;
            }

            if (request.MediaType.Equals("series", StringComparison.OrdinalIgnoreCase)
                && result.Kind == MediaKind.Movie)
            {
                continue;
            }

            if (request.Languages.Count > 0
                && !LanguageDetector.Matches(result.Languages, request.Languages))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(request.Quality)
                && !string.Equals(result.Resolution, request.Quality, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Score = ComputeScore(result, request.Query);
            filtered.Add(result);
        }

        return request.Sort switch
        {
            "size" => filtered
                .OrderByDescending(r => r.SizeBytes ?? 0)
                .ThenByDescending(r => r.Score)
                .ToList(),
            "date" => filtered
                .OrderByDescending(r => r.Published ?? DateTimeOffset.MinValue)
                .ThenByDescending(r => r.Score)
                .ToList(),
            "relevance" => filtered
                .OrderByDescending(r => r.Score)
                .ThenByDescending(r => r.Seeds ?? 0)
                .ToList(),
            _ => filtered
                .OrderByDescending(r => r.Seeds ?? 0)
                .ThenByDescending(r => r.Score)
                .ToList()
        };
    }

    private static double ComputeScore(TorrentResult result, string query)
    {
        var title = TextUtil.Normalize(result.Title ?? result.ReleaseName);
        var words = TextUtil.Normalize(query)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var score = 0d;
        if (words.Length > 0)
        {
            var matches = words.Count(w => title.Contains(w, StringComparison.Ordinal));
            score += 55 * matches / words.Length;
            if (title.StartsWith(TextUtil.Normalize(query), StringComparison.Ordinal))
            {
                score += 10;
            }
        }

        score += Math.Min(20, Math.Log10((result.Seeds ?? 0) + 1) * 10);

        if (result.Published is not null)
        {
            var days = Math.Max(0, (DateTimeOffset.UtcNow - result.Published.Value).TotalDays);
            score += Math.Max(0, 8 - (days / 120));
        }

        if (result.Resolution is "2160p" or "1080p")
        {
            score += 4;
        }

        if (result.HasLink)
        {
            score += 5;
        }

        return Math.Round(score, 2);
    }

    private static string BuildCacheKey(string query, IEnumerable<TorrentSourceConfig> sources) =>
        TextUtil.Normalize(query)
        + "|"
        + string.Join(',', sources.Select(s => s.Id).OrderBy(id => id));

    private static SourceOutcome CloneOutcome(SourceOutcome outcome) => new()
    {
        SourceId = outcome.SourceId,
        Name = outcome.Name,
        Count = outcome.Count,
        Error = outcome.Error,
        Diagnostic = outcome.Diagnostic,
        Engine = outcome.Engine,
        DurationMs = outcome.DurationMs,
        BaseUrl = outcome.BaseUrl
    };

    private void PruneCache()
    {
        if (_cache.Count <= 60)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _cache.Where(p => p.Value.Expires <= now).ToList())
        {
            _cache.TryRemove(pair.Key, out _);
        }

        while (_cache.Count > 60)
        {
            var oldest = _cache.OrderBy(p => p.Value.Expires).FirstOrDefault();
            if (oldest.Key is null)
            {
                break;
            }

            _cache.TryRemove(oldest.Key, out _);
        }
    }

    private sealed class CacheEntry
    {
        public DateTimeOffset Expires { get; init; }

        public List<TorrentResult> Results { get; init; } = new();

        public List<SourceOutcome> Outcomes { get; init; } = new();
    }
}
