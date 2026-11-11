using Jellyfin.Plugin.Torrent.DownloadClients;
using Jellyfin.Plugin.Torrent.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Services;

/// <summary>
/// Vigila las descargas del cliente y, cuando un torrent termina, lo quita del
/// cliente <b>sin borrar los archivos</b> para que Jellyfin (o Media Organizer)
/// pueda moverlos. Después avisa a Media Organizer si está instalado.
/// </summary>
public sealed class CompletedTorrentCleaner : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(45);

    private readonly DownloadClientService _clients;
    private readonly MediaOrganizerBridge _organizer;
    private readonly ILogger<CompletedTorrentCleaner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompletedTorrentCleaner"/> class.
    /// </summary>
    /// <param name="clients">Servicio de clientes de descarga.</param>
    /// <param name="organizer">Puente con Media Organizer.</param>
    /// <param name="logger">Logger.</param>
    public CompletedTorrentCleaner(
        DownloadClientService clients,
        MediaOrganizerBridge organizer,
        ILogger<CompletedTorrentCleaner> logger)
    {
        _clients = clients;
        _organizer = organizer;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation(
            "Torrent: liberación automática de descargas {State}; Media Organizer {Organizer}.",
            Plugin.Instance?.Configuration.DownloadClient.RemoveCompleted == true ? "activada" : "desactivada",
            _organizer.IsAvailable ? "detectado" : "no instalado");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Torrent: error revisando descargas completadas.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Revisa las descargas completadas y libera las que correspondan.
    /// </summary>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Número de torrents liberados.</returns>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration.DownloadClient;
        if (config is null || !config.RemoveCompleted)
        {
            return 0;
        }

        var torrents = await _clients.GetTorrentsAsync(cancellationToken).ConfigureAwait(false);
        if (torrents.Count == 0)
        {
            return 0;
        }

        var delay = TimeSpan.FromMinutes(Math.Clamp(config.RemoveCompletedAfterMinutes, 0, 24 * 60));
        var released = 0;

        foreach (var torrent in torrents.Where(t => t.Completed && IsOurs(t, config)))
        {
            if (delay > TimeSpan.Zero)
            {
                var since = torrent.CompletedOn ?? DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - since < delay)
                {
                    continue;
                }
            }

            var removed = await _clients
                .RemoveAsync(torrent.Hash, deleteFiles: false, cancellationToken)
                .ConfigureAwait(false);

            if (!removed)
            {
                _logger.LogWarning(
                    "Torrent: no se pudo quitar «{Name}» del cliente.",
                    torrent.Name);
                continue;
            }

            released++;
            _logger.LogInformation(
                "Torrent: «{Name}» completado y quitado del cliente (archivos conservados en {Path}).",
                torrent.Name,
                torrent.ContentPath ?? torrent.SavePath ?? "?");

            var path = torrent.ContentPath ?? torrent.SavePath;
            var moved = await Task.Run(() => _organizer.Notify(path), CancellationToken.None)
                .ConfigureAwait(false);

            if (moved == 0 && _organizer.IsAvailable)
            {
                _logger.LogDebug("Torrent: Media Organizer no movió nada todavía para «{Name}».", torrent.Name);
            }
        }

        return released;
    }

    /// <summary>
    /// Solo se tocan los torrents del plugin (categoría o carpeta configuradas),
    /// para no interferir con otras descargas del cliente.
    /// </summary>
    private static bool IsOurs(ClientTorrent torrent, DownloadClientConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Category))
        {
            return string.Equals(torrent.Category, config.Category, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(config.SavePath))
        {
            var savePath = config.SavePath.TrimEnd('\\', '/');
            return (torrent.SavePath?.StartsWith(savePath, StringComparison.OrdinalIgnoreCase) ?? false)
                || (torrent.ContentPath?.StartsWith(savePath, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        return true;
    }
}
