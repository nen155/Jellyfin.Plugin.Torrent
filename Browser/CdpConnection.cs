using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.Torrent.Browser;

/// <summary>
/// Conexión al protocolo DevTools de Chrome/Edge (CDP) sobre WebSocket:
/// envía comandos y reparte eventos.
/// </summary>
public sealed class CdpConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;
    private int _nextId;

    private CdpConnection(ClientWebSocket socket)
    {
        _socket = socket;
    }

    /// <summary>Se dispara con cada evento del navegador (método, parámetros).</summary>
    public event Action<string, JsonElement>? EventReceived;

    /// <summary>
    /// Abre la conexión con la URL WebSocket del navegador.
    /// </summary>
    /// <param name="webSocketUrl">URL <c>ws://…</c>.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Conexión lista.</returns>
    public static async Task<CdpConnection> ConnectAsync(
        string webSocketUrl,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(new Uri(webSocketUrl), cancellationToken).ConfigureAwait(false);

        var connection = new CdpConnection(socket);
        connection._readLoop = Task.Run(connection.ReadLoopAsync, CancellationToken.None);
        return connection;
    }

    /// <summary>
    /// Envía un comando CDP y espera su resultado.
    /// </summary>
    /// <param name="method">Método (p. ej. <c>Page.navigate</c>).</param>
    /// <param name="parameters">Parámetros.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <returns>Resultado del comando.</returns>
    public async Task<JsonElement> SendAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        var payload = JsonSerializer.Serialize(
            new { id, method, @params = parameters },
            JsonOptions);

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket
                .SendAsync(
                    Encoding.UTF8.GetBytes(payload),
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(45));
        using var registration = timeoutSource.Token.Register(() =>
        {
            if (!completion.Task.IsCompleted)
            {
                completion.TrySetException(new TimeoutException(
                    $"El navegador no respondió al comando {method}."));
            }
        });

        return await completion.Task.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ya cerrada.
        }

        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cierre best-effort.
            }
        }

        _socket.Dispose();
        _cts.Dispose();
        _sendGate.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[128 * 1024];
        using var message = new MemoryStream();

        try
        {
            while (_socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                Dispatch(json);
            }
        }
        catch (OperationCanceledException)
        {
            // Cierre normal.
        }
        catch (Exception)
        {
            // La conexión se cayó: se completan los pendientes como cancelados.
        }
        finally
        {
            foreach (var pair in _pending)
            {
                pair.Value.TrySetException(new InvalidOperationException("La conexión CDP se cerró."));
            }

            _pending.Clear();
        }
    }

    private void Dispatch(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.TryGetProperty("id", out var idElement)
                && idElement.TryGetInt32(out var id)
                && _pending.TryRemove(id, out var completion))
            {
                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var m)
                        ? m.GetString()
                        : error.GetRawText();
                    completion.TrySetException(new InvalidOperationException(message));
                }
                else
                {
                    completion.TrySetResult(
                        root.TryGetProperty("result", out var result)
                            ? result.Clone()
                            : default);
                }

                return;
            }

            if (root.TryGetProperty("method", out var methodElement))
            {
                var method = methodElement.GetString() ?? string.Empty;
                var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
                try
                {
                    EventReceived?.Invoke(method, parameters);
                }
                catch (Exception)
                {
                    // Un suscriptor no debe tumbar el bucle de lectura.
                }
            }
        }
    }
}
