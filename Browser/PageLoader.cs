using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Jellyfin.Plugin.Torrent.Models;
using Jellyfin.Plugin.Torrent.Scraping;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Browser;

/// <summary>
/// Resultado de cargar una página (con HTTP, con navegador o con FlareSolverr).
/// </summary>
public sealed class PageLoadResult
{
    /// <summary>Gets or sets el HTML.</summary>
    public string Html { get; set; } = string.Empty;

    /// <summary>Gets or sets la URL final.</summary>
    public string FinalUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets el código de estado HTTP.</summary>
    public int StatusCode { get; set; } = 200;

    /// <summary>Gets or sets las cookies de la sesión.</summary>
    public string? CookieHeader { get; set; }

    /// <summary>Gets or sets las URLs observadas en la red.</summary>
    public List<string> CapturedUrls { get; set; } = new();

    /// <summary>Gets or sets el motor usado: http, cdp o flaresolverr.</summary>
    public string Engine { get; set; } = "http";

    /// <summary>Gets or sets el error, si no se pudo cargar.</summary>
    public string? Error { get; set; }

    /// <summary>Gets a value indicating whether la carga fue correcta.</summary>
    public bool Ok => Error is null;
}

/// <summary>
/// Carga páginas eligiendo el motor: HTTP simple, navegador real (CDP) o
/// FlareSolverr. El navegador ejecuta el JavaScript de la web, así que sirve
/// para buscadores dinámicos, retos anti-bot y botones de descarga protegidos.
/// </summary>
public sealed class PageLoader : IAsyncDisposable
{
    private static readonly string[] BlockedMarkers =
    [
        "just a moment",
        "cf-chl",
        "challenge-platform",
        "checking your browser",
        "attention required! | cloudflare",
        "enable javascript and cookies to continue",
        "please enable javascript",
        "ddos-guard",
        "cf-browser-verification",
        "verifying you are human"
    ];

    private readonly HttpFetcher _http;
    private readonly FlareSolverrClient _flare;
    private readonly ILogger<PageLoader> _logger;
    private readonly ILogger<ChromeBrowser> _browserLogger;
    private readonly SemaphoreSlim _browserGate = new(1, 1);

    private ChromeBrowser? _browser;
    private string _browserSignature = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="PageLoader"/> class.
    /// </summary>
    /// <param name="http">Cliente HTTP.</param>
    /// <param name="flare">Cliente de FlareSolverr.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="browserLogger">Logger del navegador.</param>
    public PageLoader(
        HttpFetcher http,
        FlareSolverrClient flare,
        ILogger<PageLoader> logger,
        ILogger<ChromeBrowser> browserLogger)
    {
        _http = http;
        _flare = flare;
        _logger = logger;
        _browserLogger = browserLogger;
    }

    /// <summary>
    /// Gets a value indicating whether hay un navegador utilizable.
    /// </summary>
    public bool BrowserAvailable
    {
        get
        {
            var config = CurrentBrowserConfig();
            return config.Mode != BrowserMode.None
                && (ChromeBrowser.IsAvailable(config.ExecutablePath)
                    || (!string.IsNullOrWhiteSpace(config.FlareSolverrUrl)
                        && config.Mode is BrowserMode.FlareSolverr or BrowserMode.Auto));
        }
    }

