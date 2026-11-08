using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Torrent.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.DownloadClients;

/// <summary>
/// Cliente para Transmission mediante su RPC (con manejo del session-id).
/// </summary>
public sealed class TransmissionClient : IDownloadClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<TransmissionClient> _logger;
    private string? _sessionId;

    /// <summary>
    /// Initializes a new instance of the <see cref="TransmissionClient"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public TransmissionClient(ILogger<TransmissionClient> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <inheritdoc />
    public DownloadClientKind Kind => DownloadClientKind.Transmission;

    /// <inheritdoc />
    public string DisplayName => "Transmission";

    /// <inheritdoc />
    public async Task<ProbeResult> ProbeAsync(DownloadClientConfig config, CancellationToken cancellationToken)
    {
        var (success, body) = await CallAsync(
                config,
                new { method = "session-get" },
                cancellationToken)
            .ConfigureAwait(false);

        return new ProbeResult
        {
            Available = success,
            Message = success ? "Transmission conectado." : "Transmission no respondió: " + body
        };
    }

    /// <inheritdoc />
    public async Task<DownloadResult> AddAsync(
        DownloadClientConfig config,
        DownloadRequest request,
        CancellationToken cancellationToken)
    {
        var url = request.Magnet ?? request.TorrentUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return new DownloadResult { Client = DisplayName, Message = "No hay enlace que enviar." };
        }

        var arguments = new Dictionary<string, object>
        {
            ["paused"] = config.StartPaused
        };

        if (request.TorrentBytes is { Length: > 0 })
        {
            arguments["metainfo"] = Convert.ToBase64String(request.TorrentBytes);
        }
        else
        {
            arguments["filename"] = url;
        }

        if (!string.IsNullOrWhiteSpace(config.SavePath))
        {
            arguments["download-dir"] = config.SavePath;
        }

        var (success, body) = await CallAsync(
                config,
                new { method = "torrent-add", arguments },
                cancellationToken)
            .ConfigureAwait(false);

        return new DownloadResult
        {
            Client = DisplayName,
            Success = success,
            Message = success
                ? "Añadido a Transmission."
                : $"Transmission rechazó el enlace: {body}"
        };
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private async Task<(bool Success, string Body)> CallAsync(
        DownloadClientConfig config,
        object payload,
        CancellationToken cancellationToken)
    {
        var rpcUrl = BuildRpcUrl(config);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, rpcUrl)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json")
            };

            if (!string.IsNullOrEmpty(config.Username))
            {
                var credentials = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            }

            if (!string.IsNullOrEmpty(_sessionId))
            {
                request.Headers.TryAddWithoutValidation("X-Transmission-Session-Id", _sessionId);
            }

            try
            {
                using var response = await _http.SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    if (response.Headers.TryGetValues("X-Transmission-Session-Id", out var values))
                    {
                        _sessionId = values.FirstOrDefault();
                    }

                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"HTTP {(int)response.StatusCode}");
                }

                using var document = JsonDocument.Parse(body);
                var result = document.RootElement.TryGetProperty("result", out var r)
                    ? r.GetString()
                    : null;
                return (result == "success", result ?? body);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Torrent: Transmission no responde en {Url}", rpcUrl);
                return (false, ex.Message);
            }
        }

        return (false, "No se pudo obtener el session-id de Transmission.");
    }

    private static string BuildRpcUrl(DownloadClientConfig config)
    {
        var baseUrl = config.BuildBaseUrl();
        return baseUrl.Contains("/transmission/rpc", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + "/transmission/rpc";
    }
}
