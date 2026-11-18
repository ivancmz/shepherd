using System.Text.Json.Nodes;
using Shepherd.Protocol;
using Shepherd.Protocol.Client;
using Shepherd.Protocol.Wire;

namespace Shepherd.Mcp;

/// <summary>
/// The shim's long-lived link to the broker (diagram 3): locate/spawn → hello → register →
/// ready, with exponential back-off reconnection. A reconnecting session keeps its name:
/// an explicit name is requested again, and without one the broker gives back the name the
/// session key last had (ADR-0008, ADR-0005).
/// </summary>
internal sealed class BrokerLink : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<BrokerClient>> _connect;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource<BrokerClient> _ready = NewReady();
    private BrokerClient? _client;
    private AgentIdentity? _identity;
    private string? _requestedName;
    private string? _threadId;

    public BrokerLink(Func<CancellationToken, Task<BrokerClient>> connect, Action<string> log)
    {
        _connect = connect;
        _log = log;
    }

    public Func<InboxMessage, Task>? OnMessage { get; set; }
    public RegisterResult? Registration { get; private set; }

    /// <summary>One-shot notice for the agent (e.g. requested name unavailable). Cleared when read.</summary>
    public string? TakeNotice() => Interlocked.Exchange(ref _notice, null);
    private string? _notice;

    /// <summary>Starts connecting in the background; tools wait on <see cref="GetAsync"/>.</summary>
    public void Start(AgentIdentity identity)
    {
        _identity = identity;
        _requestedName = identity.RequestedName;
        _ = Task.Run(ConnectLoopAsync);
    }

    /// <summary>The connected, registered client, waiting briefly if a reconnect is in progress.</summary>
    public async Task<BrokerClient> GetAsync(CancellationToken ct)
    {
        var ready = Volatile.Read(ref _ready).Task;
        try
        {
            return await ready.WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new RpcException(RpcErrors.BrokerUnavailable, "the shepherd broker is not reachable right now; try again shortly");
        }
    }

    /// <summary>Explicit <c>register(name)</c> tool call. On success the name sticks across reconnects.</summary>
    public async Task<RegisterResult> RenameAsync(string name, CancellationToken ct)
    {
        var client = await GetAsync(ct).ConfigureAwait(false);
        var r = await client.RegisterAsync(BuildRegister(name), ct).ConfigureAwait(false);
        _requestedName = r.Name;
        Registration = r;
        return r;
    }

    /// <summary>Records the Codex thread id the first time it is seen (ADR-0003).</summary>
    public async Task ObserveThreadIdAsync(string threadId, CancellationToken ct)
    {
        if (threadId == _threadId) return;
        _threadId = threadId;
        var client = await GetAsync(ct).ConfigureAwait(false);
        Adopt(await client.IdentifyAsync(new IdentifyParams(threadId), ct).ConfigureAwait(false));
    }

    /// <summary>A Codex session that resumes a known thread gets its previous name back (ADR-0008).</summary>
    private void Adopt(IdentifyResult r)
    {
        if (r.Name is null || r.Address is null || r.Address == Registration?.Address) return;
        _log($"thread known to the broker: now registered as {r.Address}");
        Registration = new RegisterResult(r.Name, r.Address, r.Automatic);
    }

    /// <summary>One-shot "unread mail waiting" note from registration, for sessions nothing can wake (ADR-0009).</summary>
    public string? TakeMailNotice() => Interlocked.Exchange(ref _mailNotice, null);
    private string? _mailNotice;

    private async Task ConnectLoopAsync()
    {
        var delay = TimeSpan.FromMilliseconds(500);
        while (!_cts.IsCancellationRequested)
        {
            BrokerClient? client = null;
            try
            {
                client = await _connect(_cts.Token).ConfigureAwait(false);
                client.Peer.Log = _log;
                client.Peer.OnNotification = OnNotificationAsync;
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Peer.Closed += (_, _) => closed.TrySetResult();
                client.Peer.StartKeepAlive(Endpoints.KeepAliveInterval, Endpoints.KeepAliveTimeout);

                await RegisterAsync(client).ConfigureAwait(false);
                if (_threadId is { } tid) Adopt(await client.IdentifyAsync(new IdentifyParams(tid), _cts.Token).ConfigureAwait(false));

                _client = client;
                Volatile.Read(ref _ready).TrySetResult(client);
                delay = TimeSpan.FromMilliseconds(500);

                await closed.Task.WaitAsync(_cts.Token).ConfigureAwait(false);
                _log("broker connection lost; reconnecting");
                Interlocked.Exchange(ref _ready, NewReady());
                _client = null;
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log($"broker connect failed: {ex.Message}");
            }
            finally
            {
                if (client is not null && !ReferenceEquals(client, _client)) await client.DisposeAsync().ConfigureAwait(false);
            }
            try { await Task.Delay(delay, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 30_000));
        }
    }

    private async Task RegisterAsync(BrokerClient client)
    {
        var wanted = _requestedName;
        try
        {
            Registration = await client.RegisterAsync(BuildRegister(wanted), _cts.Token).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.Code is RpcErrors.NameTaken or RpcErrors.InvalidName && wanted is not null)
        {
            // ADR-0003: never steal a live name; fall back to an automatic one and tell the agent.
            _requestedName = null;
            Registration = await client.RegisterAsync(BuildRegister(null), _cts.Token).ConfigureAwait(false);
            _notice = $"The name '{wanted}' is not available ({ex.Message}); you are registered as '{Registration.Name}'. Use the register tool to choose another name.";
            _log(_notice);
        }
        // With a channel the broker pushes the pending mail itself.
        if (Registration.Notice is { } mail && !_identity!.Channel) _mailNotice = mail;
        _log($"registered as {Registration.Address}");
    }

    private RegisterParams BuildRegister(string? name) => new()
    {
        Name = name,
        Kind = _identity!.Kind,
        SessionKey = _identity.SessionKey,
        Cwd = _identity.Cwd,
        HerdrPaneId = _identity.HerdrPaneId,
        Channel = _identity.Channel,
        Pid = _identity.AgentPid,
        Distro = _identity.Distro,
    };

    private async Task OnNotificationAsync(string method, JsonNode? p)
    {
        if (method != Methods.MessageNew || OnMessage is null || p is null) return;
        var msg = Json.Result(p, ProtocolJson.Default.InboxMessage);
        await OnMessage(msg).ConfigureAwait(false);
    }

    private static TaskCompletionSource<BrokerClient> NewReady() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_client is { } c) await c.DisposeAsync().ConfigureAwait(false);
    }
}
