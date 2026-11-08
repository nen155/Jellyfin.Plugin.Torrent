using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Torrent.Parsing;

/// <summary>
/// Detecta idiomas en los títulos de los releases y en las etiquetas que
/// publican las webs (castellano, latino, VOSE, multi…).
/// </summary>
public static partial class LanguageDetector
{
    private static readonly Dictionary<string, string> Keywords = new(StringComparer.Ordinal)
    {
        ["castellano"] = "es",
        ["espanol"] = "es",
        ["español"] = "es",
        ["spanish"] = "es",
        ["spa"] = "es",
        ["esp"] = "es",
        ["latino"] = "es-419",
        ["latina"] = "es-419",
        ["latinoamerica"] = "es-419",
        ["vose"] = "vose",
        ["vos"] = "vose",
        ["vose "] = "vose",
        ["subtitulado"] = "vose",
        ["subtitulada"] = "vose",
        ["subtitulados"] = "vose",
        ["subtitles"] = "vose",
        ["subs"] = "vose",
        ["multi"] = "multi",
        ["multilenguaje"] = "multi",
        ["multilanguage"] = "multi",
        ["dual"] = "multi",
        ["dual audio"] = "multi",
        ["english"] = "en",
        ["ingles"] = "en",
        ["inglés"] = "en",
        ["eng"] = "en",
        ["french"] = "fr",
        ["francais"] = "fr",
        ["français"] = "fr",
        ["frances"] = "fr",
        ["truefrench"] = "fr",
        ["vff"] = "fr",
        ["italian"] = "it",
        ["italiano"] = "it",
        ["ita"] = "it",
        ["german"] = "de",
        ["aleman"] = "de",
        ["alemán"] = "de",
        ["deutsch"] = "de",
        ["ger"] = "de",
        ["portuguese"] = "pt",
        ["portugues"] = "pt",
        ["português"] = "pt",
        ["brazilian"] = "pt",
        ["dublado"] = "pt",
        ["legendado"] = "pt",
        ["russian"] = "ru",
        ["ruso"] = "ru",
        ["japanese"] = "ja",
        ["japones"] = "ja",
        ["japonés"] = "ja",
        ["korean"] = "ko",
        ["coreano"] = "ko",
        ["chinese"] = "zh",
        ["chino"] = "zh",
        ["mandarin"] = "zh",
        ["canton"] = "zh",
        ["hindi"] = "hi",
        ["arabic"] = "ar",
        ["arabe"] = "ar",
        ["árabe"] = "ar",
        ["turkish"] = "tr",
        ["turco"] = "tr",
        ["polish"] = "pl",
        ["polaco"] = "pl",
        ["dutch"] = "nl",
        ["holandes"] = "nl",
        ["swedish"] = "sv",
        ["danish"] = "da",
        ["norwegian"] = "no",
        ["finnish"] = "fi",
        ["czech"] = "cs",
        ["checo"] = "cs",
        ["hungarian"] = "hu",
        ["hungaro"] = "hu",
        ["romanian"] = "ro",
        ["rumano"] = "ro",
        ["greek"] = "el",
        ["griego"] = "el",
        ["hebrew"] = "he",
        ["hebreo"] = "he",
        ["ukrainian"] = "uk",
        ["ucraniano"] = "uk",
        ["catalan"] = "ca",
        ["catalán"] = "ca",
        ["catala"] = "ca"
    };

    private static readonly Dictionary<string, string> ShortCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ES"] = "es",
        ["ESP"] = "es",
        ["SPA"] = "es",
        ["LAT"] = "es-419",
        ["VOSE"] = "vose",
        ["VOS"] = "vose",
        ["EN"] = "en",
        ["ENG"] = "en",
        ["FR"] = "fr",
        ["VFF"] = "fr",
        ["IT"] = "it",
        ["ITA"] = "it",
        ["DE"] = "de",
        ["GER"] = "de",
        ["PT"] = "pt",
        ["RU"] = "ru",
        ["JA"] = "ja",
        ["JP"] = "ja",
        ["KO"] = "ko",
        ["ZH"] = "zh",
        ["HI"] = "hi",
        ["AR"] = "ar",
        ["TR"] = "tr",
        ["PL"] = "pl",
        ["NL"] = "nl",
        ["SV"] = "sv",
        ["DA"] = "da",
        ["NO"] = "no",
        ["FI"] = "fi",
        ["CS"] = "cs",
        ["HU"] = "hu",
        ["RO"] = "ro",
        ["EL"] = "el",
        ["HE"] = "he",
        ["UK"] = "uk",
        ["CA"] = "ca",
        ["MULTI"] = "multi"
    };

    [GeneratedRegex(@"\b(es|en|fr|de|it|pt|ru|ja|ko|zh|hi|ar|tr|pl|nl|sv|da|no|fi|cs|hu|ro|el|he|uk|ca)[-_][a-z]{2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocaleRegex();

    [GeneratedRegex(@"[\[\(\{]([A-Za-z]{2,6})[\]\)\}]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketedCodeRegex();

    [GeneratedRegex(@"\b([A-Z]{2,6})\b", RegexOptions.CultureInvariant)]
    private static partial Regex UpperWordRegex();

    /// <summary>
    /// Detecta los idiomas presentes en uno o varios textos.
    /// </summary>
    /// <param name="texts">Textos donde buscar.</param>
    /// <returns>Códigos de idioma ordenados y sin duplicados.</returns>
    public static List<string> Detect(params string?[] texts)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in texts)
        {
            var text = TextUtil.Clean(raw);
            if (text.Length == 0)
            {
                continue;
            }

            var normalized = TextUtil.Normalize(text);

            foreach (var pair in Keywords)
            {
                if (ContainsWord(normalized, TextUtil.Normalize(pair.Key))
                    && seen.Add(pair.Value))
                {
                    found.Add(pair.Value);
                }
            }

            foreach (Match match in LocaleRegex().Matches(text))
            {
                var code = match.Groups[1].Value.ToLowerInvariant();
                if (seen.Add(code))
                {
                    found.Add(code);
                }
            }

            foreach (Match match in BracketedCodeRegex().Matches(text))
            {
                if (ShortCodes.TryGetValue(match.Groups[1].Value, out var code) && seen.Add(code))
                {
                    found.Add(code);
                }
            }

            foreach (Match match in UpperWordRegex().Matches(text))
            {
                if (ShortCodes.TryGetValue(match.Groups[1].Value, out var code) && seen.Add(code))
                {
                    found.Add(code);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Indica si un código de idioma coincide con alguno de los deseados.
    /// </summary>
    /// <param name="resultLanguages">Idiomas del resultado.</param>
    /// <param name="wanted">Idiomas deseados.</param>
    /// <returns>True si hay coincidencia.</returns>
    public static bool Matches(IEnumerable<string> resultLanguages, IEnumerable<string> wanted)
    {
        var wantedList = wanted.Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
        if (wantedList.Count == 0)
        {
            return true;
        }

        var languages = resultLanguages.ToList();
        if (languages.Contains("multi", StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var want in wantedList)
        {
            foreach (var language in languages)
            {
                if (language.Equals(want, StringComparison.OrdinalIgnoreCase)
                    || language.StartsWith(want + "-", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsWord(string haystack, string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return false;
        }

        var index = haystack.IndexOf(word, StringComparison.Ordinal);
        while (index >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(haystack[index - 1]);
            var afterIndex = index + word.Length;
            var afterOk = afterIndex >= haystack.Length || !char.IsLetterOrDigit(haystack[afterIndex]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            index = haystack.IndexOf(word, index + 1, StringComparison.Ordinal);
        }

        return false;
    }
}
