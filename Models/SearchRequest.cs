namespace Jellyfin.Plugin.Torrent.Models;

/// <summary>
/// Petición de búsqueda enviada por la interfaz.
/// </summary>
public class SearchRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SearchRequest"/> class.
    /// </summary>
    public SearchRequest()
    {
        Query = string.Empty;
        SourceIds = new List<Guid>();
        Languages = new List<string>();
        MediaType = "all";
        Sort = "seeds";
        Quality = string.Empty;
    }

    /// <summary>Gets or sets el texto buscado.</summary>
    public string Query { get; set; }

    /// <summary>Gets or sets las fuentes a consultar (vacío = todas las activas).</summary>
    public List<Guid> SourceIds { get; set; }

    /// <summary>Gets or sets el tipo: <c>all</c>, <c>movie</c> o <c>series</c>.</summary>
    public string MediaType { get; set; }

    /// <summary>Gets or sets los idiomas deseados (códigos: es, en, multi…).</summary>
    public List<string> Languages { get; set; }

    /// <summary>Gets or sets el filtro de calidad (2160p, 1080p, 720p…).</summary>
    public string Quality { get; set; }

    /// <summary>Gets or sets el mínimo de semillas.</summary>
    public int MinSeeds { get; set; }

    /// <summary>Gets or sets el criterio de orden: seeds, size, date, relevance.</summary>
    public string Sort { get; set; }
}

/// <summary>
/// Resultado del proceso de una fuente concreta.
/// </summary>
public class SourceOutcome
{
    /// <summary>Gets or sets el id de la fuente.</summary>
    public Guid SourceId { get; set; }

    /// <summary>Gets or sets el nombre de la fuente.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets el número de resultados devueltos.</summary>
    public int Count { get; set; }

    /// <summary>Gets or sets el mensaje de error, si lo hubo.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets la duración de la consulta en milisegundos.</summary>
    public long DurationMs { get; set; }

    /// <summary>Gets or sets la URL base efectiva (tras resolver el dominio).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Gets or sets información de diagnóstico para ajustar selectores.</summary>
    public string? Diagnostic { get; set; }

    /// <summary>Gets or sets el motor usado (http, cdp, flaresolverr).</summary>
    public string? Engine { get; set; }
}

/// <summary>
/// Respuesta agregada de una búsqueda.
/// </summary>
public class SearchResponse
{
    /// <summary>Gets or sets el texto buscado.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>Gets or sets los resultados ordenados y filtrados.</summary>
    public List<TorrentResult> Results { get; set; } = new();

    /// <summary>Gets or sets el estado de cada fuente consultada.</summary>
    public List<SourceOutcome> Sources { get; set; } = new();

    /// <summary>Gets or sets el total de resultados antes de filtrar.</summary>
    public int TotalBeforeFilters { get; set; }

    /// <summary>Gets or sets los milisegundos totales de la búsqueda.</summary>
    public long DurationMs { get; set; }

    /// <summary>Gets or sets a value indicating whether la respuesta vino de la caché.</summary>
    public bool Cached { get; set; }
}
