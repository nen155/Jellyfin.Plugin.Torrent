using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Torrent.Configuration;
using Jellyfin.Plugin.Torrent.Models;
using Jellyfin.Plugin.Torrent.Parsing;
using Jellyfin.Plugin.Torrent.Scraping;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Services;

/// <summary>
/// Reúne las "sugerencias del día": los últimos estrenos publicados en las
/// listas de novedades de las fuentes activas, separados en películas y
/// series. La lista se cachea para no golpear las webs en cada visita.
/// </summary>
public sealed partial class DailySuggestionsService
{
    private const int MaxParallelSources = 3;
    private const int MinCacheMinutes = 5;
    private const int MaxCacheMinutes = 1440;
    private const int DefaultCacheMinutes = 720;
    private const int MaxPosterLookups = 16;
    private const int MaxParallelPosterLookups = 3;

    [GeneratedRegex(@"\s*\([^()]*?(19|20)\d{2}\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingYearRegex();

    private readonly IReadOnlyList<ISourceScraper> _scrapers;
    private readonly DomainResolverService _domains;
    private readonly PosterLookupService _posters;
    private readonly ILogger<DailySuggestionsService> _logger;
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private readonly object _sync = new();

    private DailySuggestionsResponse? _cached;
    private DateTimeOffset _expiresAt;
    private string _signature = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="DailySuggestionsService"/> class.
    /// </summary>
    /// <param name="scrapers">Extractores disponibles.</param>
    /// <param name="domains">Resolutor de dominios.</param>
    /// <param name="posters">Buscador de carátulas por título.</param>
    /// <param name="logger">Logger.</param>
    public DailySuggestionsService(
        IEnumerable<ISourceScraper> scrapers,
        DomainResolverService domains,
        PosterLookupService posters,
        ILogger<DailySuggestionsService> logger)
    {
        _scrapers = scrapers.ToList();
        _domains = domains;
        _posters = posters;
        _logger = logger;
    }

    /// <summary>
    /// Devuelve las sugerencias del día, de la caché o recién leídas.
    /// </summary>
    /// <param name="refresh">Fuerza una lectura nueva de las fuentes.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Sugerencias separadas en películas y series.</returns>
    public async Task<DailySuggestionsResponse> GetAsync(bool refresh, CancellationToken cancellationToken)
    {
        Plugin.Instance?.EnsureLatestDefaults();
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var response = new DailySuggestionsResponse
        {
            Enabled = config.DailySuggestionsEnabled,
            CacheMinutes = ResolveCacheMinutes(config)
        };

        if (!config.DailySuggestionsEnabled)
        {
            return response;
        }

        var signature = BuildSignature(config);
        if (!refresh && TryGetCached(signature, out var cached))
        {
            return cached!;
        }

        await _buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && TryGetCached(signature, out cached))
            {
                return cached!;
            }

            var built = await BuildAsync(config, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _cached = built;
                _signature = signature;
                _expiresAt = DateTimeOffset.UtcNow.AddMinutes(response.CacheMinutes);
            }

            return Clone(built);
        }
        finally
        {
            _buildGate.Release();
        }
    }

    /// <summary>
    /// Lee las novedades de una única fuente (para el botón de prueba de la
    /// configuración).
    /// </summary>
    /// <param name="sourceId">Id de la fuente.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Sugerencias y diagnóstico de la fuente.</returns>
    public async Task<DailySuggestionsResponse> TestSourceAsync(
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        Plugin.Instance?.EnsureLatestDefaults();
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var response = new DailySuggestionsResponse { Enabled = true, GeneratedAt = DateTimeOffset.UtcNow };
        var source = config.Sources.FirstOrDefault(s => s.Id == sourceId);
        if (source is null)
        {
            response.Sources.Add(new SourceOutcome { Error = "La fuente no existe." });
            return response;
        }

        var targets = BuildTargets(source).ToList();
        if (targets.Count == 0)
        {
            response.Sources.Add(new SourceOutcome
            {
                SourceId = source.Id,
                Name = source.Name,
                Error = "La fuente no tiene URL de novedades configurada."
            });
            return response;
        }

        foreach (var target in targets)
        {
            var (kind, items, outcome) = await FetchAsync(source, target.Kind, target.Url, config, cancellationToken)
                .ConfigureAwait(false);
            if (kind == MediaKind.Series)
            {
                response.Series.AddRange(items);
            }
            else
            {
                response.Movies.AddRange(items);
            }

            MergeOutcome(response.Sources, outcome);
        }

        return response;
    }

