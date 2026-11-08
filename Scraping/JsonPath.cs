using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.Torrent.Scraping;

/// <summary>
/// Navegación mínima por rutas JSON: <c>data.items</c>, <c>[0].name</c>,
/// <c>results[2].magnet</c>.
/// </summary>
public static class JsonPath
{
    /// <summary>
    /// Selecciona un elemento por ruta.
    /// </summary>
    /// <param name="root">Elemento raíz.</param>
    /// <param name="path">Ruta.</param>
    /// <returns>Elemento o null.</returns>
    public static JsonElement? Select(JsonElement root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var tokens = Tokenize(path);
        if (tokens.Count == 0)
        {
            return root;
        }

        JsonElement? current = root;
        foreach (var token in tokens)
        {
            if (current is null)
            {
                return null;
            }

            var element = current.Value;
            if (token.Index is not null)
            {
                if (element.ValueKind != JsonValueKind.Array
                    || token.Index.Value >= element.GetArrayLength())
                {
                    return null;
                }

                current = element[token.Index.Value];
            }
            else
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (element.TryGetProperty(token.Name!, out var property))
                {
                    current = property;
                    continue;
                }

                JsonElement? found = null;
                foreach (var candidate in element.EnumerateObject())
                {
                    if (candidate.Name.Equals(token.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        found = candidate.Value;
                        break;
                    }
                }

                if (found is null)
                {
                    return null;
                }

                current = found;
            }
        }

        return current;
    }

    /// <summary>
    /// Selecciona un valor escalar como texto.
    /// </summary>
    /// <param name="root">Elemento raíz.</param>
    /// <param name="path">Ruta.</param>
    /// <returns>Texto o null.</returns>
    public static string? SelectString(JsonElement root, string? path)
    {
        var element = Select(root, path);
        return element is null ? null : ToText(element.Value);
    }

    /// <summary>
    /// Selecciona una colección de elementos.
    /// </summary>
    /// <param name="root">Elemento raíz.</param>
    /// <param name="path">Ruta al array (vacío = raíz).</param>
    /// <returns>Elementos del array.</returns>
    public static List<JsonElement> SelectArray(JsonElement root, string? path)
    {
        var element = string.IsNullOrWhiteSpace(path) || path.Trim() == "$"
            ? root
            : Select(root, path);

        if (element is null)
        {
            return new List<JsonElement>();
        }

        return element.Value.ValueKind switch
        {
            JsonValueKind.Array => element.Value.EnumerateArray().ToList(),
            JsonValueKind.Object => new List<JsonElement> { element.Value },
            _ => new List<JsonElement>()
        };
    }

    /// <summary>
    /// Convierte un elemento JSON en texto (los arrays se unen con comas).
    /// </summary>
    /// <param name="element">Elemento.</param>
    /// <returns>Texto o null.</returns>
    public static string? ToText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => string.Join(
            ", ",
            element.EnumerateArray()
                .Select(ToText)
                .Where(v => !string.IsNullOrWhiteSpace(v))),
        JsonValueKind.Object => element.TryGetProperty("name", out var name)
            ? ToText(name)
            : element.GetRawText(),
        _ => null
    };

    /// <summary>
    /// Elimina un prefijo (y punto y coma final) antes de parsear el JSON.
    /// </summary>
    /// <param name="content">Respuesta.</param>
    /// <param name="prefix">Prefijo a eliminar.</param>
    /// <returns>JSON limpio.</returns>
    public static string StripPrefix(string content, string? prefix)
    {
        var text = content.Trim();
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            var index = text.IndexOf(prefix.Trim(), StringComparison.Ordinal);
            if (index >= 0)
            {
                text = text[(index + prefix.Trim().Length)..];
            }
        }

        text = text.Trim().TrimEnd(';').Trim();

        var start = text.IndexOfAny(['{', '[']);
        if (start > 0)
        {
            text = text[start..];
        }

        var end = Math.Max(text.LastIndexOf('}'), text.LastIndexOf(']'));
        if (end >= 0 && end < text.Length - 1)
        {
            text = text[..(end + 1)];
        }

        return text;
    }

    private static List<Token> Tokenize(string path)
    {
        var tokens = new List<Token>();
        var trimmed = path.Trim();
        if (trimmed.StartsWith('$'))
        {
            trimmed = trimmed[1..];
        }

        var buffer = new StringBuilder();
        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            switch (ch)
            {
                case '.':
                    if (buffer.Length > 0)
                    {
                        tokens.Add(new Token(buffer.ToString(), null));
                        buffer.Clear();
                    }

                    break;
                case '[':
                {
                    if (buffer.Length > 0)
                    {
                        tokens.Add(new Token(buffer.ToString(), null));
                        buffer.Clear();
                    }

                    var close = trimmed.IndexOf(']', i);
                    if (close < 0)
                    {
                        return tokens;
                    }

                    var inside = trimmed[(i + 1)..close].Trim().Trim('"', '\'');
                    if (int.TryParse(inside, out var index))
                    {
                        tokens.Add(new Token(null, index));
                    }
                    else if (inside is "*" or "")
                    {
                        tokens.Add(new Token(null, 0));
                    }
                    else
                    {
                        tokens.Add(new Token(inside, null));
                    }

                    i = close;
                    break;
                }

                default:
                    buffer.Append(ch);
                    break;
            }
        }

        if (buffer.Length > 0)
        {
            tokens.Add(new Token(buffer.ToString(), null));
        }

        return tokens;
    }

    private sealed record Token(string? Name, int? Index);
}
