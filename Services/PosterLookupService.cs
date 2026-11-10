using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.Torrent.Models;
using Jellyfin.Plugin.Torrent.Parsing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Services;

/// <summary>
/// Busca carátulas en la web por el nombre del título cuando la fuente no las
/// publica (o publica una que no se puede cargar). Usa el buscador público de
/// IMDb, que no necesita clave, y guarda el resultado en memoria.
/// </summary>
public sealed class PosterLookupService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(12);
    private const string SearchUrl = "https://v3.sg.media-imdb.com/suggestion/x/";

    private readonly HttpClient _http;
    private readonly ILogger<PosterLookupService> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PosterLookupService"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public PosterLookupService(ILogger<PosterLookupService> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>
    /// Devuelve la URL de la carátula de un título, si se encuentra.
    /// </summary>
    /// <param name="title">Título.</param>
    /// <param name="year">Año, si se conoce.</param>
    /// <param name="kind">Película o serie.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>URL de la carátula o null.</returns>
    public async Task<string?> FindAsync(
        string? title,
        int? year,
        MediaKind kind,
        CancellationToken cancellationToken)
    {
        var clean = TextUtil.Clean(title);
        if (clean.Length < 2)
        {
            return null;
        }

        var key = string.Join(
            '|',
            TextUtil.Normalize(clean),
            year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            kind.ToString());

        if (_cache.TryGetValue(key, out var entry) && entry.Expires > DateTimeOffset.UtcNow)
        {
            return entry.Url;
        }

        var found = await QueryAsync(clean, year, kind, cancellationToken).ConfigureAwait(false);
        _cache[key] = new CacheEntry
        {
            Url = found,
            Expires = DateTimeOffset.UtcNow.Add(Ttl)
        };

        Prune();
        return found;
    }

    private async Task<string?> QueryAsync(
        string title,
        int? year,
        MediaKind kind,
        CancellationToken cancellationToken)
    {
        var url = $"{SearchUrl}{Uri.EscapeDataString(title)}.json?includeVideos=0";

        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("d", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var wanted = TextUtil.Normalize(title);
            string? fallback = null;

            foreach (var item in items.EnumerateArray())
            {
                var name = TextUtil.Normalize(ReadString(item, "l"));
                if (name.Length == 0)
                {
                    continue;
                }

                // Solo se acepta una coincidencia clara para no poner la
                // carátula de otra película.
                if (!name.Contains(wanted, StringComparison.Ordinal)
                    && !wanted.Contains(name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!MatchesKind(item, kind))
                {
                    continue;
                }

                var poster = Resize(ReadString(item, "i", "imageUrl"));
                if (string.IsNullOrWhiteSpace(poster))
                {
                    continue;
                }

                var itemYear = ReadInt(item, "y");
                if (year is null || itemYear == year)
                {
                    return poster;
                }

                fallback ??= poster;
            }

            return fallback;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Torrent: no se pudo buscar la carátula de {Title}", title);
        }

        return null;
    }

    private static bool MatchesKind(JsonElement item, MediaKind kind)
    {
        if (kind == MediaKind.Unknown)
        {
            return true;
        }

        var type = ReadString(item, "qid") ?? ReadString(item, "q") ?? string.Empty;
        var isSeries = type.Contains("tvSeries", StringComparison.OrdinalIgnoreCase)
            || type.Contains("tvMiniSeries", StringComparison.OrdinalIgnoreCase)
            || type.Contains("TV series", StringComparison.OrdinalIgnoreCase)
            || type.Contains("TV mini", StringComparison.OrdinalIgnoreCase);

        return kind == MediaKind.Series ? isSeries : !isSeries;
    }

    /// <summary>
    /// Pide la imagen a 600 px de ancho (la original puede ser enorme).
    /// </summary>
    private static string? Resize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var marker = url.IndexOf("._V1_", StringComparison.Ordinal);
        return marker < 0 ? url : url[..marker] + "._V1_SX600.jpg";
    }

    private static string? ReadString(JsonElement item, string property, string? nested = null)
    {
        if (!item.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (nested is not null)
        {
            return value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty(nested, out var inner)
                && inner.ValueKind == JsonValueKind.String
                    ? inner.GetString()
                    : null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int? ReadInt(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private void Prune()
    {
        if (_cache.Count <= 400)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _cache.Where(p => p.Value.Expires <= now).ToList())
        {
            _cache.TryRemove(pair.Key, out _);
        }
    }

    private sealed class CacheEntry
    {
        public string? Url { get; init; }

        public DateTimeOffset Expires { get; init; }
    }
}
