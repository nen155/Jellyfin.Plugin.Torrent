using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Dom;
using Jellyfin.Plugin.Torrent.Models;
using Jellyfin.Plugin.Torrent.Parsing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Scraping;

/// <summary>
/// Resuelve botones de descarga protegidos con proof-of-work: pide el reto al
/// servidor, calcula el nonce y obtiene la URL de descarga, todo por HTTP y sin
/// navegador. Es mucho más rápido que abrir la ficha con Chrome.
/// </summary>
public static class PowSolver
{
    private const long MaxNonce = 20_000_000;

    private static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// Intenta resolver la descarga protegida de una ficha.
    /// </summary>
    /// <param name="source">Fuente (con la configuración del reto).</param>
    /// <param name="document">Documento de la ficha.</param>
    /// <param name="pageUrl">URL de la ficha.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>URL del .torrent o null.</returns>
    public static async Task<string?> ResolveAsync(
        TorrentSourceConfig source,
        IHtmlDocument document,
        string pageUrl,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.PowUrl))
        {
            return null;
        }

        var contentId = SelectorReader.Read(document, source.PowContentIdSelector);
        var table = SelectorReader.Read(document, source.PowTableSelector);
        if (string.IsNullOrWhiteSpace(contentId) || string.IsNullOrWhiteSpace(table))
        {
            logger.LogDebug("Torrent: la ficha no tiene los datos del reto de descarga.");
            return null;
        }

        var origin = new Uri(pageUrl).GetLeftPart(UriPartial.Authority);
        var endpoint = TextUtil.ToAbsolute(origin, source.PowUrl.Replace(
            "{base}",
            origin.TrimEnd('/'),
            StringComparison.Ordinal));
        if (endpoint is null)
        {
            return null;
        }

        var generated = await PostAsync(
                endpoint,
                new Dictionary<string, object?>
                {
                    ["action"] = "generate",
                    ["content_id"] = ParseContentId(contentId),
                    ["tabla"] = table
                },
                source,
                pageUrl,
                cancellationToken)
            .ConfigureAwait(false);

        var challenge = ReadString(generated, "challenge");
        if (string.IsNullOrWhiteSpace(challenge))
        {
            logger.LogDebug(
                "Torrent: el reto de descarga no devolvió desafío ({Reason}).",
                ReadString(generated, "error") ?? "sin detalle");
            return null;
        }

        var difficulty = Math.Clamp(source.PowDifficulty, 1, 6);
        var nonce = Solve(challenge!, difficulty, cancellationToken);
        if (nonce < 0)
        {
            logger.LogWarning("Torrent: no se pudo resolver el reto de descarga.");
            return null;
        }

        var validated = await PostAsync(
                endpoint,
                new Dictionary<string, object?>
                {
                    ["action"] = "validate",
                    ["challenge"] = challenge,
                    ["nonce"] = nonce
                },
                source,
                pageUrl,
                cancellationToken)
            .ConfigureAwait(false);

        var field = string.IsNullOrWhiteSpace(source.PowDownloadField)
            ? "download_url"
            : source.PowDownloadField!;
        var downloadUrl = ReadString(validated, field);
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            logger.LogInformation(
                "Torrent: la web no entregó el enlace de descarga ({Status}).",
                ReadString(validated, "status") ?? ReadString(validated, "error") ?? "sin detalle");
            return null;
        }

        return TextUtil.ToAbsolute(pageUrl, downloadUrl);
    }

    private static object ParseContentId(string value) =>
        long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : value.Trim();

    /// <summary>
    /// Busca el nonce cuyo SHA-256 empiece por los ceros indicados.
    /// </summary>
    private static long Solve(string challenge, int difficulty, CancellationToken cancellationToken)
    {
        var prefix = new string('0', difficulty);
        for (long nonce = 0; nonce < MaxNonce; nonce++)
        {
            if (nonce % 2000 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var text = challenge + nonce.ToString(CultureInfo.InvariantCulture);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            if (hash.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return nonce;
            }
        }

        return -1;
    }

    private static async Task<JsonElement?> PostAsync(
        string endpoint,
        Dictionary<string, object?> payload,
        TorrentSourceConfig source,
        string referer,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };

        request.Headers.TryAddWithoutValidation("Referer", referer);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        if (!string.IsNullOrWhiteSpace(source.Cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", source.Cookie);
        }

        foreach (var header in source.Headers)
        {
            if (!string.IsNullOrWhiteSpace(header.Name) && !string.IsNullOrWhiteSpace(header.Value))
            {
                request.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        try
        {
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement? element, string property)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return element.Value.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        return client;
    }
}
