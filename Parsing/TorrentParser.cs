using System.Text;
using Jellyfin.Plugin.Torrent.Models;

namespace Jellyfin.Plugin.Torrent.Parsing;

/// <summary>
/// Interpreta archivos .torrent (bencode) para obtener info-hash, tamaño,
/// lista de archivos y trackers.
/// </summary>
public static class TorrentParser
{
    /// <summary>
    /// Parsea el contenido de un .torrent.
    /// </summary>
    /// <param name="data">Bytes del archivo.</param>
    /// <returns>Información del torrent.</returns>
    /// <exception cref="FormatException">Si no es un .torrent válido.</exception>
    public static TorrentFileInfo Parse(byte[] data)
    {
        var root = Bencode.Parse(data);
        if (root.Kind != BencodeKind.Dictionary)
        {
            throw new FormatException("El archivo no es un .torrent válido.");
        }

        var info = root.Get("info")
            ?? throw new FormatException("El .torrent no contiene el diccionario 'info'.");

        var result = new TorrentFileInfo
        {
            Name = info.Get("name")?.AsString() ?? "desconocido",
            InfoHash = TorrentFileInfo.ComputeInfoHash(data, info.Start, info.End),
            PieceLength = (int)(info.Get("piece length")?.Integer ?? 0),
            IsPrivate = info.Get("private")?.Integer == 1,
            Comment = root.Get("comment")?.AsString(),
            CreatedBy = root.Get("created by")?.AsString()
        };

        var creationDate = root.Get("creation date")?.Integer;
        if (creationDate is > 0)
        {
            result.CreatedOn = DateTimeOffset.FromUnixTimeSeconds(creationDate.Value);
        }

        var files = info.Get("files");
        if (files?.Items is not null)
        {
            foreach (var file in files.Items)
            {
                var pathParts = file.Get("path")?.Items;
                if (pathParts is null)
                {
                    continue;
                }

                var path = string.Join('/', pathParts
                    .Select(p => p.AsString())
                    .Where(p => !string.IsNullOrEmpty(p)));
                var size = file.Get("length")?.Integer ?? 0;
                result.Files.Add(new TorrentFileEntry { Path = path, Size = size });
                result.TotalSize += size;
            }
        }
        else
        {
            var length = info.Get("length")?.Integer ?? 0;
            result.TotalSize = length;
            result.Files.Add(new TorrentFileEntry { Path = result.Name, Size = length });
        }

        return result;
    }

    /// <summary>
    /// Obtiene los trackers declarados en el .torrent.
    /// </summary>
    /// <param name="data">Bytes del archivo.</param>
    /// <returns>Lista de trackers.</returns>
    public static IReadOnlyList<string> GetTrackers(byte[] data)
    {
        var root = Bencode.Parse(data);
        var trackers = new List<string>();

        var announce = root.Get("announce")?.AsString();
        if (!string.IsNullOrWhiteSpace(announce))
        {
            trackers.Add(announce);
        }

        var tiers = root.Get("announce-list")?.Items;
        if (tiers is not null)
        {
            foreach (var tier in tiers)
            {
                foreach (var tracker in tier.Items ?? Enumerable.Empty<BencodeValue>())
                {
                    var value = tracker.AsString();
                    if (!string.IsNullOrWhiteSpace(value)
                        && !trackers.Contains(value, StringComparer.OrdinalIgnoreCase))
                    {
                        trackers.Add(value);
                    }
                }
            }
        }

        return trackers;
    }

    /// <summary>
    /// Indica si unos bytes parecen un .torrent (empiezan por <c>d8:announce</c>
    /// o contienen el diccionario info).
    /// </summary>
    /// <param name="data">Bytes a comprobar.</param>
    /// <returns>True si parece un .torrent.</returns>
    public static bool LooksLikeTorrent(byte[] data) =>
        data.Length > 20
        && data[0] == (byte)'d'
        && Encoding.ASCII.GetString(data, 0, Math.Min(40, data.Length)).Contains("announce", StringComparison.Ordinal);
}
