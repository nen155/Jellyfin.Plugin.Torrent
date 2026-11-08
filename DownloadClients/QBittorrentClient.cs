using System.Net;
using System.Text;
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
    public void Dispose() => _http.Dispose();

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
