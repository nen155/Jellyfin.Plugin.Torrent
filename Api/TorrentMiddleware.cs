using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Torrent.Browser;
using Jellyfin.Plugin.Torrent.DownloadClients;
using Jellyfin.Plugin.Torrent.Models;
using Jellyfin.Plugin.Torrent.Services;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Torrent.Api;

/// <summary>
/// API HTTP del plugin bajo <c>/Torrent</c>:
/// <list type="bullet">
/// <item><c>GET /Torrent/</c>: buscador completo con las sugerencias del día (para cualquier cliente).</item>
/// <item><c>GET /Torrent/inject.js</c>: botón y fila de sugerencias para jellyfin-web.</item>
/// <item><c>GET /Torrent/api/config</c>: fuentes y ajustes para la interfaz.</item>
/// <item><c>GET /Torrent/api/suggestions</c>: estrenos del día (películas y series).</item>
/// <item><c>POST /Torrent/api/search</c>: búsqueda agregada.</item>
/// <item><c>POST /Torrent/api/add</c>: envía un enlace al cliente de torrents.</item>
/// <item><c>GET /Torrent/api/image|torrent|files</c>: proxy de carátulas y .torrent.</item>
/// <item><c>POST /Torrent/api/source/test|domain/resolve</c>: diagnóstico (admin).</item>
/// </list>
/// </summary>
public class TorrentMiddleware
{
    private const string BasePath = "/Torrent";
    private const string ClientPageResource = "Configuration.searchPage.html";
    private const string InjectScriptResource = "Configuration.inject.js";

    private static readonly Lazy<string> ClientPage = new(() => ReadEmbedded(ClientPageResource));
    private static readonly Lazy<string> InjectScript = new(() => ReadEmbedded(InjectScriptResource));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly RequestDelegate _next;
    private readonly TorrentSearchService _search;
    private readonly DailySuggestionsService _suggestions;
    private readonly DownloadClientService _clients;
    private readonly ImageProxyService _images;
    private readonly TorrentFileService _torrentFiles;
    private readonly DomainResolverService _domains;
    private readonly SessionStore _sessions;
    private readonly PageLoader _pages;
    private readonly SourceDiscoveryService _discovery;
    private readonly ILogger<TorrentMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TorrentMiddleware"/> class.
    /// </summary>
    /// <param name="next">Siguiente middleware.</param>
    /// <param name="search">Servicio de búsqueda.</param>
    /// <param name="suggestions">Servicio de sugerencias del día.</param>
    /// <param name="clients">Servicio de clientes de descarga.</param>
    /// <param name="images">Proxy de imágenes.</param>
    /// <param name="torrentFiles">Servicio de .torrent.</param>
    /// <param name="domains">Resolutor de dominios.</param>
    /// <param name="sessions">Almacén de sesiones del navegador.</param>
    /// <param name="pages">Cargador de páginas (para probar el navegador).</param>
    /// <param name="discovery">Servicio de análisis de webs.</param>
    /// <param name="logger">Logger.</param>
    public TorrentMiddleware(
        RequestDelegate next,
        TorrentSearchService search,
        DailySuggestionsService suggestions,
        DownloadClientService clients,
        ImageProxyService images,
        TorrentFileService torrentFiles,
        DomainResolverService domains,
        SessionStore sessions,
        PageLoader pages,
        SourceDiscoveryService discovery,
        ILogger<TorrentMiddleware> logger)
    {
        _next = next;
        _search = search;
        _suggestions = suggestions;
        _clients = clients;
        _images = images;
        _torrentFiles = torrentFiles;
        _domains = domains;
        _sessions = sessions;
        _pages = pages;
        _discovery = discovery;
        _logger = logger;
    }

