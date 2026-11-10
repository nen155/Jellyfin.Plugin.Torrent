using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Torrent.Models;

/// <summary>
/// Un estreno destacado en la lista de novedades de una fuente. Se usa para la
/// previsualización de "sugerencias del día": no lleva enlaces de descarga,
/// solo lo necesario para mostrarlo y lanzar su búsqueda.
/// </summary>
public class DailySuggestion
{
    /// <summary>Gets or sets el id de la fuente de origen.</summary>
    public Guid SourceId { get; set; }

    /// <summary>Gets or sets el nombre de la fuente de origen.</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>Gets or sets el título del contenido.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets el nombre completo del release, si la web lo publica.</summary>
    public string? ReleaseName { get; set; }

    /// <summary>Gets or sets la URL de la ficha en la web.</summary>
    public string? DetailUrl { get; set; }

    /// <summary>Gets or sets la URL de la carátula.</summary>
    public string? PosterUrl { get; set; }

    /// <summary>Gets or sets la categoría o calidad según la web.</summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets la resolución deducida (2160p, 1080p…).</summary>
    public string? Resolution { get; set; }

    /// <summary>Gets or sets el texto de episodio (S01E02, Temporada 3…).</summary>
    public string? EpisodeText { get; set; }

    /// <summary>Gets or sets el año del contenido.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets la fecha tal y como la muestra la web.</summary>
    public string? PublishedText { get; set; }

    /// <summary>Gets or sets el tipo de contenido (película o serie).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MediaKind Kind { get; set; }
}

/// <summary>
/// Sugerencias del día separadas por tipo, con el estado de cada fuente.
/// </summary>
public class DailySuggestionsResponse
{
    /// <summary>Gets or sets a value indicating whether la función está activa.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets los estrenos de películas.</summary>
    public List<DailySuggestion> Movies { get; set; } = new();

    /// <summary>Gets or sets los estrenos de series.</summary>
    public List<DailySuggestion> Series { get; set; } = new();

    /// <summary>Gets or sets el estado de cada fuente consultada.</summary>
    public List<SourceOutcome> Sources { get; set; } = new();

    /// <summary>Gets or sets el momento en el que se generó la lista.</summary>
    public DateTimeOffset GeneratedAt { get; set; }

    /// <summary>Gets or sets los minutos que se cachea la lista.</summary>
    public int CacheMinutes { get; set; }

    /// <summary>Gets or sets a value indicating whether la respuesta vino de la caché.</summary>
    public bool Cached { get; set; }
}
