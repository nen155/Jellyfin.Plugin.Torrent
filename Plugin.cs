using Jellyfin.Plugin.Torrent.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Torrent;

/// <summary>
/// Plugin principal: meta-buscador de torrents. Hace de proxy entre las webs
/// que configure el usuario y el cliente de torrents local, extrayendo los
/// enlaces (magnet o .torrent) y toda la información disponible de cada
/// resultado.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Rutas de la aplicación.</param>
    /// <param name="xmlSerializer">Serializador XML de configuración.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Torrent Search";

    /// <inheritdoc />
    public override string Description =>
        "Buscador de torrents en las webs que configures, con información "
        + "detallada de cada resultado y envío directo al cliente de descargas.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("5c1b8a63-7e42-4b0f-9d61-3a8f2c4e9b17");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "TorrentSearch",
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.searchPage.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "search"
            },
            new PluginPageInfo
            {
                Name = "TorrentSearchConfig",
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                EnableInMainMenu = false
            }
        };
    }
}