    private async Task<DailySuggestionsResponse> BuildAsync(
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var response = new DailySuggestionsResponse
        {
            Enabled = true,
            GeneratedAt = DateTimeOffset.UtcNow,
            CacheMinutes = ResolveCacheMinutes(config)
        };

        var targets = config.Sources
            .Where(s => s.Enabled)
            .SelectMany(source => BuildTargets(source))
            .ToList();

        if (targets.Count == 0)
        {
            return response;
        }

        var gate = new SemaphoreSlim(MaxParallelSources);
        var tasks = targets.Select(async target =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await FetchAsync(target.Source, target.Kind, target.Url, config, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var movies = new List<DailySuggestion>();
        var series = new List<DailySuggestion>();
        foreach (var (kind, items, outcome) in results)
        {
            if (kind == MediaKind.Series)
            {
                series.AddRange(items);
            }
            else
            {
                movies.AddRange(items);
            }

            MergeOutcome(response.Sources, outcome);
        }

        var limit = Math.Max(1, config.DailySuggestionsCount);
        response.Movies = MixBySource(Deduplicate(movies)).Take(limit).ToList();
        response.Series = MixBySource(Deduplicate(series)).Take(limit).ToList();
        return response;
    }

    private async Task<(MediaKind Kind, List<DailySuggestion> Items, SourceOutcome Outcome)> FetchAsync(
        TorrentSourceConfig source,
        MediaKind kind,
        string urlTemplate,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = new SourceOutcome { SourceId = source.Id, Name = source.Name };
        var items = new List<DailySuggestion>();

        try
        {
            var baseUrl = await _domains.ResolveBaseUrlAsync(source, cancellationToken).ConfigureAwait(false);
            outcome.BaseUrl = baseUrl;

            var scraper = _scrapers.FirstOrDefault(s => s.Kind == source.Kind)
                ?? throw new InvalidOperationException(
                    $"No hay extractor para el tipo de fuente {source.Kind}.");

            var browse = await scraper.BrowseAsync(source, baseUrl, urlTemplate, cancellationToken)
                .ConfigureAwait(false);

            outcome.Error = browse.Error;
            outcome.Diagnostic = browse.Diagnostic;
            outcome.Engine = browse.Engine;

            var limit = Math.Max(1, config.DailySuggestionsCount * 2);
            foreach (var result in browse.Results.Take(limit))
            {
                var suggestion = ToSuggestion(result, kind);
                if (suggestion is not null)
                {
                    items.Add(suggestion);
                }
            }

            if (config.LookupPostersOnline)
            {
                await FillPostersAsync(items, kind, cancellationToken).ConfigureAwait(false);
            }

            outcome.Count = items.Count;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Torrent: fallo leyendo las novedades de {Name}", source.Name);
            outcome.Error = ex.Message;
        }
        finally
        {
            outcome.DurationMs = stopwatch.ElapsedMilliseconds;
        }

        return (kind, items, outcome);
    }

    /// <summary>
    /// Completa la carátula de las sugerencias que no la traen, buscándola en
    /// la web por el título.
    /// </summary>
    private async Task FillPostersAsync(
        List<DailySuggestion> items,
        MediaKind kind,
        CancellationToken cancellationToken)
    {
        var missing = items
            .Where(item => string.IsNullOrWhiteSpace(item.PosterUrl))
            .Take(MaxPosterLookups)
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        var gate = new SemaphoreSlim(MaxParallelPosterLookups);
        var tasks = missing.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                item.PosterUrl = await _posters
                    .FindAsync(item.Title, item.Year, kind, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Torrent: sin carátula para {Title}", item.Title);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static IEnumerable<(TorrentSourceConfig Source, MediaKind Kind, string Url)> BuildTargets(
        TorrentSourceConfig source)    {
        if (!string.IsNullOrWhiteSpace(source.LatestMoviesUrl))
        {
            yield return (source, MediaKind.Movie, source.LatestMoviesUrl!);
        }

        if (!string.IsNullOrWhiteSpace(source.LatestSeriesUrl))
        {
            yield return (source, MediaKind.Series, source.LatestSeriesUrl!);
        }
    }

    private static DailySuggestion? ToSuggestion(TorrentResult result, MediaKind kind)
    {
        var title = TextUtil.Clean(result.Title);

        // "Doctor of Love (Series 2026)" → "Doctor of Love": el año ya se muestra aparte.
        var withoutYear = TrailingYearRegex().Replace(title, string.Empty).Trim();
        if (withoutYear.Length >= 2)
        {
            title = withoutYear;
        }

        if (title.Length < 2)
        {
            return null;
        }

        return new DailySuggestion
        {
            SourceId = result.SourceId,
            SourceName = result.SourceName,
            Title = title,
            ReleaseName = result.ReleaseName,
            DetailUrl = result.DetailUrl,
            PosterUrl = result.PosterUrl,
            Category = result.Category,
            Resolution = result.Resolution,
            EpisodeText = result.EpisodeText,
            Year = result.Year,
            PublishedText = result.PublishedText,
            Kind = kind
        };
    }

    private static List<DailySuggestion> Deduplicate(IEnumerable<DailySuggestion> items)
    {
        var map = new Dictionary<string, DailySuggestion>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var key = TextUtil.Normalize(item.Title)
                + "|"
                + (item.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

            if (!map.TryGetValue(key, out var existing))
            {
                map[key] = item;
                continue;
            }

            if (Richness(item) > Richness(existing))
            {
                map[key] = item;
            }
        }

        return map.Values.ToList();
    }

    /// <summary>
    /// Alterna las fuentes para que ninguna acapare la previsualización.
    /// </summary>
    private static IEnumerable<DailySuggestion> MixBySource(IEnumerable<DailySuggestion> items)
    {
        var queues = items
            .GroupBy(item => item.SourceId)
            .Select(group => new Queue<DailySuggestion>(group))
            .ToList();

        var mixed = new List<DailySuggestion>();
        var added = true;
        while (added)
        {
            added = false;
            foreach (var queue in queues)
            {
                if (queue.Count == 0)
                {
                    continue;
                }

                mixed.Add(queue.Dequeue());
                added = true;
            }
        }

        return mixed;
    }

    private static int Richness(DailySuggestion item) =>
        (string.IsNullOrWhiteSpace(item.PosterUrl) ? 0 : 4)
        + (item.Year is null ? 0 : 2)
        + (string.IsNullOrWhiteSpace(item.Category) ? 0 : 1)
        + (string.IsNullOrWhiteSpace(item.Resolution) ? 0 : 1);

    private static void MergeOutcome(List<SourceOutcome> outcomes, SourceOutcome outcome)
    {
        var existing = outcomes.FirstOrDefault(o => o.SourceId == outcome.SourceId);
        if (existing is null)
        {
            outcomes.Add(outcome);
            return;
        }

        existing.Count += outcome.Count;
        existing.DurationMs = Math.Max(existing.DurationMs, outcome.DurationMs);
        existing.Engine ??= outcome.Engine;
        existing.BaseUrl ??= outcome.BaseUrl;
        if (!string.IsNullOrWhiteSpace(outcome.Error))
        {
            existing.Error = string.IsNullOrWhiteSpace(existing.Error)
                ? outcome.Error
                : existing.Error + " · " + outcome.Error;
        }
    }

    private bool TryGetCached(string signature, out DailySuggestionsResponse? response)
    {
        lock (_sync)
        {
            if (_cached is not null
                && string.Equals(_signature, signature, StringComparison.Ordinal)
                && _expiresAt > DateTimeOffset.UtcNow)
            {
                response = Clone(_cached);
                response.Cached = true;
                return true;
            }
        }

        response = null;
        return false;
    }

    private static int ResolveCacheMinutes(PluginConfiguration config) =>
        Math.Clamp(
            config.DailySuggestionsCacheMinutes <= 0 ? DefaultCacheMinutes : config.DailySuggestionsCacheMinutes,
            MinCacheMinutes,
            MaxCacheMinutes);

    private static string BuildSignature(PluginConfiguration config) =>
        string.Join(
            '|',
            config.Sources
                .Where(s => s.Enabled)
                .Select(s => $"{s.Id}:{s.LatestMoviesUrl}:{s.LatestSeriesUrl}:"
                    + $"{s.LatestRowSelector}:{s.LatestTitleSelector}:"
                    + $"{s.LatestLinkSelector}:{s.LatestPosterSelector}"))
        + "|" + config.DailySuggestionsCount;

    private static DailySuggestionsResponse Clone(DailySuggestionsResponse source) => new()
    {
        Enabled = source.Enabled,
        Movies = source.Movies.Select(CloneSuggestion).ToList(),
        Series = source.Series.Select(CloneSuggestion).ToList(),
        Sources = source.Sources.Select(o => new SourceOutcome
        {
            SourceId = o.SourceId,
            Name = o.Name,
            Count = o.Count,
            Error = o.Error,
            Diagnostic = o.Diagnostic,
            Engine = o.Engine,
            DurationMs = o.DurationMs,
            BaseUrl = o.BaseUrl
        }).ToList(),
        GeneratedAt = source.GeneratedAt,
        CacheMinutes = source.CacheMinutes
    };

    private static DailySuggestion CloneSuggestion(DailySuggestion source) => new()
    {
        SourceId = source.SourceId,
        SourceName = source.SourceName,
        Title = source.Title,
        ReleaseName = source.ReleaseName,
        DetailUrl = source.DetailUrl,
        PosterUrl = source.PosterUrl,
        Category = source.Category,
        Resolution = source.Resolution,
        EpisodeText = source.EpisodeText,
        Year = source.Year,
        PublishedText = source.PublishedText,
        Kind = source.Kind
    };
}
