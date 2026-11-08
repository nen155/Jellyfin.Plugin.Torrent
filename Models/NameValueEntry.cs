namespace Jellyfin.Plugin.Torrent.Models;

/// <summary>
/// Par nombre/valor serializable a XML (los diccionarios no lo son).
/// </summary>
public class NameValueEntry
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NameValueEntry"/> class.
    /// </summary>
    public NameValueEntry()
    {
        Name = string.Empty;
        Value = string.Empty;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NameValueEntry"/> class.
    /// </summary>
    /// <param name="name">Nombre o clave.</param>
    /// <param name="value">Valor.</param>
    public NameValueEntry(string name, string value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>
    /// Gets or sets el nombre o clave.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Gets or sets el valor.
    /// </summary>
    public string Value { get; set; }
}