    /// <summary>
    /// Carga una página aplicando el modo configurado en la fuente.
    /// </summary>
    /// <param name="source">Fuente.</param>
    /// <param name="url">URL.</param>
    /// <param name="postBody">Cuerpo del POST (null para GET).</param>
    /// <param name="query">Texto buscado (para el script del navegador).</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <param name="detailPage">True si es una ficha de detalle.</param>
    /// <param name="modeOverride">Fuerza un motor concreto (opcional).</param>
    /// <param name="waitForText">Texto que debe aparecer (solo navegador).</param>
    /// <param name="waitForStableDom">Espera a que el DOM deje de cambiar.</param>
    /// <returns>Resultado de la carga.</returns>
    public async Task<PageLoadResult> LoadAsync(
        TorrentSourceConfig source,
        string url,
        string? postBody,
        string? query,
        CancellationToken cancellationToken,
        bool detailPage = false,
        FetchMode? modeOverride = null,
        string? waitForText = null,
        bool waitForStableDom = false)
    {
        var mode = modeOverride ?? ResolveMode(source, detailPage);
        var script = detailPage ? source.DetailBrowserScript : source.BrowserScript;
        var waitSelector = detailPage ? source.DetailWaitSelector : source.BrowserWaitSelector;
        var waitMs = detailPage ? source.DetailBrowserWaitMs : source.BrowserWaitMs;
        script = ApplyQuery(script, query);

        PageLoadResult? httpResult = null;

        if (mode is FetchMode.Http or FetchMode.Auto)
        {
            httpResult = await TryHttpAsync(source, url, postBody, cancellationToken).ConfigureAwait(false);
            if (mode == FetchMode.Http
                || (httpResult.Ok && !LooksBlocked(httpResult.StatusCode, httpResult.Html)))
            {
                return httpResult;
            }
        }

        var browserConfig = CurrentBrowserConfig();
        if (browserConfig.Mode == BrowserMode.None)
        {
            return httpResult ?? new PageLoadResult
            {
                FinalUrl = url,
                Error = "La fuente pide navegador pero está desactivado en la configuración del plugin."
            };
        }

        var browserResult = await TryBrowserAsync(
                browserConfig, url, postBody, script, waitSelector, waitMs, waitForText, waitForStableDom, cancellationToken)
            .ConfigureAwait(false);

        if (browserResult is not null)
        {
            return browserResult;
        }

        if (httpResult is not null)
        {
            if (httpResult.Ok && LooksBlocked(httpResult.StatusCode, httpResult.Html))
            {
                httpResult.Error = "La web bloqueó la petición (protección anti-bot) y el navegador no pudo resolverla.";
            }

            return httpResult;
        }

        return new PageLoadResult
        {
            FinalUrl = url,
            Error = "No se pudo cargar la página con ningún motor."
        };
    }

    /// <summary>
    /// Parsea HTML en un documento navegable.
    /// </summary>
    /// <param name="html">HTML.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Documento.</returns>
    public Task<IHtmlDocument> ParseHtmlAsync(string html, CancellationToken cancellationToken) =>
        _http.ParseHtmlAsync(html, cancellationToken);

    /// <summary>
    /// Descarga bytes (imágenes, .torrent) con referer y cookies opcionales.
    /// </summary>
    /// <param name="url">URL.</param>
    /// <param name="referer">Referer.</param>
    /// <param name="cookie">Cookie de sesión.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Bytes.</returns>
    public Task<byte[]> GetBytesAsync(
        string url,
        string? referer,
        string? cookie,
        CancellationToken cancellationToken) =>
        _http.GetBytesAsync(url, referer, cookie, cancellationToken);

    /// <summary>
    /// Petición GET simple con las cabeceras de la fuente.
    /// </summary>
    /// <param name="url">URL.</param>
    /// <param name="source">Fuente.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Respuesta.</returns>
    public Task<FetchResult> GetAsync(
        string url,
        TorrentSourceConfig source,
        CancellationToken cancellationToken) =>
        _http.GetAsync(url, source, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync().ConfigureAwait(false);
            _browser = null;
        }

