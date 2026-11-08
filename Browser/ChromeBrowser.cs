using System.Diagnostics;
using System.Text.Json;
using Jellyfin.Plugin.Torrent.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torrent.Browser;

/// <summary>
/// Opciones de una navegación.
/// </summary>
public sealed class BrowserFetchOptions
{
    /// <summary>Gets or sets el JavaScript a ejecutar tras cargar la página.</summary>
    public string? Script { get; set; }

    /// <summary>Gets or sets el selector que debe existir para continuar.</summary>
    public string? WaitSelector { get; set; }

    /// <summary>Gets or sets la espera tras cargar (ms).</summary>
    public int WaitMs { get; set; }

    /// <summary>
    /// Gets or sets un texto que debe aparecer en la página para considerarla
    /// cargada (útil con resultados pintados por JavaScript).
    /// </summary>
    public string? WaitForText { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether se espera a que el DOM deje de
    /// cambiar (listas pintadas por JavaScript que tardan en aparecer).
    /// </summary>
    public bool WaitForStableDom { get; set; }

    /// <summary>Gets or sets el tiempo máximo total (segundos).</summary>
    public int TimeoutSeconds { get; set; } = 45;
}

/// <summary>
/// Resultado de cargar una página con el navegador.
/// </summary>
public sealed class BrowserPageResult
{
    /// <summary>Gets or sets el HTML renderizado.</summary>
    public string Html { get; set; } = string.Empty;

    /// <summary>Gets or sets la URL final.</summary>
    public string FinalUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets las cookies de la sesión para esa URL.</summary>
    public string? CookieHeader { get; set; }

    /// <summary>Gets or sets las URLs de red observadas (incluidas descargas).</summary>
    public List<string> CapturedUrls { get; set; } = new();
}

/// <summary>
/// Chrome/Edge controlado por CDP: navega, ejecuta el JavaScript de la web,
/// espera selectores y captura las peticiones de red (magnet, .torrent,
/// endpoints de descarga protegidos).
/// </summary>
public sealed class ChromeBrowser : IAsyncDisposable
{
    private readonly ILogger<ChromeBrowser> _logger;
    private readonly BrowserConfig _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _networkUrls = new();
    private readonly List<string> _downloadUrls = new();
    private readonly object _urlLock = new();

    private Process? _process;
    private string? _userDataDir;
    private CdpConnection? _connection;
    private DateTimeOffset _lastUsed = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChromeBrowser"/> class.
    /// </summary>
    /// <param name="config">Configuración del navegador.</param>
    /// <param name="logger">Logger.</param>
    public ChromeBrowser(BrowserConfig config, ILogger<ChromeBrowser> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Comprueba si hay un navegador instalado que se pueda usar.
    /// </summary>
    /// <param name="configuredPath">Ruta configurada (opcional).</param>
    /// <returns>True si se encuentra el ejecutable.</returns>
    public static bool IsAvailable(string? configuredPath) => FindExecutable(configuredPath) is not null;

    /// <summary>
    /// Carga una página en el navegador.
    /// </summary>
    /// <param name="url">URL.</param>
    /// <param name="options">Opciones.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>HTML renderizado, cookies y URLs capturadas.</returns>
    public async Task<BrowserPageResult> FetchAsync(
        string url,
        BrowserFetchOptions options,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

            lock (_urlLock)
            {
                _networkUrls.Clear();
                _downloadUrls.Clear();
            }

            var connection = _connection
                ?? throw new InvalidOperationException("El navegador no está disponible.");

            var timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 180));

            using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operationTimeout.CancelAfter(timeout + TimeSpan.FromSeconds(15));
            cancellationToken = operationTimeout.Token;

