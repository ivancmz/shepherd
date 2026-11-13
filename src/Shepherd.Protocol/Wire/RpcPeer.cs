using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shepherd.Protocol.Wire;

public delegate Task<JsonNode?> RpcRequestHandler(string method, JsonNode? parameters, JsonNode? meta, CancellationToken ct);
public delegate Task RpcNotificationHandler(string method, JsonNode? parameters);

/// <summary>
/// Bidirectional JSON-RPC 2.0 endpoint over a <see cref="LineChannel"/>. Both sides can send
/// requests and notifications. Incoming requests are dispatched concurrently so a slow handler
/// never blocks the read loop (and therefore never blocks responses or keep-alives).
/// </summary>
public sealed class RpcPeer : IAsyncDisposable
{
    private readonly LineChannel _channel;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private long _nextId;
    private long _lastReceivedTicks = DateTime.UtcNow.Ticks;
    private int _closed;

    public RpcPeer(LineChannel channel, string name)
    {
        _channel = channel;
        Name = name;
    }

    public string Name { get; }
    public RpcRequestHandler? OnRequest { get; set; }
    public RpcNotificationHandler? OnNotification { get; set; }
    public Action<string>? Log { get; set; }

    /// <summary>Raised once when the connection ends (EOF, error, keep-alive timeout or dispose).</summary>
    public event Action<RpcPeer, Exception?>? Closed;

    public DateTime LastReceived => new(Interlocked.Read(ref _lastReceivedTicks), DateTimeKind.Utc);
    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    public CancellationToken Lifetime => _cts.Token;

    /// <summary>Runs the read loop until the connection closes.</summary>
    public async Task RunAsync()
    {
        Exception? error = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await _channel.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null) break;
                Interlocked.Exchange(ref _lastReceivedTicks, DateTime.UtcNow.Ticks);
                if (line.IsTooLarge)
                {
                    await SendErrorAsync(null, RpcErrors.TooLarge, "message exceeds the 1 MiB limit").ConfigureAwait(false);
                    continue;
                }
                await HandleLineAsync(line.Text!).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            error = ex;
        }
        finally
        {
            Close(error);
        }
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        if (IsClosed) throw new RpcException(RpcErrors.BrokerUnavailable, "connection closed");
        var id = Interlocked.Increment(ref _nextId);
        var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) msg["params"] = parameters;
        try
        {
            await WriteAsync(msg).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            if (timeout is { } t) linked.CancelAfter(t);
            try
            {
                return await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new RpcException(RpcErrors.BrokerUnavailable, IsClosed ? "connection closed" : $"timeout waiting for {method}");
            }
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    public Task NotifyAsync(string method, JsonNode? parameters)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null) msg["params"] = parameters;
        return WriteAsync(msg);
    }

    /// <summary>
    /// Sends <c>ping</c> every <paramref name="interval"/> and closes the connection if nothing
    /// was received for <paramref name="timeout"/> (ADR-0005: 15 s / 45 s).
    /// </summary>
    public void StartKeepAlive(TimeSpan interval, TimeSpan timeout)
    {
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
                {
                    if (DateTime.UtcNow - LastReceived > timeout)
                    {
                        Log?.Invoke($"{Name}: keep-alive timeout");
                        Close(new TimeoutException("keep-alive timeout"));
                        return;
                    }
                    try { await RequestAsync("ping", null, _cts.Token, interval).ConfigureAwait(false); }
                    catch (RpcException) { /* judged by LastReceived */ }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task HandleLineAsync(string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            await SendErrorAsync(null, RpcErrors.ParseError, "invalid JSON").ConfigureAwait(false);
            return;
        }
        if (node is not JsonObject obj)
        {
            await SendErrorAsync(null, RpcErrors.InvalidRequest, "expected a JSON object").ConfigureAwait(false);
            return;
        }

        var method = obj["method"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : null;
        var id = obj["id"];

        if (method is null)
        {
            // A response to one of our requests.
            if (id is null || !_pending.TryRemove(IdKey(id), out var tcs)) return;
            if (obj["error"] is JsonObject err)
            {
                var code = err["code"] is JsonValue cv && cv.TryGetValue<int>(out var c) ? c : RpcErrors.InternalError;
                var message = err["message"] is JsonValue msv && msv.TryGetValue<string>(out var s) ? s : "error";
                tcs.TrySetException(new RpcException(code, message, err["data"]?.DeepClone()));
            }
            else
            {
                tcs.TrySetResult(obj["result"]?.DeepClone());
            }
            return;
        }

        var parameters = obj["params"]?.DeepClone();
        var meta = parameters is JsonObject po ? po["_meta"]?.DeepClone() : null;

        if (id is null)
        {
            if (OnNotification is { } nh)
            {
                _ = Task.Run(async () =>
                {
                    try { await nh(method, parameters).ConfigureAwait(false); }
                    catch (Exception ex) { Log?.Invoke($"{Name}: notification {method} failed: {ex.Message}"); }
                });
            }
            return;
        }

        var idCopy = id.DeepClone();
        _ = Task.Run(async () =>
        {
            if (method == "ping")
            {
                await SendResultAsync(idCopy, new JsonObject()).ConfigureAwait(false);
                return;
            }
            if (OnRequest is not { } handler)
            {
                await SendErrorAsync(idCopy, RpcErrors.MethodNotFound, $"method not found: {method}").ConfigureAwait(false);
                return;
            }
            try
            {
                var result = await handler(method, parameters, meta, _cts.Token).ConfigureAwait(false);
                await SendResultAsync(idCopy, result ?? new JsonObject()).ConfigureAwait(false);
            }
            catch (RpcException rex)
            {
                await SendErrorAsync(idCopy, rex.Code, rex.Message, rex.ErrorData).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // connection closing
            }
            catch (Exception ex)
            {
                Log?.Invoke($"{Name}: {method} failed: {ex}");
                await SendErrorAsync(idCopy, RpcErrors.InternalError, ex.Message).ConfigureAwait(false);
            }
        });
    }

    private Task SendResultAsync(JsonNode id, JsonNode result) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });

    private Task SendErrorAsync(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var err = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null) err["data"] = data.DeepClone();
        return WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = err });
    }

    private async Task WriteAsync(JsonObject msg)
    {
        if (IsClosed) return;
        try
        {
            await _channel.WriteLineAsync(msg.ToJsonString(), _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Close(ex is OperationCanceledException ? null : ex);
        }
    }

    private static string IdKey(JsonNode id) => id is JsonValue v && v.TryGetValue<string>(out var s) ? s : id.ToJsonString();

    public void Close(Exception? error = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var (_, tcs) in _pending)
            tcs.TrySetException(new RpcException(RpcErrors.BrokerUnavailable, "connection closed"));
        Closed?.Invoke(this, error);
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        await _channel.DisposeAsync().ConfigureAwait(false);
        // _cts is intentionally not disposed: background keep-alive/handler tasks may still
        // observe its token after the connection is gone.
    }
}
