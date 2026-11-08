namespace Jellyfin.Plugin.Torrent.Models;

/// <summary>
/// Un enlace torrent encontrado, con toda la información deducible de la web.
/// </summary>
public class TorrentResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TorrentResult"/> class.
    /// </summary>
    public TorrentResult()
    {
        Id = string.Empty;
        SourceName = string.Empty;
        Title = string.Empty;
        Languages = new List<string>();
        Extra = new List<NameValueEntry>();
    }

    /// <summary>Gets or sets el identificador estable del resultado.</summary>
    public string Id { get; set; }

    /// <summary>Gets or sets el id de la fuente de origen.</summary>
    public Guid SourceId { get; set; }

    /// <summary>Gets or sets el nombre de la fuente de origen.</summary>
    public string SourceName { get; set; }

    /// <summary>Gets or sets el título del contenido (película o serie).</summary>
    public string Title { get; set; }

    /// <summary>Gets or sets el nombre completo del release (con calidad y grupo).</summary>
    public string? ReleaseName { get; set; }

    /// <summary>Gets or sets la URL de la ficha en la web.</summary>
    public string? DetailUrl { get; set; }

    /// <summary>Gets or sets el enlace magnet, si la web lo publica.</summary>
    public string? Magnet { get; set; }

    /// <summary>Gets or sets la URL del archivo .torrent, si la web lo publica.</summary>
    public string? TorrentUrl { get; set; }

    /// <summary>Gets or sets el info-hash del torrent (si se conoce).</summary>
    public string? InfoHash { get; set; }

    /// <summary>Gets or sets el tamaño en bytes.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>Gets or sets el tamaño tal y como lo muestra la web.</summary>
    public string? SizeText { get; set; }

    /// <summary>Gets or sets las semillas (fuentes disponibles).</summary>
    public int? Seeds { get; set; }

    /// <summary>Gets or sets los clientes (leeches).</summary>
    public int? Leeches { get; set; }

    /// <summary>Gets or sets las descargas completadas.</summary>
    public int? Completed { get; set; }

    /// <summary>Gets or sets el año del contenido.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets la duración en minutos, si la web la publica.</summary>
    public int? RuntimeMinutes { get; set; }

    /// <summary>Gets or sets la fecha de publicación.</summary>
    public DateTimeOffset? Published { get; set; }

    /// <summary>Gets or sets la fecha tal y como la muestra la web.</summary>
    public string? PublishedText { get; set; }

    /// <summary>Gets or sets la URL de la carátula.</summary>
    public string? PosterUrl { get; set; }

    /// <summary>Gets or sets la categoría o tipo según la web.</summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets el tipo de contenido deducido.</summary>
    public MediaKind Kind { get; set; }

    /// <summary>Gets or sets los idiomas detectados (es, en, multi…).</summary>
    public List<string> Languages { get; set; }

    /// <summary>Gets or sets el idioma tal y como lo indica la web.</summary>
    public string? LanguageText { get; set; }

    /// <summary>Gets or sets la resolución (2160p, 1080p, 720p…).</summary>
    public string? Resolution { get; set; }

    /// <summary>Gets or sets el medio de origen (BluRay, WEB-DL, HDTV, DVD…).</summary>
    public string? Medium { get; set; }

    /// <summary>Gets or sets el códec de vídeo (x264, x265, AV1…).</summary>
    public string? VideoCodec { get; set; }

    /// <summary>Gets or sets el códec de audio (AC3, DTS, AAC…).</summary>
    public string? AudioCodec { get; set; }

    /// <summary>Gets or sets los canales de audio (5.1, 7.1, 2.0…).</summary>
    public string? AudioChannels { get; set; }

    /// <summary>Gets or sets el grupo de release.</summary>
    public string? ReleaseGroup { get; set; }

    /// <summary>Gets or sets el contenedor (mkv, mp4, avi…).</summary>
    public string? Container { get; set; }

    /// <summary>Gets or sets a value indicating whether es HDR/Dolby Vision.</summary>
    public bool IsHdr { get; set; }

    /// <summary>Gets or sets a value indicating whether es un remux.</summary>
    public bool IsRemux { get; set; }

    /// <summary>Gets or sets a value indicating whether es 3D.</summary>
    public bool Is3D { get; set; }

    /// <summary>Gets or sets la temporada (series).</summary>
    public int? Season { get; set; }

    /// <summary>Gets or sets el episodio (series).</summary>
    public int? Episode { get; set; }

    /// <summary>Gets or sets el texto de episodio (S01E02, Temporada 3…).</summary>
    public string? EpisodeText { get; set; }

    /// <summary>Gets or sets la sinopsis o descripción de la web.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets datos adicionales de la ficha (director, género…).</summary>
    public List<NameValueEntry> Extra { get; set; }

    /// <summary>Gets or sets la puntuación de relevancia calculada (0-100).</summary>
    public double Score { get; set; }

    /// <summary>
    /// Gets a value indicating whether hay algún enlace utilizable.
    /// </summary>
    public bool HasLink => !string.IsNullOrWhiteSpace(Magnet) || !string.IsNullOrWhiteSpace(TorrentUrl);
}
