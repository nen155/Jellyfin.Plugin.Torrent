using System.Text.Json;
using Jellyfin.Plugin.Torrent.Browser;
using Jellyfin.Plugin.Torrent.Models;
using Jellyfin.Plugin.Torrent.Parsing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Scraping;

/// <summary>
/// Extractor para fuentes que responden JSON (APIs propias de las webs o
/// catálogos completos que se filtran en local).
/// </summary>
public sealed class JsonSourceScraper : SourceScraperBase, ISourceScraper
{
    private readonly SessionStore _sessions;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonSourceScraper"/> class.
    /// </summary>
    /// <param name="loader">Cargador de páginas.</param>
    /// <param name="sessions">Almacén de sesiones del navegador.</param>
    /// <param name="logger">Logger.</param>
    public JsonSourceScraper(PageLoader loader, SessionStore sessions, ILogger<JsonSourceScraper> logger)
        : base(loader, logger)
    {
        _sessions = sessions;
    }

    /// <inheritdoc />
    public SourceKind Kind => SourceKind.Json;

    /// <inheritdoc />
    public async Task<SourceSearchResult> SearchAsync(
        TorrentSourceConfig source,
        string baseUrl,
        string query,
        CancellationToken cancellationToken)
    {
        var outcome = new SourceSearchResult();
        var url = BuildUrl(source.SearchUrl, baseUrl, query, 1);
        if (url is null)
        {
            outcome.Error = "La plantilla de búsqueda no genera una URL válida.";
            return outcome;
        }

        var postBody = source.SearchMethod == SearchHttpMethod.Post
            ? BuildBody(source.SearchBody, query, 1)
            : null;

        var loaded = await Loader
            .LoadAsync(source, url, postBody, query, cancellationToken)
            .ConfigureAwait(false);

        if (!loaded.Ok)
        {
            outcome.Error = loaded.Error;
            return outcome;
        }

        if (loaded.StatusCode >= 400)
        {
            outcome.Error = $"La web respondió HTTP {loaded.StatusCode}.";
            return outcome;
        }

        outcome.Engine = loaded.Engine;

        var json = JsonPath.StripPrefix(loaded.Html, source.JsonStripPrefix);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            outcome.Error = "La respuesta no es JSON válido (revisa la plantilla o el prefijo a eliminar).";
            outcome.Diagnostic = loaded.Html.Length > 400 ? loaded.Html[..400] : loaded.Html;
            return outcome;
        }

        using (document)
        {
            var items = JsonPath.SelectArray(document.RootElement, source.JsonResultsPath);
            if (items.Count == 0)
            {
                outcome.Error = "La ruta de resultados no devolvió ningún elemento.";
                outcome.Diagnostic = json.Length > 400 ? json[..400] : json;
                return outcome;
            }

            var fields = BuildFieldMap(source);
            foreach (var item in items)
            {
                if (!MatchesFilter(item, source, fields, query))
                {
                    continue;
                }

                var result = MapItem(item, source, fields, loaded.FinalUrl);
                if (result is not null)
                {
                    outcome.Results.Add(result);
                }
            }
        }

        return outcome;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TorrentResult>> EnrichAsync(
        TorrentSourceConfig source,
        string baseUrl,
        TorrentResult result,
        CancellationToken cancellationToken) =>
        DetailEnricher.EnrichAsync(Loader, source, result, _sessions, Logger, cancellationToken);

