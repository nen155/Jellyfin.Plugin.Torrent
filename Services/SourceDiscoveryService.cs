using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Jellyfin.Plugin.Torrent.Browser;
using Jellyfin.Plugin.Torrent.Models;
using Jellyfin.Plugin.Torrent.Parsing;
using Jellyfin.Plugin.Torrent.Scraping;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Services;

/// <summary>
/// Resumen de una pasada del análisis (HTTP o navegador).
/// </summary>
public sealed class DiscoveryPassInfo
{
    /// <summary>Gets or sets el motor usado.</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether la pasada terminó bien.</summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets el error, si lo hubo.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets el selector de fila detectado.</summary>
    public string? RowSelector { get; set; }

    /// <summary>Gets or sets el número de resultados de la prueba.</summary>
    public int Results { get; set; }

    /// <summary>Gets or sets a value indicating whether los selectores parecen fiables.</summary>
    public bool Confident { get; set; }

    /// <summary>Gets or sets la URL de búsqueda usada.</summary>
    public string? SearchUrl { get; set; }

    /// <summary>Gets or sets el tamaño del HTML recibido.</summary>
    public int HtmlLength { get; set; }

    /// <summary>Gets or sets los candidatos de fila con su puntuación.</summary>
    public string? Candidates { get; set; }
}

/// <summary>
/// Resultado del análisis automático de una web.
/// </summary>
public sealed class DiscoveryResult
{
    /// <summary>Gets or sets a value indicating whether se pudo generar una configuración.</summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets el error, si no se pudo analizar.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets la configuración propuesta.</summary>
    public TorrentSourceConfig? Source { get; set; }

    /// <summary>Gets or sets el resumen legible de lo detectado.</summary>
    public string Report { get; set; } = string.Empty;

    /// <summary>Gets or sets los avisos (cosas que conviene revisar).</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>Gets or sets el número de resultados de la prueba.</summary>
    public int SampleResults { get; set; }

    /// <summary>Gets or sets el motor usado para cargar las páginas.</summary>
    public string Engine { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether los selectores parecen fiables.</summary>
    public bool Confident { get; set; }

    /// <summary>Gets or sets el detalle de cada pasada.</summary>
    public List<DiscoveryPassInfo> Passes { get; set; } = new();

    /// <summary>Gets or sets el tamaño del HTML analizado.</summary>
    public int HtmlLength { get; set; }

    /// <summary>Gets or sets los candidatos de fila con su puntuación.</summary>
    public string? Candidates { get; set; }
}

/// <summary>
/// Analiza una web de torrents y propone la configuración de la fuente:
/// formulario de búsqueda, selector de fila, título, enlace, magnet/.torrent,
/// tamaño, semillas, fecha, carátula e idioma. Si la lista no publica enlaces,
/// visita la primera ficha y detecta allí los selectores.
/// </summary>
public sealed partial class SourceDiscoveryService
{
    private static readonly string[] SearchInputNames =
    [
        "q", "query", "search", "s", "buscar", "valor", "term", "keyword", "k", "searchterm", "busqueda"
    ];

    private static readonly string[] GenericClasses =
    [
        "row", "item", "col", "container", "content", "card", "list", "block", "clearfix", "active", "even", "odd"
    ];

