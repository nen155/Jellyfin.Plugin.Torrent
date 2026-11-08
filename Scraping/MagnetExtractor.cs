using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Jellyfin.Plugin.Torrent.Parsing;

namespace Jellyfin.Plugin.Torrent.Scraping;

/// <summary>
/// Localiza enlaces magnet, info-hashes y enlaces .torrent dentro de una página.
/// </summary>
public static partial class MagnetExtractor
{
    [GeneratedRegex(@"magnet:\?[^""'<>\s\\]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MagnetRegex();

    [GeneratedRegex(@"(?:urn:btih:)(?<hash>[a-zA-Z0-9]{32,40})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InfoHashRegex();

    /// <summary>
    /// Extrae todos los enlaces magnet presentes en un texto o HTML.
    /// </summary>
    /// <param name="content">HTML o texto.</param>
    /// <returns>Magnets sin duplicados.</returns>
    public static List<string> FindMagnets(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new List<string>();
        }

        var decoded = content.Replace("&amp;", "&").Replace("&#38;", "&");
        return MagnetRegex()
            .Matches(decoded)
            .Select(m => TextUtil.Clean(m.Value).TrimEnd(';', ','))
            .Where(m => m.Length > 20)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Extrae el info-hash de un enlace magnet.
    /// </summary>
    /// <param name="magnet">Enlace magnet.</param>
    /// <returns>Hash en hexadecimal (minúsculas) o null.</returns>
    public static string? GetInfoHash(string? magnet)
    {
        if (string.IsNullOrWhiteSpace(magnet))
        {
            return null;
        }

        var match = InfoHashRegex().Match(magnet);
        if (!match.Success)
        {
            return null;
        }

        var hash = match.Groups["hash"].Value;
        if (hash.Length == 40)
        {
            return hash.ToLowerInvariant();
        }

        if (hash.Length == 32)
        {
            return Base32ToHex(hash);
        }

        return null;
    }

    /// <summary>
    /// Extrae el nombre (<c>dn</c>) de un enlace magnet.
    /// </summary>
    /// <param name="magnet">Enlace magnet.</param>
    /// <returns>Nombre decodificado o null.</returns>
    public static string? GetDisplayName(string? magnet)
    {
        if (string.IsNullOrWhiteSpace(magnet))
        {
            return null;
        }

        var match = Regex.Match(
            magnet,
            @"[?&]dn=(?<dn>[^&]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var value = Uri.UnescapeDataString(match.Groups["dn"].Value.Replace('+', ' '));
        return TextUtil.Clean(value);
    }

    /// <summary>
    /// Construye un enlace magnet a partir de un info-hash.
    /// </summary>
    /// <param name="infoHash">Info-hash en hexadecimal.</param>
    /// <param name="name">Nombre del contenido.</param>
    /// <param name="trackers">Trackers opcionales.</param>
    /// <returns>Enlace magnet.</returns>
    public static string BuildMagnet(string infoHash, string? name, IEnumerable<string>? trackers = null)
    {
        var builder = new System.Text.StringBuilder("magnet:?xt=urn:btih:");
        builder.Append(infoHash.ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(name))
        {
            builder.Append("&dn=").Append(Uri.EscapeDataString(name));
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
    /// Un enlace de descarga encontrado en la página, con su contexto.
    /// </summary>
    /// <param name="Url">URL absoluta.</param>
    /// <param name="Name">Nombre del release (atributo title o texto del enlace).</param>
    /// <param name="SizeText">Texto del contenedor, donde suele estar el tamaño.</param>
    public sealed record TorrentLink(string Url, string? Name, string? SizeText);

    /// <summary>
    /// Indica si una URL parece un archivo .torrent (y no una página intermedia).
    /// </summary>
    /// <param name="url">URL.</param>
    /// <returns>True si parece un .torrent directo.</returns>
    public static bool LooksLikeDirectTorrent(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && url.Contains(".torrent", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Indica si una URL capturada del navegador parece una descarga de torrent.
    /// </summary>
    /// <param name="url">URL.</param>
    /// <returns>True si es un magnet o un enlace de descarga.</returns>
    public static bool LooksLikeTorrentDownload(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return url.Contains(".torrent", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/download", StringComparison.OrdinalIgnoreCase)
            || url.Contains("gettorrent", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/dl/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("download.php", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Busca enlaces de descarga en una página con su nombre y tamaño.
    /// </summary>
    /// <param name="document">Documento HTML.</param>
    /// <param name="baseUrl">URL base.</param>
    /// <returns>Enlaces encontrados.</returns>
    public static List<TorrentLink> FindTorrentLinksDetailed(IHtmlDocument document, string? baseUrl)
    {
        var links = new List<TorrentLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var anchor in document.QuerySelectorAll("a[href], a[onclick], button[onclick]"))
        {
            var href = anchor.GetAttribute("href");
            var candidate = href;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = ExtractUrlFromOnClick(anchor.GetAttribute("onclick"));
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var absolute = TextUtil.ToAbsolute(baseUrl, candidate);
            if (absolute is null || !LooksLikeTorrentDownload(absolute) || !seen.Add(absolute))
            {
                continue;
            }

            var name = TextUtil.Clean(anchor.GetAttribute("title"));
            if (name.Length == 0)
            {
                name = TextUtil.Clean(anchor.TextContent);
            }

            var container = anchor.Closest("li") ?? anchor.ParentElement;
            var sizeText = TextUtil.Clean(container?.TextContent);

            links.Add(new TorrentLink(
                absolute,
                name.Length > 3 ? name : null,
                string.IsNullOrWhiteSpace(sizeText) ? null : sizeText));
        }

        return links;
    }

    private static string? ExtractUrlFromOnClick(string? onclick)
    {
        if (string.IsNullOrWhiteSpace(onclick))
        {
            return null;
        }

        var match = Regex.Match(
            onclick,
            @"['""](?<url>(?:https?://|/)[^'""]*?(?:\.torrent|/download|gettorrent|/dl/)[^'""]*)['""]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["url"].Value : null;
    }

    /// <summary>
    /// Busca enlaces a archivos .torrent en una página.
    /// </summary>
    /// <param name="document">Documento HTML.</param>
    /// <param name="baseUrl">URL base para resolver enlaces relativos.</param>
    /// <returns>URLs absolutas sin duplicados.</returns>
    public static List<string> FindTorrentLinks(IHtmlDocument document, string? baseUrl)
    {
        var links = new List<string>();
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var isTorrent = href.Contains(".torrent", StringComparison.OrdinalIgnoreCase)
                || href.Contains("/download", StringComparison.OrdinalIgnoreCase)
                || href.Contains("download.php", StringComparison.OrdinalIgnoreCase);

            if (!isTorrent)
            {
                continue;
            }

            var absolute = TextUtil.ToAbsolute(baseUrl, href);
            if (absolute is not null && !links.Contains(absolute, StringComparer.OrdinalIgnoreCase))
            {
                links.Add(absolute);
            }
        }

        return links;
    }

    /// <summary>
    /// Convierte un info-hash base32 (32 caracteres) a hexadecimal.
    /// </summary>
    /// <param name="base32">Hash base32.</param>
    /// <returns>Hash hexadecimal o null si no es válido.</returns>
    public static string? Base32ToHex(string base32)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var value = base32.ToUpperInvariant();
        var bits = 0;
        var buffer = 0;
        var bytes = new List<byte>(20);

        foreach (var ch in value)
        {
            var index = alphabet.IndexOf(ch, StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return bytes.Count == 20
            ? Convert.ToHexString(bytes.ToArray()).ToLowerInvariant()
            : null;
    }
}
