using System.Collections.Concurrent;
using System.Diagnostics;
using Jellyfin.Plugin.Torrent.Models;
using Jellyfin.Plugin.Torrent.Parsing;
using Jellyfin.Plugin.Torrent.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.DownloadClients;

/// <summary>
/// Fachada de envío: usa el cliente configurado y, en modo automático, prueba
/// los que respondan en el host/puerto indicado. Si ninguno responde intenta
/// abrir el cliente y, si aun así no hay forma, guarda el .torrent en la
/// carpeta configurada.
/// </summary>
public sealed class DownloadClientService
{
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ClientStartWait = TimeSpan.FromSeconds(6);

    private readonly IReadOnlyList<IDownloadClient> _clients;
    private readonly TorrentFileService _torrentFiles;
    private readonly ILogger<DownloadClientService> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _available = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadClientService"/> class.
    /// </summary>
    /// <param name="clients">Clientes disponibles.</param>
    /// <param name="torrentFiles">Servicio para descargar .torrent protegidos.</param>
    /// <param name="logger">Logger.</param>
    public DownloadClientService(
        IEnumerable<IDownloadClient> clients,
        TorrentFileService torrentFiles,
        ILogger<DownloadClientService> logger)
    {
        _clients = clients.ToList();
        _torrentFiles = torrentFiles;
        _logger = logger;
    }

