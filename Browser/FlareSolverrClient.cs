using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Browser;

/// <summary>
/// Cliente del servicio <c>FlareSolverr</c> (o compatible), que resuelve los
/// retos de Cloudflare/DDoS-Guard en un navegador real y devuelve el HTML.
/// </summary>
public sealed class FlareSolverrClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<FlareSolverrClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FlareSolverrClient"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public FlareSolverrClient(ILogger<FlareSolverrClient> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    }

    /// <summary>
    /// Pide al servicio que abra una URL y devuelva el HTML resuelto.
    /// </summary>
    /// <param name="serviceUrl">URL base del servicio (p. ej. <c>http://localhost:8191</c>).</param>
    /// <param name="url">URL a resolver.</param>
    /// <param name="postBody">Cuerpo del POST, si aplica.</param>
    /// <param name="timeoutMs">Tiempo máximo que concede el servicio.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Página resuelta o null.</returns>
    public async Task<BrowserPageResult?> SolveAsync(
        string serviceUrl,
        string url,
        string? postBody,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serviceUrl))
        {
            return null;
        }

        var endpoint = serviceUrl.TrimEnd('/') + "/v1";
        var payload = new Dictionary<string, object>
        {
            ["cmd"] = postBody is null ? "request.get" : "request.post",
            ["url"] = url,
            ["maxTimeout"] = Math.Clamp(timeoutMs, 5000, 180000)
        };

        if (postBody is not null)
        {
            payload["postData"] = postBody;
        }

        try
        {
            using var response = await _http
                .PostAsJsonAsync(endpoint, payload, cancellationToken)
                .ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Torrent: FlareSolverr respondió {Status} para {Url}",
                    (int)response.StatusCode,
                    url);
                return null;
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var status = root.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;

            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("solution", out var solution))
            {
                var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                _logger.LogWarning("Torrent: FlareSolverr no resolvió {Url}: {Message}", url, message);
                return null;
            }

            var html = solution.TryGetProperty("response", out var responseElement)
                ? responseElement.GetString() ?? string.Empty
                : string.Empty;
            var finalUrl = solution.TryGetProperty("url", out var urlElement)
                ? urlElement.GetString() ?? url
                : url;

            var cookieHeader = BuildCookies(solution);

            return new BrowserPageResult
            {
                Html = html,
                FinalUrl = finalUrl,
                CookieHeader = cookieHeader,
                CapturedUrls = new List<string>()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Torrent: error hablando con FlareSolverr ({Url})", endpoint);
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private static string? BuildCookies(JsonElement solution)
    {
        if (!solution.TryGetProperty("cookies", out var cookies)
            || cookies.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var cookie in cookies.EnumerateArray())
        {
            var name = cookie.TryGetProperty("name", out var n) ? n.GetString() : null;
            var value = cookie.TryGetProperty("value", out var v) ? v.GetString() : null;
            if (!string.IsNullOrEmpty(name))
            {
                parts.Add(name + "=" + value);
            }
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}
