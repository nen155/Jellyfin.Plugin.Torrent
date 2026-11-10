using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Jellyfin.Plugin.Torrent.Models;
using Jellyfin.Plugin.Torrent.Parsing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Scraping;

/// <summary>
/// Respuesta de una petición HTTP ya decodificada.
/// </summary>
public sealed class FetchResult
{
    /// <summary>Gets or sets la URL final tras redirecciones.</summary>
    public string FinalUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets el contenido como texto.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Gets or sets el tipo de contenido.</summary>
    public string? ContentType { get; set; }

    /// <summary>Gets or sets el código de estado.</summary>
    public int StatusCode { get; set; }
}

/// <summary>
/// Cliente HTTP usado por el motor de scraping: aplica User-Agent, cabeceras y
/// cookie de cada fuente, detecta el juego de caracteres y expone el HTML ya
/// parseado.
/// </summary>
public sealed partial class HttpFetcher : IDisposable
{
    [GeneratedRegex(@"<meta[^>]+charset\s*=\s*[""']?\s*(?<cs>[\w-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaCharsetRegex();

    private readonly ILogger<HttpFetcher> _logger;
    private readonly HttpClient _http;
    private readonly HtmlParser _parser = new(new HtmlParserOptions { IsScripting = false });

    /// <summary>
    /// Initializes static members of the <see cref="HttpFetcher"/> class.
    /// Registra las codificaciones antiguas (windows-1252, iso-8859-15…) que
    /// usan muchas webs en español.
    /// </summary>
    static HttpFetcher()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch (Exception)
        {
            // Sin proveedor extra: se usará UTF-8 como respaldo.
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpFetcher"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public HttpFetcher(ILogger<HttpFetcher> logger)
    {
        _logger = logger;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("es-ES,es;q=0.9,en;q=0.8");
    }

    /// <summary>
    /// Realiza una petición GET.
    /// </summary>
    /// <param name="url">URL.</param>
    /// <param name="source">Fuente (cabeceras y cookie).</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Respuesta decodificada.</returns>
    public Task<FetchResult> GetAsync(
        string url,
        TorrentSourceConfig source,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, url, null, source, cancellationToken);

    /// <summary>
    /// Realiza una petición POST con cuerpo de formulario.
    /// </summary>
    /// <param name="url">URL.</param>
    /// <param name="body">Cuerpo ya codificado.</param>
    /// <param name="source">Fuente (cabeceras y cookie).</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Respuesta decodificada.</returns>
    public Task<FetchResult> PostAsync(
        string url,
        string body,
        TorrentSourceConfig source,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, url, body, source, cancellationToken);

    /// <summary>
    /// Parsea HTML en un documento navegable.
    /// </summary>
    /// <param name="html">HTML.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Documento.</returns>
    public Task<IHtmlDocument> ParseHtmlAsync(string html, CancellationToken cancellationToken) =>
        _parser.ParseDocumentAsync(html, cancellationToken);

    /// <summary>
    /// Descarga bytes (imágenes, .torrent) con un referer opcional.
    /// </summary>
    /// <param name="url">URL.</param>
    /// <param name="referer">Referer a enviar.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Bytes descargados.</returns>
    public Task<byte[]> GetBytesAsync(
        string url,
        string? referer,
        CancellationToken cancellationToken) =>
        GetBytesAsync(url, referer, null, cancellationToken);

    /// <summary>
    /// Descarga bytes (imágenes, .torrent) con un referer y una cookie opcionales.
    /// </summary>
    /// <param name="url">URL.</param>
    /// <param name="referer">Referer a enviar.</param>
    /// <param name="cookie">Cookie de sesión.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Bytes descargados.</returns>
    public async Task<byte[]> GetBytesAsync(
        string url,
        string? referer,
        string? cookie,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(referer)
            && Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
        {
            request.Headers.Referrer = refererUri;
        }

        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<FetchResult> SendAsync(
        HttpMethod method,
        string url,
        string? body,
        TorrentSourceConfig source,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);

        if (!string.IsNullOrWhiteSpace(source.Cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", source.Cookie);
        }

        foreach (var header in source.Headers)
        {
            if (string.IsNullOrWhiteSpace(header.Name))
            {
                continue;
            }

            if (header.Name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", header.Value);
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                request.Content?.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        if (!request.Headers.Contains("Referer") && !string.IsNullOrWhiteSpace(source.BaseUrl))
        {
            request.Headers.TryAddWithoutValidation("Referer", source.BaseUrl);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        }

        _logger.LogDebug("Torrent: {Method} {Url}", method, url);

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var charset = response.Content.Headers.ContentType?.CharSet;
        var text = Decode(bytes, charset);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Torrent: {Url} respondió {Status}",
                url,
                (int)response.StatusCode);
        }

        return new FetchResult
        {
            FinalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url,
            Content = text,
            ContentType = response.Content.Headers.ContentType?.MediaType,
            StatusCode = (int)response.StatusCode
        };
    }

    private static string Decode(byte[] bytes, string? charsetFromHeader)
    {
        var encoding = ResolveEncoding(charsetFromHeader);
        if (encoding is null)
        {
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            var match = MetaCharsetRegex().Match(head);
            if (match.Success)
            {
                encoding = ResolveEncoding(match.Groups["cs"].Value);
            }
        }

        encoding ??= Encoding.UTF8;
        try
        {
            return encoding.GetString(bytes);
        }
        catch (Exception)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private static Encoding? ResolveEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            return Encoding.GetEncoding(name.Trim().Trim('"', '\''));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Lectura de valores de un documento HTML mediante selectores CSS.
/// </summary>
public static class SelectorReader
{
    /// <summary>
    /// Lee un valor con la sintaxis <c>selector</c> (texto) o
    /// <c>selector@atributo</c>. El selector especial <c>@self</c> usa el
    /// propio elemento.
    /// </summary>
    /// <param name="scope">Elemento o documento donde buscar.</param>
    /// <param name="spec">Especificación del selector.</param>
    /// <returns>Valor limpio o null.</returns>
    public static string? Read(IParentNode scope, string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        var (selector, attribute) = Split(spec);
        IElement? element;
        if (selector.Length == 0 || selector.Equals("@self", StringComparison.OrdinalIgnoreCase))
        {
            element = scope as IElement;
        }
        else
        {
            element = scope.QuerySelector(selector);
        }

        if (element is null)
        {
            return null;
        }

        if (attribute is null || attribute.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            var text = TextUtil.Clean(element.TextContent);
            return text.Length == 0 ? null : text;
        }

        if (attribute.Equals("ownText", StringComparison.OrdinalIgnoreCase))
        {
            return ReadOwnText(element);
        }

        if (attribute.Equals("html", StringComparison.OrdinalIgnoreCase))
        {
            return element.InnerHtml;
        }

        var value = element.GetAttribute(attribute);
        if (string.IsNullOrWhiteSpace(value)
            && (attribute.Equals("src", StringComparison.OrdinalIgnoreCase)
                || attribute.Equals("href", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var fallback in new[] { "data-src", "data-original", "data-lazy-src", "data-url", "data-href" })
            {
                value = element.GetAttribute(fallback);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    break;
                }
            }
        }

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Como <see cref="Read"/> pero, si el selector coincide con varios
    /// elementos y se pide el texto, devuelve el más largo. Útil para títulos
    /// donde la primera coincidencia puede ser un contador de comentarios.
    /// </summary>
    /// <param name="scope">Elemento o documento donde buscar.</param>
    /// <param name="spec">Especificación del selector.</param>
    /// <returns>Valor limpio o null.</returns>
    public static string? ReadBest(IParentNode scope, string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        var (selector, attribute) = Split(spec);
        if (attribute is not null || selector.Length == 0 || selector.Equals("@self", StringComparison.OrdinalIgnoreCase))
        {
            return Read(scope, spec);
        }

        try
        {
            var elements = scope.QuerySelectorAll(selector);
            if (elements.Length <= 1)
            {
                return Read(scope, spec);
            }

            return elements
                .Select(e => TextUtil.Clean(e.TextContent))
                .Where(t => t.Length > 0)
                .OrderByDescending(t => t.Length)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return Read(scope, spec);
        }
    }

    /// <summary>
    /// Lee todos los valores de un selector (texto o atributo) en orden.
    /// </summary>
    /// <param name="scope">Elemento o documento donde buscar.</param>
    /// <param name="spec">Especificación del selector.</param>
    /// <returns>Valores limpios sin duplicados.</returns>
    public static List<string> ReadAll(IParentNode scope, string? spec)
    {
        var values = new List<string>();
        if (string.IsNullOrWhiteSpace(spec))
        {
            return values;
        }

        var (selector, attribute) = Split(spec);
        if (selector.Length == 0 || selector.Equals("@self", StringComparison.OrdinalIgnoreCase))
        {
            var single = Read(scope, spec);
            if (!string.IsNullOrWhiteSpace(single))
            {
                values.Add(single!);
            }

            return values;
        }

        try
        {
            foreach (var element in scope.QuerySelectorAll(selector))
            {
                string? value;
                if (attribute is null || attribute.Equals("text", StringComparison.OrdinalIgnoreCase))
                {
                    value = TextUtil.Clean(element.TextContent);
                }
                else if (attribute.Equals("ownText", StringComparison.OrdinalIgnoreCase))
                {
                    value = ReadOwnText(element);
                }
                else if (attribute.Equals("html", StringComparison.OrdinalIgnoreCase))
                {
                    value = element.InnerHtml;
                }
                else
                {
                    value = element.GetAttribute(attribute);
                }

                if (!string.IsNullOrWhiteSpace(value)
                    && !values.Contains(value!, StringComparer.OrdinalIgnoreCase))
                {
                    values.Add(value!);
                }
            }
        }
        catch (Exception)
        {
            // Selector inválido.
        }

        return values;
    }

    /// <summary>
    /// Lee solo el texto propio del elemento, sin el de sus hijos
    /// (<c>.title@ownText</c> evita que se pegue el texto de <c>.episode</c>).
    /// </summary>
    private static string? ReadOwnText(IElement element)
    {
        var text = TextUtil.Clean(string.Concat(
            element.ChildNodes.OfType<IText>().Select(node => node.Text)));
        return text.Length == 0 ? null : text;
    }

    private static (string Selector, string? Attribute) Split(string spec)    {
        var trimmed = spec.Trim();
        var index = trimmed.LastIndexOf('@');
        if (index <= 0)
        {
            return (trimmed, null);
        }

        var selector = trimmed[..index].Trim();
        var attribute = trimmed[(index + 1)..].Trim();
        if (selector.Length == 0)
        {
            selector = "@self";
        }

        return (selector, attribute.Length == 0 ? null : attribute);
    }
}
