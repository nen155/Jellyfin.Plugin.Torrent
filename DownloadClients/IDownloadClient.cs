using Jellyfin.Plugin.Torrent.Models;

namespace Jellyfin.Plugin.Torrent.DownloadClients;

/// <summary>
/// Enlace que se quiere enviar al cliente de torrents.
/// </summary>
public sealed class DownloadRequest
{
    /// <summary>Gets or sets el enlace magnet.</summary>
    public string? Magnet { get; set; }

    /// <summary>Gets or sets la URL del archivo .torrent.</summary>
    public string? TorrentUrl { get; set; }

    /// <summary>Gets or sets el nombre del release (para el cliente).</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets el referer con el que descargar el .torrent.</summary>
    public string? Referer { get; set; }

    /// <summary>
    /// Gets or sets la cookie de la sesión del navegador, necesaria cuando la
    /// web protege la descarga.
    /// </summary>
    public string? Cookie { get; set; }

    /// <summary>Gets or sets el contenido del .torrent ya descargado.</summary>
    public byte[]? TorrentBytes { get; set; }

    /// <summary>Gets or sets el nombre de archivo del .torrent.</summary>
    public string? FileName { get; set; }

    /// <summary>Gets a value indicating whether hay algún enlace utilizable.</summary>
    public bool HasLink =>
        !string.IsNullOrWhiteSpace(Magnet) || !string.IsNullOrWhiteSpace(TorrentUrl);
}

/// <summary>
/// Resultado de enviar un enlace al cliente.
/// </summary>
public sealed class DownloadResult
{
    /// <summary>Gets or sets a value indicating whether se envió correctamente.</summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether no se pudo usar el cliente y se
    /// devuelve el enlace para que el navegador lo abra.
    /// </summary>
    public bool Fallback { get; set; }

    /// <summary>Gets or sets el cliente usado.</summary>
    public string Client { get; set; } = string.Empty;

    /// <summary>Gets or sets el mensaje para el usuario.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets el magnet que el navegador puede abrir.</summary>
    public string? Magnet { get; set; }

    /// <summary>Gets or sets la URL del .torrent.</summary>
    public string? TorrentUrl { get; set; }

    /// <summary>
    /// Gets or sets la ruta del archivo guardado cuando no había cliente
    /// disponible (.torrent o .magnet).
    /// </summary>
    public string? SavedPath { get; set; }
}

/// <summary>
/// Resultado de comprobar un cliente.
/// </summary>
public sealed class ProbeResult
{
    /// <summary>Gets or sets a value indicating whether el cliente responde.</summary>
    public bool Available { get; set; }

    /// <summary>Gets or sets el mensaje para el usuario.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Cliente de torrents capaz de aceptar enlaces.
/// </summary>
public interface IDownloadClient
{
    /// <summary>Gets el tipo de cliente.</summary>
    DownloadClientKind Kind { get; }

    /// <summary>Gets el nombre legible del cliente.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Comprueba si el cliente responde con la configuración dada.
    /// </summary>
    /// <param name="config">Configuración del cliente.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Estado y mensaje del cliente.</returns>
    Task<ProbeResult> ProbeAsync(DownloadClientConfig config, CancellationToken cancellationToken);

    /// <summary>
    /// Envía el enlace al cliente.
    /// </summary>
    /// <param name="config">Configuración del cliente.</param>
    /// <param name="request">Enlace.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Resultado.</returns>
    Task<DownloadResult> AddAsync(
        DownloadClientConfig config,
        DownloadRequest request,
        CancellationToken cancellationToken);
}
