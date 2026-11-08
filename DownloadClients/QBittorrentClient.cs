using System.Net;
using Jellyfin.Plugin.Torrent.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.DownloadClients;

/// <summary>
/// Cliente para qBittorrent mediante su WebUI (API v2).
/// </summary>
public sealed class QBittorrentClient : IDownloadClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<QBittorrentClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="QBittorrentClient"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public QBittorrentClient(ILogger<QBittorrentClient> logger)
    {
        _logger = logger;
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <inheritdoc />
    public DownloadClientKind Kind => DownloadClientKind.QBittorrent;

    /// <inheritdoc />
    public string DisplayName => "qBittorrent";

    /// <inheritdoc />
    public async Task<bool> ProbeAsync(DownloadClientConfig config, CancellationToken cancellationToken)
    {
        var baseUrl = config.BuildBaseUrl();
        try
        {
            if (!await LoginAsync(baseUrl, config, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            using var response = await _http
                .GetAsync($"{baseUrl}/api/v2/app/version", cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Torrent: qBittorrent no responde en {BaseUrl}", baseUrl);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<DownloadResult> AddAsync(
        DownloadClientConfig config,
        DownloadRequest request,
        CancellationToken cancellationToken)
    {
        var baseUrl = config.BuildBaseUrl();
        var result = new DownloadResult { Client = DisplayName };

        if (!await LoginAsync(baseUrl, config, cancellationToken).ConfigureAwait(false))
        {
            result.Message = "No se pudo iniciar sesión en qBittorrent (revisa host, puerto y credenciales).";
            return result;
        }

        var url = request.Magnet ?? request.TorrentUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            result.Message = "No hay enlace que enviar.";
            return result;
        }

        using var form = new MultipartFormDataContent();

        if (request.TorrentBytes is { Length: > 0 })
        {
            var file = new ByteArrayContent(request.TorrentBytes);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-bittorrent");
            form.Add(file, "torrents", request.FileName ?? "torrent.torrent");
        }
        else
        {
            form.Add(new StringContent(url), "urls");
        }

        if (!string.IsNullOrWhiteSpace(config.SavePath))
        {
            form.Add(new StringContent(config.SavePath), "savepath");
            form.Add(new StringContent("false"), "autoTMM");
        }

        if (!string.IsNullOrWhiteSpace(config.Category))
        {
            form.Add(new StringContent(config.Category), "category");
        }

        if (!string.IsNullOrWhiteSpace(config.Tags))
        {
            form.Add(new StringContent(config.Tags), "tags");
        }

        form.Add(new StringContent(config.StartPaused ? "true" : "false"), "paused");

        using var response = await _http
            .PostAsync($"{baseUrl}/api/v2/torrents/add", form, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        result.Success = response.IsSuccessStatusCode
            && !body.Contains("Fails", StringComparison.OrdinalIgnoreCase);
        result.Message = result.Success
            ? "Añadido a qBittorrent."
            : $"qBittorrent rechazó el enlace ({(int)response.StatusCode}): {body.Trim()}";

        if (!result.Success)
        {
            _logger.LogWarning("Torrent: qBittorrent respondió {Status} {Body}", (int)response.StatusCode, body);
        }

        return result;
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private async Task<bool> LoginAsync(
        string baseUrl,
        DownloadClientConfig config,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", config.Username ?? string.Empty),
            new KeyValuePair<string, string>("password", config.Password ?? string.Empty)
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v2/auth/login")
        {
            Content = content
        };
        request.Headers.Referrer = new Uri(baseUrl);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode && body.Contains("Ok", StringComparison.OrdinalIgnoreCase);
    }
}
