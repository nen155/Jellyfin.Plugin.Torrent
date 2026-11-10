using Jellyfin.Plugin.Torrent.Models;

namespace Jellyfin.Plugin.Torrent.Services;

/// <summary>
/// Plantilla de fuente lista para añadir desde la configuración.
/// </summary>
public sealed class SourceTemplate
{
    /// <summary>Gets or sets la clave interna.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets el nombre visible.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets la descripción.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether los selectores se han
    /// comprobado contra la web en el momento de crear la plantilla.
    /// </summary>
    public bool Verified { get; set; }

    /// <summary>Gets or sets la configuración de la fuente.</summary>
    public TorrentSourceConfig Source { get; set; } = new();
}

/// <summary>
/// Plantillas incluidas con el plugin. Todas son editables: si una web cambia
/// su HTML basta con ajustar los selectores con el botón "Probar".
/// </summary>
public static class SourceTemplates
{
    /// <summary>
    /// Devuelve todas las plantillas.
    /// </summary>
    /// <returns>Plantillas.</returns>
    public static IReadOnlyList<SourceTemplate> All() => new List<SourceTemplate>
    {
        new()
        {
            Key = "nyaa",
            Name = "Nyaa",
            Verified = true,
            Description = "Anime y contenido asiático. Publica magnet y .torrent en la propia lista de resultados.",
            Source = new TorrentSourceConfig
            {
                Name = "Nyaa",
                Enabled = false,
                BaseUrl = "https://nyaa.si",
                SearchUrl = "{base}/?f=0&c=0_0&q={query}&p={page}",
                RowSelector = "table.torrent-list tbody tr",
                TitleSelector = "td a[href^='/view/']:not(.comments)",
                LinkSelector = "td a[href^='/view/']@href",
                MagnetSelector = "a[href^='magnet:']@href",
                TorrentSelector = "a[href^='/download/']@href",
                SizeSelector = "td:nth-child(4)",
                DateSelector = "td:nth-child(5)",
                SeedsSelector = "td:nth-child(6)",
                LeechesSelector = "td:nth-child(7)",
                CategorySelector = "td:first-child img@alt",
                FetchDetails = false
            }
        },
        new()
        {
            Key = "1337x",
            Name = "1337x",
            Verified = false,
            Description = "Índice generalista con magnet en la propia fila. (Selectores según su HTML habitual; "
                + "si tu red recibe 403 de Cloudflare, necesitarás una cookie de sesión.)",
            Source = new TorrentSourceConfig
            {
                Name = "1337x",
                Enabled = false,
                BaseUrl = "https://1337x.to",
                SearchUrl = "{base}/search/{query}/{page}/",
                RowSelector = "table.table-list tbody tr",
                TitleSelector = "td.name a[href^='/torrent/']",
                LinkSelector = "td.name a[href^='/torrent/']@href",
                MagnetSelector = "td.name a[href^='magnet:']@href",
                SizeSelector = "td.size",
                SeedsSelector = "td.seeds",
                LeechesSelector = "td.leeches",
                DateSelector = "td.coll-date",
                FetchDetails = false
            }
        },
        new()
        {
            Key = "yts",
            Name = "YTS",
            Verified = false,
            Description = "Películas en 720p/1080p/2160p. La lista no trae enlaces: se abren las fichas "
                + "para extraer todos los magnet y .torrent de cada calidad.",
            Source = new TorrentSourceConfig
            {
                Name = "YTS",
                Enabled = false,
                BaseUrl = "https://yts.mx",
                SearchUrl = "{base}/browse-movies/{query}/all/all/0/latest/0/all",
                RowSelector = ".browse-movie-wrap",
                TitleSelector = ".browse-movie-title",
                LinkSelector = "a.browse-movie-link@href",
                PosterSelector = "img@src",
                CategorySelector = ".browse-movie-year",
                FetchDetails = true,
                DetailMagnetSelector = "a[href^='magnet:']@href",
                DetailTorrentSelector = "a[href*='/torrent/download/']@href"
            }
        },
        new()
        {
            Key = "dontorrent",
            Name = "DonTorrent (metadatos)",
            Verified = true,
            Description = "Búsqueda y metadatos en español. El botón de descarga está protegido con "
                + "proof-of-work: el plugin resuelve el reto por HTTP (sin navegador) y obtiene el "
                + ".torrent al instante. Trae configuradas las listas de novedades de películas y "
                + "series para las sugerencias del día.",
            Source = new TorrentSourceConfig
            {
                Name = "DonTorrent",
                Enabled = false,
                BaseUrl = "https://dontorrent.tips",
                SearchUrl = "{base}/buscar",
                SearchMethod = SearchHttpMethod.Post,
                SearchBody = "valor={query}",
                RowSelector = "li.search-result",
                TitleSelector = "a.search-result-link",
                LinkSelector = "a.search-result-link@href",
                CategorySelector = ".badge",
                LatestMoviesUrl = "{base}/peliculas",
                LatestSeriesUrl = "{base}/series",
                LatestRowSelector = ".noticiasContent a[href*='/pelicula/'], .noticiasContent a[href*='/serie/']",
                LatestTitleSelector = "@self",
                LatestLinkSelector = "@self@href",
                LatestPosterSelector = "img@src",
                PowUrl = "{base}/api_validate_pow.php",
                PowContentIdSelector = ".protected-download@data-content-id",
                PowTableSelector = ".protected-download@data-tabla",
                PowDifficulty = 3,
                PowDownloadField = "download_url",
                FetchDetails = true,
                DetailFetchMode = FetchMode.Browser,
                DetailBrowserWaitMs = 9000,
                DetailBrowserScript = "(function(){var b=document.querySelector('.protected-download');if(!b){return 'sin-boton';}b.click();return 'pulsado';})()",
                ResolveReleaseLinks = false,
                DetailPosterSelector = "img.img-thumbnail@src",
                DetailDescriptionSelector = "meta[name='description']@content",
                Domain = new DomainResolverConfig
                {
                    Provider = DomainProvider.None,
                    CacheMinutes = 60,
                    AutoApply = true
                }
            }
        },
        new()
        {
            Key = "watchsomuch",
            Name = "WatchSoMuch",
            Verified = true,
            Description = "Buscador 100 % JavaScript: necesita el navegador headless. La ficha enlaza a una "
                + "página de descarga por calidad; el plugin las abre y extrae el magnet de cada una. Trae "
                + "configuradas las listas de novedades de películas y series para las sugerencias del día.",
            Source = new TorrentSourceConfig
            {
                Name = "WatchSoMuch",
                Enabled = false,
                BaseUrl = "https://watchsomuch.to",
                SearchUrl = "{base}/Movies/?Search={query}",
                FetchMode = FetchMode.Auto,
                BrowserWaitSelector = ".MediumMovie",
                BrowserWaitMs = 1500,
                RowSelector = ".MediumMovie",
                TitleSelector = ".header .title",
                LinkSelector = "a.thumbnailHolder@href",
                PosterSelector = "img.poster@src",
                CategorySelector = ".quality",
                LanguageSelector = ".language@title",
                DateSelector = ".releaseDate@title",
                CompletedSelector = ".downloads@title",
                LatestMoviesUrl = "{base}/Movies/",
                LatestSeriesUrl = "{base}/Movies/Series/",
                LatestTitleSelector = ".header .title@ownText",
                LatestPosterSelector = "a.thumbnailHolder img@src",
                FetchDetails = true,
                DetailFetchMode = FetchMode.Http,
                DetailTorrentSelector = "a[href*='/Download-']@href",
                ResolveReleaseLinks = true,
                MaxPages = 1,
                DelayMs = 400
            }
        },
        new()
        {
            Key = "torznab",
            Name = "Torznab (Jackett / Prowlarr)",
            Verified = false,
            Description = "Cualquier índice con API Torznab. Pega la URL de búsqueda que te da Jackett o "
                + "Prowlarr sustituyendo la consulta por {query} (incluye la apikey).",
            Source = new TorrentSourceConfig
            {
                Name = "Torznab",
                Enabled = false,
                Kind = SourceKind.Torznab,
                BaseUrl = "http://localhost:9696",
                SearchUrl = "{base}/api/v1/search?apikey=CAMBIAME&t=search&q={query}",
                FetchDetails = false,
                MaxPages = 1,
                DelayMs = 0
            }
        },
        new()
        {
            Key = "html-generic",
            Name = "Plantilla HTML genérica",
            Verified = false,
            Description = "Punto de partida para cualquier web: rellena la URL de búsqueda y los selectores "
                + "CSS. Puedes usar la forma «selector@atributo» (por ejemplo a.title@href).",
            Source = new TorrentSourceConfig
            {
                Name = "Mi web",
                Enabled = false,
                BaseUrl = "https://ejemplo.org",
                SearchUrl = "{base}/search?q={query}&page={page}",
                RowSelector = ".result",
                TitleSelector = ".title",
                LinkSelector = "a@href",
                MagnetSelector = "a[href^='magnet:']@href",
                TorrentSelector = "a[href$='.torrent']@href",
                SizeSelector = ".size",
                SeedsSelector = ".seeds",
                LeechesSelector = ".leeches",
                DateSelector = ".date",
                PosterSelector = "img@src",
                CategorySelector = ".category",
                FetchDetails = true,
                DetailMagnetSelector = "a[href^='magnet:']@href",
                DetailTorrentSelector = "a[href$='.torrent']@href",
                DetailPosterSelector = "img.poster@src",
                DetailDescriptionSelector = ".synopsis",
                DetailSizeSelector = ".size",
                DetailDateSelector = ".date",
                DetailLanguageSelector = ".language",
                DetailSeedsSelector = ".seeds",
                DetailLeechesSelector = ".leeches"
            }
        },
        new()
        {
            Key = "json-generic",
            Name = "Plantilla JSON genérica",
            Verified = false,
            Description = "Para webs con API propia. Indica la ruta al array de resultados y el mapa "
                + "campo = ruta JSON (admite índices: [0].name).",
            Source = new TorrentSourceConfig
            {
                Name = "Mi API",
                Enabled = false,
                Kind = SourceKind.Json,
                BaseUrl = "https://ejemplo.org",
                SearchUrl = "{base}/api/search?q={query}",
                JsonResultsPath = "results",
                JsonFields = new List<NameValueEntry>
                {
                    new("title", "name"),
                    new("id", "id"),
                    new("magnet", "magnet"),
                    new("torrentUrl", "torrent"),
                    new("size", "size"),
                    new("seeds", "seeders"),
                    new("leeches", "leechers"),
                    new("date", "added"),
                    new("poster", "poster"),
                    new("category", "category"),
                    new("language", "language")
                },
                DetailUrlTemplate = "{base}/item/{id}",
                FetchDetails = false
            }
        }
    };
}
