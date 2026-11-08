using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Torrent.Models;
using Jellyfin.Plugin.Torrent.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.DownloadClients;

/// <summary>
/// Cliente para Deluge mediante su interfaz web (JSON-RPC).
/// </summary>
public sealed class DelugeClient : IDownloadClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly TorrentFileService _torrentFiles;
    private readonly ILogger<DelugeClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DelugeClient"/> class.
    /// </summary>
    /// <param name="torrentFiles">Servicio de archivos .torrent.</param>
    /// <param name="logger">Logger.</param>
    public DelugeClient(TorrentFileService torrentFiles, ILogger<DelugeClient> logger)
    {
        _torrentFiles = torrentFiles;
        _logger = logger;
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
    }

    /// <inheritdoc />
    public DownloadClientKind Kind => DownloadClientKind.Deluge;

    /// <inheritdoc />
    public string DisplayName => "Deluge";

    /// <inheritdoc />
    public async Task<bool> ProbeAsync(DownloadClientConfig config, CancellationToken cancellationToken)
    {
        try
        {
            return await LoginAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Torrent: Deluge no responde");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<DownloadResult> AddAsync(
        DownloadClientConfig config,
        DownloadRequest request,
        CancellationToken cancellationToken)
    {
        var result = new DownloadResult { Client = DisplayName };
        if (!await LoginAsync(config, cancellationToken).ConfigureAwait(false))
        {
            result.Message = "No se pudo iniciar sesión en Deluge (revisa host, puerto y contraseña).";
            return result;
        }

        var options = new Dictionary<string, object>
        {
            ["add_paused"] = config.StartPaused
        };

        if (!string.IsNullOrWhiteSpace(config.SavePath))
        {
            options["download_location"] = config.SavePath;
        }

        if (!string.IsNullOrWhiteSpace(request.Magnet))
        {
            var (ok, body) = await CallAsync(
                    config,
                    "core.add_torrent_magnet",
                    new object[] { request.Magnet, options },
                    cancellationToken)
                .ConfigureAwait(false);
            result.Success = ok;
            result.Message = ok ? "Añadido a Deluge." : $"Deluge rechazó el magnet: {body}";
            return result;
        }

        if (string.IsNullOrWhiteSpace(request.TorrentUrl))
        {
            result.Message = "No hay enlace que enviar.";
            return result;
        }

        var bytes = request.TorrentBytes
            ?? await _torrentFiles
                .GetBytesAsync(request.TorrentUrl, request.Referer, request.Cookie, cancellationToken)
                .ConfigureAwait(false);
        if (bytes is null)
        {
            result.Message = "No se pudo descargar el .torrent para enviarlo a Deluge.";
            return result;
        }

        var (uploaded, uploadBody) = await CallAsync(
                config,
                "core.add_torrent_file",
                new object[] { request.Title ?? "torrent.torrent", Convert.ToBase64String(bytes), options },
                cancellationToken)
            .ConfigureAwait(false);

        result.Success = uploaded;
        result.Message = uploaded ? "Añadido a Deluge." : $"Deluge rechazó el .torrent: {uploadBody}";
        return result;
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private async Task<bool> LoginAsync(DownloadClientConfig config, CancellationToken cancellationToken)
    {
        var (ok, _) = await CallAsync(
                config,
                "auth.login",
                new object[] { config.Password ?? string.Empty },
                cancellationToken)
            .ConfigureAwait(false);
        return ok;
    }

    private async Task<(bool Success, string Body)> CallAsync(
        DownloadClientConfig config,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var url = BuildJsonUrl(config);
        var payload = JsonSerializer.Serialize(new
        {
            method,
            @params = parameters,
            id = Random.Shared.Next(1, 100000)
        });

        try
        {
            using var response = await _http
                .PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"), cancellationToken)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"HTTP {(int)response.StatusCode}");
            }

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind != JsonValueKind.Null)
            {
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : error.GetRawText();
                return (false, message ?? body);
            }

            if (!document.RootElement.TryGetProperty("result", out var resultElement))
            {
                return (false, body);
            }

            var success = resultElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.String => !string.IsNullOrWhiteSpace(resultElement.GetString()),
                JsonValueKind.Object => true,
                _ => false
            };

            return (success, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Torrent: Deluge no responde en {Url}", url);
            return (false, ex.Message);
        }
    }

    private static string BuildJsonUrl(DownloadClientConfig config)
    {
        var baseUrl = config.BuildBaseUrl();
        return baseUrl.EndsWith("/json", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + "/json";
    }
}
