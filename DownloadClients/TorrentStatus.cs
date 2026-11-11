namespace Jellyfin.Plugin.Torrent.DownloadClients;

/// <summary>
/// Estado de un torrent dentro del cliente de descargas.
/// </summary>
public sealed class TorrentStatus
{
    /// <summary>Gets or sets el nombre del cliente que informa.</summary>
    public string Client { get; set; } = string.Empty;

    /// <summary>Gets or sets el info-hash del torrent.</summary>
    public string InfoHash { get; set; } = string.Empty;

    /// <summary>Gets or sets el nombre del torrent.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets el progreso entre 0 y 1.</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets el estado legible ("Descargando", "En pausa"…).</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether la descarga ha terminado.</summary>
    public bool IsComplete { get; set; }

    /// <summary>Gets or sets el tamaño total en bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Gets or sets lo descargado en bytes.</summary>
    public long DownloadedBytes { get; set; }

    /// <summary>Gets or sets la velocidad de descarga en bytes por segundo.</summary>
    public long DownloadSpeed { get; set; }

    /// <summary>Gets or sets el tiempo restante en segundos (null si se desconoce).</summary>
    public long? EtaSeconds { get; set; }

    /// <summary>Gets or sets las semillas conectadas.</summary>
    public int Seeds { get; set; }

    /// <summary>Gets or sets los clientes conectados.</summary>
    public int Leeches { get; set; }
}

/// <summary>
/// Estado de un conjunto de descargas, tal y como lo consume la interfaz.
/// </summary>
public sealed class DownloadStatusResponse
{
    /// <summary>Gets or sets a value indicating whether el cliente responde.</summary>
    public bool Available { get; set; }

    /// <summary>Gets or sets el nombre del cliente consultado.</summary>
    public string Client { get; set; } = string.Empty;

    /// <summary>Gets or sets el mensaje cuando no hay cliente disponible.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets los estados encontrados.</summary>
    public List<TorrentStatus> Statuses { get; set; } = new();
}
