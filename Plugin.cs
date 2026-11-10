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
    private int _latestDefaultsApplied;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Rutas de la aplicación.</param>
    /// <param name="xmlSerializer">Serializador XML de configuración.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // XmlSerializer ejecuta el constructor y luego AÑADE los elementos a la
        // lista, así que las fuentes por defecto se crean aquí, solo cuando no
        // hay ninguna guardada.
        if (Configuration.Sources.Count == 0)
        {
            Configuration.Sources.AddRange(PluginConfiguration.CreateDefaultSources());
            SaveConfiguration();
        }
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Rellena las URLs de novedades de las fuentes incluidas (WatchSoMuch y
    /// DonTorrent) cuando todavía no están configuradas, para que las
    /// sugerencias del día funcionen también al actualizar el plugin.
    /// </summary>
    public void EnsureLatestDefaults()
    {
        if (Volatile.Read(ref _latestDefaultsApplied) == 1 || Configuration.Sources.Count == 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _latestDefaultsApplied, 1) == 1)
        {
            return;
        }

        var templates = Services.SourceTemplates.All().ToDictionary(t => t.Key, StringComparer.Ordinal);
        var known = new Dictionary<Guid, string>
        {
            [PluginConfiguration.WatchSoMuchSourceId] = "watchsomuch",
            [PluginConfiguration.DonTorrentSourceId] = "dontorrent"
        };

        var changed = false;
        foreach (var source in Configuration.Sources)
        {
            if (!known.TryGetValue(source.Id, out var key)
                || !templates.TryGetValue(key, out var template)
                || !IsLatestEmpty(source))
            {
                continue;
            }

            source.LatestMoviesUrl = template.Source.LatestMoviesUrl;
            source.LatestSeriesUrl = template.Source.LatestSeriesUrl;
            source.LatestRowSelector = template.Source.LatestRowSelector;
            source.LatestTitleSelector = template.Source.LatestTitleSelector;
            source.LatestLinkSelector = template.Source.LatestLinkSelector;
            source.LatestPosterSelector = template.Source.LatestPosterSelector;
            changed = true;
        }

        if (changed)
        {
            SaveConfiguration();
        }
    }

    private static bool IsLatestEmpty(Models.TorrentSourceConfig source) =>
        string.IsNullOrWhiteSpace(source.LatestMoviesUrl)
        && string.IsNullOrWhiteSpace(source.LatestSeriesUrl)
        && string.IsNullOrWhiteSpace(source.LatestRowSelector)
        && string.IsNullOrWhiteSpace(source.LatestTitleSelector)
        && string.IsNullOrWhiteSpace(source.LatestLinkSelector)
        && string.IsNullOrWhiteSpace(source.LatestPosterSelector);

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
