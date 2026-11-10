namespace Jellyfin.Plugin.Torrent.Models;

/// <summary>
/// Configuración del cliente de torrents al que se envían los enlaces.
/// </summary>
public class DownloadClientConfig
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadClientConfig"/> class.
    /// </summary>
    public DownloadClientConfig()
    {
        Kind = DownloadClientKind.Auto;
        Host = "http://localhost";
        Port = 8080;
        Username = string.Empty;
        Password = string.Empty;
        SavePath = string.Empty;
        Category = string.Empty;
        Tags = string.Empty;
        StartPaused = false;
        ExecutablePath = string.Empty;
        AutoStart = true;
        FallbackPath = string.Empty;
    }

    /// <summary>Gets or sets el tipo de cliente.</summary>
    public DownloadClientKind Kind { get; set; }

    /// <summary>Gets or sets el host (con esquema, p. ej. <c>http://localhost</c>).</summary>
    public string Host { get; set; }

    /// <summary>Gets or sets el puerto.</summary>
    public int Port { get; set; }

    /// <summary>Gets or sets el usuario.</summary>
    public string Username { get; set; }

    /// <summary>Gets or sets la contraseña.</summary>
    public string Password { get; set; }

    /// <summary>Gets or sets la carpeta de descarga (vacío = la del cliente).</summary>
    public string SavePath { get; set; }

    /// <summary>Gets or sets la categoría/etiqueta de qBittorrent.</summary>
    public string Category { get; set; }

    /// <summary>Gets or sets las etiquetas de qBittorrent (separadas por comas).</summary>
    public string Tags { get; set; }

    /// <summary>Gets or sets a value indicating whether se añade en pausa.</summary>
    public bool StartPaused { get; set; }

    /// <summary>
    /// Gets or sets la ruta del ejecutable del cliente. Vacío = se busca en las
    /// ubicaciones habituales (qBittorrent, Transmission, Deluge).
    /// </summary>
    public string ExecutablePath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether se intenta abrir el cliente
    /// cuando no responde.
    /// </summary>
    public bool AutoStart { get; set; }

    /// <summary>
    /// Gets or sets la carpeta donde se guarda el .torrent cuando no hay
    /// cliente disponible. Vacío = la carpeta de descarga y, si tampoco hay,
    /// la carpeta «Descargas» del usuario.
    /// </summary>
    public string FallbackPath { get; set; }

    /// <summary>
    /// Gets or sets la URL base completa del cliente
    /// (<c>host</c> + <c>puerto</c> + ruta opcional).
    /// </summary>
    /// <returns>URL base sin barra final.</returns>
    public string BuildBaseUrl()
    {
        var host = string.IsNullOrWhiteSpace(Host) ? "http://localhost" : Host.Trim().TrimEnd('/');
        if (!host.Contains("://", StringComparison.Ordinal))
        {
            host = "http://" + host;
        }

        if (!Uri.TryCreate(host, UriKind.Absolute, out var uri))
        {
            return host;
        }

        var builder = new UriBuilder(uri);
        if (Port > 0 && uri.IsDefaultPort)
        {
            builder.Port = Port;
        }

        return builder.Uri.ToString().TrimEnd('/');
    }
}
