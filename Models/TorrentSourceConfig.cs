namespace Jellyfin.Plugin.Torrent.Models;

/// <summary>
/// Configuración de una fuente (web de torrents). Los selectores son CSS y
/// admiten el sufijo <c>@atributo</c> para leer un atributo concreto
/// (p. ej. <c>a.title@href</c>); sin sufijo se usa el texto del elemento.
/// </summary>
public class TorrentSourceConfig
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TorrentSourceConfig"/> class.
    /// </summary>
    public TorrentSourceConfig()
    {
        Id = Guid.NewGuid();
        Name = string.Empty;
        BaseUrl = string.Empty;
        SearchUrl = string.Empty;
        SearchBody = string.Empty;
        Headers = new List<NameValueEntry>();
        JsonFields = new List<NameValueEntry>();
        Domain = new DomainResolverConfig();
        MaxPages = 1;
        DelayMs = 300;
        Kind = SourceKind.Html;
        SearchMethod = SearchHttpMethod.Get;
        FetchDetails = true;
        FetchMode = FetchMode.Http;
        DetailFetchMode = FetchMode.Inherit;
        BrowserWaitMs = 1200;
        DetailBrowserWaitMs = 2500;
        ResolveReleaseLinks = true;
    }

    /// <summary>Gets or sets el identificador interno de la fuente.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets el nombre visible de la fuente.</summary>
    public string Name { get; set; }

    /// <summary>Gets or sets a value indicating whether la fuente está activa.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets el tipo de extracción.</summary>
    public SourceKind Kind { get; set; }

    /// <summary>Gets or sets la URL base (p. ej. <c>https://ejemplo.to</c>).</summary>
    public string BaseUrl { get; set; }

    /// <summary>
    /// Gets or sets la plantilla de búsqueda. Admite <c>{query}</c> (texto
    /// codificado para URL) y <c>{page}</c>.
    /// </summary>
    public string SearchUrl { get; set; }

    /// <summary>Gets or sets el método HTTP de la búsqueda.</summary>
    public SearchHttpMethod SearchMethod { get; set; }

    /// <summary>
    /// Gets or sets el cuerpo del formulario para POST
    /// (p. ej. <c>valor={query}</c>). Admite <c>{query}</c> y <c>{page}</c>.
    /// </summary>
    public string SearchBody { get; set; }

    /// <summary>Gets or sets cabeceras HTTP extra (Referer, Accept-Language…).</summary>
    public List<NameValueEntry> Headers { get; set; }

    /// <summary>
    /// Gets or sets la cookie de sesión (para webs que la exigen). Se envía
    /// tal cual en la cabecera <c>Cookie</c>.
    /// </summary>
    public string? Cookie { get; set; }

    /// <summary>Gets or sets el número de páginas de resultados a recorrer.</summary>
    public int MaxPages { get; set; }

    /// <summary>Gets or sets la pausa entre peticiones a esta fuente (ms).</summary>
    public int DelayMs { get; set; }

    /// <summary>
    /// Gets or sets el motor de la búsqueda: HTTP, navegador real o automático.
    /// </summary>
    public FetchMode FetchMode { get; set; }

    /// <summary>
    /// Gets or sets el motor de la ficha de detalle (Inherit = el de la búsqueda).
    /// </summary>
    public FetchMode DetailFetchMode { get; set; }

    /// <summary>
    /// Gets or sets el selector que debe aparecer para dar por cargada la
    /// página cuando se usa el navegador (p. ej. la lista de resultados).
    /// </summary>
    public string? BrowserWaitSelector { get; set; }

    /// <summary>Gets or sets la espera extra tras cargar la página (ms).</summary>
    public int BrowserWaitMs { get; set; }

    /// <summary>
    /// Gets or sets el JavaScript que se ejecuta tras cargar la página de
    /// búsqueda. Admite <c>{query}</c>, p. ej. rellenar el buscador de la web.
    /// </summary>
    public string? BrowserScript { get; set; }

    /// <summary>Gets or sets el selector a esperar en la ficha de detalle.</summary>
    public string? DetailWaitSelector { get; set; }

    /// <summary>Gets or sets la espera extra tras cargar la ficha (ms).</summary>
    public int DetailBrowserWaitMs { get; set; }

    /// <summary>
    /// Gets or sets el JavaScript que se ejecuta en la ficha de detalle
    /// (p. ej. pulsar el botón de descarga de la web).
    /// </summary>
    public string? DetailBrowserScript { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether los enlaces de la ficha que
    /// apuntan a otra página (no a un .torrent) se abren para extraer el magnet.
    /// </summary>
    public bool ResolveReleaseLinks { get; set; }

    // ------------------------- Extracción HTML -------------------------

    /// <summary>Gets or sets el selector de cada fila de resultado.</summary>
    public string? RowSelector { get; set; }

    /// <summary>Gets or sets el selector del título.</summary>
    public string? TitleSelector { get; set; }

    /// <summary>Gets or sets el selector del enlace a la ficha de detalle.</summary>
    public string? LinkSelector { get; set; }

    /// <summary>Gets or sets el selector del enlace magnet dentro de la fila.</summary>
    public string? MagnetSelector { get; set; }

    /// <summary>Gets or sets el selector del enlace .torrent dentro de la fila.</summary>
    public string? TorrentSelector { get; set; }

    /// <summary>Gets or sets el selector del tamaño.</summary>
    public string? SizeSelector { get; set; }

    /// <summary>Gets or sets el selector de semillas (seeds).</summary>
    public string? SeedsSelector { get; set; }

    /// <summary>Gets or sets el selector de clientes (leeches).</summary>
    public string? LeechesSelector { get; set; }

    /// <summary>Gets or sets el selector de la fecha de publicación.</summary>
    public string? DateSelector { get; set; }

    /// <summary>Gets or sets el selector de descargas completadas.</summary>
    public string? CompletedSelector { get; set; }

    /// <summary>Gets or sets el selector de la carátula.</summary>
    public string? PosterSelector { get; set; }

    /// <summary>Gets or sets el selector de la categoría o tipo.</summary>
    public string? CategorySelector { get; set; }

    /// <summary>Gets or sets el selector del idioma indicado por la web.</summary>
    public string? LanguageSelector { get; set; }

    // ------------------------ Ficha de detalle ------------------------

    /// <summary>
    /// Gets or sets a value indicating whether se visita la ficha de detalle
    /// para completar magnet, carátula, idioma, sinopsis…
    /// </summary>
    public bool FetchDetails { get; set; }

    /// <summary>Gets or sets el selector del magnet en la ficha de detalle.</summary>
    public string? DetailMagnetSelector { get; set; }

    /// <summary>Gets or sets el selector del .torrent en la ficha de detalle.</summary>
    public string? DetailTorrentSelector { get; set; }

    /// <summary>Gets or sets el selector de la carátula en la ficha.</summary>
    public string? DetailPosterSelector { get; set; }

    /// <summary>Gets or sets el selector de la sinopsis en la ficha.</summary>
    public string? DetailDescriptionSelector { get; set; }

    /// <summary>Gets or sets el selector del tamaño en la ficha.</summary>
    public string? DetailSizeSelector { get; set; }

    /// <summary>Gets or sets el selector de la fecha en la ficha.</summary>
    public string? DetailDateSelector { get; set; }

    /// <summary>Gets or sets el selector del idioma en la ficha.</summary>
    public string? DetailLanguageSelector { get; set; }

    /// <summary>Gets or sets el selector de semillas en la ficha.</summary>
    public string? DetailSeedsSelector { get; set; }

    /// <summary>Gets or sets el selector de clientes en la ficha.</summary>
    public string? DetailLeechesSelector { get; set; }

    // ------------------------ Extracción JSON -------------------------

    /// <summary>
    /// Gets or sets la ruta al array de resultados dentro del JSON
    /// (p. ej. <c>data.items</c> o <c>$</c> para la raíz).
    /// </summary>
    public string? JsonResultsPath { get; set; }

    /// <summary>
    /// Gets or sets el mapa campo → ruta JSON. Claves admitidas: title, id,
    /// detailUrl, magnet, torrentUrl, size, seeds, leeches, date, poster,
    /// category, language. Las rutas admiten índices (<c>[0].name</c>).
    /// </summary>
    public List<NameValueEntry> JsonFields { get; set; }

    /// <summary>
    /// Gets or sets el prefijo a eliminar antes de parsear el JSON
    /// (para respuestas tipo <c>var datos = [...]</c>).
    /// </summary>
    public string? JsonStripPrefix { get; set; }

    /// <summary>
    /// Gets or sets el campo por el que filtrar localmente cuando la fuente
    /// devuelve un catálogo completo en vez de resultados de búsqueda.
    /// </summary>
    public string? JsonFilterField { get; set; }

    /// <summary>
    /// Gets or sets la plantilla de la ficha de detalle para fuentes JSON.
    /// Admite los campos extraídos entre llaves (<c>{id}</c>, <c>{title}</c>).
    /// </summary>
    public string? DetailUrlTemplate { get; set; }

    /// <summary>Gets or sets la resolución automática del dominio.</summary>
    public DomainResolverConfig Domain { get; set; }

    /// <summary>
    /// Crea una copia independiente de la configuración (para pruebas y
    /// ediciones sin tocar la configuración activa).
    /// </summary>
    /// <returns>Copia.</returns>
    public TorrentSourceConfig Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        Kind = Kind,
        BaseUrl = BaseUrl,
        SearchUrl = SearchUrl,
        SearchMethod = SearchMethod,
        SearchBody = SearchBody,
        Headers = Headers.Select(h => new NameValueEntry(h.Name, h.Value)).ToList(),
        Cookie = Cookie,
        MaxPages = MaxPages,
        DelayMs = DelayMs,
        FetchMode = FetchMode,
        DetailFetchMode = DetailFetchMode,
        BrowserWaitSelector = BrowserWaitSelector,
        BrowserWaitMs = BrowserWaitMs,
        BrowserScript = BrowserScript,
        DetailWaitSelector = DetailWaitSelector,
        DetailBrowserWaitMs = DetailBrowserWaitMs,
        DetailBrowserScript = DetailBrowserScript,
        ResolveReleaseLinks = ResolveReleaseLinks,
        RowSelector = RowSelector,
        TitleSelector = TitleSelector,
        LinkSelector = LinkSelector,
        MagnetSelector = MagnetSelector,
        TorrentSelector = TorrentSelector,
        SizeSelector = SizeSelector,
        SeedsSelector = SeedsSelector,
        LeechesSelector = LeechesSelector,
        DateSelector = DateSelector,
        CompletedSelector = CompletedSelector,
        PosterSelector = PosterSelector,
        CategorySelector = CategorySelector,
        LanguageSelector = LanguageSelector,
        FetchDetails = FetchDetails,
        DetailMagnetSelector = DetailMagnetSelector,
        DetailTorrentSelector = DetailTorrentSelector,
        DetailPosterSelector = DetailPosterSelector,
        DetailDescriptionSelector = DetailDescriptionSelector,
        DetailSizeSelector = DetailSizeSelector,
        DetailDateSelector = DetailDateSelector,
        DetailLanguageSelector = DetailLanguageSelector,
        DetailSeedsSelector = DetailSeedsSelector,
        DetailLeechesSelector = DetailLeechesSelector,
        JsonResultsPath = JsonResultsPath,
        JsonFields = JsonFields.Select(f => new NameValueEntry(f.Name, f.Value)).ToList(),
        JsonStripPrefix = JsonStripPrefix,
        JsonFilterField = JsonFilterField,
        DetailUrlTemplate = DetailUrlTemplate,
        Domain = new DomainResolverConfig
        {
            Provider = Domain.Provider,
            Url = Domain.Url,
            Regex = Domain.Regex,
            DiscordBotToken = Domain.DiscordBotToken,
            DiscordChannelId = Domain.DiscordChannelId,
            CacheMinutes = Domain.CacheMinutes,
            AutoApply = Domain.AutoApply
        }
    };
}
