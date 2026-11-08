using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Jellyfin.Plugin.Torrent.Browser;
using Jellyfin.Plugin.Torrent.Models;
using Jellyfin.Plugin.Torrent.Parsing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Scraping;

/// <summary>
/// Extractor genérico para webs de torrents: recorre las páginas de resultados
/// con selectores CSS y completa cada resultado con su ficha de detalle. Si la
/// fuente lo pide, la página se carga con un navegador real (JavaScript,
/// anti-bot, buscadores dinámicos).
/// </summary>
public sealed class HtmlSourceScraper : SourceScraperBase, ISourceScraper
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HtmlSourceScraper"/> class.
    /// </summary>
    /// <param name="loader">Cargador de páginas.</param>
    /// <param name="sessions">Almacén de sesiones del navegador.</param>
    /// <param name="logger">Logger.</param>
    public HtmlSourceScraper(
        PageLoader loader,
        SessionStore sessions,
        ILogger<HtmlSourceScraper> logger)
        : base(loader, logger)
    {
        _sessions = sessions;
    }

    private readonly SessionStore _sessions;

    /// <inheritdoc />
    public SourceKind Kind => SourceKind.Html;

    /// <inheritdoc />
    public async Task<SourceSearchResult> SearchAsync(
        TorrentSourceConfig source,
        string baseUrl,
        string query,
        CancellationToken cancellationToken)
    {
        var outcome = new SourceSearchResult();
        var pages = Math.Clamp(source.MaxPages, 1, 10);

        for (var page = 1; page <= pages; page++)
        {
            var url = BuildUrl(source.SearchUrl, baseUrl, query, page);
            if (url is null)
            {
                outcome.Error = "La plantilla de búsqueda no genera una URL válida.";
                return outcome;
            }

            var postBody = source.SearchMethod == SearchHttpMethod.Post
                ? BuildBody(source.SearchBody, query, page)
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
                if (page == 1)
                {
                    outcome.Diagnostic = loaded.Html.Length > 600 ? loaded.Html[..600] : loaded.Html;
                }

                return outcome;
            }

            outcome.Engine ??= loaded.Engine;

            var document = await Loader.ParseHtmlAsync(loaded.Html, cancellationToken)
                .ConfigureAwait(false);

            var rows = SelectRows(document, source.RowSelector);
            if (rows.Count == 0)
            {
                if (page == 1)
                {
                    outcome.Error = string.IsNullOrWhiteSpace(source.RowSelector)
                        ? "Falta el selector de fila de resultados."
                        : $"El selector de fila no encontró resultados (motor: {loaded.Engine}).";
                    outcome.Diagnostic = DetailEnricher.BuildDiagnostic(document);
                }

                break;
            }

            foreach (var row in rows)
            {
                var result = MapRow(row, source, loaded.FinalUrl);
                if (result is not null)
                {
                    outcome.Results.Add(result);
                }
            }

            if (page < pages)
            {
                await DelayAsync(source, cancellationToken).ConfigureAwait(false);
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

    private static IReadOnlyList<IElement> SelectRows(IHtmlDocument document, string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return Array.Empty<IElement>();
        }

        try
        {
            return document.QuerySelectorAll(selector).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<IElement>();
        }
    }

    private static TorrentResult? MapRow(IElement row, TorrentSourceConfig source, string pageUrl)
    {
        var title = SelectorReader.ReadBest(row, source.TitleSelector);
        var linkHref = SelectorReader.Read(row, source.LinkSelector);
        var detailUrl = TextUtil.ToAbsolute(pageUrl, linkHref);

        if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(source.LinkSelector))
        {
            var linkSelector = source.LinkSelector.Split('@')[0].Trim();
            title = SelectorReader.Read(row, linkSelector.Length == 0 ? "@self" : linkSelector);
        }

        string? magnet = null;
        string? torrentUrl = null;
        ClassifyLink(SelectorReader.Read(row, source.MagnetSelector), pageUrl, ref magnet, ref torrentUrl);
        ClassifyLink(SelectorReader.Read(row, source.TorrentSelector), pageUrl, ref magnet, ref torrentUrl);

        if (magnet is null && torrentUrl is null)
        {
            var rowMagnets = MagnetExtractor.FindMagnets(row.OuterHtml);
            if (rowMagnets.Count > 0)
            {
                magnet = rowMagnets[0];
            }
        }

        if (title is null && detailUrl is null)
        {
            return null;
        }

        var result = new TorrentResult
        {
            SourceId = source.Id,
            SourceName = source.Name,
            Title = title ?? string.Empty,
            ReleaseName = title ?? string.Empty,
            DetailUrl = detailUrl,
            Magnet = magnet,
            TorrentUrl = torrentUrl,
            SizeText = SelectorReader.Read(row, source.SizeSelector),
            PublishedText = SelectorReader.Read(row, source.DateSelector),
            Category = SelectorReader.Read(row, source.CategorySelector),
            LanguageText = SelectorReader.Read(row, source.LanguageSelector)
        };

        result.Seeds = TextUtil.ToInt(SelectorReader.Read(row, source.SeedsSelector));
        result.Leeches = TextUtil.ToInt(SelectorReader.Read(row, source.LeechesSelector));
        result.Completed = TextUtil.ToInt(SelectorReader.Read(row, source.CompletedSelector));
        result.PosterUrl = TextUtil.ToAbsolute(pageUrl, SelectorReader.Read(row, source.PosterSelector));

        if (magnet is not null)
        {
            result.InfoHash = MagnetExtractor.GetInfoHash(magnet);
            var name = MagnetExtractor.GetDisplayName(magnet);
            if (!string.IsNullOrWhiteSpace(name))
            {
                result.ReleaseName = name!;
            }
        }

        EnrichMetadata(result);
        return result;
    }
}
