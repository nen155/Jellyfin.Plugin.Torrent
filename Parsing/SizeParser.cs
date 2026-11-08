using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Torrent.Parsing;

/// <summary>
/// Interpreta tamaños escritos por las webs (<c>1.5 GB</c>, <c>1,5 GB</c>,
/// <c>700 MiB</c>, <c>2.3GB</c>).
/// </summary>
public static partial class SizeParser
{
    [GeneratedRegex(
        @"(?<num>\d+(?:[.,]\d+)?)\s*(?<unit>b|bytes?|kb|kib|mb|mib|gb|gib|tb|tib)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizeRegex();

    private static readonly Dictionary<string, double> Multipliers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["b"] = 1,
        ["byte"] = 1,
        ["bytes"] = 1,
        ["kb"] = 1024d,
        ["kib"] = 1024d,
        ["mb"] = 1024d * 1024,
        ["mib"] = 1024d * 1024,
        ["gb"] = 1024d * 1024 * 1024,
        ["gib"] = 1024d * 1024 * 1024,
        ["tb"] = 1024d * 1024 * 1024 * 1024,
        ["tib"] = 1024d * 1024 * 1024 * 1024
    };

    /// <summary>
    /// Convierte un texto de tamaño a bytes.
    /// </summary>
    /// <param name="text">Texto de entrada.</param>
    /// <returns>Bytes o null si no se reconoce.</returns>
    public static long? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = SizeRegex().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var numberText = match.Groups["num"].Value;
        if (numberText.Contains(',') && !numberText.Contains('.'))
        {
            numberText = numberText.Replace(',', '.');
        }
        else
        {
            numberText = numberText.Replace(",", string.Empty);
        }

        if (!double.TryParse(
                numberText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return null;
        }

        if (!Multipliers.TryGetValue(match.Groups["unit"].Value, out var multiplier))
        {
            return null;
        }

        return (long)Math.Round(number * multiplier);
    }

    /// <summary>
    /// Formatea bytes de forma legible.
    /// </summary>
    /// <param name="bytes">Tamaño en bytes.</param>
    /// <returns>Texto formateado.</returns>
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", value, units[index]);
    }
}
