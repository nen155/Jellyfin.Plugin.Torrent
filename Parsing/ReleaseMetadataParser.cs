using System.Text.RegularExpressions;
using Jellyfin.Plugin.Torrent.Models;

namespace Jellyfin.Plugin.Torrent.Parsing;

/// <summary>
/// Deduce calidad, idioma, grupo, temporada… a partir del nombre del release.
/// Solo rellena los campos que la web no haya indicado ya.
/// </summary>
public static partial class ReleaseMetadataParser
{
    private static readonly HashSet<string> NonGroupTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "x264", "x265", "h264", "h265", "hevc", "avc", "av1", "xvid", "divx",
        "1080p", "720p", "2160p", "480p", "576p", "360p", "4k", "uhd", "8k",
        "web", "webdl", "web-dl", "webrip", "bluray", "bdrip", "brrip", "remux",
        "hdtv", "dvdrip", "hdrip", "aac", "ac3", "eac3", "ddp", "dts", "truehd",
        "atmos", "mkv", "mp4", "avi", "multi", "vose", "castellano", "latino",
        "hdr", "hdr10", "dv", "dovi", "10bit", "8bit", "repack", "proper",
        "extended", "unrated", "directors", "cut", "imax", "complete", "completa",
        "m4a", "flac", "mp3", "ogg", "wav", "epub", "cbz"
    };

    [GeneratedRegex(@"(?<!\d)(2160p|1080p|720p|576p|480p|360p|2160|1080|720)(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResolutionRegex();

    [GeneratedRegex(@"\b(4k|uhd)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UhdRegex();

    [GeneratedRegex(@"\b(8k)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EightKRegex();

    [GeneratedRegex(@"(?<!\d)(19\d{2}|20\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\bS(?<s>\d{1,2})[\s._-]?E(?<e>\d{1,3})(?:[\s._-]?(?:-|E)?(?<e2>\d{1,3}))?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"\b(?<s>\d{1,2})x(?<e>\d{1,3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex AlternateEpisodeRegex();

    [GeneratedRegex(@"\b(?:season|temporada|t)\s*(?<s>\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonOnlyRegex();

    [GeneratedRegex(@"(?<!\d)(7\.1|6\.1|5\.1|3\.0|2\.0|1\.0)(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelsRegex();

    [GeneratedRegex(@"\.(mkv|mp4|avi|m4v|ts|mov|wmv|mpg|mpeg|webm|iso|m4a|flac|mp3|ogg|wav)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContainerRegex();

    [GeneratedRegex(@"\.(mkv|mp4|avi|m4v|ts|mov|wmv|mpg|mpeg|webm|iso|m4a|flac|mp3|ogg|wav)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingExtensionRegex();

    [GeneratedRegex(@"-([A-Za-z0-9][A-Za-z0-9._]{1,24})(?:\[[^\]]*\])?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupDashRegex();

    [GeneratedRegex(@"\[([A-Za-z0-9][A-Za-z0-9._-]{1,24})\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupBracketRegex();

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^(formato|format|calidad|quality|resoluci[oó]n|resolution|v[ií]deo|video|audio|duraci[oó]n|duration|c[oó]dec|codec|tama[nñ]o|size)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalFieldRegex();

    [GeneratedRegex(@"^(a[nñ]o|year|estreno|release\s*date)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YearFieldRegex();

    [GeneratedRegex(@"^(idioma|idiomas|language|languages|audio|voces|subt[ií]tulos|subtitles)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LanguageFieldRegex();

    [GeneratedRegex(@"^(duraci[oó]n|duration|runtime|tiempo)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeFieldRegex();

    [GeneratedRegex(@"(?<h>\d{1,2})\s*(?:h|horas?|hrs?)\s*(?<m>\d{1,2})?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HoursRuntimeRegex();

    [GeneratedRegex(@"(?<m>\d{1,4})\s*(?:min|minutos?|mins?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MinutesRuntimeRegex();

    [GeneratedRegex(@"^\s*(?<h>\d{1,2}):(?<m>\d{2})(?::(?<s>\d{2}))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ClockRuntimeRegex();

    [GeneratedRegex(@"^\s*[.,;:\-]+|[.,;:\-]+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TitleEdgesRegex();

    /// <summary>
    /// Completa los metadatos técnicos del resultado a partir de su nombre.
    /// </summary>
    /// <param name="result">Resultado a completar.</param>
    public static void Enrich(TorrentResult result)
    {
        var release = TextUtil.Clean(
            string.IsNullOrWhiteSpace(result.ReleaseName) ? result.Title : result.ReleaseName);
        var technicalExtra = string.Join(' ', result.Extra
            .Where(e => TechnicalFieldRegex().IsMatch(e.Name))
            .Select(e => e.Value));
        var searchText = TextUtil.Clean(
            $"{result.ReleaseName} {result.Title} {result.Category} {technicalExtra}");

        if (!string.IsNullOrWhiteSpace(release))
        {
            if (string.IsNullOrWhiteSpace(result.ReleaseName))
            {
                result.ReleaseName = release;
            }

            result.ReleaseName = CleanTitle(result.ReleaseName);

            result.Resolution ??= DetectResolution(searchText);
            var (medium, isRemux) = DetectMedium(searchText);
            result.Medium ??= medium;
            if (isRemux)
            {
                result.IsRemux = true;
            }

            result.VideoCodec ??= DetectVideoCodec(searchText);
            var (audio, channels) = DetectAudio(searchText);
            result.AudioCodec ??= audio;
            result.AudioChannels ??= channels;
            result.Container ??= DetectContainer(searchText);
            result.ReleaseGroup ??= DetectGroup(release);

            if (result.IsHdr == false)
            {
                result.IsHdr = Regex.IsMatch(
                    searchText, @"\b(hdr|hdr10|hdr10\+|dolby\s*vision|dovi|dv)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            if (!result.Is3D)
            {
                result.Is3D = Regex.IsMatch(
                    searchText, @"\b(3d|hsbs|hou|sbs)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            DetectEpisode(result, searchText);

            if (string.IsNullOrWhiteSpace(result.Title) || result.Title == result.ReleaseName)
            {
                var derived = DeriveTitle(release);
                if (!string.IsNullOrWhiteSpace(derived))
                {
                    result.Title = derived;
                }
            }
        }

        result.Year ??= DetectYear(searchText);
        if (result.Year is null)
        {
            var yearPair = result.Extra.FirstOrDefault(e => YearFieldRegex().IsMatch(e.Name));
            if (yearPair is not null)
            {
                result.Year = DetectYear(yearPair.Value);
            }
        }

        if (result.RuntimeMinutes is null)
        {
            var runtimePair = result.Extra.FirstOrDefault(e => RuntimeFieldRegex().IsMatch(e.Name));
            if (runtimePair is not null)
            {
                result.RuntimeMinutes = ParseRuntime(runtimePair.Value);
            }
        }

        if (string.IsNullOrWhiteSpace(result.Title))
        {
            result.Title = result.ReleaseName ?? string.Empty;
        }

        result.Title = CleanTitle(result.Title);

        if (result.Kind == MediaKind.Unknown)
        {
            result.Kind = DetectKind(result);
        }

        var languageExtra = string.Join(' ', result.Extra
            .Where(e => LanguageFieldRegex().IsMatch(e.Name))
            .Select(e => e.Value));
        var languages = LanguageDetector.Detect(
            result.LanguageText, languageExtra, result.ReleaseName, result.Title, result.Category);
        foreach (var language in languages)
        {
            if (!result.Languages.Contains(language, StringComparer.OrdinalIgnoreCase))
            {
                result.Languages.Add(language);
            }
        }

        if (string.IsNullOrWhiteSpace(result.LanguageText) && result.Languages.Count > 0)
        {
            result.LanguageText = string.Join(", ", result.Languages.Select(DescribeLanguage));
        }

        if (result.SizeBytes is null && !string.IsNullOrWhiteSpace(result.SizeText))
        {
            result.SizeBytes = SizeParser.Parse(result.SizeText);
        }

        if (result.SizeBytes is not null)
        {
            result.SizeText = SizeParser.Format(result.SizeBytes.Value);
        }

        if (result.Published is null && !string.IsNullOrWhiteSpace(result.PublishedText))
        {
            result.Published = DateParser.Parse(result.PublishedText);
        }

        if (!string.IsNullOrWhiteSpace(result.Description))
        {
            result.Description = CleanDescription(result.Description);
        }
    }

    /// <summary>
    /// Nombre legible de un código de idioma.
    /// </summary>
    /// <param name="code">Código (es, es-419, vose…).</param>
    /// <returns>Nombre legible.</returns>
    public static string DescribeLanguage(string code) => code.ToLowerInvariant() switch
    {
        "es" => "Español (Castellano)",
        "es-419" => "Español (Latino)",
        "vose" => "VOSE (subtítulos)",
        "multi" => "Multi",
        "en" => "Inglés",
        "fr" => "Francés",
        "it" => "Italiano",
        "de" => "Alemán",
        "pt" => "Portugués",
        "ru" => "Ruso",
        "ja" => "Japonés",
        "ko" => "Coreano",
        "zh" => "Chino",
        "hi" => "Hindi",
        "ar" => "Árabe",
        "tr" => "Turco",
        "pl" => "Polaco",
        "nl" => "Neerlandés",
        "sv" => "Sueco",
        "da" => "Danés",
        "no" => "Noruego",
        "fi" => "Finlandés",
        "cs" => "Checo",
        "hu" => "Húngaro",
        "ro" => "Rumano",
        "el" => "Griego",
        "he" => "Hebreo",
        "uk" => "Ucraniano",
        "ca" => "Catalán",
        _ => code
    };

    private static string? DetectResolution(string text)
    {
        var match = ResolutionRegex().Match(text);
        if (match.Success)
        {
            var value = match.Groups[1].Value.ToLowerInvariant();
            return value.EndsWith('p') ? value : value + "p";
        }

        if (UhdRegex().IsMatch(text))
        {
            return "2160p";
        }

        return EightKRegex().IsMatch(text) ? "4320p" : null;
    }

    private static (string? Medium, bool IsRemux) DetectMedium(string text)
    {
        if (Regex.IsMatch(text, @"\bremux\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return ("BluRay", true);
        }

        if (Regex.IsMatch(text, @"\b(blu-?ray|bdrip|brrip|bdremux|bd25|bd50|bd)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return ("BluRay", false);
        }

        if (Regex.IsMatch(text, @"\b(web-?dl|webdl|webrip|web)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return ("WEB", false);
        }

        if (Regex.IsMatch(text, @"\b(hdtv|pdtv|sdtv)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return ("HDTV", false);
        }

        if (Regex.IsMatch(text, @"\b(dvdrip|dvd5|dvd9|dvd|dvdscr)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return ("DVD", false);
        }

        if (Regex.IsMatch(text, @"\b(hdrip)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return ("HDRip", false);
        }

        if (Regex.IsMatch(text, @"\b(screener|scr)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return ("SCR", false);
        }

        if (Regex.IsMatch(text, @"\b(hdcam|cam|telesync|ts|telecine|tc)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return ("CAM", false);
        }

        return (null, false);
    }

    private static string? DetectVideoCodec(string text)
    {
        if (Regex.IsMatch(text, @"\b(x265|h\.?265|hevc)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "H.265";
        }

        if (Regex.IsMatch(text, @"\b(x264|h\.?264|avc)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "H.264";
        }

        if (Regex.IsMatch(text, @"\bav1\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "AV1";
        }

        if (Regex.IsMatch(text, @"\bxvid\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "XviD";
        }

        if (Regex.IsMatch(text, @"\bdivx\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "DivX";
        }

        if (Regex.IsMatch(text, @"\bvp9\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "VP9";
        }

        return null;
    }

    private static (string? Codec, string? Channels) DetectAudio(string text)
    {
        string? codec = null;
        if (Regex.IsMatch(text, @"\b(truehd|dolby\s*truehd)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            codec = "TrueHD";
        }
        else if (Regex.IsMatch(text, @"\bdts-?hd\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            codec = Regex.IsMatch(text, @"\bdts-?hd[\s._-]*ma\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                ? "DTS-HD MA"
                : "DTS-HD";
        }
        else if (Regex.IsMatch(text, @"\bdts[-\s]?x\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            codec = "DTS:X";
        }
        else if (Regex.IsMatch(text, @"\bdts\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            codec = "DTS";
        }
        else if (Regex.IsMatch(text, @"\b(ddp|eac3|dd\+|ddp\d)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            codec = "E-AC3";
        }
        else if (Regex.IsMatch(text, @"\b(ac3|dd\d(?:\.\d)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            codec = "AC3";
        }
        else if (Regex.IsMatch(text, @"\baac\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            codec = "AAC";
        }
        else if (Regex.IsMatch(text, @"\bflac\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            codec = "FLAC";
        }
        else if (Regex.IsMatch(text, @"\b(opus)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            codec = "Opus";
        }
        else if (Regex.IsMatch(text, @"\bmp3\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            codec = "MP3";
        }
        else if (Regex.IsMatch(text, @"\b(lpcm|pcm)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            codec = "PCM";
        }

        if (codec is not null
            && Regex.IsMatch(text, @"\batmos\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            codec += " Atmos";
        }

        var channels = ChannelsRegex().Match(text);
        return (codec, channels.Success ? channels.Groups[1].Value : null);
    }

    private static string? DetectContainer(string text)
    {
        var match = ContainerRegex().Match(text);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static string? DetectGroup(string release)
    {
        var cleaned = TrailingExtensionRegex().Replace(release, string.Empty).Trim();
        var match = GroupDashRegex().Match(cleaned);
        if (!match.Success)
        {
            match = GroupBracketRegex().Match(cleaned);
        }

        if (!match.Success)
        {
            return null;
        }

        var group = match.Groups[1].Value.Trim('.', '_', '-');
        return NonGroupTokens.Contains(group) || group.Length < 2 ? null : group;
    }

    private static void DetectEpisode(TorrentResult result, string text)
    {
        var match = SeasonEpisodeRegex().Match(text);
        if (match.Success)
        {
            result.Season ??= int.Parse(match.Groups["s"].Value);
            result.Episode ??= int.Parse(match.Groups["e"].Value);
            result.EpisodeText ??= match.Groups["e2"].Success
                ? $"S{int.Parse(match.Groups["s"].Value):00}E{int.Parse(match.Groups["e"].Value):00}-E{int.Parse(match.Groups["e2"].Value):00}"
                : $"S{int.Parse(match.Groups["s"].Value):00}E{int.Parse(match.Groups["e"].Value):00}";
            return;
        }

        match = AlternateEpisodeRegex().Match(text);
        if (match.Success)
        {
            result.Season ??= int.Parse(match.Groups["s"].Value);
            result.Episode ??= int.Parse(match.Groups["e"].Value);
            result.EpisodeText ??= $"S{result.Season:00}E{result.Episode:00}";
            return;
        }

        match = SeasonOnlyRegex().Match(text);
        if (match.Success)
        {
            result.Season ??= int.Parse(match.Groups["s"].Value);
            result.EpisodeText ??= $"Temporada {result.Season}";
            return;
        }

        if (Regex.IsMatch(text, @"\b(complete|completa|temporada\s*completa|season\s*complete)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            result.EpisodeText ??= "Temporada completa";
        }
    }

    private static int? DetectYear(string text)
    {
        var limit = DateTime.UtcNow.Year + 2;
        foreach (Match match in YearRegex().Matches(text))
        {
            if (int.TryParse(match.Value, out var year) && year >= 1900 && year <= limit)
            {
                return year;
            }
        }

        return null;
    }

    private static MediaKind DetectKind(TorrentResult result)
    {
        if (result.Season is not null
            || !string.IsNullOrWhiteSpace(result.EpisodeText)
            || Regex.IsMatch(
                $"{result.Category} {result.ReleaseName}",
                @"\b(serie|series|tv|episodio|episode|temporada|season)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return MediaKind.Series;
        }

        if (Regex.IsMatch(
                result.Category ?? string.Empty,
                @"\b(pelicula|película|movie|film|cine)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return MediaKind.Movie;
        }

        return result.Year is not null ? MediaKind.Movie : MediaKind.Unknown;
    }

    /// <summary>
    /// Interpreta una duración escrita como <c>136 min</c>, <c>2h 16min</c> o
    /// <c>02:16:00</c>.
    /// </summary>
    /// <param name="text">Texto de la web.</param>
    /// <returns>Minutos o null.</returns>
    public static int? ParseRuntime(string? text)
    {
        var value = TextUtil.Clean(text);
        if (value.Length == 0)
        {
            return null;
        }

        var clock = ClockRuntimeRegex().Match(value);
        if (clock.Success)
        {
            return (int.Parse(clock.Groups["h"].Value) * 60) + int.Parse(clock.Groups["m"].Value);
        }

        var hours = HoursRuntimeRegex().Match(value);
        if (hours.Success && (hours.Groups["m"].Success || hours.Value.Contains('h')))
        {
            var total = int.Parse(hours.Groups["h"].Value) * 60;
            if (hours.Groups["m"].Success)
            {
                total += int.Parse(hours.Groups["m"].Value);
            }

            return total > 0 ? total : null;
        }

        var minutes = MinutesRuntimeRegex().Match(value);
        return minutes.Success ? int.Parse(minutes.Groups["m"].Value) : null;
    }

    private static string CleanTitle(string? title)
    {
        var clean = TextUtil.Clean(title);
        clean = TitleEdgesRegex().Replace(clean, string.Empty);
        return clean.Length > 1 ? clean : (title ?? string.Empty);
    }

    private static string DeriveTitle(string release)
    {
        var cleaned = release.Replace('[', ' ').Replace(']', ' ');
        var tokens = cleaned.Split(['.', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        var keep = new List<string>();
        foreach (var token in tokens)
        {
            var bare = token.Trim('-', '(', ')');
            if (IsMetadataToken(bare))
            {
                break;
            }

            keep.Add(token);
        }

        var title = string.Join(' ', keep).Replace('-', ' ').Trim();
        return title.Length > 1 ? title : release;
    }

    private static bool IsMetadataToken(string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        if (Regex.IsMatch(token, @"^(19|20)\d{2}$", RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(token, @"^\d{4}[-.]\d{2}[-.]\d{2}$", RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(token, @"^s\d{1,2}(e\d{1,3})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(token, @"^\d{1,2}x\d{1,3}$", RegexOptions.CultureInvariant))
        {
            return true;
        }

        return NonGroupTokens.Contains(token)
            || Regex.IsMatch(token, @"^\d{3,4}p$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(token, @"^\d+bit$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string CleanDescription(string html)
    {
        var withoutScripts = ScriptStyleRegex().Replace(html, " ");
        var text = TagRegex().Replace(withoutScripts, " ");
        text = TextUtil.Clean(text.Replace("&nbsp;", " "));
        return text.Length > 2000 ? text[..2000] : text;
    }
}
