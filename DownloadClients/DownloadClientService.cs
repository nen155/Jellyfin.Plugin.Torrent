using System.Collections.Concurrent;
using Jellyfin.Plugin.Torrent.Models;
using Jellyfin.Plugin.Torrent.Parsing;
using Jellyfin.Plugin.Torrent.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.DownloadClients;

/// <summary>
/// Fachada de envío: usa el cliente configurado y, en modo automático, prueba
/// los que respondan en el host/puerto indicado. Si ninguno responde devuelve
/// el enlace para que el navegador lo abra con el programa del sistema.
/// </summary>
public sealed class DownloadClientService
{
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromMinutes(2);

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
        if (request.TorrentBytes is null
            && string.IsNullOrWhiteSpace(request.Magnet)
            && !string.IsNullOrWhiteSpace(request.TorrentUrl)
            && !string.IsNullOrWhiteSpace(request.Cookie))
        {
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
        }

        if (config.Kind != DownloadClientKind.Auto)
        {
            var client = _clients.FirstOrDefault(c => c.Kind == config.Kind);
            if (client is null)
            {
                return Fallback(request, $"El cliente {config.Kind} no está disponible.");
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

        return Fallback(
            request,
            "Ningún cliente de torrents respondió en el host configurado; se abre el enlace en el navegador.");
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