    private static Dictionary<string, string> BuildFieldMap(TorrentSourceConfig source)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in source.JsonFields)
        {
            if (!string.IsNullOrWhiteSpace(field.Name) && !string.IsNullOrWhiteSpace(field.Value))
            {
                map[field.Name.Trim()] = field.Value.Trim();
            }
        }

        return map;
    }

    private static bool MatchesFilter(
        JsonElement item,
        TorrentSourceConfig source,
        Dictionary<string, string> fields,
        string query)
    {
        if (string.IsNullOrWhiteSpace(source.JsonFilterField) || string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var path = fields.TryGetValue(source.JsonFilterField, out var mapped)
            ? mapped
            : source.JsonFilterField;

        var value = JsonPath.SelectString(item, path);
        return TextUtil.ContainsAllWords(value, query);
    }

    private static TorrentResult? MapItem(
        JsonElement item,
        TorrentSourceConfig source,
        Dictionary<string, string> fields,
        string pageUrl)
    {
        string? Value(string field) =>
            fields.TryGetValue(field, out var path) ? JsonPath.SelectString(item, path) : null;

        var title = TextUtil.Clean(Value("title"));
        var releaseName = TextUtil.Clean(Value("releaseName"));
        if (releaseName.Length == 0)
        {
            releaseName = title;
        }

        var magnet = TextUtil.Clean(Value("magnet"));
        var torrentUrl = TextUtil.Clean(Value("torrentUrl"));
        var detailUrl = TextUtil.Clean(Value("detailUrl"));

        string? resolvedMagnet = null;
        string? resolvedTorrent = null;
        ClassifyLink(magnet, pageUrl, ref resolvedMagnet, ref resolvedTorrent);
        ClassifyLink(torrentUrl, pageUrl, ref resolvedMagnet, ref resolvedTorrent);

        if (string.IsNullOrWhiteSpace(detailUrl) && !string.IsNullOrWhiteSpace(source.DetailUrlTemplate))
        {
            detailUrl = BuildDetailUrl(source, item, fields, title, pageUrl);
        }

        detailUrl = TextUtil.ToAbsolute(pageUrl, detailUrl) ?? detailUrl;

        if (title.Length == 0 && releaseName.Length == 0 && resolvedMagnet is null && resolvedTorrent is null)
        {
            return null;
        }

        var result = new TorrentResult
        {
            SourceId = source.Id,
            SourceName = source.Name,
            Title = title,
            ReleaseName = releaseName,
            DetailUrl = string.IsNullOrWhiteSpace(detailUrl) ? null : detailUrl,
            Magnet = resolvedMagnet,
            TorrentUrl = resolvedTorrent,
            SizeText = Value("size"),
            PublishedText = Value("date"),
            Category = Value("category"),
            LanguageText = Value("language"),
            Description = Value("description"),
            Seeds = TextUtil.ToInt(Value("seeds")),
            Leeches = TextUtil.ToInt(Value("leeches")),
            Completed = TextUtil.ToInt(Value("completed"))
        };

        result.PosterUrl = TextUtil.ToAbsolute(pageUrl, Value("poster"));

        if (resolvedMagnet is not null)
        {
            result.InfoHash = MagnetExtractor.GetInfoHash(resolvedMagnet);
        }

        var id = Value("id");
        if (!string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(result.Id))
        {
            result.Id = TextUtil.Slugify($"{source.Id}-{id}");
        }

        EnrichMetadata(result);
        return result;
    }

    private static string? BuildDetailUrl(
        TorrentSourceConfig source,
        JsonElement item,
        Dictionary<string, string> fields,
        string title,
        string pageUrl)
    {
        var template = source.DetailUrlTemplate;
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        foreach (var field in fields)
        {
            var value = JsonPath.SelectString(item, field.Value) ?? string.Empty;
            template = template.Replace(
                "{" + field.Key + "}",
                Uri.EscapeDataString(value),
                StringComparison.OrdinalIgnoreCase);

            if (field.Key.Equals("title", StringComparison.OrdinalIgnoreCase))
            {
                template = template.Replace("{slug}", TextUtil.Slugify(value), StringComparison.OrdinalIgnoreCase);
            }
        }

        template = template
            .Replace("{base}", pageUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            .Replace("{slug}", TextUtil.Slugify(title), StringComparison.OrdinalIgnoreCase)
            .Replace("{query}", Uri.EscapeDataString(title), StringComparison.OrdinalIgnoreCase);

        return TextUtil.ToAbsolute(pageUrl, template);
    }
}
