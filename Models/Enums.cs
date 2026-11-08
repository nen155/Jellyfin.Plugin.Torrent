namespace Jellyfin.Plugin.Torrent.Models;

/// <summary>
/// Cómo se extraen los resultados de una fuente.
/// </summary>
public enum SourceKind
{
    /// <summary>Búsqueda sobre HTML con selectores CSS (la mayoría de webs).</summary>
    Html,

    /// <summary>Búsqueda sobre una respuesta JSON (APIs de las webs).</summary>
    Json,

    /// <summary>Fuente Torznab (Jackett, Prowlarr y trackers con API compatible).</summary>
    Torznab
}

/// <summary>
/// Método HTTP de la búsqueda.
/// </summary>
public enum SearchHttpMethod
{
    /// <summary>GET con la consulta en la URL.</summary>
    Get,

    /// <summary>POST con la consulta en el cuerpo del formulario.</summary>
    Post
}

/// <summary>
/// Motor con el que se descarga una página.
/// </summary>
public enum FetchMode
{
    /// <summary>Petición HTTP simple (rápida, sin JavaScript).</summary>
    Http,

    /// <summary>Navegador real: ejecuta el JavaScript de la web.</summary>
    Browser,

    /// <summary>HTTP primero y, si la web bloquea o exige JavaScript, navegador.</summary>
    Auto,

    /// <summary>Hereda el modo de la búsqueda (solo para la ficha de detalle).</summary>
    Inherit
}

/// <summary>
/// Navegador/motor de navegación disponible.
/// </summary>
public enum BrowserMode
{
    /// <summary>Desactivado.</summary>
    None,

    /// <summary>Chrome/Edge en modo headless controlado por CDP.</summary>
    Cdp,

    /// <summary>Servicio FlareSolverr (resuelve retos de Cloudflare).</summary>
    FlareSolverr,

    /// <summary>CDP y, si falla, FlareSolverr.</summary>
    Auto
}

/// <summary>
/// Cliente de torrents al que se envían los enlaces.
/// </summary>
public enum DownloadClientKind
{
    /// <summary>Prueba el cliente configurado y, si no responde, devuelve el enlace magnet.</summary>
    Auto,

    /// <summary>qBittorrent WebUI (API v2).</summary>
    QBittorrent,

    /// <summary>Transmission RPC.</summary>
    Transmission,

    /// <summary>Deluge Web (JSON-RPC).</summary>
    Deluge,

    /// <summary>No se contacta con ningún cliente: la interfaz abre el enlace magnet.</summary>
    MagnetOnly
}

/// <summary>
/// De dónde se obtiene el dominio vigente de una fuente que rota dominios.
/// </summary>
public enum DomainProvider
{
    /// <summary>Sin resolución automática: se usa la URL base configurada.</summary>
    None,

    /// <summary>Se lee una página HTTP y se busca el dominio con una expresión regular.</summary>
    HttpPage,

    /// <summary>Se leen los últimos mensajes de un canal de Discord con un bot.</summary>
    Discord
}

/// <summary>
/// Tipo de contenido deducido de un resultado.
/// </summary>
public enum MediaKind
{
    /// <summary>No se ha podido deducir.</summary>
    Unknown,

    /// <summary>Película.</summary>
    Movie,

    /// <summary>Serie o episodio.</summary>
    Series
}
