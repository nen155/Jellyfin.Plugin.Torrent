using System.Collections.Concurrent;
using Jellyfin.Plugin.Torrent.Models;
using Jellyfin.Plugin.Torrent.Parsing;
using Jellyfin.Plugin.Torrent.Scraping;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Services;

/// <summary>
/// Descarga y analiza archivos .torrent: info-hash, tamaño real, trackers y
/// lista de archivos que contiene.
/// </summary>
public sealed class TorrentFileService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private readonly HttpFetcher _fetcher;
    private readonly ILogger<TorrentFileService> _logger;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, TorrentFileInfo Info)> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TorrentFileService"/> class.
    /// </summary>
    /// <param name="fetcher">Cliente HTTP.</param>
    /// <param name="logger">Logger.</param>
    public TorrentFileService(HttpFetcher fetcher, ILogger<TorrentFileService> logger)
    {
        _fetcher = fetcher;
        _logger = logger;
    }

    /// <summary>
    /// Descarga y analiza un .torrent.
    /// </summary>
    /// <param name="url">URL del .torrent.</param>
    /// <param name="referer">Referer a enviar.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Información o null si no se pudo leer.</returns>
    public async Task<TorrentFileInfo?> GetInfoAsync(
        string url,
        string? referer,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(url, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
        {
            return cached.Info;
        }

        try
        {
            var bytes = await _fetcher.GetBytesAsync(url, referer, cancellationToken).ConfigureAwait(false);
            if (!TorrentParser.LooksLikeTorrent(bytes))
            {
                _logger.LogWarning("Torrent: {Url} no devolvió un .torrent válido", url);
                return null;
            }

            var info = TorrentParser.Parse(bytes);
            info.Files = info.Files.OrderByDescending(f => f.Size).ToList();
            _cache[url] = (DateTimeOffset.UtcNow.Add(Ttl), info);
            return info;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Torrent: no se pudo analizar el .torrent {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Descarga el .torrent y devuelve sus bytes (para reenviarlo a un cliente
    /// que no acepta URLs).
    /// </summary>
    /// <param name="url">URL del .torrent.</param>
    /// <param name="referer">Referer a enviar.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Bytes o null.</returns>
    public Task<byte[]?> GetBytesAsync(
        string url,
        string? referer,
        CancellationToken cancellationToken) =>
        GetBytesAsync(url, referer, null, cancellationToken);

    /// <summary>
    /// Descarga el .torrent con la cookie de la sesión del navegador.
    /// </summary>
    /// <param name="url">URL del .torrent.</param>
    /// <param name="referer">Referer a enviar.</param>
    /// <param name="cookie">Cookie de sesión.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Bytes o null.</returns>
    public async Task<byte[]?> GetBytesAsync(
        string url,
        string? referer,
        string? cookie,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _fetcher.GetBytesAsync(url, referer, cookie, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Torrent: no se pudo descargar el .torrent {Url}", url);
            return null;
        }
    }
}
