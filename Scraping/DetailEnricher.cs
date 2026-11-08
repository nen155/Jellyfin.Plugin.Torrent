using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Jellyfin.Plugin.Torrent.Browser;
using Jellyfin.Plugin.Torrent.Models;
using Jellyfin.Plugin.Torrent.Parsing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Scraping;

/// <summary>
/// Completa un resultado con la información de su ficha de detalle: todos los
/// enlaces magnet/.torrent, carátula, sinopsis, idioma, tamaño, fecha y los
/// pares de datos que publique la web (director, género, duración…).
/// Si la ficha enlaza a páginas intermedias de descarga (p. ej.
/// <c>/Download-123/release</c>), las abre para extraer el magnet.
/// </summary>
public static partial class DetailEnricher
{
    private const int MaxExtraReleases = 25;
    private const int MaxReleasePages = 6;

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<title[^>]*>(?<t>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TitleTagRegex();

    [GeneratedRegex(@"^(?:download|descargar)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DownloadPrefixRegex();

    /// <summary>
    /// Visita la ficha de detalle y completa el resultado.
    /// </summary>
    /// <param name="loader">Cargador de páginas.</param>
    /// <param name="source">Fuente.</param>
    /// <param name="result">Resultado a completar.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>El resultado completado y los releases adicionales de la ficha.</returns>
    public static Task<IReadOnlyList<TorrentResult>> EnrichAsync(
        PageLoader loader,
        TorrentSourceConfig source,
        TorrentResult result,
        ILogger logger,
        CancellationToken cancellationToken) =>
        EnrichAsync(loader, source, result, null, logger, cancellationToken);

    /// <summary>
    /// Visita la ficha de detalle y completa el resultado, registrando la
    /// sesión del navegador para poder descargar después el .torrent.
    /// </summary>
    /// <param name="loader">Cargador de páginas.</param>
    /// <param name="source">Fuente.</param>
    /// <param name="result">Resultado a completar.</param>
    /// <param name="sessions">Almacén de sesiones (opcional).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>El resultado completado y los releases adicionales de la ficha.</returns>
    public static async Task<IReadOnlyList<TorrentResult>> EnrichAsync(
        PageLoader loader,
        TorrentSourceConfig source,
        TorrentResult result,
        SessionStore? sessions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!source.FetchDetails || string.IsNullOrWhiteSpace(result.DetailUrl))
        {
            SourceScraperBase.EnrichMetadata(result);
            return new[] { result };
        }

        PageLoadResult loaded;
        try
        {
            loaded = await loader
                .LoadAsync(source, result.DetailUrl, null, null, cancellationToken, detailPage: true)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Torrent: no se pudo abrir la ficha {Url}", result.DetailUrl);
            SourceScraperBase.EnrichMetadata(result);
            return new[] { result };
        }

        if (!loaded.Ok || loaded.StatusCode >= 400)
        {
            SourceScraperBase.EnrichMetadata(result);
            return new[] { result };
        }

        var document = await loader.ParseHtmlAsync(loaded.Html, cancellationToken).ConfigureAwait(false);
        var finalUrl = loaded.FinalUrl;

        var magnets = new List<string>();
        var links = new List<MagnetExtractor.TorrentLink>();

        var magnetValues = SelectorReader.ReadAll(document, source.DetailMagnetSelector);
        var torrentValues = SelectorReader.ReadAll(document, source.DetailTorrentSelector);

        foreach (var value in magnetValues)
        {
            string? magnet = null;
            string? torrent = null;
            SourceScraperBase.ClassifyLink(value, finalUrl, ref magnet, ref torrent);
            if (magnet is not null)
            {
                magnets.Add(magnet);
            }

            if (torrent is not null)
            {
                links.Add(new MagnetExtractor.TorrentLink(torrent, null, null));
            }
        }

        foreach (var value in torrentValues)
        {
            string? magnet = null;
            string? torrent = null;
            SourceScraperBase.ClassifyLink(value, finalUrl, ref magnet, ref torrent);
            if (magnet is not null)
            {
                magnets.Add(magnet);
            }

            if (torrent is not null)
            {
                links.Add(new MagnetExtractor.TorrentLink(torrent, null, null));
            }
        }

        var scoped = magnetValues.Count > 0 || torrentValues.Count > 0;

        // Enlaces que el navegador ha pedido por su cuenta (descargas protegidas,
        // magnet generados por JavaScript, botones con proof-of-work…).
        var capturedLinks = new List<MagnetExtractor.TorrentLink>();
        foreach (var captured in loaded.CapturedUrls)
        {
            if (captured.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                magnets.Add(captured);
            }
            else if (MagnetExtractor.LooksLikeTorrentDownload(captured)
                && !capturedLinks.Any(l => l.Url.Equals(captured, StringComparison.OrdinalIgnoreCase)))
            {
                capturedLinks.Add(new MagnetExtractor.TorrentLink(
                    captured,
                    GuessReleaseFromUrl(captured),
                    null));
            }
        }

        if (!scoped)
        {
            // Sin selectores: si el navegador capturó la descarga real, manda
            // ella; si no, se escanea la ficha completa.
            if (capturedLinks.Count > 0)
            {
                links.AddRange(capturedLinks);
            }
            else
            {
                magnets.AddRange(MagnetExtractor.FindMagnets(loaded.Html));
                links.AddRange(MagnetExtractor.FindTorrentLinksDetailed(document, finalUrl));
            }
        }
        else
        {
            links.AddRange(capturedLinks.Where(c =>
                !links.Any(l => l.Url.Equals(c.Url, StringComparison.OrdinalIgnoreCase))));
        }

        // Páginas intermedias de descarga: se abren para sacar el magnet.
        var resolved = source.ResolveReleaseLinks
            ? await ResolveReleasePagesAsync(loader, source, links, logger, cancellationToken)
                .ConfigureAwait(false)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        ApplyDetailFields(document, source, result, finalUrl);

        var candidates = BuildCandidates(magnets, links, resolved);
        var results = new List<TorrentResult> { result };

        if (candidates.Count > 0)
        {
            ApplyCandidate(result, candidates[0].Magnet, candidates[0].Link);
        }

        foreach (var candidate in candidates.Skip(1).Take(MaxExtraReleases))
        {
            results.Add(CloneWithLink(result, candidate.Magnet, candidate.Link));
        }

        foreach (var item in results)
        {
            SourceScraperBase.EnrichMetadata(item);
        }

        if (sessions is not null)
        {
            foreach (var item in results)
            {
                sessions.Register(item.Id, new SessionStore.Entry
                {
                    SourceId = source.Id,
                    Cookie = loaded.CookieHeader,
                    Referer = finalUrl,
                    TorrentUrl = item.TorrentUrl
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Construye un texto de diagnóstico con lo que se ha descargado (útil
    /// para ajustar los selectores de una fuente).
    /// </summary>
    /// <param name="document">Documento.</param>
    /// <returns>Resumen.</returns>
    public static string BuildDiagnostic(IHtmlDocument document)
    {
        var title = TextUtil.Clean(document.Title);
        var body = document.Body?.TextContent ?? string.Empty;
        var clean = TextUtil.Clean(body);
        var snippet = clean.Length > 400 ? clean[..400] : clean;
        var anchors = document.QuerySelectorAll("a[href]").Length;
        return $"Título: {title} | Enlaces: {anchors} | Texto: {snippet}";
    }

    private static List<(string? Magnet, MagnetExtractor.TorrentLink? Link)> BuildCandidates(
        List<string> magnets,
        List<MagnetExtractor.TorrentLink> links,
        Dictionary<string, string> resolved)
    {
        var candidates = new List<(string?, MagnetExtractor.TorrentLink?)>();
        var usedMagnets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var magnet in magnets
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var link = links.FirstOrDefault(l =>
                resolved.TryGetValue(l.Url, out var m)
                && m.Equals(magnet, StringComparison.OrdinalIgnoreCase));

            if (link is not null)
            {
                usedLinks.Add(link.Url);
            }

            usedMagnets.Add(magnet);
            candidates.Add((magnet, link));
        }

        foreach (var link in links)
        {
            if (usedLinks.Contains(link.Url))
            {
                continue;
            }

            string? magnet = null;
            if (resolved.TryGetValue(link.Url, out var resolvedMagnet)
                && usedMagnets.Add(resolvedMagnet))
            {
                magnet = resolvedMagnet;
            }

            candidates.Add((magnet, link));
        }

        return candidates;
    }

    private static void ApplyCandidate(
        TorrentResult result,
        string? magnet,
        MagnetExtractor.TorrentLink? link)
    {
        if (!string.IsNullOrWhiteSpace(magnet))
        {
            result.Magnet = magnet;
            result.InfoHash ??= MagnetExtractor.GetInfoHash(magnet);
            var name = MagnetExtractor.GetDisplayName(magnet);
            if (!string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(result.ReleaseName))
            {
                result.ReleaseName = name!;
            }
        }

        if (link is not null)
        {
            result.TorrentUrl ??= link.Url;
            if (!string.IsNullOrWhiteSpace(link.Name))
            {
                result.ReleaseName = link.Name;
            }

            if (!string.IsNullOrWhiteSpace(link.SizeText))
            {
                result.SizeText = link.SizeText;
                result.SizeBytes = SizeParser.Parse(link.SizeText);
            }
        }
    }

    private static async Task<Dictionary<string, string>> ResolveReleasePagesAsync(
        PageLoader loader,
        TorrentSourceConfig source,
        List<MagnetExtractor.TorrentLink> links,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var budget = MaxReleasePages;

        var targets = links
            .Where(l => !MagnetExtractor.LooksLikeDirectTorrent(l.Url))
            .DistinctBy(l => l.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var link in targets)
        {
            if (budget-- <= 0)
            {
                break;
            }

            try
            {
                var page = await loader
                    .LoadAsync(source, link.Url, null, null, cancellationToken, modeOverride: FetchMode.Http)
                    .ConfigureAwait(false);

                var magnets = page.Ok ? MagnetExtractor.FindMagnets(page.Html) : new List<string>();
                if (magnets.Count == 0 && page.Ok && source.FetchMode != FetchMode.Http)
                {
                    page = await loader
                        .LoadAsync(source, link.Url, null, null, cancellationToken)
                        .ConfigureAwait(false);
                    magnets = page.Ok ? MagnetExtractor.FindMagnets(page.Html) : new List<string>();
                }

                if (magnets.Count == 0 || !page.Ok)
                {
                    continue;
                }

                resolved[link.Url] = magnets[0];

                var document = await loader.ParseHtmlAsync(page.Html, cancellationToken)
                    .ConfigureAwait(false);
                var name = CleanPageTitle(document.Title);
                var index = links.FindIndex(l => l.Url.Equals(link.Url, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    var updated = links[index];
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        updated = updated with { Name = name };
                    }

                    if (string.IsNullOrWhiteSpace(updated.SizeText))
                    {
                        var size = FindSize(document);
                        if (!string.IsNullOrWhiteSpace(size))
                        {
                            updated = updated with { SizeText = size };
                        }
                    }

                    links[index] = updated;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Torrent: no se pudo abrir el enlace de descarga {Url}", link.Url);
            }
        }

        return resolved;
    }

    private static string? CleanPageTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var clean = DownloadPrefixRegex().Replace(TextUtil.Clean(title), string.Empty);
        var dash = clean.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 10)
        {
            clean = clean[..dash];
        }

        var pipe = clean.LastIndexOf(" | ", StringComparison.Ordinal);
        if (pipe > 10)
        {
            clean = clean[..pipe];
        }

        return clean.Length > 3 ? clean.Trim() : null;
    }

    private static string? FindSize(IHtmlDocument document)
    {
        foreach (var element in document.QuerySelectorAll("[class*='infoDesc'], [class*='value'], td, dd"))
        {
            var text = TextUtil.Clean(element.TextContent);
            if (text.Length is > 0 and < 80 && SizeParser.Parse(text) is not null)
            {
                return text;
            }
        }

        return null;
    }

    private static void ApplyDetailFields(
        IHtmlDocument document,
        TorrentSourceConfig source,
        TorrentResult result,
        string pageUrl)
    {
        var poster = SelectorReader.Read(document, source.DetailPosterSelector)
            ?? ReadMeta(document, "og:image")
            ?? ReadMeta(document, "twitter:image");
        if (!string.IsNullOrWhiteSpace(poster))
        {
            result.PosterUrl = TextUtil.ToAbsolute(pageUrl, poster) ?? result.PosterUrl;
        }

        var description = SelectorReader.Read(document, source.DetailDescriptionSelector)
            ?? ReadMeta(document, "og:description")
            ?? ReadMeta(document, "description");
        if (!string.IsNullOrWhiteSpace(description))
        {
            result.Description = description;
        }

        var size = SelectorReader.Read(document, source.DetailSizeSelector);
        if (!string.IsNullOrWhiteSpace(size))
        {
            result.SizeText = size;
        }

        var date = SelectorReader.Read(document, source.DetailDateSelector);
        if (!string.IsNullOrWhiteSpace(date))
        {
            result.PublishedText = date;
        }

        var language = SelectorReader.Read(document, source.DetailLanguageSelector);
        if (!string.IsNullOrWhiteSpace(language))
        {
            result.LanguageText = string.IsNullOrWhiteSpace(result.LanguageText)
                ? language
                : result.LanguageText + ", " + language;
        }

        var seeds = SelectorReader.Read(document, source.DetailSeedsSelector);
        if (!string.IsNullOrWhiteSpace(seeds))
        {
            result.Seeds ??= TextUtil.ToInt(seeds);
        }

        var leeches = SelectorReader.Read(document, source.DetailLeechesSelector);
        if (!string.IsNullOrWhiteSpace(leeches))
        {
            result.Leeches ??= TextUtil.ToInt(leeches);
        }

        if (string.IsNullOrWhiteSpace(result.Title) || result.Title.Length < 3)
        {
            var heading = SelectorReader.Read(document, "h1") ?? ReadMeta(document, "og:title");
            if (!string.IsNullOrWhiteSpace(heading))
            {
                result.Title = heading!;
            }
        }

        foreach (var pair in ExtractInfoPairs(document))
        {
            if (result.Extra.All(e => !e.Name.Equals(pair.Name, StringComparison.OrdinalIgnoreCase)))
            {
                result.Extra.Add(pair);
            }
        }
    }

    private static TorrentResult CloneWithLink(
        TorrentResult template,
        string? magnet,
        MagnetExtractor.TorrentLink? link)
    {
        var releaseName = link?.Name
            ?? MagnetExtractor.GetDisplayName(magnet)
            ?? GuessReleaseFromUrl(link?.Url)
            ?? template.ReleaseName;

        var clone = new TorrentResult
        {
            SourceId = template.SourceId,
            SourceName = template.SourceName,
            Title = template.Title,
            ReleaseName = releaseName,
            DetailUrl = template.DetailUrl,
            Magnet = magnet,
            TorrentUrl = link?.Url,
            PosterUrl = template.PosterUrl,
            Category = template.Category,
            LanguageText = template.LanguageText,
            Description = template.Description,
            PublishedText = template.PublishedText,
            Published = template.Published,
            Year = template.Year,
            Seeds = template.Seeds,
            Leeches = template.Leeches,
            Kind = template.Kind,
            Extra = template.Extra,
            SizeText = link?.SizeText,
            InfoHash = MagnetExtractor.GetInfoHash(magnet)
        };

        return clone;
    }

    private static string? GuessReleaseFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        var last = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(last))
        {
            return null;
        }

        var name = Uri.UnescapeDataString(
            last.Replace(".torrent", string.Empty, StringComparison.OrdinalIgnoreCase));
        return name.Length > 3 ? name.Replace('_', ' ').Replace('.', ' ') : null;
    }

    private static string? ReadMeta(IHtmlDocument document, string name)
    {
        var element = document.QuerySelector($"meta[property='{name}']")
            ?? document.QuerySelector($"meta[name='{name}']")
            ?? document.QuerySelector($"meta[property='og:{name}']");
        var value = element?.GetAttribute("content");
        return string.IsNullOrWhiteSpace(value) ? null : TextUtil.Clean(value);
    }

    private static IEnumerable<NameValueEntry> ExtractInfoPairs(IHtmlDocument document)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in document.QuerySelectorAll("tr"))
        {
            var name = SelectorReader.Read(row, "th");
            var value = SelectorReader.Read(row, "td");
            if (IsUsefulPair(name, value) && seen.Add(name!))
            {
                yield return new NameValueEntry(name!, value!);
            }
        }

        foreach (var row in document.QuerySelectorAll("[class*='InfoRow'], [class*='info-row']"))
        {
            var name = SelectorReader.Read(row, "[class*='infoTitle'], [class*='info-title'], dt");
            var value = SelectorReader.Read(row, "[class*='infoDesc'], [class*='info-desc'], dd");
            if (IsUsefulPair(name, value) && seen.Add(name!))
            {
                yield return new NameValueEntry(name!, value!);
            }
        }

        foreach (var paragraph in document.QuerySelectorAll("p"))
        {
            var label = paragraph.QuerySelector("b, strong");
            if (label is null)
            {
                continue;
            }

            var name = TextUtil.Clean(label.TextContent).TrimEnd(':', ' ');
            var full = TextUtil.Clean(paragraph.TextContent);
            if (name.Length < 2 || full.Length <= name.Length)
            {
                continue;
            }

            var value = full.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                ? full[name.Length..].TrimStart(':', ' ')
                : full;

            if (IsUsefulPair(name, value) && seen.Add(name))
            {
                yield return new NameValueEntry(name, value);
            }
        }

        foreach (var term in document.QuerySelectorAll("dt"))
        {
            var value = term.NextElementSibling;
            if (value is null || !value.TagName.Equals("DD", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = TextUtil.Clean(term.TextContent);
            var text = TextUtil.Clean(value.TextContent);
            if (IsUsefulPair(name, text) && seen.Add(name))
            {
                yield return new NameValueEntry(name, text);
            }
        }
    }

    private static bool IsUsefulPair(string? name, string? value) =>
        !string.IsNullOrWhiteSpace(name)
        && !string.IsNullOrWhiteSpace(value)
        && name!.Length is > 1 and <= 32
        && value!.Length <= 240
        && !value.Equals("—", StringComparison.Ordinal)
        && !value.Equals("-", StringComparison.Ordinal);
}