        _browserGate.Dispose();
    }

    private static FetchMode ResolveMode(TorrentSourceConfig source, bool detailPage)
    {
        if (!detailPage)
        {
            return source.FetchMode;
        }

        return source.DetailFetchMode == FetchMode.Inherit ? source.FetchMode : source.DetailFetchMode;
    }

    private static string? ApplyQuery(string? script, string? query)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return script;
        }

        var escaped = (query ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

        return script.Replace("{query}", escaped, StringComparison.Ordinal);
    }

    private static bool LooksBlocked(int statusCode, string? html)
    {
        if (statusCode is 401 or 403 or 429 or 503)
        {
            return true;
        }

        var text = html ?? string.Empty;
        if (text.Length < 600)
        {
            return true;
        }

        foreach (var marker in BlockedMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<PageLoadResult> TryHttpAsync(
        TorrentSourceConfig source,
        string url,
        string? postBody,
        CancellationToken cancellationToken)
    {
        try
        {
            var fetch = postBody is null
                ? await _http.GetAsync(url, source, cancellationToken).ConfigureAwait(false)
                : await _http.PostAsync(url, postBody, source, cancellationToken).ConfigureAwait(false);

            return new PageLoadResult
            {
                Html = fetch.Content,
                FinalUrl = fetch.FinalUrl,
                StatusCode = fetch.StatusCode,
                CookieHeader = string.IsNullOrWhiteSpace(source.Cookie) ? null : source.Cookie,
                Engine = "http"
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PageLoadResult
            {
                FinalUrl = url,
                StatusCode = 0,
                Engine = "http",
                Error = "No se pudo consultar la web: " + ex.Message
            };
        }
    }

    private async Task<PageLoadResult?> TryBrowserAsync(
        BrowserConfig config,
        string url,
        string? postBody,
        string? script,
        string? waitSelector,
        int waitMs,
        string? waitForText,
        bool waitForStableDom,
        CancellationToken cancellationToken)
    {
        var canUseCdp = config.Mode is BrowserMode.Cdp or BrowserMode.Auto
            && ChromeBrowser.IsAvailable(config.ExecutablePath)
            && postBody is null;

        if (canUseCdp)
        {
            try
            {
                var browser = await GetBrowserAsync(config, cancellationToken).ConfigureAwait(false);
                var page = await browser.FetchAsync(
                        url,
                        new BrowserFetchOptions
                        {
                            Script = script,
                            WaitSelector = waitSelector,
                            WaitForText = waitForText,
                            WaitForStableDom = waitForStableDom,
                            WaitMs = Math.Clamp(waitMs, 0, 30000),
                            TimeoutSeconds = Math.Clamp(config.TimeoutSeconds, 5, 180)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return new PageLoadResult
                {
                    Html = page.Html,
                    FinalUrl = page.FinalUrl,
                    StatusCode = 200,
                    CookieHeader = page.CookieHeader,
                    CapturedUrls = page.CapturedUrls,
                    Engine = "cdp"
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Torrent: el navegador falló al abrir {Url}", url);
            }
        }

        var canUseFlare = !string.IsNullOrWhiteSpace(config.FlareSolverrUrl)
            && config.Mode is BrowserMode.FlareSolverr or BrowserMode.Auto;

        if (canUseFlare)
        {
            var solved = await _flare.SolveAsync(
                    config.FlareSolverrUrl,
                    url,
                    postBody,
                    Math.Clamp(config.TimeoutSeconds, 5, 180) * 1000,
                    cancellationToken)
                .ConfigureAwait(false);

            if (solved is not null)
            {
                return new PageLoadResult
                {
                    Html = solved.Html,
                    FinalUrl = solved.FinalUrl,
                    StatusCode = 200,
                    CookieHeader = solved.CookieHeader,
                    Engine = "flaresolverr"
                };
            }
        }

        return null;
    }

    private async Task<ChromeBrowser> GetBrowserAsync(
        BrowserConfig config,
        CancellationToken cancellationToken)
    {
        var signature = string.Join(
            '|',
            config.ExecutablePath,
            config.Headless,
            config.IdleSeconds,
            config.UserAgent);

        await _browserGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_browser is not null && _browserSignature == signature)
            {
                return _browser;
            }

            if (_browser is not null)
            {
                await _browser.DisposeAsync().ConfigureAwait(false);
            }

            _browser = new ChromeBrowser(config, _browserLogger);
            _browserSignature = signature;
            return _browser;
        }
        finally
        {
            _browserGate.Release();
        }
    }

    private static BrowserConfig CurrentBrowserConfig() =>
        Plugin.Instance?.Configuration.Browser ?? new BrowserConfig();
}
