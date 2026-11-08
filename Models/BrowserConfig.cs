namespace Jellyfin.Plugin.Torrent.Models;

/// <summary>
/// Configuración del navegador headless que usa el plugin para ejecutar el
/// JavaScript de las webs (búsquedas dinámicas, retos de Cloudflare,
/// proof-of-work del botón de descarga…).
/// </summary>
public class BrowserConfig
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BrowserConfig"/> class.
    /// </summary>
    public BrowserConfig()
    {
        Mode = BrowserMode.Auto;
        ExecutablePath = string.Empty;
        FlareSolverrUrl = string.Empty;
        TimeoutSeconds = 45;
        Headless = true;
        IdleSeconds = 300;
        UserAgent = string.Empty;
    }

    /// <summary>Gets or sets el motor de navegación.</summary>
    public BrowserMode Mode { get; set; }

    /// <summary>
    /// Gets or sets la ruta del ejecutable de Chrome/Edge/Chromium. Vacío =
    /// se busca automáticamente.
    /// </summary>
    public string ExecutablePath { get; set; }

    /// <summary>
    /// Gets or sets la URL del servicio FlareSolverr
    /// (p. ej. <c>http://localhost:8191</c>).
    /// </summary>
    public string FlareSolverrUrl { get; set; }

    /// <summary>Gets or sets el tiempo máximo de navegación (segundos).</summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>Gets or sets a value indicating whether el navegador es headless.</summary>
    public bool Headless { get; set; }

    /// <summary>
    /// Gets or sets los segundos de inactividad tras los que se cierra el
    /// navegador para liberar memoria.
    /// </summary>
    public int IdleSeconds { get; set; }

    /// <summary>
    /// Gets or sets el User-Agent que se aplica al navegador. Vacío = el del
    /// propio Chrome/Edge.
    /// </summary>
    public string UserAgent { get; set; }
}