    /// <summary>
    /// Procesa la petición.
    /// </summary>
    /// <param name="context">Contexto HTTP.</param>
    /// <param name="authorizationContext">Contexto de autorización de Jellyfin.</param>
    /// <returns>Task.</returns>
    public async Task InvokeAsync(HttpContext context, IAuthorizationContext authorizationContext)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (!IsPluginPath(path))
        {
            if (HttpMethods.IsGet(context.Request.Method)
                && ShouldInjectIntoIndex(context.Request.Path)
                && Plugin.Instance?.Configuration.Enabled == true
                && IsWebInjectionEnabled())
            {
                await InjectScriptIntoHtml(context).ConfigureAwait(false);
                return;
            }

            await _next(context).ConfigureAwait(false);
            return;
        }

        var sub = path.Length > BasePath.Length ? path[BasePath.Length..] : string.Empty;

        if (HttpMethods.IsGet(context.Request.Method))
        {
            switch (sub.ToLowerInvariant())
            {
                case "":
                    // Sin barra final las rutas relativas de la página no resuelven.
                    context.Response.StatusCode = StatusCodes.Status302Found;
                    context.Response.Headers.Location = $"{context.Request.PathBase}{BasePath}/";
                    return;

                case "/":
                case "/index.html":
                    await ServeText(context, ClientPage.Value, "text/html; charset=utf-8")
                        .ConfigureAwait(false);
                    return;

                case "/inject.js":
                    await ServeText(context, InjectScript.Value, "application/javascript; charset=utf-8")
                        .ConfigureAwait(false);
                    return;
            }
        }

        if (!sub.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var (authenticated, isAdmin) = await AuthorizeAsync(context, authorizationContext)
            .ConfigureAwait(false);
        if (!authenticated)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await WriteJson(context, new { error = "Necesitas una sesión de Jellyfin." })
                .ConfigureAwait(false);
            return;
        }

