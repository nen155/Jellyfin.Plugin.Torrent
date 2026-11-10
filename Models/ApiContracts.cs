namespace Jellyfin.Plugin.Torrent.Models;

/// <summary>
/// Petición para enviar un enlace al cliente de torrents.
/// </summary>
public class AddTorrentRequest
{
    /// <summary>Gets or sets el enlace magnet.</summary>
    public string? Magnet { get; set; }

    /// <summary>Gets or sets la URL del .torrent.</summary>
    public string? TorrentUrl { get; set; }

    /// <summary>Gets or sets el título del release.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets el referer con el que descargar el .torrent.</summary>
    public string? Referer { get; set; }

    /// <summary>
    /// Gets or sets el id del resultado de la búsqueda: permite recuperar la
    /// sesión del navegador (cookies) con la que se descubrió el enlace.
    /// </summary>
    public string? ResultId { get; set; }
}

/// <summary>
/// Petición para analizar una web y proponer su configuración.
/// </summary>
public class DiscoverRequest
{
    /// <summary>Gets or sets la URL de la web.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets el texto de prueba.</summary>
    public string Query { get; set; } = "matrix";
}

/// <summary>
/// Petición para probar el navegador con una URL.
/// </summary>
public class BrowserTestRequest
{
    /// <summary>Gets or sets la URL a abrir.</summary>
    public string Url { get; set; } = "https://example.com";

    /// <summary>Gets or sets el JavaScript a ejecutar.</summary>
    public string? Script { get; set; }

    /// <summary>Gets or sets el selector a esperar.</summary>
    public string? WaitSelector { get; set; }

    /// <summary>Gets or sets la espera extra (ms).</summary>
    public int WaitMs { get; set; } = 1500;
}

/// <summary>
/// Petición para probar una fuente.
/// </summary>
public class SourceTestRequest
{
    /// <summary>Gets or sets el id de la fuente.</summary>
    public Guid SourceId { get; set; }

    /// <summary>Gets or sets el texto de prueba.</summary>
    public string Query { get; set; } = "matrix";

    /// <summary>Gets or sets a value indicating whether se visitan fichas de detalle.</summary>
    public bool FetchDetails { get; set; } = true;
}

/// <summary>
/// Petición para resolver el dominio de una fuente.
/// </summary>
public class DomainResolveRequest
{
    /// <summary>Gets or sets el id de la fuente.</summary>
    public Guid SourceId { get; set; }
}

/// <summary>
/// Fuente tal y como se expone a la interfaz.
/// </summary>
public class PublicSource
{
    /// <summary>Gets or sets el id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets el nombre.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether está activa.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets el tipo de extractor.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets la URL base.</summary>
    public string BaseUrl { get; set; } = string.Empty;
}

/// <summary>
/// Configuración expuesta a la interfaz de búsqueda.
/// </summary>
public class PublicConfig
{
    /// <summary>Gets or sets a value indicating whether el plugin está activo.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets las fuentes.</summary>
    public List<PublicSource> Sources { get; set; } = new();

    /// <summary>Gets or sets el tipo de cliente configurado.</summary>
    public string ClientKind { get; set; } = string.Empty;

    /// <summary>Gets or sets el nombre del cliente configurado.</summary>
    public string ClientName { get; set; } = string.Empty;

    /// <summary>Gets or sets el mínimo de semillas por defecto.</summary>
    public int MinSeeds { get; set; }

    /// <summary>Gets or sets a value indicating whether se proxifican las carátulas.</summary>
    public bool ProxyImages { get; set; }

    /// <summary>Gets or sets los minutos de caché de búsqueda.</summary>
    public int CacheMinutes { get; set; }

    /// <summary>Gets or sets el límite de fichas de detalle por búsqueda.</summary>
    public int DetailFetchLimit { get; set; }

    /// <summary>Gets or sets a value indicating whether hay sugerencias del día.</summary>
    public bool DailySuggestionsEnabled { get; set; }

    /// <summary>Gets or sets cuántas sugerencias se muestran por tipo.</summary>
    public int DailySuggestionsCount { get; set; }

    /// <summary>Gets or sets a value indicating whether se añade la fila de sugerencias al inicio.</summary>
    public bool DailySuggestionsHomeRow { get; set; }

    /// <summary>Gets or sets a value indicating whether se añade el botón flotante.</summary>
    public bool DailySuggestionsWebButton { get; set; }

    /// <summary>Gets or sets la versión del plugin.</summary>
    public string Version { get; set; } = string.Empty;
}
