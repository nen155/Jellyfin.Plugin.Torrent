using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Torrent.Models;

/// <summary>
/// Contenido de un archivo .torrent ya interpretado.
/// </summary>
public class TorrentFileInfo
{
    /// <summary>Initializes a new instance of the <see cref="TorrentFileInfo"/> class.</summary>
    public TorrentFileInfo()
    {
        Name = string.Empty;
        Files = new List<TorrentFileEntry>();
    }

    /// <summary>Gets or sets el nombre del torrent.</summary>
    public string Name { get; set; }

    /// <summary>Gets or sets el info-hash en hexadecimal.</summary>
    public string InfoHash { get; set; } = string.Empty;

    /// <summary>Gets or sets el tamaño total en bytes.</summary>
    public long TotalSize { get; set; }

    /// <summary>Gets or sets el tamaño de pieza.</summary>
    public int PieceLength { get; set; }

    /// <summary>Gets or sets a value indicating whether es un torrent privado.</summary>
    public bool IsPrivate { get; set; }

    /// <summary>Gets or sets el comentario del torrent.</summary>
    public string? Comment { get; set; }

    /// <summary>Gets or sets el cliente que creó el torrent.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Gets or sets la fecha de creación.</summary>
    public DateTimeOffset? CreatedOn { get; set; }

    /// <summary>Gets or sets la lista de archivos.</summary>
    public List<TorrentFileEntry> Files { get; set; }

    /// <summary>
    /// Construye el enlace magnet a partir del info-hash y el nombre.
    /// </summary>
    /// <param name="trackers">Trackers opcionales.</param>
    /// <returns>Enlace magnet.</returns>
    public string BuildMagnet(IEnumerable<string>? trackers = null)
    {
        var builder = new StringBuilder("magnet:?xt=urn:btih:");
        builder.Append(InfoHash);
        if (!string.IsNullOrWhiteSpace(Name))
        {
            builder.Append("&dn=").Append(Uri.EscapeDataString(Name));
        }

        foreach (var tracker in trackers ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(tracker))
            {
                builder.Append("&tr=").Append(Uri.EscapeDataString(tracker));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Calcula el info-hash (SHA-1) del diccionario <c>info</c>.
    /// </summary>
    /// <param name="torrentBytes">Contenido del .torrent.</param>
    /// <param name="infoStart">Inicio del diccionario info.</param>
    /// <param name="infoEnd">Fin del diccionario info.</param>
    /// <returns>Info-hash en hexadecimal.</returns>
    public static string ComputeInfoHash(byte[] torrentBytes, int infoStart, int infoEnd)
    {
        var length = infoEnd - infoStart;
        var slice = new byte[length];
        Array.Copy(torrentBytes, infoStart, slice, 0, length);
        return Convert.ToHexString(SHA1.HashData(slice)).ToLowerInvariant();
    }
}

/// <summary>
/// Un archivo dentro de un torrent.
/// </summary>
public class TorrentFileEntry
{
    /// <summary>Gets or sets la ruta dentro del torrent.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets el tamaño en bytes.</summary>
    public long Size { get; set; }
}
