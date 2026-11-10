using Jellyfin.Plugin.Torrent.Models;

namespace Jellyfin.Plugin.Torrent.Scraping;

/// <summary>
/// Resultado de consultar una fuente.
/// </summary>
public sealed class SourceSearchResult
{
    /// <summary>Gets or sets los resultados encontrados.</summary>
    public List<TorrentResult> Results { get; set; } = new();

    /// <summary>Gets or sets el error que impidió la consulta.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets información de diagnóstico (para ajustar selectores).</summary>
    public string? Diagnostic { get; set; }

    /// <summary>Gets or sets el motor usado para cargar la página (http, cdp…).</summary>
    public string? Engine { get; set; }
}

/// <summary>
/// Extractor de resultados de una fuente.
/// </summary>
public interface ISourceScraper
{
    /// <summary>Gets el tipo de fuente que maneja.</summary>
    SourceKind Kind { get; }

    /// <summary>
    /// Ejecuta la búsqueda en la fuente.
    /// </summary>
    /// <param name="source">Configuración de la fuente.</param>
    /// <param name="baseUrl">URL base efectiva (tras resolver el dominio).</param>
    /// <param name="query">Texto buscado.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Resultados o error.</returns>
    Task<SourceSearchResult> SearchAsync(
        TorrentSourceConfig source,
        string baseUrl,
        string query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lee la lista de novedades (estrenos) de la fuente, sin consulta de
    /// búsqueda. Se usa para las sugerencias del día.
    /// </summary>
    /// <param name="source">Configuración de la fuente.</param>
    /// <param name="baseUrl">URL base efectiva (tras resolver el dominio).</param>
    /// <param name="urlTemplate">Plantilla de la URL de novedades ({base}, {page}).</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Resultados o error.</returns>
    Task<SourceSearchResult> BrowseAsync(
        TorrentSourceConfig source,
        string baseUrl,
        string urlTemplate,
        CancellationToken cancellationToken);

    /// <summary>
    /// Completa un resultado con los datos de su ficha de detalle.
    /// </summary>
    /// <param name="source">Configuración de la fuente.</param>
    /// <param name="baseUrl">URL base efectiva.</param>
    /// <param name="result">Resultado a completar.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>El resultado completado más los releases adicionales encontrados.</returns>
    Task<IReadOnlyList<TorrentResult>> EnrichAsync(
        TorrentSourceConfig source,
        string baseUrl,
        TorrentResult result,
        CancellationToken cancellationToken);
}
