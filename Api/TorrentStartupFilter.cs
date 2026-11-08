using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.Torrent.Api;

/// <summary>
/// Inserta <see cref="TorrentMiddleware"/> al inicio del pipeline HTTP de
/// Jellyfin mediante <see cref="IStartupFilter"/>.
/// </summary>
public class TorrentStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<TorrentMiddleware>();
            next(app);
        };
    }
}
