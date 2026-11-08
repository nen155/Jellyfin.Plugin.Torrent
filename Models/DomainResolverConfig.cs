namespace Jellyfin.Plugin.Torrent.Models;

/// <summary>
/// Resolución automática del dominio de una fuente que cambia de dirección.
/// </summary>
public class DomainResolverConfig
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DomainResolverConfig"/> class.
    /// </summary>
    public DomainResolverConfig()
    {
        Provider = DomainProvider.None;
        Url = string.Empty;
        Regex = string.Empty;
        DiscordBotToken = string.Empty;
        DiscordChannelId = string.Empty;
        CacheMinutes = 60;
        AutoApply = true;
    }

    /// <summary>Gets or sets de dónde se obtiene el dominio.</summary>
    public DomainProvider Provider { get; set; }

    /// <summary>
    /// Gets or sets la página que publica el dominio vigente
    /// (usado con <see cref="DomainProvider.HttpPage"/>).
    /// </summary>
    public string Url { get; set; }

    /// <summary>
    /// Gets or sets la expresión regular con la que se localiza el dominio
    /// (por defecto cualquier URL del dominio base configurado).
    /// </summary>
    public string Regex { get; set; }

    /// <summary>Gets or sets el token del bot de Discord.</summary>
    public string DiscordBotToken { get; set; }

    /// <summary>Gets or sets el ID del canal de Discord donde se publica el dominio.</summary>
    public string DiscordChannelId { get; set; }

    /// <summary>Gets or sets los minutos que se cachea el dominio resuelto.</summary>
    public int CacheMinutes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether el dominio resuelto se aplica
    /// automáticamente al buscar.
    /// </summary>
    public bool AutoApply { get; set; }
}
