using System.Text;

namespace Jellyfin.Plugin.Torrent.Parsing;

/// <summary>
/// Tipo de valor bencode.
/// </summary>
public enum BencodeKind
{
    /// <summary>Entero.</summary>
    Integer,

    /// <summary>Cadena de bytes.</summary>
    Bytes,

    /// <summary>Lista.</summary>
    List,

    /// <summary>Diccionario.</summary>
    Dictionary
}

/// <summary>
/// Valor bencode con los desplazamientos del texto original (necesarios para
/// calcular el info-hash de un .torrent).
/// </summary>
public sealed class BencodeValue
{
    /// <summary>Gets el tipo de valor.</summary>
    public BencodeKind Kind { get; init; }

    /// <summary>Gets el valor entero.</summary>
    public long Integer { get; init; }

    /// <summary>Gets los bytes (texto en UTF-8 normalmente).</summary>
    public byte[]? Bytes { get; init; }

    /// <summary>Gets los elementos de la lista.</summary>
    public List<BencodeValue>? Items { get; init; }

    /// <summary>Gets las entradas del diccionario.</summary>
    public Dictionary<string, BencodeValue>? Entries { get; init; }

    /// <summary>Gets la posición inicial en el archivo original.</summary>
    public int Start { get; init; }

    /// <summary>Gets la posición final (exclusiva) en el archivo original.</summary>
    public int End { get; init; }

    /// <summary>
    /// Devuelve el valor como texto UTF-8.
    /// </summary>
    /// <returns>Texto o null.</returns>
    public string? AsString() => Bytes is null ? null : Encoding.UTF8.GetString(Bytes);

    /// <summary>
    /// Obtiene una entrada del diccionario.
    /// </summary>
    /// <param name="key">Clave.</param>
    /// <returns>Valor o null.</returns>
    public BencodeValue? Get(string key) =>
        Entries is not null && Entries.TryGetValue(key, out var value) ? value : null;
}

/// <summary>
/// Lector mínimo de bencode (suficiente para .torrent).
/// </summary>
public static class Bencode
{
    /// <summary>
    /// Parsea un documento bencode.
    /// </summary>
    /// <param name="data">Contenido del archivo.</param>
    /// <returns>Valor raíz.</returns>
    /// <exception cref="FormatException">Si el contenido no es bencode válido.</exception>
    public static BencodeValue Parse(byte[] data)
    {
        var index = 0;
        var value = ParseValue(data, ref index);
        return value;
    }

    private static BencodeValue ParseValue(byte[] data, ref int index)
    {
        if (index >= data.Length)
        {
            throw new FormatException("Bencode truncado.");
        }

        var start = index;
        var marker = (char)data[index];
        switch (marker)
        {
            case 'i':
            {
                index++;
                var end = Array.IndexOf(data, (byte)'e', index);
                if (end < 0)
                {
                    throw new FormatException("Entero bencode sin cierre.");
                }

                var text = Encoding.ASCII.GetString(data, index, end - index);
                if (!long.TryParse(text, out var number))
                {
                    throw new FormatException($"Entero bencode inválido: {text}");
                }

                index = end + 1;
                return new BencodeValue { Kind = BencodeKind.Integer, Integer = number, Start = start, End = index };
            }

            case 'l':
            {
                index++;
                var items = new List<BencodeValue>();
                while (index < data.Length && data[index] != 'e')
                {
                    items.Add(ParseValue(data, ref index));
                }

                index++;
                return new BencodeValue { Kind = BencodeKind.List, Items = items, Start = start, End = index };
            }

            case 'd':
            {
                index++;
                var entries = new Dictionary<string, BencodeValue>(StringComparer.Ordinal);
                while (index < data.Length && data[index] != 'e')
                {
                    var key = ParseValue(data, ref index);
                    if (key.Kind != BencodeKind.Bytes)
                    {
                        throw new FormatException("Clave bencode no textual.");
                    }

                    var value = ParseValue(data, ref index);
                    entries[key.AsString() ?? string.Empty] = value;
                }

                index++;
                return new BencodeValue { Kind = BencodeKind.Dictionary, Entries = entries, Start = start, End = index };
            }

            default:
            {
                var colon = Array.IndexOf(data, (byte)':', index);
                if (colon < 0)
                {
                    throw new FormatException("Cadena bencode sin longitud.");
                }

                var lengthText = Encoding.ASCII.GetString(data, index, colon - index);
                if (!int.TryParse(lengthText, out var length) || length < 0)
                {
                    throw new FormatException($"Longitud bencode inválida: {lengthText}");
                }

                var contentStart = colon + 1;
                if (contentStart + length > data.Length)
                {
                    throw new FormatException("Cadena bencode truncada.");
                }

                var bytes = new byte[length];
                Array.Copy(data, contentStart, bytes, 0, length);
                index = contentStart + length;
                return new BencodeValue { Kind = BencodeKind.Bytes, Bytes = bytes, Start = start, End = index };
            }
        }
    }
}
