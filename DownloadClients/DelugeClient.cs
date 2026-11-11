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
    public async Task<ProbeResult> ProbeAsync(DownloadClientConfig config, CancellationToken cancellationToken)
    {
        try
        {
            var ok = await LoginAsync(config, cancellationToken).ConfigureAwait(false);
            return new ProbeResult
            {
                Available = ok,
                Message = ok
                    ? "Deluge conectado."
                    : "Deluge rechazó el inicio de sesión (revisa la contraseña del Web UI)."
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Torrent: Deluge no responde");
            return new ProbeResult { Available = false, Message = "No hay conexión con Deluge: " + ex.Message };
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
    public async Task<TorrentStatus?> GetStatusAsync(
        DownloadClientConfig config,
        string infoHash,
        CancellationToken cancellationToken)
    {
        if (!await LoginAsync(config, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var keys = new[]
        {
            "name", "progress", "state", "download_payload_rate", "eta",
            "total_size", "total_done", "num_seeds", "num_peers", "is_finished"
        };

        var (ok, body) = await CallAsync(
                config,
                "core.get_torrent_status",
                new object[] { infoHash.ToLowerInvariant(), keys },
                cancellationToken)
            .ConfigureAwait(false);

        if (!ok)
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // En Deluge el progreso va de 0 a 100.
        var progress = GetDouble(result, "progress") / 100.0;
        var state = result.TryGetProperty("state", out var s) ? s.GetString() ?? string.Empty : string.Empty;
        var finished = result.TryGetProperty("is_finished", out var f) && f.ValueKind == JsonValueKind.True;
        var complete = finished || progress >= 0.999 || state.Equals("Seeding", StringComparison.OrdinalIgnoreCase);
        var eta = (long)GetDouble(result, "eta");

        return new TorrentStatus
        {
            Client = DisplayName,
            InfoHash = infoHash.ToLowerInvariant(),
            Name = result.TryGetProperty("name", out var name) ? name.GetString() : null,
            Progress = Math.Clamp(progress, 0, 1),
            State = TranslateState(state),
            IsComplete = complete,
            SizeBytes = (long)GetDouble(result, "total_size"),
            DownloadedBytes = (long)GetDouble(result, "total_done"),
            DownloadSpeed = complete ? 0 : (long)GetDouble(result, "download_payload_rate"),
            EtaSeconds = !complete && eta is > 0 and < 8640000 ? eta : null,
            Seeds = (int)GetDouble(result, "num_seeds"),
            Leeches = (int)GetDouble(result, "num_peers")
        };
    }

    /// <inheritdoc />
    public async Task<List<ClientTorrent>> GetTorrentsAsync(
        DownloadClientConfig config,
        CancellationToken cancellationToken)
    {
        var torrents = new List<ClientTorrent>();
        if (!await LoginAsync(config, cancellationToken).ConfigureAwait(false))
        {
            return torrents;
        }

        var (ok, body) = await CallAsync(
                config,
                "core.get_torrents_status",
                new object[]
                {
                    new Dictionary<string, object>(),
                    new[]
                    {
                        "hash", "name", "progress", "state", "save_path", "ratio",
                        "seeding_time", "completed_time", "total_size"
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!ok || string.IsNullOrWhiteSpace(body))
        {
            return torrents;
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object)
        {
            return torrents;
        }

        foreach (var entry in result.EnumerateObject())
        {
            var item = entry.Value;
            var progress = item.TryGetProperty("progress", out var progressElement)
                && progressElement.TryGetDouble(out var progressValue)
                    ? progressValue / 100d
                    : 0;
            var state = item.TryGetProperty("state", out var stateElement)
                ? stateElement.GetString() ?? string.Empty
                : string.Empty;
            var completedTime = item.TryGetProperty("completed_time", out var timeElement)
                && timeElement.TryGetDouble(out var timeValue)
                && timeValue > 0
                    ? DateTimeOffset.FromUnixTimeSeconds((long)timeValue)
                    : (DateTimeOffset?)null;

            torrents.Add(new ClientTorrent
            {
                Hash = entry.Name,
                Name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty,
                State = state,
                Progress = progress,
                Size = item.TryGetProperty("total_size", out var sizeElement) && sizeElement.TryGetInt64(out var sizeValue) ? sizeValue : 0,
                SavePath = item.TryGetProperty("save_path", out var saveElement) ? saveElement.GetString() : null,
                Ratio = item.TryGetProperty("ratio", out var ratioElement) && ratioElement.TryGetDouble(out var ratioValue) ? ratioValue : 0,
                SeedingSeconds = item.TryGetProperty("seeding_time", out var seedingElement) && seedingElement.TryGetInt64(out var seedingValue) ? seedingValue : 0,
                Completed = progress >= 0.999 || state.Equals("Seeding", StringComparison.OrdinalIgnoreCase),
                CompletedOn = completedTime
            });
        }

        return torrents;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(
        DownloadClientConfig config,
        string hash,
        bool deleteFiles,
        CancellationToken cancellationToken)
    {
        if (!await LoginAsync(config, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var (ok, _) = await CallAsync(
                config,
                "core.remove_torrent",
                new object[] { hash, deleteFiles },
                cancellationToken)
            .ConfigureAwait(false);

        return ok;
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private static string TranslateState(string state) => state switch
    {
        "Downloading" => "Descargando",
        "Seeding" => "Completado",
        "Paused" => "En pausa",
        "Queued" => "En cola",
        "Checking" or "Allocating" => "Comprobando",
        "Error" => "Error",
        _ => string.IsNullOrEmpty(state) ? "Desconocido" : state
    };

    private static double GetDouble(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

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
