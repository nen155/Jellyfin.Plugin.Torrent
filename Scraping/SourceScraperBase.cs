using Jellyfin.Plugin.Torrent.Browser;
using Jellyfin.Plugin.Torrent.Models;
using Jellyfin.Plugin.Torrent.Parsing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Scraping;

/// <summary>
/// Base común de los extractores: construcción de URLs, plantillas y pausas.
/// </summary>
public abstract class SourceScraperBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SourceScraperBase"/> class.
    /// </summary>
    /// <param name="loader">Cargador de páginas (HTTP/navegador).</param>
    /// <param name="logger">Logger.</param>
    protected SourceScraperBase(PageLoader loader, ILogger logger)
    {
        Loader = loader;
        Logger = logger;
    }

    /// <summary>Gets el cargador de páginas.</summary>
    protected PageLoader Loader { get; }

    /// <summary>Gets el logger.</summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Construye la URL de búsqueda a partir de la plantilla.
    /// </summary>
    /// <param name="template">Plantilla con {base}, {query} y {page}.</param>
    /// <param name="baseUrl">URL base.</param>
    /// <param name="query">Texto buscado.</param>
    /// <param name="page">Número de página.</param>
    /// <returns>URL absoluta o null.</returns>
    protected static string? BuildUrl(string? template, string baseUrl, string query, int page)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var url = template
            .Replace("{base}", baseUrl.TrimEnd('/'), StringComparison.Ordinal)
            .Replace("{query}", Uri.EscapeDataString(query), StringComparison.Ordinal)
            .Replace("{queryRaw}", query, StringComparison.Ordinal)
            .Replace("{page}", page.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        return TextUtil.ToAbsolute(baseUrl, url);
    }

    /// <summary>
    /// Construye el cuerpo de un POST reemplazando la consulta.
    /// </summary>
    /// <param name="template">Plantilla del cuerpo.</param>
    /// <param name="query">Texto buscado.</param>
    /// <param name="page">Número de página.</param>
    /// <returns>Cuerpo codificado.</returns>
    protected static string BuildBody(string? template, string query, int page)
    {
        var body = string.IsNullOrWhiteSpace(template) ? "q={query}" : template;
        return body
            .Replace("{query}", Uri.EscapeDataString(query), StringComparison.Ordinal)
            .Replace("{page}", page.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>
    /// Pausa entre peticiones para no saturar la web.
    /// </summary>
    /// <param name="source">Fuente.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Task.</returns>
    protected static Task DelayAsync(TorrentSourceConfig source, CancellationToken cancellationToken) =>
        source.DelayMs <= 0
            ? Task.CompletedTask
            : Task.Delay(Math.Clamp(source.DelayMs, 0, 5000), cancellationToken);

    /// <summary>
    /// Normaliza un enlace magnet o URL de .torrent encontrado en un atributo.
    /// </summary>
    /// <param name="value">Valor leído.</param>
    /// <param name="pageUrl">URL de la página para resolver relativos.</param>
    /// <param name="magnet">Magnet resultante.</param>
    /// <param name="torrentUrl">URL de .torrent resultante.</param>
    public static void ClassifyLink(
        string? value,
        string pageUrl,
        ref string? magnet,
        ref string? torrentUrl)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            magnet ??= trimmed;
            return;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^[a-fA-F0-9]{40}$")
            || System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^[A-Z2-7]{32}$"))
        {
            var hash = MagnetExtractor.GetInfoHash("urn:btih:" + trimmed) ?? trimmed.ToLowerInvariant();
            magnet ??= MagnetExtractor.BuildMagnet(hash, null);
            return;
        }

        if (trimmed.Contains("urn:btih:", StringComparison.OrdinalIgnoreCase))
        {
            magnet ??= "magnet:?" + trimmed[trimmed.IndexOf("urn:btih:", StringComparison.OrdinalIgnoreCase)..];
            return;
        }

        var absolute = TextUtil.ToAbsolute(pageUrl, trimmed);
        if (absolute is null)
        {
            return;
        }

        if (MagnetExtractor.LooksLikeTorrentDownload(absolute))
        {
            torrentUrl ??= absolute;
        }
    }

    /// <summary>
    /// Devuelve el primer valor con contenido (para heredar selectores).
    /// </summary>
    /// <param name="values">Valores en orden de preferencia.</param>
    /// <returns>El primero no vacío o null.</returns>
    protected static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// Aplica los metadatos deducidos del nombre del release.
    /// </summary>
    /// <param name="result">Resultado.</param>
    public static void EnrichMetadata(TorrentResult result)
    {
        if (string.IsNullOrWhiteSpace(result.ReleaseName))
        {
            result.ReleaseName = result.Title;
        }

        ReleaseMetadataParser.Enrich(result);

        if (string.IsNullOrWhiteSpace(result.Id))
        {
            result.Id = !string.IsNullOrWhiteSpace(result.InfoHash)
                ? result.InfoHash!
                : !string.IsNullOrWhiteSpace(result.DetailUrl)
                    ? result.DetailUrl!
                    : TextUtil.Slugify($"{result.Title}-{result.SizeText}-{result.SourceName}");
        }
    }
}