        try
        {
            switch (sub.ToLowerInvariant())
            {
                case "/api/config" when HttpMethods.IsGet(context.Request.Method):
                    await WriteJson(context, BuildPublicConfig()).ConfigureAwait(false);
                    return;

                case "/api/suggestions" when HttpMethods.IsGet(context.Request.Method):
                    await HandleSuggestions(context).ConfigureAwait(false);
                    return;

                case "/api/suggestions/test" when HttpMethods.IsGet(context.Request.Method):
                    if (!isAdmin)
                    {
                        await DenyAdmin(context).ConfigureAwait(false);
                        return;
                    }

                    await HandleSuggestionsTest(context).ConfigureAwait(false);
                    return;

                case "/api/sources" when HttpMethods.IsGet(context.Request.Method):
                    await WriteJson(context, BuildPublicConfig().Sources).ConfigureAwait(false);
                    return;

                case "/api/templates" when HttpMethods.IsGet(context.Request.Method):
                    if (!isAdmin)
                    {
                        await DenyAdmin(context).ConfigureAwait(false);
                        return;
                    }

                    await WriteJson(context, SourceTemplates.All()).ConfigureAwait(false);
                    return;

                case "/api/search" when HttpMethods.IsPost(context.Request.Method):
                    await HandleSearch(context).ConfigureAwait(false);
                    return;

                case "/api/add" when HttpMethods.IsPost(context.Request.Method):
                    await HandleAdd(context).ConfigureAwait(false);
                    return;

                case "/api/image" when HttpMethods.IsGet(context.Request.Method):
                    await HandleImage(context).ConfigureAwait(false);
                    return;

                case "/api/torrent" when HttpMethods.IsGet(context.Request.Method):
                    await HandleTorrentDownload(context).ConfigureAwait(false);
                    return;

                case "/api/files" when HttpMethods.IsGet(context.Request.Method):
                    await HandleTorrentInfo(context).ConfigureAwait(false);
                    return;

                case "/api/source/test" when HttpMethods.IsPost(context.Request.Method):
                    if (!isAdmin)
                    {
                        await DenyAdmin(context).ConfigureAwait(false);
                        return;
                    }

                    await HandleSourceTest(context).ConfigureAwait(false);
                    return;

                case "/api/domain/resolve" when HttpMethods.IsPost(context.Request.Method):
                    if (!isAdmin)
                    {
                        await DenyAdmin(context).ConfigureAwait(false);
                        return;
                    }

                    await HandleDomainResolve(context).ConfigureAwait(false);
                    return;

                case "/api/source/discover" when HttpMethods.IsPost(context.Request.Method):
                    if (!isAdmin)
                    {
                        await DenyAdmin(context).ConfigureAwait(false);
                        return;
                    }

                    await HandleDiscover(context).ConfigureAwait(false);
                    return;

                case "/api/browser/probe" when HttpMethods.IsGet(context.Request.Method):
                    if (!isAdmin)
                    {
                        await DenyAdmin(context).ConfigureAwait(false);
                        return;
                    }

                    await HandleBrowserProbe(context).ConfigureAwait(false);
                    return;

                case "/api/browser/test" when HttpMethods.IsPost(context.Request.Method):
                    if (!isAdmin)
                    {
                        await DenyAdmin(context).ConfigureAwait(false);
                        return;
                    }

                    await HandleBrowserTest(context).ConfigureAwait(false);
                    return;

                case "/api/client/probe" when HttpMethods.IsGet(context.Request.Method):
                    if (!isAdmin)
                    {
                        await DenyAdmin(context).ConfigureAwait(false);
                        return;
                    }

                    await WriteJson(context, await _clients.ProbeAllAsync(context.RequestAborted)
                        .ConfigureAwait(false)).ConfigureAwait(false);
                    return;

                default:
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // El cliente canceló: nada que responder.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Torrent: error procesando {Path}", path);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await WriteJson(context, new { error = ex.Message }).ConfigureAwait(false);
            }
        }
    }

    private PublicConfig BuildPublicConfig()
    {
        Plugin.Instance?.EnsureLatestDefaults();
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var client = config.DownloadClient;
        return new PublicConfig
        {
            Enabled = config.Enabled,
            Sources = config.Sources
                .Select(s => new PublicSource
                {
                    Id = s.Id,
                    Name = s.Name,
                    Enabled = s.Enabled,
                    Kind = s.Kind.ToString(),
                    BaseUrl = s.BaseUrl
                })
                .ToList(),
            ClientKind = client.Kind.ToString(),
            ClientName = DescribeClient(client),
            MinSeeds = config.MinSeeds,
            ProxyImages = config.ProxyImages,
            CacheMinutes = config.CacheMinutes,
            DetailFetchLimit = config.DetailFetchLimit,
            DailySuggestionsEnabled = config.DailySuggestionsEnabled,
            DailySuggestionsCount = config.DailySuggestionsCount,
            DailySuggestionsHomeRow = config.DailySuggestionsHomeRow,
            DailySuggestionsWebButton = config.DailySuggestionsWebButton,
            Version = Plugin.Instance?.Version.ToString() ?? "1.0.0.0"
        };
    }

    private async Task HandleSuggestions(HttpContext context)
    {
        var refresh = context.Request.Query["refresh"]
            .Any(value => value is "1" or "true" or "yes");
        var response = await _suggestions.GetAsync(refresh, context.RequestAborted)
            .ConfigureAwait(false);
        await WriteJson(context, response).ConfigureAwait(false);
    }

    private async Task HandleSuggestionsTest(HttpContext context)
    {
        if (!Guid.TryParse(context.Request.Query["sourceId"].ToString(), out var sourceId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteJson(context, new { error = "Falta el id de la fuente." }).ConfigureAwait(false);
            return;
        }

        var response = await _suggestions.TestSourceAsync(sourceId, context.RequestAborted)
            .ConfigureAwait(false);
        await WriteJson(context, response).ConfigureAwait(false);
    }

    private static string DescribeClient(DownloadClientConfig client) => client.Kind switch
    {
        DownloadClientKind.QBittorrent => "qBittorrent",
        DownloadClientKind.Transmission => "Transmission",
        DownloadClientKind.Deluge => "Deluge",
        DownloadClientKind.MagnetOnly => "Enlace magnet del navegador",
        _ => "Automático (qBittorrent / Transmission / Deluge)"
    };

    private async Task HandleSearch(HttpContext context)
    {
        var request = await ReadBodyAsync<SearchRequest>(context).ConfigureAwait(false)
            ?? new SearchRequest();

        if (!string.IsNullOrWhiteSpace(request.Query) && request.Query.Trim().Length < 2)
        {
            await WriteJson(context, new SearchResponse { Query = request.Query }).ConfigureAwait(false);
            return;
        }

        var response = await _search.SearchAsync(request, context.RequestAborted).ConfigureAwait(false);
        await WriteJson(context, response).ConfigureAwait(false);
    }

    private async Task HandleAdd(HttpContext context)
    {
        var request = await ReadBodyAsync<AddTorrentRequest>(context).ConfigureAwait(false);
        if (request is null || (string.IsNullOrWhiteSpace(request.Magnet) && string.IsNullOrWhiteSpace(request.TorrentUrl)))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteJson(context, new DownloadResult { Message = "Falta el enlace magnet o la URL del .torrent." })
                .ConfigureAwait(false);
            return;
        }

        var download = new DownloadRequest
        {
            Magnet = request.Magnet,
            TorrentUrl = request.TorrentUrl,
            Title = request.Title,
            Referer = request.Referer
        };

        if (!string.IsNullOrWhiteSpace(request.ResultId)
            && _sessions.TryGet(request.ResultId, out var session))
        {
            download.Cookie ??= session.Cookie;
            download.Referer ??= session.Referer;
            download.TorrentUrl ??= session.TorrentUrl;
        }

        var result = await _clients.SendAsync(download, context.RequestAborted)
            .ConfigureAwait(false);

        await WriteJson(context, result).ConfigureAwait(false);
    }

    private async Task HandleImage(HttpContext context)
    {
        var url = context.Request.Query["u"].ToString();
        var referer = context.Request.Query["r"].ToString();
        if (string.IsNullOrWhiteSpace(url))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var image = await _images.GetAsync(url, referer, context.RequestAborted).ConfigureAwait(false);
        if (image is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = image.Value.ContentType;
        context.Response.Headers.CacheControl = "private, max-age=604800";
        await context.Response.Body.WriteAsync(image.Value.Bytes, context.RequestAborted)
            .ConfigureAwait(false);
    }

    private async Task HandleTorrentDownload(HttpContext context)
    {
        var url = context.Request.Query["u"].ToString();
        var referer = context.Request.Query["r"].ToString();
        if (string.IsNullOrWhiteSpace(url))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var bytes = await _torrentFiles.GetBytesAsync(url, referer, context.RequestAborted)
            .ConfigureAwait(false);
        if (bytes is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var name = context.Request.Query["name"].ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "torrent";
        }

        name = string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        context.Response.ContentType = "application/x-bittorrent";
        context.Response.Headers.ContentDisposition =
            new ContentDispositionHeaderValue("attachment") { FileName = name + ".torrent" }.ToString();
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task HandleTorrentInfo(HttpContext context)
    {
        var url = context.Request.Query["u"].ToString();
        var referer = context.Request.Query["r"].ToString();
        if (string.IsNullOrWhiteSpace(url))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var info = await _torrentFiles.GetInfoAsync(url, referer, context.RequestAborted)
            .ConfigureAwait(false);
        if (info is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await WriteJson(context, new
        {
            info.Name,
            info.InfoHash,
            info.TotalSize,
            info.PieceLength,
            info.IsPrivate,
            info.Comment,
            info.CreatedBy,
            info.CreatedOn,
            magnet = info.BuildMagnet(),
            files = info.Files.Select(f => new { f.Path, f.Size })
        }).ConfigureAwait(false);
    }

    private async Task HandleSourceTest(HttpContext context)
    {
        var request = await ReadBodyAsync<SourceTestRequest>(context).ConfigureAwait(false)
            ?? new SourceTestRequest();

        var query = string.IsNullOrWhiteSpace(request.Query) ? "matrix" : request.Query;
        var response = await _search
            .TestSourceAsync(request.SourceId, query, request.FetchDetails, context.RequestAborted)
            .ConfigureAwait(false);
        await WriteJson(context, response).ConfigureAwait(false);
    }

    private async Task HandleDiscover(HttpContext context)
    {
        var request = await ReadBodyAsync<DiscoverRequest>(context).ConfigureAwait(false);
        if (request is null || string.IsNullOrWhiteSpace(request.Url))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteJson(context, new { error = "Falta la URL de la web." }).ConfigureAwait(false);
            return;
        }

        var query = string.IsNullOrWhiteSpace(request.Query) ? "matrix" : request.Query;
        var result = await _discovery
            .DiscoverAsync(request.Url, query, context.RequestAborted)
            .ConfigureAwait(false);

        await WriteJson(context, result).ConfigureAwait(false);
    }

    private async Task HandleBrowserProbe(HttpContext context)
    {
        var config = Plugin.Instance?.Configuration.Browser ?? new BrowserConfig();
        var executable = ChromeBrowser.FindExecutable(config.ExecutablePath);
        var ok = executable is not null || !string.IsNullOrWhiteSpace(config.FlareSolverrUrl);

        await WriteJson(context, new
        {
            available = ok,
            mode = config.Mode.ToString(),
            executable,
            headless = config.Headless,
            flareSolverrUrl = config.FlareSolverrUrl,
            message = executable is not null
                ? "Navegador listo: " + executable
                : (string.IsNullOrWhiteSpace(config.FlareSolverrUrl)
                    ? "No se encontró Chrome/Edge/Chromium y FlareSolverr no está configurado."
                    : "Solo FlareSolverr disponible (" + config.FlareSolverrUrl + ").")
        }).ConfigureAwait(false);
    }

    private async Task HandleBrowserTest(HttpContext context)
    {
        var request = await ReadBodyAsync<BrowserTestRequest>(context).ConfigureAwait(false)
            ?? new BrowserTestRequest();

        var source = new TorrentSourceConfig
        {
            Name = "prueba",
            BaseUrl = request.Url,
            FetchMode = FetchMode.Browser,
            BrowserScript = request.Script,
            BrowserWaitSelector = request.WaitSelector,
            BrowserWaitMs = Math.Clamp(request.WaitMs, 0, 30000)
        };

        var page = await _pages
            .LoadAsync(source, request.Url, null, null, context.RequestAborted)
            .ConfigureAwait(false);

        var title = string.Empty;
        if (page.Ok)
        {
            var document = await _pages.ParseHtmlAsync(page.Html, context.RequestAborted)
                .ConfigureAwait(false);
            title = document.Title ?? string.Empty;
        }

        await WriteJson(context, new
        {
            ok = page.Ok,
            engine = page.Engine,
            finalUrl = page.FinalUrl,
            title,
            htmlLength = page.Html.Length,
            captured = page.CapturedUrls.Take(30),
            hasCookies = !string.IsNullOrWhiteSpace(page.CookieHeader),
            error = page.Error
        }).ConfigureAwait(false);
    }

    private async Task HandleDomainResolve(HttpContext context)
    {
        var request = await ReadBodyAsync<DomainResolveRequest>(context).ConfigureAwait(false);
        var config = Plugin.Instance?.Configuration;
        var source = config?.Sources.FirstOrDefault(s => s.Id == request?.SourceId);
        if (source is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await WriteJson(context, new { error = "La fuente no existe." }).ConfigureAwait(false);
            return;
        }

        var resolved = await _domains.ResolveNowAsync(source, context.RequestAborted)
            .ConfigureAwait(false);
        await WriteJson(context, new
        {
            ok = resolved is not null,
            baseUrl = resolved,
            provider = source.Domain.Provider.ToString(),
            error = resolved is null ? "No se encontró ningún dominio en el origen configurado." : null
        }).ConfigureAwait(false);
    }

    private static bool IsPluginPath(string path) =>
        path.Equals(BasePath, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(BasePath + "/", StringComparison.OrdinalIgnoreCase);

    private static bool IsWebInjectionEnabled()
    {
        var config = Plugin.Instance?.Configuration;
        return config is not null && (config.DailySuggestionsHomeRow || config.DailySuggestionsWebButton);
    }

    private static bool ShouldInjectIntoIndex(PathString path)
    {
        var value = path.Value;
        return value is not null
            && (value.Equals("/", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadEmbedded(string suffix)
    {
        var assembly = typeof(TorrentMiddleware).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return string.Empty;
        }

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task ServeText(HttpContext context, string content, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        context.Response.ContentType = contentType;
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Inyecta &lt;script src="/Torrent/inject.js"&gt; en el index.html de
    /// jellyfin-web para que el botón y la fila de sugerencias estén
    /// disponibles en todos los clientes web.
    /// </summary>
    private async Task InjectScriptIntoHtml(HttpContext context)
    {
        var originalBody = context.Response.Body;
        context.Request.Headers.Remove(HeaderNames.AcceptEncoding);

        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        await _next(context).ConfigureAwait(false);

        if (context.Response.StatusCode == StatusCodes.Status200OK
            && context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            buffer.Seek(0, SeekOrigin.Begin);
            var html = await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync()
                .ConfigureAwait(false);

            var scriptPath = $"{context.Request.PathBase}{BasePath}/inject.js";
            if (!html.Contains(scriptPath, StringComparison.Ordinal))
            {
                var tag = $"<script src=\"{scriptPath}\" defer></script>";
                html = html.Replace("</body>", tag + "</body>", StringComparison.OrdinalIgnoreCase);
            }

            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.Body = originalBody;
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        context.Response.Body = originalBody;
        buffer.Seek(0, SeekOrigin.Begin);
        await buffer.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task<(bool Authenticated, bool IsAdmin)> AuthorizeAsync(
        HttpContext context,
        IAuthorizationContext authorizationContext)
    {
        try
        {
            // Las etiquetas <img> y los enlaces no envían cabeceras: el token
            // llega como ?api_key=. Se promociona a cabecera para que Jellyfin
            // lo valide igual que el resto de peticiones.
            if (!context.Request.Headers.ContainsKey(HeaderNames.Authorization))
            {
                var token = context.Request.Query["api_key"].ToString();
                if (string.IsNullOrWhiteSpace(token))
                {
                    token = context.Request.Query["ApiKey"].ToString();
                }

                if (!string.IsNullOrWhiteSpace(token))
                {
                    context.Request.Headers[HeaderNames.Authorization] =
                        $"MediaBrowser Token=\"{token}\"";
                }
            }

            var info = await authorizationContext.GetAuthorizationInfo(context.Request)
                .ConfigureAwait(false);
            if (info is null)
            {
                return (false, false);
            }

            var isAdmin = info.IsApiKey
                || info.User?.Permissions?.Any(p => p.Kind == PermissionKind.IsAdministrator && p.Value) == true;
            return (info.IsApiKey || info.User is not null, isAdmin);
        }
        catch (Exception)
        {
            return (false, false);
        }
    }

    private static async Task DenyAdmin(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await WriteJson(context, new { error = "Requiere permisos de administrador." })
            .ConfigureAwait(false);
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpContext context)
        where T : class
    {
        if (context.Request.ContentLength == 0)
        {
            return null;
        }

        try
        {
            return await JsonSerializer
                .DeserializeAsync<T>(context.Request.Body, JsonOptions, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task WriteJson(HttpContext context, object payload)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                payload,
                payload.GetType(),
                JsonOptions,
                context.RequestAborted)
            .ConfigureAwait(false);
    }
}
