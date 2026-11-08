using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Torrent.Models;
using Jellyfin.Plugin.Torrent.Parsing;
using Jellyfin.Plugin.Torrent.Scraping;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Services;

/// <summary>
/// Resuelve el dominio vigente de las fuentes que rotan de dirección, leyendo
/// una página HTTP o los últimos mensajes de un canal de Discord.
/// </summary>
public sealed class DomainResolverService : IDisposable
{
    private readonly HttpFetcher _fetcher;
    private readonly ILogger<DomainResolverService> _logger;
    private readonly HttpClient _discord;
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset Expires, string BaseUrl)> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DomainResolverService"/> class.
    /// </summary>
    /// <param name="fetcher">Cliente HTTP.</param>
    /// <param name="logger">Logger.</param>
    public DomainResolverService(HttpFetcher fetcher, ILogger<DomainResolverService> logger)
    {
        _fetcher = fetcher;
        _logger = logger;
        _discord = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _discord.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.TorrentSearch/1.0");
    }

    /// <summary>
    /// Obtiene la URL base efectiva de una fuente (resuelta y cacheada).
    /// </summary>
    /// <param name="source">Fuente.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>URL base a usar.</returns>
    public async Task<string> ResolveBaseUrlAsync(
        TorrentSourceConfig source,
        CancellationToken cancellationToken)
    {
        var config = source.Domain;
        if (config.Provider == DomainProvider.None || !config.AutoApply)
        {
            return source.BaseUrl;
        }

        if (_cache.TryGetValue(source.Id, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
        {
            return cached.BaseUrl;
        }

        var resolved = await ResolveNowAsync(source, cancellationToken).ConfigureAwait(false);
        return resolved ?? source.BaseUrl;
    }

    /// <summary>
    /// Fuerza la resolución del dominio y actualiza la caché.
    /// </summary>
    /// <param name="source">Fuente.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>URL base resuelta o null si no se pudo.</returns>
    public async Task<string?> ResolveNowAsync(
        TorrentSourceConfig source,
        CancellationToken cancellationToken)
    {
        var config = source.Domain;
        if (config.Provider == DomainProvider.None)
        {
            return null;
        }

        string? host = null;
        try
        {
            host = config.Provider switch
            {
                DomainProvider.HttpPage => await ResolveFromPageAsync(source, cancellationToken)
                    .ConfigureAwait(false),
                DomainProvider.Discord => await ResolveFromDiscordAsync(source, cancellationToken)
                    .ConfigureAwait(false),
                _ => null
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Torrent: no se pudo resolver el dominio de {Name}", source.Name);
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var baseUrl = CombineBaseUrl(source.BaseUrl, host!);
        var ttl = TimeSpan.FromMinutes(Math.Clamp(config.CacheMinutes, 1, 24 * 60));
        _cache[source.Id] = (DateTimeOffset.UtcNow.Add(ttl), baseUrl);
        _logger.LogInformation(
            "Torrent: dominio de {Name} resuelto a {BaseUrl}",
            source.Name,
            baseUrl);
        return baseUrl;
    }

    /// <inheritdoc />
    public void Dispose() => _discord.Dispose();

    private async Task<string?> ResolveFromPageAsync(
        TorrentSourceConfig source,
        CancellationToken cancellationToken)
    {
        var url = source.Domain.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            url = source.BaseUrl;
        }

        var fetch = await _fetcher.GetAsync(url, source, cancellationToken).ConfigureAwait(false);
        if (fetch.StatusCode >= 400)
        {
            return null;
        }

        var match = BuildRegex(source).Match(fetch.Content);
        return match.Success ? ExtractHost(match.Value) : null;
    }

    private async Task<string?> ResolveFromDiscordAsync(
        TorrentSourceConfig source,
        CancellationToken cancellationToken)
    {
        var config = source.Domain;
        if (string.IsNullOrWhiteSpace(config.DiscordBotToken)
            || string.IsNullOrWhiteSpace(config.DiscordChannelId))
        {
            return null;
        }

        var url = $"https://discord.com/api/v10/channels/{config.DiscordChannelId.Trim()}/messages?limit=50";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", config.DiscordBotToken.Trim());
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await _discord.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Torrent: Discord respondió {Status} para la fuente {Name}",
                (int)response.StatusCode,
                source.Name);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var regex = BuildRegex(source);
        foreach (var message in document.RootElement.EnumerateArray())
        {
            var candidates = new List<string?>
            {
                message.TryGetProperty("content", out var content) ? content.GetString() : null
            };

            if (message.TryGetProperty("embeds", out var embeds)
                && embeds.ValueKind == JsonValueKind.Array)
            {
                foreach (var embed in embeds.EnumerateArray())
                {
                    foreach (var field in new[] { "title", "description", "url" })
                    {
                        if (embed.TryGetProperty(field, out var value)
                            && value.ValueKind == JsonValueKind.String)
                        {
                            candidates.Add(value.GetString());
                        }
                    }
                }
            }

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var match = regex.Match(candidate);
                if (match.Success)
                {
                    var host = ExtractHost(match.Value);
                    if (!string.IsNullOrWhiteSpace(host))
                    {
                        return host;
                    }
                }
            }
        }

        return null;
    }

    private static Regex BuildRegex(TorrentSourceConfig source)
    {
        var pattern = source.Domain.Regex;
        if (!string.IsNullOrWhiteSpace(pattern))
        {
            try
            {
                return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException)
            {
                // Patrón inválido: se usa el heurístico por defecto.
            }
        }

        var label = SecondLevelLabel(source.BaseUrl);
        return string.IsNullOrWhiteSpace(label)
            ? new Regex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            : new Regex(
                @"https?://(?:www\.)?" + Regex.Escape(label) + @"\.[a-z0-9-]+(?:\.[a-z]{2,})?",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? SecondLevelLabel(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var parts = uri.Host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^2] : parts.FirstOrDefault();
    }

    private static string? ExtractHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = TextUtil.Clean(value).TrimEnd('.', ',', ')', ']', ';');
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "https://" + candidate;
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : null;
    }

    private static string CombineBaseUrl(string baseUrl, string host)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var original))
        {
            return "https://" + host;
        }

        var builder = new UriBuilder(original) { Host = host };
        return builder.Uri.ToString().TrimEnd('/');
    }
}
