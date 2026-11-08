using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Torrent.Scraping;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Services;

/// <summary>
/// Sirve las carátulas a través del plugin: evita el bloqueo por referer de
/// muchas webs y el contenido mixto (http) en jellyfin-web, y cachea en disco.
/// </summary>
public sealed class ImageProxyService
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private readonly HttpFetcher _fetcher;
    private readonly ILogger<ImageProxyService> _logger;
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, (string ContentType, DateTimeOffset CachedAt)> _index = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageProxyService"/> class.
    /// </summary>
    /// <param name="fetcher">Cliente HTTP.</param>
    /// <param name="applicationPaths">Rutas de la aplicación (para la caché).</param>
    /// <param name="logger">Logger.</param>
    public ImageProxyService(
        HttpFetcher fetcher,
        IApplicationPaths applicationPaths,
        ILogger<ImageProxyService> logger)
    {
        _fetcher = fetcher;
        _logger = logger;
        _cacheDir = Path.Combine(applicationPaths.DataPath, "torrent-cache", "images");
    }

    /// <summary>
    /// Obtiene una imagen (de caché o de la web).
    /// </summary>
    /// <param name="url">URL de la imagen.</param>
    /// <param name="referer">Referer a enviar a la web.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Bytes y tipo MIME, o null si no se pudo obtener.</returns>
    public async Task<(byte[] Bytes, string ContentType)?> GetAsync(
        string url,
        string? referer,
        CancellationToken cancellationToken)
    {
        if (!IsSafeUrl(url))
        {
            return null;
        }

        var path = GetCachePath(url);
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            if (DateTimeOffset.UtcNow - info.LastWriteTimeUtc < MaxAge)
            {
                try
                {
                    var cached = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                    return (cached, SniffContentType(cached));
                }
                catch (IOException)
                {
                    // Caché ilegible: se vuelve a descargar.
                }
            }
        }

        try
        {
            var bytes = await _fetcher.GetBytesAsync(url, referer, cancellationToken).ConfigureAwait(false);
            if (bytes.Length is 0 or > MaxBytes)
            {
                return null;
            }

            Directory.CreateDirectory(_cacheDir);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            _index[url] = (SniffContentType(bytes), DateTimeOffset.UtcNow);
            return (bytes, SniffContentType(bytes));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Torrent: no se pudo descargar la carátula {Url}", url);
            return null;
        }
    }

    private string GetCachePath(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(_cacheDir, hash + ".img");
    }

    private static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IPAddress.TryParse(uri.Host, out var address))
        {
            var bytes = address.GetAddressBytes();
            var isPrivate = address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || IPAddress.IsLoopback(address)
                || (bytes.Length == 4 && (bytes[0] == 10
                    || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168)
                    || (bytes[0] == 169 && bytes[1] == 254)));
            if (isPrivate)
            {
                return false;
            }
        }

        return true;
    }

    private static string SniffContentType(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 4 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
        {
            return "image/gif";
        }

        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "image/webp";
        }

        if (bytes.Length >= 12
            && bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
        {
            return "image/avif";
        }

        return "image/jpeg";
    }
}
