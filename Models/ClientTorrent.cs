namespace Jellyfin.Plugin.Torrent.Models;

/// <summary>
/// Un torrent tal y como lo ve el cliente de descargas.
/// </summary>
public class ClientTorrent
{
    /// <summary>Gets or sets el hash (identificador).</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Gets or sets el nombre.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets el estado que muestra el cliente.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Gets or sets el progreso (0-1).</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets el tamaño total en bytes.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets la carpeta de descarga.</summary>
    public string? SavePath { get; set; }

    /// <summary>Gets or sets la ruta del contenido descargado.</summary>
    public string? ContentPath { get; set; }

    /// <summary>Gets or sets la categoría (qBittorrent).</summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets el ratio de compartición.</summary>
    public double Ratio { get; set; }

    /// <summary>Gets or sets los segundos sembrando.</summary>
    public long SeedingSeconds { get; set; }

    /// <summary>Gets or sets a value indicating whether está completo.</summary>
    public bool Completed { get; set; }

    /// <summary>Gets or sets la fecha de finalización.</summary>
    public DateTimeOffset? CompletedOn { get; set; }
}
