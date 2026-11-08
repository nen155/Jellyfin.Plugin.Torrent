using System.Globalization;
using System.Xml.Linq;
using Jellyfin.Plugin.Torrent.Browser;
using Jellyfin.Plugin.Torrent.Models;
using Jellyfin.Plugin.Torrent.Parsing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Scraping;

/// <summary>
/// Extractor para fuentes Torznab: Jackett, Prowlarr y trackers con API
/// compatible. Devuelve resultados ya normalizados (título, magnet/.torrent,
/// tamaño, semillas, categoría y fecha).
/// </summary>
public sealed class TorznabSourceScraper : SourceScraperBase, ISourceScraper
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TorznabSourceScraper"/> class.
    /// </summary>
    /// <param name="loader">Cargador de páginas.</param>
    /// <param name="logger">Logger.</param>
    public TorznabSourceScraper(PageLoader loader, ILogger<TorznabSourceScraper> logger)
        : base(loader, logger)
    {
    }

    /// <inheritdoc />
    public SourceKind Kind => SourceKind.Torznab;

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
            outcome.Error = "La URL Torznab no es válida.";
            return outcome;
        }

        var loaded = await Loader
            .LoadAsync(source, url, null, query, cancellationToken, modeOverride: FetchMode.Http)
            .ConfigureAwait(false);

        if (!loaded.Ok)
        {
            outcome.Error = loaded.Error;
            return outcome;
        }

        if (loaded.StatusCode >= 400)
        {
            outcome.Error = $"La API Torznab respondió HTTP {loaded.StatusCode}.";
            return outcome;
        }

        outcome.Engine = loaded.Engine;

        XDocument document;
        try
        {
            document = XDocument.Parse(loaded.Html);
        }
        catch (Exception)
        {
            outcome.Error = "La respuesta Torznab no es XML válido (revisa la URL y la API key).";
            outcome.Diagnostic = loaded.Html.Length > 400 ? loaded.Html[..400] : loaded.Html;
            return outcome;
        }

        foreach (var item in document.Descendants().Where(e => e.Name.LocalName == "item"))
        {
            var result = MapItem(item, source, loaded.FinalUrl);
            if (result is not null)
            {
                outcome.Results.Add(result);
            }
        }

        if (outcome.Results.Count == 0 && outcome.Error is null)
        {
            var error = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "error");
            outcome.Error = error?.Attribute("description")?.Value
                ?? "La API Torznab no devolvió resultados.";
        }

        return outcome;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TorrentResult>> EnrichAsync(
        TorrentSourceConfig source,
        string baseUrl,
        TorrentResult result,
        CancellationToken cancellationToken)
    {
        SourceScraperBase.EnrichMetadata(result);
        return Task.FromResult<IReadOnlyList<TorrentResult>>(new[] { result });
    }

    private static TorrentResult? MapItem(XElement item, TorrentSourceConfig source, string pageUrl)
    {
        string? Child(string name) =>
            item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

        string? Attr(string name) =>
            item.Elements()
                .Where(e => e.Name.LocalName == "attr")
                .FirstOrDefault(e => string.Equals(
                    e.Attribute("name")?.Value,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                ?.Attribute("value")
                ?.Value;

        var title = TextUtil.Clean(Child("title"));
        if (title.Length == 0)
        {
            return null;
        }

        var link = TextUtil.Clean(Child("link"));
        var enclosure = item.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "enclosure")
            ?.Attribute("url")?.Value;
        var magnet = TextUtil.Clean(Attr("magneturl"));

        string? resolvedMagnet = null;
        string? resolvedTorrent = null;
        SourceScraperBase.ClassifyLink(
            magnet.Length > 0 ? magnet : null, pageUrl, ref resolvedMagnet, ref resolvedTorrent);
        SourceScraperBase.ClassifyLink(link, pageUrl, ref resolvedMagnet, ref resolvedTorrent);
        SourceScraperBase.ClassifyLink(enclosure, pageUrl, ref resolvedMagnet, ref resolvedTorrent);

        var sizeText = Attr("size") ?? TextUtil.Clean(Child("size"));
        long? sizeBytes = long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
            ? size
            : SizeParser.Parse(sizeText);

        var result = new TorrentResult
        {
            SourceId = source.Id,
            SourceName = source.Name,
            Title = title,
            ReleaseName = title,
            Magnet = resolvedMagnet,
            TorrentUrl = resolvedTorrent,
            SizeBytes = sizeBytes,
            SizeText = sizeBytes is null ? sizeText : SizeParser.Format(sizeBytes.Value),
            Seeds = TextUtil.ToInt(Attr("seeders")),
            Leeches = TextUtil.ToInt(Attr("peers") ?? Attr("leechers")),
            PublishedText = Child("pubDate"),
            Category = DescribeCategory(Child("category")),
            Description = Child("description"),
            InfoHash = MagnetExtractor.GetInfoHash(resolvedMagnet)
        };

        var poster = Attr("poster") ?? Attr("coverurl") ?? Attr("image");
        if (!string.IsNullOrWhiteSpace(poster))
        {
            result.PosterUrl = TextUtil.ToAbsolute(pageUrl, poster);
        }

        var imdb = Attr("imdb");
        if (!string.IsNullOrWhiteSpace(imdb) && imdb!.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            result.Extra.Add(new NameValueEntry("IMDb", "https://www.imdb.com/title/" + imdb + "/"));
        }

        EnrichMetadata(result);
        return result;
    }

    private static string? DescribeCategory(string? raw)
    {
        var value = TextUtil.Clean(raw);
        if (value.Length == 0)
        {
            return null;
        }

        var code = TextUtil.ToInt(value);
        return code switch
        {
            >= 2000 and < 3000 => "Película",
            >= 5000 and < 6000 => "Serie",
            >= 3000 and < 4000 => "Audio",
            >= 4000 and < 5000 => "Juegos/PC",
            >= 7000 and < 8000 => "Libros",
            _ => value
        };
    }
}
