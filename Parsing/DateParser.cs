using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Torrent.Parsing;

/// <summary>
/// Interpreta fechas de publicación en inglés y español, absolutas o relativas.
/// </summary>
public static partial class DateParser
{
    [GeneratedRegex(
        @"hace\s+(?<n>\d+)\s*(?<u>segundos?|minutos?|horas?|d[ií]as?|semanas?|mes(?:es)?|años?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpanishRelativeRegex();

    [GeneratedRegex(
        @"(?<n>\d+)\s*(?<u>seconds?|minutes?|hours?|days?|weeks?|months?|years?)\s+ago",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishRelativeRegex();

    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd",
        "yyyy/MM/dd",
        "dd/MM/yyyy HH:mm:ss",
        "d/M/yyyy HH:mm:ss",
        "d/M/yyyy HH:mm",
        "d/M/yyyy",
        "dd/MM/yyyy HH:mm",
        "dd/MM/yyyy",
        "dd-MM-yyyy HH:mm",
        "dd-MM-yyyy",
        "dd.MM.yyyy",
        "d MMM yyyy",
        "d MMMM yyyy",
        "MMM d, yyyy",
        "MMMM d, yyyy",
        "MMM d yyyy"
    ];

    /// <summary>
    /// Convierte un texto de fecha en una fecha real.
    /// </summary>
    /// <param name="text">Texto de entrada.</param>
    /// <param name="now">Instante de referencia para fechas relativas.</param>
    /// <returns>Fecha UTC o null.</returns>
    public static DateTimeOffset? Parse(string? text, DateTimeOffset? now = null)
    {
        var value = TextUtil.Clean(text);
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var reference = now ?? DateTimeOffset.UtcNow;
        var lowered = value.ToLowerInvariant();

        if (lowered is "hoy" or "today" or "just now" or "ahora" or "hace un momento")
        {
            return reference;
        }

        if (lowered is "ayer" or "yesterday")
        {
            return reference.AddDays(-1);
        }

        var relative = SpanishRelativeRegex().Match(value);
        if (!relative.Success)
        {
            relative = EnglishRelativeRegex().Match(value);
        }

        if (relative.Success && int.TryParse(relative.Groups["n"].Value, out var amount))
        {
            var unit = relative.Groups["u"].Value.ToLowerInvariant();
            return unit switch
            {
                "segundo" or "segundos" or "second" or "seconds" => reference.AddSeconds(-amount),
                "minuto" or "minutos" or "minute" or "minutes" => reference.AddMinutes(-amount),
                "hora" or "horas" or "hour" or "hours" => reference.AddHours(-amount),
                "dia" or "dias" or "día" or "días" or "day" or "days" => reference.AddDays(-amount),
                "semana" or "semanas" or "week" or "weeks" => reference.AddDays(-7 * amount),
                "mes" or "meses" or "month" or "months" => reference.AddMonths(-amount),
                "año" or "años" or "year" or "years" => reference.AddYears(-amount),
                _ => null
            };
        }

        var cleaned = value.Replace(" · ", " ").Replace(" | ", " ").Trim();

        if (DateTimeOffset.TryParse(
                cleaned,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var invariant))
        {
            return invariant;
        }

        foreach (var culture in new[] { "es-ES", "en-US", "en-GB" })
        {
            if (DateTime.TryParseExact(
                    cleaned,
                    Formats,
                    CultureInfo.GetCultureInfo(culture),
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return new DateTimeOffset(parsed, TimeSpan.Zero);
            }
        }

        return null;
    }
}