            await connection.SendAsync("Page.navigate", new { url }, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(options.WaitSelector))
            {
                await WaitForReadyAsync(TimeSpan.FromSeconds(Math.Min(15, timeout.TotalSeconds)), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (options.WaitMs > 0)
            {
                await Task.Delay(options.WaitMs, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(options.Script))
            {
                await EvaluateAsync(options.Script!, cancellationToken).ConfigureAwait(false);
                await Task.Delay(Math.Max(500, options.WaitMs), cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(options.WaitForText))
            {
                await WaitForTextAsync(options.WaitForText!, timeout, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (options.WaitForStableDom)
            {
                await WaitForStableDomAsync(timeout, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(options.WaitSelector))
            {
                await WaitForSelectorAsync(options.WaitSelector!, timeout, cancellationToken)
                    .ConfigureAwait(false);
            }

            string? html = null;
            string? finalUrl = null;
            string? cookies = null;

            try
            {
                html = await EvaluateStringAsync("document.documentElement.outerHTML", cancellationToken)
                    .ConfigureAwait(false);
                finalUrl = await EvaluateStringAsync("location.href", cancellationToken)
                    .ConfigureAwait(false);
                cookies = await GetCookieHeaderAsync(
                        string.IsNullOrWhiteSpace(finalUrl) ? url : finalUrl,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // La página puede haberse convertido en una descarga o estar navegando.
                _logger.LogDebug(ex, "Torrent: no se pudo leer el DOM final de {Url}", url);
                try
                {
                    cookies = await GetCookieHeaderAsync(url, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Sin cookies.
                }
            }

            var captured = new List<string>();
            lock (_urlLock)
            {
                captured.AddRange(_downloadUrls);
                captured.AddRange(_networkUrls);
            }

            return new BrowserPageResult
            {
                Html = html ?? string.Empty,
                FinalUrl = string.IsNullOrWhiteSpace(finalUrl) ? url : finalUrl!,
                CookieHeader = cookies,
                CapturedUrls = captured
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(400)
                    .ToList()
            };
        }
        finally
        {
            _lastUsed = DateTimeOffset.UtcNow;
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// Busca el ejecutable de un navegador compatible.
    /// </summary>
    /// <param name="configuredPath">Ruta configurada.</param>
    /// <returns>Ruta o null.</returns>
    public static string? FindExecutable(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(programFiles))
            {
                candidates.Add(Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"));
                candidates.Add(Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"));
            }

            if (!string.IsNullOrEmpty(programFilesX86))
            {
                candidates.Add(Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"));
                candidates.Add(Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"));
            }

            if (!string.IsNullOrEmpty(localAppData))
            {
                candidates.Add(Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"));
            }
        }
        else
        {
            candidates.AddRange(new[]
            {
                "/usr/bin/google-chrome",
                "/usr/bin/google-chrome-stable",
                "/usr/bin/chromium",
                "/usr/bin/chromium-browser",
                "/snap/bin/chromium",
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"
            });
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        var idle = TimeSpan.FromSeconds(Math.Clamp(_config.IdleSeconds, 30, 3600));
        if (_connection is not null
            && _process is { HasExited: false }
            && DateTimeOffset.UtcNow - _lastUsed < idle)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);

        var executable = FindExecutable(_config.ExecutablePath)
            ?? throw new InvalidOperationException(
                "No se encontró Chrome, Edge ni Chromium. Indica la ruta del ejecutable en la "
                + "configuración del plugin (pestaña Navegador).");

        _userDataDir = Path.Combine(
            Path.GetTempPath(),
            "jellyfin-torrent-browser-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_userDataDir);

        var arguments = new List<string>();
        if (_config.Headless)
        {
            arguments.Add("--headless=new");
        }

        arguments.AddRange(new[]
        {
            "--disable-gpu",
            "--disable-dev-shm-usage",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-extensions",
            "--disable-background-networking",
            "--disable-sync",
            "--disable-features=Translate,MediaRouter",
            "--metrics-recording-only",
            "--mute-audio",
            "--window-size=1280,900",
            "--remote-debugging-port=0",
            "--user-data-dir=" + _userDataDir
        });

        if (!string.IsNullOrWhiteSpace(_config.UserAgent))
        {
            arguments.Add("--user-agent=" + _config.UserAgent);
        }

        if (!OperatingSystem.IsWindows())
        {
            arguments.Add("--no-sandbox");
        }

        arguments.Add("about:blank");

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            Arguments = string.Join(' ', arguments.Select(Quote))
        };

        _logger.LogInformation("Torrent: arrancando navegador {Exe} (headless={Headless})", executable, _config.Headless);
        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("No se pudo iniciar el navegador.");

        var port = await WaitForDevToolsPortAsync(_userDataDir, cancellationToken).ConfigureAwait(false);
        var target = await GetPageTargetAsync(port, cancellationToken).ConfigureAwait(false);

        _connection = await CdpConnection.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
        _connection.EventReceived += OnEventReceived;

        await _connection.SendAsync("Page.enable", null, cancellationToken).ConfigureAwait(false);
        await _connection.SendAsync("Network.enable", null, cancellationToken).ConfigureAwait(false);
        await _connection.SendAsync("Runtime.enable", null, cancellationToken).ConfigureAwait(false);

        try
        {
            await _connection.SendAsync(
                    "Browser.setDownloadBehavior",
                    new { behavior = "allow", downloadPath = _userDataDir, eventsEnabled = true },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // No todos los navegadores exponen este comando en el target de página.
        }
    }

    private static string Quote(string value) =>
        value.Contains(' ') ? "\"" + value + "\"" : value;

    private static async Task<int> WaitForDevToolsPortAsync(
        string userDataDir,
        CancellationToken cancellationToken)
    {
        var file = Path.Combine(userDataDir, "DevToolsActivePort");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(file))
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
                    if (lines.Length > 0 && int.TryParse(lines[0], out var port) && port > 0)
                    {
                        return port;
                    }
                }
                catch (IOException)
                {
                    // El navegador aún está escribiendo el archivo.
                }
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "El navegador no abrió el puerto de depuración (¿está bloqueado por antivirus o política?).");
    }

    private static async Task<string> GetPageTargetAsync(int port, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        Exception? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await http
                    .GetStringAsync($"http://127.0.0.1:{port}/json/list", cancellationToken)
                    .ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type)
                        && type.GetString() == "page"
                        && item.TryGetProperty("webSocketDebuggerUrl", out var ws)
                        && ws.GetString() is { Length: > 0 } url)
                    {
                        return url;
                    }
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "No se pudo conectar con la pestaña del navegador: " + (last?.Message ?? "sin destinos"));
    }

    private void OnEventReceived(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "Network.requestWillBeSent":
                if (parameters.TryGetProperty("request", out var request)
                    && request.TryGetProperty("url", out var requestUrl)
                    && requestUrl.GetString() is { Length: > 0 } value)
                {
                    lock (_urlLock)
                    {
                        if (_networkUrls.Count < 600)
                        {
                            _networkUrls.Add(value);
                        }
                    }
                }

                break;

            case "Browser.downloadWillBegin":
            case "Page.downloadWillBegin":
                if (parameters.TryGetProperty("url", out var downloadUrl)
                    && downloadUrl.GetString() is { Length: > 0 } download)
                {
                    lock (_urlLock)
                    {
                        _downloadUrls.Add(download);
                    }
                }

                break;
        }
    }

    private async Task WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var connection = _connection!;
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var state = await EvaluateStringAsync("document.readyState", cancellationToken)
                    .ConfigureAwait(false);
                if (state is "complete" or "interactive")
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // La página está navegando: se reintenta.
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("Torrent: la página no llegó a readyState=complete a tiempo");
    }

    private async Task WaitForSelectorAsync(
        string selector,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var expression = $"document.querySelector({JsonSerializer.Serialize(selector)}) !== null";
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var found = Unwrap(await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false));
                if (found is not null && found.Value.ValueKind == JsonValueKind.True)
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // Selector inválido o contexto recargándose.
            }

            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("Torrent: el selector {Selector} no apareció a tiempo", selector);
    }

    private async Task WaitForStableDomAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var previous = -1;
        var stableCount = 0;
        var started = DateTimeOffset.UtcNow;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length;
            try
            {
                var value = await EvaluateStringAsync(
                        "(document.body ? document.body.innerHTML.length : 0).toString()",
                        cancellationToken)
                    .ConfigureAwait(false);
                length = int.TryParse(value, out var parsed) ? parsed : -1;
            }
            catch (InvalidOperationException)
            {
                length = -1;
            }

            if (length > 0 && length == previous)
            {
                stableCount++;
                if (stableCount >= 2 && DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(2))
                {
                    return;
                }
            }
            else
            {
                stableCount = 0;
                previous = length;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForTextAsync(
        string text,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var expression =
            "(document.body && document.body.innerText || '').toLowerCase().includes("
            + JsonSerializer.Serialize(text.ToLowerInvariant()) + ")";
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var found = Unwrap(await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false));
                if (found is not null && found.Value.ValueKind == JsonValueKind.True)
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // La página está navegando.
            }

            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement?> EvaluateAsync(string expression, CancellationToken cancellationToken)
    {
        var result = await _connection!.SendAsync(
                "Runtime.evaluate",
                new
                {
                    expression,
                    returnByValue = true,
                    awaitPromise = true,
                    userGesture = true
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("exceptionDetails", out var exception))
        {
            var text = exception.TryGetProperty("text", out var t) ? t.GetString() : exception.GetRawText();
            throw new InvalidOperationException("Error de JavaScript: " + text);
        }

        return result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("result", out var value)
            ? value
            : null;
    }

    private async Task<string?> EvaluateStringAsync(string expression, CancellationToken cancellationToken)
    {
        var value = Unwrap(await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false));
        if (value is null)
        {
            return null;
        }

        return value.Value.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Object when value.Value.TryGetProperty("value", out var inner) =>
                inner.ValueKind == JsonValueKind.String ? inner.GetString() : inner.GetRawText(),
            _ => value.Value.GetRawText()
        };
    }

    private static JsonElement? Unwrap(JsonElement? evaluated)
    {
        if (evaluated is null)
        {
            return null;
        }

        var element = evaluated.Value;
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("value", out var inner))
        {
            return inner;
        }

        return element;
    }

    private async Task<string?> GetCookieHeaderAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _connection!.SendAsync(
                    "Network.getCookies",
                    new { urls = new[] { url } },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.TryGetProperty("cookies", out var cookies)
                || cookies.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var parts = new List<string>();
            foreach (var cookie in cookies.EnumerateArray())
            {
                var name = cookie.TryGetProperty("name", out var n) ? n.GetString() : null;
                var value = cookie.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (!string.IsNullOrEmpty(name))
                {
                    parts.Add(name + "=" + value);
                }
            }

            return parts.Count == 0 ? null : string.Join("; ", parts);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task StopAsync()
    {
        if (_connection is not null)
        {
            _connection.EventReceived -= OnEventReceived;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _connection.SendAsync("Browser.close", null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // El proceso se cierra a mano.
            }

            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Ya cerrado.
            }

            _process.Dispose();
            _process = null;
        }

        if (_userDataDir is not null)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(_userDataDir, recursive: true);
                    break;
                }
                catch (Exception)
                {
                    await Task.Delay(200).ConfigureAwait(false);
                }
            }

            _userDataDir = null;
        }
    }
}