    /// <summary>
    /// Envía un enlace al cliente configurado.
    /// </summary>
    /// <param name="request">Enlace.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Resultado del envío.</returns>
    public async Task<DownloadResult> SendAsync(
        DownloadRequest request,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration.DownloadClient ?? new DownloadClientConfig();

        if (!request.HasLink)
        {
            return new DownloadResult
            {
                Message = "Este resultado no tiene enlace magnet ni archivo .torrent."
            };
        }

        if (config.Kind == DownloadClientKind.MagnetOnly)
        {
            return Fallback(request, "Abre el enlace con tu programa de torrents.");
        }

        // Si la web protege la descarga, el .torrent se descarga aquí con la
        // sesión del navegador y se envía como archivo al cliente.
        var prepareError = await PrepareTorrentAsync(request, cancellationToken).ConfigureAwait(false);
        if (prepareError is not null)
        {
            return prepareError;
        }

        var result = await TrySendAsync(config, request, cancellationToken).ConfigureAwait(false);

        if (!result.Success && config.AutoStart && TryStartClient(config))
        {
            await Task.Delay(ClientStartWait, cancellationToken).ConfigureAwait(false);
            result = await TrySendAsync(config, request, cancellationToken).ConfigureAwait(false);
        }

        if (result.Success)
        {
            return result;
        }

        return await SaveToFolderAsync(request, config, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DownloadResult?> PrepareTorrentAsync(
        DownloadRequest request,
        CancellationToken cancellationToken)
    {
        if (request.TorrentBytes is not null
            || !string.IsNullOrWhiteSpace(request.Magnet)
            || string.IsNullOrWhiteSpace(request.TorrentUrl)
            || string.IsNullOrWhiteSpace(request.Cookie))
        {
            return null;
        }

        request.TorrentBytes = await _torrentFiles
            .GetBytesAsync(request.TorrentUrl, request.Referer, request.Cookie, cancellationToken)
            .ConfigureAwait(false);

        if (request.TorrentBytes is null)
        {
            return new DownloadResult
            {
                Client = "Navegador",
                Message = "No se pudo descargar el .torrent con la sesión de la web.",
                Magnet = request.Magnet,
                TorrentUrl = request.TorrentUrl
            };
        }

        request.FileName = BuildFileName(request.Title);
        return null;
    }

    private async Task<DownloadResult> TrySendAsync(
        DownloadClientConfig config,
        DownloadRequest request,
        CancellationToken cancellationToken)
    {
        if (config.Kind != DownloadClientKind.Auto)
        {
            var client = _clients.FirstOrDefault(c => c.Kind == config.Kind);
            if (client is null)
            {
                return new DownloadResult
                {
                    Message = $"El cliente {config.Kind} no está disponible.",
                    Magnet = request.Magnet,
                    TorrentUrl = request.TorrentUrl
                };
            }

            var result = await client.AddAsync(config, request, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                result.Magnet ??= request.Magnet;
                result.TorrentUrl ??= request.TorrentUrl;
            }

            return result;
        }

        foreach (var client in _clients)
        {
            if (!await IsAvailableAsync(client, config, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var result = await client.AddAsync(config, request, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                return result;
            }

            _logger.LogWarning(
                "Torrent: {Client} respondió pero rechazó el enlace: {Message}",
                client.DisplayName,
                result.Message);
        }

        return new DownloadResult
        {
            Message = "Ningún cliente de torrents respondió en el host configurado.",
            Magnet = request.Magnet,
            TorrentUrl = request.TorrentUrl
        };
    }

    /// <summary>
    /// Intenta abrir el cliente de torrents cuando está cerrado.
    /// </summary>
    private bool TryStartClient(DownloadClientConfig config)
    {
        var executable = ResolveClientExecutable(config);
        if (executable is null)
        {
            _logger.LogInformation(
                "Torrent: no se encontró el ejecutable del cliente de torrents para abrirlo.");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
            _logger.LogInformation("Torrent: abriendo el cliente de torrents {Path}", executable);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Torrent: no se pudo abrir el cliente de torrents {Path}", executable);
            return false;
        }
    }

    private static string? ResolveClientExecutable(DownloadClientConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ExecutablePath))
        {
            var configured = Environment.ExpandEnvironmentVariables(config.ExecutablePath.Trim());
            return File.Exists(configured) ? configured : null;
        }

        var kinds = config.Kind == DownloadClientKind.Auto
            ? new[] { DownloadClientKind.QBittorrent, DownloadClientKind.Transmission, DownloadClientKind.Deluge }
            : new[] { config.Kind };

        foreach (var kind in kinds)
        {
            foreach (var candidate in CandidateExecutables(kind))
            {
                var expanded = Environment.ExpandEnvironmentVariables(candidate);
                if (File.Exists(expanded))
                {
                    return expanded;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateExecutables(DownloadClientKind kind)
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles") ?? "C:\\Program Files";
            var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "C:\\Program Files (x86)";
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty;

            return kind switch
            {
                DownloadClientKind.QBittorrent => new[]
                {
                    Path.Combine(programFiles, "qBittorrent", "qbittorrent.exe"),
                    Path.Combine(programFilesX86, "qBittorrent", "qbittorrent.exe"),
                    Path.Combine(localAppData, "Programs", "qBittorrent", "qbittorrent.exe")
                },
                DownloadClientKind.Transmission => new[]
                {
                    Path.Combine(programFiles, "Transmission", "transmission-qt.exe"),
                    Path.Combine(programFilesX86, "Transmission", "transmission-qt.exe"),
                    Path.Combine(programFiles, "Transmission", "transmission-daemon.exe")
                },
                DownloadClientKind.Deluge => new[]
                {
                    Path.Combine(programFiles, "Deluge", "deluge.exe"),
                    Path.Combine(programFilesX86, "Deluge", "deluge.exe")
                },
                _ => Array.Empty<string>()
            };
        }

        return kind switch
        {
            DownloadClientKind.QBittorrent => new[]
            {
                "/usr/bin/qbittorrent",
                "/Applications/qBittorrent.app/Contents/MacOS/qBittorrent"
            },
            DownloadClientKind.Transmission => new[]
            {
                "/usr/bin/transmission-gtk",
                "/usr/bin/transmission-qt",
                "/Applications/Transmission.app/Contents/MacOS/Transmission"
            },
            DownloadClientKind.Deluge => new[]
            {
                "/usr/bin/deluge",
                "/Applications/Deluge.app/Contents/MacOS/Deluge"
            },
            _ => Array.Empty<string>()
        };
    }

    /// <summary>
    /// Guarda el .torrent (o el magnet) en la carpeta configurada cuando no hay
    /// ningún cliente disponible.
    /// </summary>
    private async Task<DownloadResult> SaveToFolderAsync(
        DownloadRequest request,
        DownloadClientConfig config,
        CancellationToken cancellationToken)
    {
        var folder = ResolveFallbackFolder(config);
        var name = TextUtil.Slugify(request.Title);
        if (name.Length == 0)
        {
            name = "torrent";
        }

        try
        {
            Directory.CreateDirectory(folder);

            var bytes = request.TorrentBytes;
            if (bytes is null && !string.IsNullOrWhiteSpace(request.TorrentUrl))
            {
                bytes = await _torrentFiles
                    .GetBytesAsync(request.TorrentUrl, request.Referer, request.Cookie, cancellationToken)
                    .ConfigureAwait(false);
            }

            string path;
            if (bytes is not null)
            {
                path = Path.Combine(folder, name + ".torrent");
                await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(request.Magnet))
            {
                path = Path.Combine(folder, name + ".magnet");
                await File.WriteAllTextAsync(path, request.Magnet, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return Fallback(request, "No hay cliente de torrents y no se pudo guardar el enlace.");
            }

            _logger.LogInformation("Torrent: enlace guardado en {Path}", path);
            return new DownloadResult
            {
                Success = true,
                Fallback = true,
                Client = "Archivo",
                Message = $"No hay cliente de torrents: se ha guardado en {path}",
                Magnet = request.Magnet,
                TorrentUrl = request.TorrentUrl,
                SavedPath = path
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Torrent: no se pudo guardar el enlace en {Folder}", folder);
            return Fallback(request, "No hay cliente de torrents y no se pudo guardar el archivo: " + ex.Message);
        }
    }

    private static string ResolveFallbackFolder(DownloadClientConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.FallbackPath))
        {
            return Environment.ExpandEnvironmentVariables(config.FallbackPath.Trim());
        }

        if (!string.IsNullOrWhiteSpace(config.SavePath))
        {
            return Environment.ExpandEnvironmentVariables(config.SavePath.Trim());
        }

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        return Directory.Exists(downloads) ? downloads : Path.GetTempPath();
    }

    /// <summary>
    /// Comprueba qué clientes responden con la configuración actual.
    /// </summary>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Estado de cada cliente.</returns>
    public async Task<List<ClientProbe>> ProbeAllAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration.DownloadClient ?? new DownloadClientConfig();
        var probes = new List<ClientProbe>();

        foreach (var client in _clients)
        {
            var probe = await client
                .ProbeAsync(config, cancellationToken)
                .ConfigureAwait(false);
            probes.Add(new ClientProbe
            {
                Kind = client.Kind.ToString(),
                Name = client.DisplayName,
                Available = probe.Available,
                Message = probe.Message
            });
        }

        return probes;
    }

    private async Task<bool> IsAvailableAsync(
        IDownloadClient client,
        DownloadClientConfig config,
        CancellationToken cancellationToken)
    {
        var key = $"{client.Kind}|{config.BuildBaseUrl()}";
        if (_available.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow)
        {
            return true;
        }

        var probe = await client.ProbeAsync(config, cancellationToken).ConfigureAwait(false);
        if (probe.Available)
        {
            _available[key] = DateTimeOffset.UtcNow.Add(ProbeTtl);
        }

        return probe.Available;
    }

    private static string BuildFileName(string? title)
    {
        var slug = TextUtil.Slugify(title);
        return (slug.Length == 0 ? "torrent" : slug) + ".torrent";
    }

    private static DownloadResult Fallback(DownloadRequest request, string message) => new()
    {
        Success = true,
        Fallback = true,
        Client = "Navegador",
        Message = message,
        Magnet = request.Magnet,
        TorrentUrl = request.TorrentUrl
    };
}

/// <summary>
/// Estado de un cliente de torrents.
/// </summary>
public sealed class ClientProbe
{
    /// <summary>Gets or sets el tipo de cliente.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets el nombre visible.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether responde.</summary>
    public bool Available { get; set; }

    /// <summary>Gets or sets el detalle del estado.</summary>
    public string Message { get; set; } = string.Empty;
}
