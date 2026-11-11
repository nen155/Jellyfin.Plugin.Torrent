using System.Collections;
using System.Reflection;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Services;

/// <summary>
/// Puente opcional con el plugin <c>Media Organizer</c>: cuando el plugin de
/// torrents libera un archivo (quita el torrent del cliente sin borrarlo),
/// avisa al organizador para que lo mueva a la biblioteca.
///
/// Se resuelve por reflexión para no acoplar los dos plugins: si Media
/// Organizer no está instalado, no se hace nada.
/// </summary>
public sealed class MediaOrganizerBridge
{
    private const string EngineTypeName = "Jellyfin.Plugin.MediaOrganizer.Services.OrganizerEngine";

    private static readonly Guid MediaOrganizerId = Guid.Parse("9eb40cfc-85d4-4445-88de-21bb90c60a7c");

    private readonly IServiceProvider _services;
    private readonly IPluginManager _pluginManager;
    private readonly ILogger<MediaOrganizerBridge> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaOrganizerBridge"/> class.
    /// </summary>
    /// <param name="services">Contenedor de servicios.</param>
    /// <param name="pluginManager">Gestor de plugins de Jellyfin.</param>
    /// <param name="logger">Logger.</param>
    public MediaOrganizerBridge(
        IServiceProvider services,
        IPluginManager pluginManager,
        ILogger<MediaOrganizerBridge> logger)
    {
        _services = services;
        _pluginManager = pluginManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether Media Organizer está instalado y activo.
    /// </summary>
    public bool IsAvailable => FindMediaOrganizer() is not null;

    /// <summary>
    /// Pide a Media Organizer que organice las carpetas que contengan la ruta
    /// indicada (o todas si la ruta es nula).
    /// </summary>
    /// <param name="path">Archivo o carpeta recién liberado.</param>
    /// <returns>Número de vídeos organizados (0 si no hay nada que hacer).</returns>
    public int Notify(string? path)
    {
        var plugin = FindMediaOrganizer();
        if (plugin is null)
        {
            _logger.LogDebug("Torrent: Media Organizer no está instalado; no se le avisa.");
            return 0;
        }

        // El ensamblado del otro plugin vive en su propio contexto de carga: se
        // obtiene desde la instancia, no con Type.GetType.
        var engineType = plugin.GetType().Assembly.GetType(EngineTypeName, throwOnError: false);
        if (engineType is null)
        {
            return 0;
        }

        object? engine;
        try
        {
            engine = _services.GetService(engineType);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Torrent: no se pudo resolver OrganizerEngine.");
            return 0;
        }

        if (engine is null)
        {
            return 0;
        }

        var watchFolders = GetWatchFolders();
        if (watchFolders.Count == 0)
        {
            return 0;
        }

        var resolveRoots = engineType.GetMethod("ResolveRoots", BindingFlags.Public | BindingFlags.Instance);
        var organizeAll = engineType.GetMethod("OrganizeAll", BindingFlags.Public | BindingFlags.Instance);
        if (organizeAll is null)
        {
            return 0;
        }

        var total = 0;
        foreach (var watch in watchFolders)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && resolveRoots is not null)
                {
                    var roots = resolveRoots.Invoke(engine, new[] { watch }) as IEnumerable;
                    var matches = roots is not null
                        && roots.Cast<object?>()
                            .Select(r => r?.ToString())
                            .Where(r => !string.IsNullOrWhiteSpace(r))
                            .Any(r => IsUnder(path!, r!));

                    if (!matches)
                    {
                        continue;
                    }
                }

                var moved = organizeAll.Invoke(engine, new[] { watch });
                if (moved is int count && count > 0)
                {
                    total += count;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Torrent: error avisando a Media Organizer.");
            }
        }

        if (total > 0)
        {
            _logger.LogInformation(
                "Torrent: Media Organizer ha organizado {Count} archivo(s) tras liberar la descarga.",
                total);
        }
        else
        {
            _logger.LogDebug("Torrent: Media Organizer no tenía nada que mover para {Path}.", path);
        }

        return total;
    }

    private IPlugin? FindMediaOrganizer()
    {
        try
        {
            return _pluginManager.GetPlugin(MediaOrganizerId)?.Instance;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Torrent: no se pudo consultar la lista de plugins.");
            return null;
        }
    }

    private List<object> GetWatchFolders()
    {
        var folders = new List<object>();
        try
        {
            var instance = FindMediaOrganizer();
            if (instance is null)
            {
                return folders;
            }

            var configuration = instance.GetType()
                .GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(instance);
            if (configuration is null)
            {
                return folders;
            }

            var list = configuration.GetType()
                .GetProperty("WatchFolders", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(configuration) as IEnumerable;

            if (list is not null)
            {
                folders.AddRange(list.Cast<object?>().Where(f => f is not null)!);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Torrent: no se pudo leer la configuración de Media Organizer.");
        }

        return folders;
    }

    private static bool IsUnder(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd('\\', '/');
        var normalizedRoot = Path.GetFullPath(root).TrimEnd('\\', '/');

        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedRoot.StartsWith(normalizedPath, StringComparison.OrdinalIgnoreCase);
    }
}
