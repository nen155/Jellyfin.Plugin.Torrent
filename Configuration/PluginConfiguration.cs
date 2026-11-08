using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Torrent.Configuration;

/// <summary>
/// Configuración del plugin Torrent Search.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        Enabled = true;
        UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        SearchTimeoutSeconds = 25;
        MaxResultsPerSource = 60;
        MinSeeds = 0;
        ProxyImages = true;
        CacheMinutes = 10;
        DetailFetchLimit = 12;
        BlockedKeywords = new List<string>();
        Sources = new List<Models.TorrentSourceConfig>();
        DownloadClient = new Models.DownloadClientConfig();
        Browser = new Models.BrowserConfig();
    }

    /// <summary>Gets or sets a value indicating whether el plugin está activo.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets el User-Agent usado en todas las peticiones.</summary>
    public string UserAgent { get; set; }

    /// <summary>Gets or sets el tiempo máximo por petición (segundos).</summary>
    public int SearchTimeoutSeconds { get; set; }

    /// <summary>Gets or sets el máximo de resultados que se guardan por fuente.</summary>
    public int MaxResultsPerSource { get; set; }

    /// <summary>Gets or sets el mínimo de semillas para mostrar un resultado.</summary>
    public int MinSeeds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether las carátulas se sirven a
    /// través del plugin (evita bloqueos por referer y contenido mixto).
    /// </summary>
    public bool ProxyImages { get; set; }

    /// <summary>Gets or sets los minutos que se cachea una búsqueda.</summary>
    public int CacheMinutes { get; set; }

    /// <summary>
    /// Gets or sets cuántas fichas de detalle se visitan como máximo por
    /// búsqueda para completar la información (0 = todas).
    /// </summary>
    public int DetailFetchLimit { get; set; }

    /// <summary>Gets or sets palabras que descartan un resultado.</summary>
    public List<string> BlockedKeywords { get; set; }

    /// <summary>Gets or sets las fuentes configuradas.</summary>
    public List<Models.TorrentSourceConfig> Sources { get; set; }

    /// <summary>Gets or sets la configuración del cliente de torrents.</summary>
    public Models.DownloadClientConfig DownloadClient { get; set; }

    /// <summary>Gets or sets la configuración del navegador headless.</summary>
    public Models.BrowserConfig Browser { get; set; }
}
