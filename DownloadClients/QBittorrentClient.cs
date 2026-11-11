using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Torrent.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.DownloadClients;

/// <summary>
/// Cliente para qBittorrent mediante su WebUI (API v2).
///
/// Detalles que rompen la integración si no se cuidan:
/// <list type="bullet">
/// <item>La cookie <c>SID</c> se marca <c>Secure</c> cuando el WebUI tiene
/// activada la opción "cookie segura" y se accede por HTTP: un
/// <see cref="CookieContainer"/> no la devuelve. Aquí se guarda y se envía a mano.</item>
/// <item>La protección CSRF exige <c>Referer</c> u <c>Origin</c> en los POST.</item>
/// <item>La validación de cabecera <c>Host</c> obliga a usar el mismo host con el
/// que se abrió la sesión.</item>
/// </list>
/// </summary>
public sealed class QBittorrentClient : IDownloadClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<QBittorrentClient> _logger;
    private string? _sid;
    private string _sessionBase = string.Empty;

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
    public async Task<ProbeResult> ProbeAsync(DownloadClientConfig config, CancellationToken cancellationToken)
    {
        var baseUrl = config.BuildBaseUrl();
        try
        {
            var login = await LoginAsync(baseUrl, config, cancellationToken).ConfigureAwait(false);
            if (!login.Available)
            {
                return login;
            }

            using var request = CreateRequest(HttpMethod.Get, baseUrl, "/api/v2/app/version", baseUrl);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                return new ProbeResult
                {
                    Available = false,
                    Message = "El WebUI responde pero rechaza la sesión (revisa usuario/contraseña y "
                        + "«Bypass authentication for clients on localhost»)."
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ProbeResult
                {
                    Available = false,
                    Message = $"El WebUI respondió HTTP {(int)response.StatusCode}."
                };
            }

            var version = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            return new ProbeResult
            {
                Available = true,
                Message = string.IsNullOrEmpty(version) ? "qBittorrent conectado." : "qBittorrent " + version + "."
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Torrent: qBittorrent no responde en {BaseUrl}", baseUrl);
            return new ProbeResult
            {
                Available = false,
                Message = $"No hay conexión con {baseUrl}. Comprueba que el WebUI esté activado y el puerto libre."
            };
        }
        catch (Exception ex)
        {
            return new ProbeResult { Available = false, Message = ex.Message };
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

        var login = await LoginAsync(baseUrl, config, cancellationToken).ConfigureAwait(false);
        if (!login.Available)
        {
            result.Message = login.Message;
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
            var url = request.Magnet ?? request.TorrentUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                result.Message = "No hay enlace que enviar.";
                return result;
            }

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

        var paused = config.StartPaused ? "true" : "false";
        form.Add(new StringContent(paused), "paused");
        form.Add(new StringContent(paused), "stopped");

        using var httpRequest = CreateRequest(HttpMethod.Post, baseUrl, "/api/v2/torrents/add", baseUrl);
        httpRequest.Content = form;

        using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            result.Message = "qBittorrent rechazó la petición (403). Suele ser la protección CSRF: "
                + "desactiva «Enable Host header validation» o permite el host en el WebUI.";
            return result;
        }

        if (response.StatusCode == HttpStatusCode.UnsupportedMediaType)
        {
            result.Message = "qBittorrent no aceptó el archivo .torrent (formato inválido).";
            return result;
        }

        result.Success = response.IsSuccessStatusCode
            && !body.Contains("Fails", StringComparison.OrdinalIgnoreCase);
        result.Message = result.Success
            ? "Añadido a qBittorrent."
            : $"qBittorrent rechazó el enlace ({(int)response.StatusCode}): {body.Trim()}";

        if (!result.Success)
        {
            _logger.LogWarning(
                "Torrent: qBittorrent respondió {Status} {Body}",
                (int)response.StatusCode,
                body);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<TorrentStatus?> GetStatusAsync(
        DownloadClientConfig config,
        string infoHash,
        CancellationToken cancellationToken)
    {
        var baseUrl = config.BuildBaseUrl();
        var login = await LoginAsync(baseUrl, config, cancellationToken).ConfigureAwait(false);
        if (!login.Available)
        {
            return null;
        }

        using var request = CreateRequest(
            HttpMethod.Get,
            baseUrl,
            "/api/v2/torrents/info?hashes=" + Uri.EscapeDataString(infoHash.ToLowerInvariant()),
            baseUrl);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var state = GetString(item, "state") ?? string.Empty;
            var progress = GetDouble(item, "progress");
            var eta = GetLong(item, "eta");
            var complete = progress >= 0.999 || IsCompleteState(state);
            var size = GetLong(item, "size");

            // "completed" son los bytes verificados (coincide con el progreso);
            // "downloaded" no cuenta lo que ya estaba en disco al añadirlo.
            var verified = GetLong(item, "completed");

            return new TorrentStatus
            {
                Client = DisplayName,
                InfoHash = GetString(item, "hash") ?? infoHash.ToLowerInvariant(),
                Name = GetString(item, "name"),
                Progress = Math.Clamp(progress, 0, 1),
                State = TranslateState(state),
                IsComplete = complete,
                SizeBytes = size,
                DownloadedBytes = verified > 0 ? verified : (long)(size * Math.Clamp(progress, 0, 1)),
                DownloadSpeed = complete ? 0 : GetLong(item, "dlspeed"),
                EtaSeconds = !complete && eta is > 0 and < 8640000 ? eta : null,
                Seeds = (int)GetLong(item, "num_seeds"),
                Leeches = (int)GetLong(item, "num_leechs")
            };
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<List<ClientTorrent>> GetTorrentsAsync(
        DownloadClientConfig config,
        CancellationToken cancellationToken)
    {
        var torrents = new List<ClientTorrent>();
        var baseUrl = config.BuildBaseUrl();

        var login = await LoginAsync(baseUrl, config, cancellationToken).ConfigureAwait(false);
        if (!login.Available)
        {
            return torrents;
        }

        using var request = CreateRequest(HttpMethod.Get, baseUrl, "/api/v2/torrents/info", baseUrl);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return torrents;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return torrents;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var state = item.TryGetProperty("state", out var stateElement)
                ? stateElement.GetString() ?? string.Empty
                : string.Empty;
            var progress = item.TryGetProperty("progress", out var progressElement)
                && progressElement.TryGetDouble(out var progressValue)
                    ? progressValue
                    : 0;

            var completed = progress >= 0.999
                || state.EndsWith("UP", StringComparison.OrdinalIgnoreCase);

            var completionOn = item.TryGetProperty("completion_on", out var completionElement)
                && completionElement.TryGetInt64(out var completionValue)
                && completionValue > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(completionValue)
                    : (DateTimeOffset?)null;

            torrents.Add(new ClientTorrent
            {
                Hash = item.TryGetProperty("hash", out var hashElement) ? hashElement.GetString() ?? string.Empty : string.Empty,
                Name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty,
                State = state,
                Progress = progress,
                Size = item.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var sizeValue) ? sizeValue : 0,
                SavePath = item.TryGetProperty("save_path", out var saveElement) ? saveElement.GetString() : null,
                ContentPath = item.TryGetProperty("content_path", out var contentElement) ? contentElement.GetString() : null,
                Category = item.TryGetProperty("category", out var categoryElement) ? categoryElement.GetString() : null,
                Ratio = item.TryGetProperty("ratio", out var ratioElement) && ratioElement.TryGetDouble(out var ratioValue) ? ratioValue : 0,
                SeedingSeconds = item.TryGetProperty("seeding_time", out var seedingElement) && seedingElement.TryGetInt64(out var seedingValue) ? seedingValue : 0,
                Completed = completed,
                CompletedOn = completionOn
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
        var baseUrl = config.BuildBaseUrl();

        var login = await LoginAsync(baseUrl, config, cancellationToken).ConfigureAwait(false);
        if (!login.Available)
        {
            return false;
        }

        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("hashes", hash),
            new KeyValuePair<string, string>("deleteFiles", deleteFiles ? "true" : "false")
        });

        using var request = CreateRequest(HttpMethod.Post, baseUrl, "/api/v2/torrents/delete", baseUrl);
        request.Content = content;

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private static string TranslateState(string state) => state switch
    {
        "downloading" or "forcedDL" or "metaDL" => "Descargando",
        "stalledDL" => "Sin fuentes",
        "queuedDL" or "queuedForChecking" => "En cola",
        "pausedDL" or "stoppedDL" => "En pausa",
        "checkingDL" or "checkingUP" or "checkingResumeData" => "Comprobando",
        "uploading" or "forcedUP" or "stalledUP" => "Completado",
        "pausedUP" or "stoppedUP" => "Completado (en pausa)",
        "queuedUP" => "Completado (en cola)",
        "error" or "missingFiles" => "Error",
        "moving" => "Moviendo",
        _ => string.IsNullOrEmpty(state) ? "Desconocido" : state
    };

    private static bool IsCompleteState(string state) =>
        state is "uploading" or "forcedUP" or "stalledUP" or "pausedUP" or "stoppedUP"
            or "queuedUP" or "moving";

    private static string? GetString(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double GetDouble(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    private static long GetLong(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    private async Task<ProbeResult> LoginAsync(
        string baseUrl,
        DownloadClientConfig config,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_sid) && _sessionBase == baseUrl)
        {
            return new ProbeResult { Available = true, Message = "Sesión reutilizada." };
        }

        _sid = null;
        _sessionBase = baseUrl;

        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", config.Username ?? string.Empty),
            new KeyValuePair<string, string>("password", config.Password ?? string.Empty)
        });

        using var request = CreateRequest(HttpMethod.Post, baseUrl, "/api/v2/auth/login", baseUrl);
        request.Content = content;

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return new ProbeResult
            {
                Available = false,
                Message = "qBittorrent bloqueó el intento (403). Revisa «Enable Host header validation» "
                    + "y la lista de IPs/hosts permitidos en el WebUI."
            };
        }

        if (!response.IsSuccessStatusCode)
        {
            return new ProbeResult
            {
                Available = false,
                Message = $"El WebUI respondió HTTP {(int)response.StatusCode} al iniciar sesión."
            };
        }

        if (body.Contains("Fails", StringComparison.OrdinalIgnoreCase))
        {
            return new ProbeResult
            {
                Available = false,
                Message = "Usuario o contraseña incorrectos (o el WebUI exige autenticación local: "
                    + "pon las credenciales en el plugin o activa «Bypass authentication for clients on localhost»)."
            };
        }

        _sid = ExtractSessionCookie(response);
        return new ProbeResult { Available = true, Message = "Sesión iniciada." };
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string baseUrl,
        string path,
        string originUrl)
    {
        var request = new HttpRequestMessage(method, baseUrl + path);
        request.Headers.Referrer = new Uri(originUrl.TrimEnd('/') + "/");
        request.Headers.TryAddWithoutValidation("Origin", originUrl.TrimEnd('/'));

        if (!string.IsNullOrEmpty(_sid))
        {
            request.Headers.TryAddWithoutValidation("Cookie", "SID=" + _sid);
        }

        return request;
    }

    /// <summary>
    /// Extrae la cookie de sesión aunque venga marcada como <c>Secure</c> (el
    /// CookieContainer no la reenviaría por HTTP).
    /// </summary>
    private static string? ExtractSessionCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        foreach (var header in values)
        {
            var match = Regex.Match(header, @"(?:^|;\s*)SID=(?<sid>[^;]+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups["sid"].Value.Trim();
            }
        }

        return null;
    }
}
