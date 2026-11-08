using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Torrent.Browser;

/// <summary>
/// Guarda, por resultado, la sesión del navegador (cookies y referer) para
/// poder descargar después el .torrent protegido y enviarlo al cliente.
/// </summary>
public sealed class SessionStore
{
    private const int MaxEntries = 500;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(90);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registra la sesión de un resultado.
    /// </summary>
    /// <param name="resultId">Id del resultado.</param>
    /// <param name="entry">Datos de sesión.</param>
    public void Register(string resultId, Entry entry)
    {
        if (string.IsNullOrWhiteSpace(resultId))
        {
            return;
        }

        entry.Expires = DateTimeOffset.UtcNow.Add(Ttl);
        _entries[resultId] = entry;

        if (_entries.Count > MaxEntries)
        {
            Prune();
        }
    }

    /// <summary>
    /// Recupera la sesión de un resultado.
    /// </summary>
    /// <param name="resultId">Id del resultado.</param>
    /// <param name="entry">Datos de sesión.</param>
    /// <returns>True si existe y no ha caducado.</returns>
    public bool TryGet(string resultId, out Entry entry)
    {
        if (_entries.TryGetValue(resultId, out entry!) && entry.Expires > DateTimeOffset.UtcNow)
        {
            return true;
        }

        _entries.TryRemove(resultId, out _);
        entry = new Entry();
        return false;
    }

    /// <summary>
    /// Elimina las entradas caducadas.
    /// </summary>
    public void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _entries.Where(p => p.Value.Expires <= now).ToList())
        {
            _entries.TryRemove(pair.Key, out _);
        }

        while (_entries.Count > MaxEntries)
        {
            var oldest = _entries.OrderBy(p => p.Value.Expires).FirstOrDefault();
            if (oldest.Key is null)
            {
                break;
            }

            _entries.TryRemove(oldest.Key, out _);
        }
    }

    /// <summary>
    /// Datos de sesión asociados a un resultado.
    /// </summary>
    public sealed class Entry
    {
        /// <summary>Gets or sets la fuente.</summary>
        public Guid SourceId { get; set; }

        /// <summary>Gets or sets la cookie de la sesión del navegador.</summary>
        public string? Cookie { get; set; }

        /// <summary>Gets or sets el referer a usar al descargar el .torrent.</summary>
        public string? Referer { get; set; }

        /// <summary>Gets or sets la URL del .torrent descubierta.</summary>
        public string? TorrentUrl { get; set; }

        /// <summary>Gets or sets el momento de caducidad.</summary>
        public DateTimeOffset Expires { get; set; }
    }
}