    [GeneratedRegex(@"\d+([.,]\d+)?\s*(?:b|kb|kib|mb|mib|gb|gib|tb|tib)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"\b(seed|seeds|seeders|semillas|sembrado)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeedWordRegex();

    [GeneratedRegex(@"\b(leech|leechers|peers|clientes|descargando)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeechWordRegex();

    [GeneratedRegex(@"\b(date|fecha|added|publicado|published|uploaded|release)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateWordRegex();

    [GeneratedRegex(@"\b(lang|language|idioma|audio|voces)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LanguageWordRegex();

    [GeneratedRegex(@"\b(cat|category|type|tipo|genre|genero|quality|calidad|format|formato)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CategoryWordRegex();

    [GeneratedRegex(@"\b(poster|cover|thumb|image|imagen|caratula)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PosterWordRegex();

    private readonly PageLoader _loader;
    private readonly ILogger<SourceDiscoveryService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceDiscoveryService"/> class.
    /// </summary>
    /// <param name="loader">Cargador de páginas.</param>
    /// <param name="logger">Logger.</param>
    public SourceDiscoveryService(PageLoader loader, ILogger<SourceDiscoveryService> logger)
    {
        _loader = loader;
        _logger = logger;
    }

    /// <summary>
    /// Analiza una URL y propone la configuración de la fuente.
    /// </summary>
    /// <param name="url">URL de la web (p. ej. <c>https://ejemplo.to</c>).</param>
    /// <param name="sampleQuery">Texto de prueba para validar los selectores.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Configuración propuesta y resumen.</returns>
    public async Task<DiscoveryResult> DiscoverAsync(
        string url,
        string sampleQuery,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeUrl(url);
        if (normalized is null)
        {
            return new DiscoveryResult { Error = "La URL no es válida." };
        }

        var baseUri = new Uri(normalized);
        var baseUrl = baseUri.GetLeftPart(UriPartial.Authority);

        // Primera pasada: HTTP (rápida). Si el resultado no es fiable, se
        // repite con el navegador para ejecutar el JavaScript de la web.
        var passes = new List<DiscoveryPassInfo>();

        var first = await RunPassAsync(baseUrl, normalized, sampleQuery, FetchMode.Auto, cancellationToken)
            .ConfigureAwait(false);
        passes.Add(Summarize("http/auto", first));

        if (!first.Ok)
        {
            var browser = await RunPassAsync(baseUrl, normalized, sampleQuery, FetchMode.Browser, cancellationToken)
                .ConfigureAwait(false);
            passes.Add(Summarize("browser", browser));
            if (browser.Ok)
            {
                browser.Warnings.Add("La web necesita el navegador (buscador dinámico o protección anti-bot).");
                browser.Passes = passes;
                return browser;
            }

            first.Passes = passes;
            return first;
        }

        if (!first.Confident && _loader.BrowserAvailable)
        {
            var second = await RunPassAsync(baseUrl, normalized, sampleQuery, FetchMode.Browser, cancellationToken)
                .ConfigureAwait(false);
            passes.Add(Summarize("browser", second));
            if (second.Ok && second.Confident)
            {
                second.Warnings.Add("La lista de resultados solo aparece con JavaScript: se ha configurado el navegador.");
                second.Passes = passes;
                return second;
            }
        }

        first.Passes = passes;
        return first;
    }

    private static DiscoveryPassInfo Summarize(string mode, DiscoveryResult result) => new()
    {
        Mode = mode,
        Ok = result.Ok,
        Error = result.Error,
        RowSelector = result.Source?.RowSelector,
        Results = result.SampleResults,
        Confident = result.Confident,
        SearchUrl = result.Source?.SearchUrl,
        HtmlLength = result.HtmlLength,
        Candidates = result.Candidates
    };

    private async Task<DiscoveryResult> RunPassAsync(
        string baseUrl,
        string normalizedUrl,
        string sampleQuery,
        FetchMode mode,
        CancellationToken cancellationToken)
    {
        var result = new DiscoveryResult();
        var source = new TorrentSourceConfig
        {
            Name = new Uri(normalizedUrl).Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase),
            BaseUrl = baseUrl,
            Enabled = false,
            Kind = SourceKind.Html,
            FetchMode = mode,
            DelayMs = 400,
            MaxPages = 1,
            ResolveReleaseLinks = true,
            BrowserWaitMs = mode == FetchMode.Browser ? 8000 : 0
        };

        var home = await _loader.LoadAsync(source, normalizedUrl, null, null, cancellationToken)
            .ConfigureAwait(false);
        if (!home.Ok)
        {
            result.Error = home.Error ?? "No se pudo abrir la web.";
            return result;
        }

        result.Engine = home.Engine;
        var homeDoc = await _loader.ParseHtmlAsync(home.Html, cancellationToken).ConfigureAwait(false);

        var form = DetectSearchForm(homeDoc, baseUrl);
        if (form is null)
        {
            source.SearchUrl = "{base}/search?q={query}";
            result.Warnings.Add(
                "No se encontró un formulario de búsqueda: se ha propuesto {base}/search?q={query}. "
                + "Ajusta la URL de búsqueda si la web usa otra ruta.");
        }
        else
        {
            source.SearchUrl = form.Url;
            source.SearchMethod = form.Method;
            source.SearchBody = form.Body ?? string.Empty;
            result.Warnings.AddRange(form.Warnings);
        }

        var searchUrl = BuildSearchUrl(source, baseUrl, sampleQuery);
        var search = searchUrl is null
            ? null
            : await _loader.LoadAsync(
                    source,
                    searchUrl,
                    source.SearchMethod == SearchHttpMethod.Post
                        ? source.SearchBody.Replace("{query}", Uri.EscapeDataString(sampleQuery), StringComparison.Ordinal)
                        : null,
                    sampleQuery,
                    cancellationToken,
                    waitForStableDom: mode == FetchMode.Browser)
                .ConfigureAwait(false);

        if (search is null || !search.Ok)
        {
            result.Error = search?.Error ?? "No se pudo ejecutar la búsqueda de prueba.";
            return result;
        }

        result.Engine = search.Engine;
        result.HtmlLength = search.Html.Length;
        var searchDoc = await _loader.ParseHtmlAsync(search.Html, cancellationToken).ConfigureAwait(false);

        var (rowSelector, rows, candidates) = DetectRows(searchDoc, sampleQuery);
        result.Candidates = candidates;
        if (rowSelector is null || rows.Count == 0)
        {
            result.Error = "No se reconoció la lista de resultados.";
            result.Warnings.Add($"Motor: {search.Engine}. Puede que la web necesite el navegador o una cookie.");
            return result;
        }

        source.RowSelector = rowSelector;
        DetectRowFields(rows.Take(3).ToList(), source, result.Warnings);

        result.SampleResults = CountUsableRows(searchDoc, source);
        result.Confident = result.SampleResults >= 2 && IsConfident(rows, source, sampleQuery);

        if (string.IsNullOrWhiteSpace(source.MagnetSelector)
            && string.IsNullOrWhiteSpace(source.TorrentSelector))
        {
            var detailUrl = SelectorReader.Read(rows[0], source.LinkSelector);
            var absolute = TextUtil.ToAbsolute(search.FinalUrl, detailUrl);
            if (absolute is not null)
            {
                source.FetchDetails = true;
                await DetectDetailAsync(source, absolute, result.Warnings, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (result.SampleResults == 0)
        {
            result.Warnings.Add("La prueba no devolvió resultados con los selectores propuestos: revísalos.");
        }

        result.Source = source;
        result.Ok = true;
        result.Report = BuildReport(source, result);
        return result;
    }

    private static bool IsConfident(List<IElement> rows, TorrentSourceConfig source, string query)
    {
        if (rows.Count is < 2 or > 80)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(source.TitleSelector))
        {
            return false;
        }

        var goodLinks = rows.Count(row =>
        {
            var href = SelectorReader.Read(row, source.LinkSelector);
            return !string.IsNullOrWhiteSpace(href)
                && !href!.StartsWith('#')
                && href.Contains('/');
        });

        var matchesQuery = rows.Count(row =>
            TextUtil.Normalize(row.TextContent).Contains(TextUtil.Normalize(query), StringComparison.Ordinal));

        return goodLinks >= Math.Max(2, rows.Count / 2)
            && matchesQuery >= Math.Max(1, rows.Count / 2);
    }

    private static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var value = url.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : null;
    }

    private static string? BuildSearchUrl(TorrentSourceConfig source, string baseUrl, string query)
    {
        if (string.IsNullOrWhiteSpace(source.SearchUrl))
        {
            return null;
        }

        var url = source.SearchUrl
            .Replace("{base}", baseUrl.TrimEnd('/'), StringComparison.Ordinal)
            .Replace("{query}", Uri.EscapeDataString(query), StringComparison.Ordinal);

        return TextUtil.ToAbsolute(baseUrl, url);
    }

    private sealed record SearchForm(string Url, SearchHttpMethod Method, string? Body, List<string> Warnings);

    private static SearchForm? DetectSearchForm(IHtmlDocument document, string baseUrl)
    {
        var warnings = new List<string>();

        foreach (var form in document.QuerySelectorAll("form"))
        {
            var input = form.QuerySelectorAll("input").FirstOrDefault(i =>
            {
                var name = i.GetAttribute("name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    return false;
                }

                var type = i.GetAttribute("type")?.ToLowerInvariant();
                if (type is "submit" or "hidden" or "checkbox" or "radio" or "password" or "email")
                {
                    return false;
                }

                return SearchInputNames.Contains(name.Trim().ToLowerInvariant())
                    || string.Equals(type, "search", StringComparison.OrdinalIgnoreCase);
            });

            if (input is null)
            {
                continue;
            }

            var name = input.GetAttribute("name")!.Trim();
            var action = form.GetAttribute("action") ?? string.Empty;
            var method = (form.GetAttribute("method") ?? "get").Trim().ToLowerInvariant() == "post"
                ? SearchHttpMethod.Post
                : SearchHttpMethod.Get;

            if (action.Length == 0)
            {
                action = "/";
            }

            if (!action.StartsWith('/') && !action.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                action = "/" + action;
            }

            var absoluteAction = TextUtil.ToAbsolute(baseUrl, action) ?? (baseUrl + action);

            if (method == SearchHttpMethod.Post)
            {
                return new SearchForm(
                    absoluteAction,
                    SearchHttpMethod.Post,
                    name + "={query}",
                    warnings);
            }

            var separator = absoluteAction.Contains('?') ? "&" : "?";
            return new SearchForm(
                absoluteAction + separator + name + "={query}",
                SearchHttpMethod.Get,
                null,
                warnings);
        }

        // Sin formulario: enlaces del tipo /search?q= o /buscar
        var link = document.QuerySelectorAll("a[href]")
            .Select(a => a.GetAttribute("href"))
            .FirstOrDefault(href => href is not null
                && (href.Contains("search", StringComparison.OrdinalIgnoreCase)
                    || href.Contains("buscar", StringComparison.OrdinalIgnoreCase)));

        if (link is not null)
        {
            var absolute = TextUtil.ToAbsolute(baseUrl, link);
            if (absolute is not null)
            {
                warnings.Add("No hay formulario: se ha usado el enlace de búsqueda de la web.");
                var separator = absolute.Contains('?') ? "&" : "?";
                return new SearchForm(absolute + separator + "q={query}", SearchHttpMethod.Get, null, warnings);
            }
        }

        return null;
    }

    private static (string? Selector, List<IElement> Rows, string Candidates) DetectRows(
        IHtmlDocument document,
        string query)
    {
        var candidates = new Dictionary<string, (int Score, List<IElement> Rows)>(StringComparer.Ordinal);
        var normalizedQuery = TextUtil.Normalize(query);

        void Evaluate(string selector, string tag, string classes, IEnumerable<IElement> matches)
        {
            // Una lista de resultados son hermanos bajo el mismo padre.
            var group = matches
                .GroupBy(m => m.ParentElement)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()
                ?.ToList();

            if (group is null || group.Count < 2 || group.Count > 90)
            {
                return;
            }

            var siblings = group.Count;
            var withLinks = group.Count(m => m.QuerySelector("a[href]") is not null);
            var richRows = group.Count(m => TextUtil.Clean(m.TextContent).Length >= 12);
            var withQuery = normalizedQuery.Length == 0
                ? 0
                : group.Count(m => TextUtil.Normalize(m.TextContent).Contains(normalizedQuery, StringComparison.Ordinal));

            var score = 10;

            if (withLinks >= Math.Max(2, siblings / 2))
            {
                score += 6;
            }
            else
            {
                score -= 8;
            }

            if (withQuery >= Math.Max(2, siblings / 2))
            {
                score += 20;
            }
            else if (normalizedQuery.Length > 0)
            {
                score -= 4;
            }

            if (tag is "tr" or "li" or "div" or "article")
            {
                score += 4;
            }

            if (Regex.IsMatch(classes, "result|search|item|torrent|movie|film|browse|entry|release", RegexOptions.IgnoreCase))
            {
                score += 8;
            }

            if (Regex.IsMatch(classes, "nav|menu|header|footer|sidebar|breadcrumb|pagination|social", RegexOptions.IgnoreCase))
            {
                score -= 20;
            }

            if (richRows < 2)
            {
                score -= 6;
            }

            if (!candidates.TryGetValue(selector, out var existing) || existing.Score < score)
            {
                candidates[selector] = (score, group);
            }
        }

        Evaluate("table tbody tr", "tr", string.Empty, document.QuerySelectorAll("table tbody tr"));
        Evaluate("ul > li", "li", string.Empty, document.QuerySelectorAll("ul > li"));

        foreach (var element in document.QuerySelectorAll("[class]"))
        {
            if (IsInsideChrome(element))
            {
                continue;
            }

            var className = element.ClassList.FirstOrDefault(c =>
                !GenericClasses.Contains(c, StringComparer.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(className))
            {
                continue;
            }

            var selector = element.LocalName + "." + className;
            if (candidates.ContainsKey(selector))
            {
                continue;
            }

            var matches = document.QuerySelectorAll(selector);
            if (matches.Length < 2 || matches.Length > 200)
            {
                continue;
            }

            Evaluate(
                selector,
                element.LocalName,
                string.Join(' ', element.ClassList),
                matches);
        }

        var summary = string.Join(
            ", ",
            candidates.OrderByDescending(c => c.Value.Score).Take(6).Select(c => $"{c.Key}={c.Value.Score}"));

        var best = candidates.OrderByDescending(c => c.Value.Score).FirstOrDefault();
        if (best.Key is null)
        {
            return (null, new List<IElement>(), summary);
        }

        return (best.Key, best.Value.Rows, summary);
    }

    /// <summary>
    /// Número máximo de elementos de la colección que comparten el mismo padre
    /// (una lista de resultados son hermanos; el menú de navegación, no).
    /// </summary>
    private static int MaxSiblings(IEnumerable<IElement> matches) =>
        matches
            .GroupBy(m => m.ParentElement)
            .Select(g => g.Count())
            .DefaultIfEmpty(0)
            .Max();

    private static bool IsInsideChrome(IElement element)
    {
        var current = element;
        while (current is not null)
        {
            if (current.LocalName is "nav" or "header" or "footer" or "aside")
            {
                return true;
            }

            if (current.ClassList.Any(c => Regex.IsMatch(
                    c,
                    "^(nav|navbar|menu|sidebar|breadcrumb|footer|header)$",
                    RegexOptions.IgnoreCase)))
            {
                return true;
            }

            current = current.ParentElement!;
        }

        return false;
    }

    private static void DetectRowFields(
        List<IElement> rows,
        TorrentSourceConfig source,
        List<string> warnings)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var row = rows[0];

        // Título: un elemento con clase title/name, o el ancla con más texto.
        var titleElement = row.QuerySelectorAll("[class]")
            .FirstOrDefault(e => e.ClassList.Any(c =>
                Regex.IsMatch(c, "^(title|name|nombre|movie-title|item-title)$", RegexOptions.IgnoreCase))
                && TextUtil.Clean(e.TextContent).Length > 2);

        var anchors = row.QuerySelectorAll("a[href]")
            .Where(a => !string.IsNullOrWhiteSpace(a.TextContent))
            .OrderByDescending(a => TextUtil.Clean(a.TextContent).Length)
            .ToList();

        var titleAnchor = anchors.FirstOrDefault(a =>
            !a.GetAttribute("href")!.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
            && !MagnetExtractor.LooksLikeTorrentDownload(a.GetAttribute("href")));

        if (titleElement is not null)
        {
            source.TitleSelector = BuildElementSelector(titleElement, row);
        }
        else if (titleAnchor is not null)
        {
            source.TitleSelector = BuildElementSelector(titleAnchor, row);
        }

        if (titleAnchor is not null)
        {
            var href = titleAnchor.GetAttribute("href");
            if (!string.IsNullOrWhiteSpace(href))
            {
                source.LinkSelector = BuildElementSelector(titleAnchor, row) + "@href";
            }
        }

        var magnet = row.QuerySelector("a[href^='magnet:']");
        if (magnet is not null)
        {
            source.MagnetSelector = "a[href^='magnet:']@href";
        }

        var torrent = row.QuerySelectorAll("a[href]")
            .FirstOrDefault(a => MagnetExtractor.LooksLikeDirectTorrent(a.GetAttribute("href")));
        if (torrent is not null)
        {
            source.TorrentSelector = "a[href*='.torrent']@href";
        }

        var poster = row.QuerySelectorAll("img").FirstOrDefault();
        if (poster is not null)
        {
            var selector = BuildElementSelector(poster, row);
            source.PosterSelector = selector + "@src";
        }

        // Tamaño: el elemento cuyo texto parece un tamaño.
        foreach (var element in row.QuerySelectorAll("*"))
        {
            var text = TextUtil.Clean(element.TextContent);
            if (text.Length is > 2 and < 40 && SizeRegex().IsMatch(text))
            {
                var selector = BuildElementSelector(element, row);
                if (!string.IsNullOrWhiteSpace(selector))
                {
                    source.SizeSelector = selector;
                }

                break;
            }
        }

        // Semillas / clientes por nombre de clase.
        foreach (var element in row.QuerySelectorAll("[class]"))
        {
            var classes = string.Join(' ', element.ClassList);
            if (source.SeedsSelector is null && SeedWordRegex().IsMatch(classes))
            {
                source.SeedsSelector = BuildElementSelector(element, row);
            }
            else if (source.LeechesSelector is null && LeechWordRegex().IsMatch(classes))
            {
                source.LeechesSelector = BuildElementSelector(element, row);
            }
            else if (source.DateSelector is null && DateWordRegex().IsMatch(classes))
            {
                source.DateSelector = BuildElementSelector(element, row);
            }
            else if (source.LanguageSelector is null && LanguageWordRegex().IsMatch(classes))
            {
                source.LanguageSelector = BuildElementSelector(element, row) + "@title";
            }
            else if (source.CategorySelector is null && CategoryWordRegex().IsMatch(classes))
            {
                source.CategorySelector = BuildElementSelector(element, row);
            }
        }

        if (source.SeedsSelector is null || source.LeechesSelector is null)
        {
            warnings.Add("No se detectaron las semillas/clientes por clase: revísalos a mano si la web los muestra.");
        }
    }

    private async Task DetectDetailAsync(
        TorrentSourceConfig source,
        string detailUrl,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var page = await _loader
            .LoadAsync(source, detailUrl, null, null, cancellationToken, detailPage: true)
            .ConfigureAwait(false);

        if (!page.Ok)
        {
            warnings.Add("No se pudo abrir la ficha de detalle para buscar los enlaces.");
            return;
        }

        var document = await _loader.ParseHtmlAsync(page.Html, cancellationToken).ConfigureAwait(false);

        var magnets = MagnetExtractor.FindMagnets(page.Html);
        if (magnets.Count > 0)
        {
            var anchor = document.QuerySelector("a[href^='magnet:']");
            source.DetailMagnetSelector = anchor is not null
                ? "a[href^='magnet:']@href"
                : "#txtMagnetLink@value";
            warnings.Add("Los enlaces están en la ficha: se ha activado «visitar la ficha de detalle».");
        }

        var torrents = MagnetExtractor.FindTorrentLinksDetailed(document, page.FinalUrl);
        if (torrents.Count > 0)
        {
            source.DetailTorrentSelector = "a[href*='.torrent']@href";
            if (magnets.Count == 0)
            {
                warnings.Add("La ficha publica archivos .torrent (no magnets).");
            }
        }

        if (source.DetailPosterSelector is null)
        {
            var images = document.QuerySelectorAll("img").ToList();
            var poster = images.FirstOrDefault(img =>
            {
                var src = img.GetAttribute("src") ?? string.Empty;
                var alt = img.GetAttribute("alt") ?? string.Empty;
                var cls = string.Join(' ', img.ClassList);
                return PosterWordRegex().IsMatch(src) || PosterWordRegex().IsMatch(alt)
                    || PosterWordRegex().IsMatch(cls);
            }) ?? images.FirstOrDefault(img =>
            {
                var src = img.GetAttribute("src") ?? string.Empty;
                var width = TextUtil.ToInt(img.GetAttribute("width")) ?? 0;
                return width is >= 80 and <= 400
                    && !Regex.IsMatch(src, "logo|icon|avatar|banner|spinner", RegexOptions.IgnoreCase);
            });

            if (poster is not null)
            {
                source.DetailPosterSelector = BuildElementSelector(poster, document.Body!) + "@src";
            }
        }

        if (magnets.Count == 0 && torrents.Count == 0)
        {
            warnings.Add(
                "La ficha no expone magnet ni .torrent en el HTML. Si la web los genera con JavaScript, "
                + "pon «Motor de la ficha» en Navegador y un script que pulse el botón de descarga.");
        }
    }

    private static int CountUsableRows(IHtmlDocument document, TorrentSourceConfig source)
    {
        if (string.IsNullOrWhiteSpace(source.RowSelector))
        {
            return 0;
        }

        try
        {
            return document.QuerySelectorAll(source.RowSelector)
                .Count(row => SelectorReader.Read(row, source.TitleSelector) is not null);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string BuildElementSelector(IElement element, IElement scope)
    {
        if (!string.IsNullOrWhiteSpace(element.Id))
        {
            return "#" + element.Id;
        }

        var className = element.ClassList.FirstOrDefault(c =>
            !GenericClasses.Contains(c, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(className))
        {
            return element.LocalName + "." + className;
        }

        if (element.ParentElement is not null)
        {
            var index = element.ParentElement.Children
                .Where(c => c.LocalName == element.LocalName)
                .ToList()
                .IndexOf(element);
            if (index >= 0)
            {
                return $"{element.LocalName}:nth-of-type({index + 1})";
            }
        }

        return element.LocalName;
    }

    private static string BuildReport(TorrentSourceConfig source, DiscoveryResult result)
    {
        var parts = new List<string>
        {
            $"Búsqueda: {source.SearchMethod.ToString().ToUpperInvariant()} {source.SearchUrl}",
            $"Fila: {source.RowSelector}",
            $"Título: {source.TitleSelector ?? "(no detectado)"}",
            $"Enlace: {source.LinkSelector ?? "(no detectado)"}"
        };

        if (!string.IsNullOrWhiteSpace(source.MagnetSelector))
        {
            parts.Add("Magnet: " + source.MagnetSelector);
        }

        if (!string.IsNullOrWhiteSpace(source.TorrentSelector))
        {
            parts.Add(".torrent: " + source.TorrentSelector);
        }

        if (source.FetchDetails)
        {
            parts.Add("Ficha: " + (source.DetailMagnetSelector ?? source.DetailTorrentSelector ?? "(sin enlaces)"));
        }

        parts.Add($"Resultados de la prueba: {result.SampleResults} (motor {result.Engine})");
        return string.Join(" · ", parts);
    }
}
