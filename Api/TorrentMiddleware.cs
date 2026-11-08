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
/// <item><c>GET /Torrent/api/config</c>: fuentes y ajustes para la interfaz.</item>
/// <item><c>POST /Torrent/api/search</c>: búsqueda agregada.</item>
/// <item><c>POST /Torrent/api/add</c>: envía un enlace al cliente de torrents.</item>
/// <item><c>GET /Torrent/api/image|torrent|files</c>: proxy de carátulas y .torrent.</item>
/// <item><c>POST /Torrent/api/source/test|domain/resolve</c>: diagnóstico (admin).</item>
/// </list>
/// </summary>
public class TorrentMiddleware
{
    private const string BasePath = "/Torrent";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly RequestDelegate _next;
    private readonly TorrentSearchService _search;
    private readonly DownloadClientService _clients;
    private readonly ImageProxyService _images;
    private readonly TorrentFileService _torrentFiles;
    private readonly DomainResolverService _domains;
    private readonly SessionStore _sessions;
    private readonly PageLoader _pages;
    private readonly ILogger<TorrentMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TorrentMiddleware"/> class.
    /// </summary>
    /// <param name="next">Siguiente middleware.</param>
    /// <param name="search">Servicio de búsqueda.</param>
    /// <param name="clients">Servicio de clientes de descarga.</param>
    /// <param name="images">Proxy de imágenes.</param>
    /// <param name="torrentFiles">Servicio de .torrent.</param>
    /// <param name="domains">Resolutor de dominios.</param>
    /// <param name="sessions">Almacén de sesiones del navegador.</param>
    /// <param name="pages">Cargador de páginas (para probar el navegador).</param>
    /// <param name="logger">Logger.</param>
    public TorrentMiddleware(
        RequestDelegate next,
        TorrentSearchService search,
        DownloadClientService clients,
        ImageProxyService images,
        TorrentFileService torrentFiles,
        DomainResolverService domains,
        SessionStore sessions,
        PageLoader pages,
        ILogger<TorrentMiddleware> logger)
    {
        _next = next;
        _search = search;
        _clients = clients;
        _images = images;
        _torrentFiles = torrentFiles;
        _domains = domains;
        _sessions = sessions;
        _pages = pages;
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
        if (!path.StartsWith(BasePath, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var sub = path.Length > BasePath.Length ? path[BasePath.Length..] : "/";
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
            Version = Plugin.Instance?.Version.ToString() ?? "1.0.0.0"
        };
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

    private static async Task<(bool Authenticated, bool IsAdmin)> AuthorizeAsync(
        HttpContext context,
        IAuthorizationContext authorizationContext)
    {
        try
        {
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
