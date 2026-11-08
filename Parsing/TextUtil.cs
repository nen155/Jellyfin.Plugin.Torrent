using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Torrent.Parsing;

/// <summary>
/// Utilidades de texto compartidas por el motor de scraping.
/// </summary>
public static partial class TextUtil
{
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Normaliza espacios, saltos de línea y entidades HTML.
    /// </summary>
    /// <param name="value">Texto de entrada.</param>
    /// <returns>Texto limpio.</returns>
    public static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decoded = WebUtility.HtmlDecode(value);
        return WhitespaceRegex().Replace(decoded, " ").Trim();
    }

    /// <summary>
    /// Convierte un enlace relativo en absoluto respecto a una base.
    /// </summary>
    /// <param name="baseUrl">URL base.</param>
    /// <param name="href">Enlace (posiblemente relativo).</param>
    /// <returns>URL absoluta o null si no se puede resolver.</returns>
    public static string? ToAbsolute(string? baseUrl, string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        var value = Clean(href);
        if (value.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute.ToString();
        }

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, value, out var combined))
        {
            return combined.ToString();
        }

        return null;
    }

    /// <summary>
    /// Extrae el primer entero de un texto (<c>1.234</c> → 1234).
    /// </summary>
    /// <param name="value">Texto.</param>
    /// <returns>Entero o null.</returns>
    public static int? ToInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            return null;
        }

        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    /// <summary>
    /// Extrae el primer decimal de un texto (<c>7.9</c> → 7.9).
    /// </summary>
    /// <param name="value">Texto.</param>
    /// <returns>Decimal o null.</returns>
    public static double? ToDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value, @"\d+([.,]\d+)?", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var normalized = match.Value.Replace(',', '.');
        return double.TryParse(
            normalized,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : null;
    }

    /// <summary>
    /// Quita acentos y pasa a minúsculas (para comparar títulos).
    /// </summary>
    /// <param name="value">Texto.</param>
    /// <returns>Texto normalizado.</returns>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(ch);
            }
        }

        return WhitespaceRegex()
            .Replace(builder.ToString(), " ")
            .ToLowerInvariant()
            .Trim();
    }

    /// <summary>
    /// Comprueba si un texto contiene todas las palabras de una consulta.
    /// </summary>
    /// <param name="haystack">Texto donde buscar.</param>
    /// <param name="query">Consulta.</param>
    /// <returns>True si todas las palabras están presentes.</returns>
    public static bool ContainsAllWords(string? haystack, string? query)
    {
        var normalizedHaystack = Normalize(haystack);
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var words = Normalize(query)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.All(w => normalizedHaystack.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>
    /// Convierte un texto en un slug apto para URL.
    /// </summary>
    /// <param name="value">Texto.</param>
    /// <returns>Slug.</returns>
    public static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = Normalize(value);
        var builder = new StringBuilder(normalized.Length);
        var lastDash = false;
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastDash = false;
            }
            else if (!lastDash && builder.Length > 0)
            {
                builder.Append('-');
                lastDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }
}
