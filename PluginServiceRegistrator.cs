using Jellyfin.Plugin.Torrent.Api;
using Jellyfin.Plugin.Torrent.Browser;
using Jellyfin.Plugin.Torrent.DownloadClients;
using Jellyfin.Plugin.Torrent.Scraping;
using Jellyfin.Plugin.Torrent.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Torrent;

/// <summary>
/// Registra los servicios del plugin en el contenedor DI de Jellyfin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<HttpFetcher>();
        serviceCollection.AddSingleton<FlareSolverrClient>();
        serviceCollection.AddSingleton<PageLoader>();
        serviceCollection.AddSingleton<SessionStore>();
        serviceCollection.AddSingleton<ISourceScraper, HtmlSourceScraper>();
        serviceCollection.AddSingleton<ISourceScraper, JsonSourceScraper>();
        serviceCollection.AddSingleton<ISourceScraper, TorznabSourceScraper>();
        serviceCollection.AddSingleton<DomainResolverService>();
        serviceCollection.AddSingleton<SourceDiscoveryService>();
        serviceCollection.AddSingleton<TorrentSearchService>();
        serviceCollection.AddSingleton<PosterLookupService>();
        serviceCollection.AddSingleton<DailySuggestionsService>();
        serviceCollection.AddSingleton<ImageProxyService>();
        serviceCollection.AddSingleton<TorrentFileService>();

        serviceCollection.AddSingleton<IDownloadClient, QBittorrentClient>();
        serviceCollection.AddSingleton<IDownloadClient, TransmissionClient>();
        serviceCollection.AddSingleton<IDownloadClient, DelugeClient>();
        serviceCollection.AddSingleton<DownloadClientService>();

        serviceCollection.AddTransient<IStartupFilter, TorrentStartupFilter>();
    }
}
