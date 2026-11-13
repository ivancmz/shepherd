using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using Shepherd.Protocol.Wire;

namespace Shepherd.Protocol.Client;

/// <summary>
/// One connection to the broker: connect (pipe), <c>hello</c>, typed calls.
/// Reconnection policy lives in the caller (the MCP shim keeps a long-lived link; the CLI
/// does one call and exits).
/// </summary>
public sealed class BrokerClient : IAsyncDisposable
{
    private readonly RpcPeer _peer;
    private readonly Task _readLoop;

    private BrokerClient(RpcPeer peer)
    {
        _peer = peer;
        _readLoop = Task.Run(peer.RunAsync);
    }

    public RpcPeer Peer => _peer;
    public bool IsConnected => !_peer.IsClosed;
    public HelloResult? Hello { get; private set; }

    /// <summary>Connects to the local broker pipe. Returns null if no broker is listening.</summary>
    public static async Task<BrokerClient?> TryConnectPipeAsync(string pipeName, string clientKind, TimeSpan timeout, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        var client = new BrokerClient(new RpcPeer(new LineChannel(pipe, pipe), $"client:{clientKind}"));
        await client.HelloAsync(clientKind, null, ct).ConfigureAwait(false);
        return client;
    }

    /// <summary>
    /// Connects to the local broker, starting it (<c>shepherd broker run</c>) if it is not running.
    /// </summary>
    public static async Task<BrokerClient> ConnectOrSpawnAsync(string pipeName, string clientKind, CancellationToken ct = default)
    {
        var client = await TryConnectPipeAsync(pipeName, clientKind, TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
        if (client is not null) return client;

        if (Endpoints.IsWsl)
            throw new RpcException(RpcErrors.BrokerUnavailable, "no broker inside WSL: WSL agents use the Windows broker over TCP");
        BrokerLauncher.Spawn(pipeName);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            client = await TryConnectPipeAsync(pipeName, clientKind, TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            if (client is not null) return client;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        throw new RpcException(RpcErrors.BrokerUnavailable, "the broker did not start (see shepherd broker run)");
    }

    /// <summary>Connects over TCP and presents the node token (WSL → Windows, ADR-0001).</summary>
    public static async Task<BrokerClient?> TryConnectTcpAsync(string host, int port, string token, string clientKind, TimeSpan timeout,
        CancellationToken ct = default, string? node = null)
    {
        var tcp = new System.Net.Sockets.TcpClient { NoDelay = true };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            tcp.Dispose();
            return null;
        }
        var stream = tcp.GetStream();
        var client = new BrokerClient(new RpcPeer(new LineChannel(stream, stream), $"client:{clientKind}"));
        try
        {
            await client.HelloAsync(clientKind, token, ct, node).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return client;
    }

    /// <summary>
    /// The right connection for this process: inside WSL, TCP to the Windows broker using the
    /// endpoint and token it published (never spawns); on Windows, the user pipe (spawning the
    /// broker if needed).
    /// </summary>
    public static async Task<BrokerClient> ConnectAsync(string clientKind, CancellationToken ct = default)
    {
        if (!Endpoints.IsWsl)
            return await ConnectOrSpawnAsync(Endpoints.PipeName(), clientKind, ct).ConfigureAwait(false);

        var dir = Endpoints.WindowsDataDirFromWsl()
            ?? throw new RpcException(RpcErrors.BrokerUnavailable,
                "no Windows shepherd broker found from WSL: start one on Windows (any `shepherd` command there), or set SHEPHERD_WIN_HOME");
        BrokerEndpoint ep;
        string token;
        try
        {
            ep = System.Text.Json.JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(dir, Endpoints.EndpointFileName), ct).ConfigureAwait(false),
                ProtocolJson.Default.BrokerEndpoint) ?? throw new IOException("empty endpoint file");
            token = (await File.ReadAllTextAsync(Path.Combine(dir, Endpoints.TokenFileName), ct).ConfigureAwait(false)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            throw new RpcException(RpcErrors.BrokerUnavailable, $"cannot read the Windows broker endpoint in {dir}: {ex.Message}");
        }
        var host = Environment.GetEnvironmentVariable("SHEPHERD_BROKER_HOST") is { Length: > 0 } h ? h : ep.Address;
        return await TryConnectTcpAsync(host, ep.Port, token, clientKind, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false)
            ?? throw new RpcException(RpcErrors.BrokerUnavailable,
                $"cannot reach the Windows broker at {host}:{ep.Port} (is it running? is the firewall rule installed? see `shepherd firewall` on Windows)");
    }

    private async Task HelloAsync(string clientKind, string? token, CancellationToken ct, string? node = null)
    {
        var p = new HelloParams(Endpoints.ProtocolVersion, node ?? Names.LocalNode(), clientKind, token);
        var r = await _peer.RequestAsync(Methods.Hello, Json.ToNode(p, ProtocolJson.Default.HelloParams), ct, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Hello = Json.Result(r, ProtocolJson.Default.HelloResult);
    }

    public async Task<RegisterResult> RegisterAsync(RegisterParams p, CancellationToken ct = default) =>
        Json.Result(await Call(Methods.Register, Json.ToNode(p, ProtocolJson.Default.RegisterParams), ct), ProtocolJson.Default.RegisterResult);

    public Task IdentifyAsync(IdentifyParams p, CancellationToken ct = default) =>
        Call(Methods.Identify, Json.ToNode(p, ProtocolJson.Default.IdentifyParams), ct);

    public async Task<SendResult> SendAsync(SendParams p, CancellationToken ct = default) =>
        Json.Result(await Call(Methods.Send, Json.ToNode(p, ProtocolJson.Default.SendParams), ct), ProtocolJson.Default.SendResult);

    public async Task<SendResult> ReplyAsync(ReplyParams p, CancellationToken ct = default) =>
        Json.Result(await Call(Methods.Reply, Json.ToNode(p, ProtocolJson.Default.ReplyParams), ct), ProtocolJson.Default.SendResult);

    public async Task<ReadInboxResult> ReadInboxAsync(ReadInboxParams p, CancellationToken ct = default) =>
        Json.Result(await Call(Methods.ReadInbox, Json.ToNode(p, ProtocolJson.Default.ReadInboxParams), ct), ProtocolJson.Default.ReadInboxResult);

    public Task AckAsync(AckParams p, CancellationToken ct = default) =>
        Call(Methods.Ack, Json.ToNode(p, ProtocolJson.Default.AckParams), ct);

    public async Task<ListAgentsResult> ListAgentsAsync(CancellationToken ct = default) =>
        Json.Result(await Call(Methods.ListAgents, null, ct), ProtocolJson.Default.ListAgentsResult);

    public async Task<StatusResult> StatusAsync(CancellationToken ct = default) =>
        Json.Result(await Call(Methods.Status, null, ct), ProtocolJson.Default.StatusResult);

    public Task ShutdownAsync(CancellationToken ct = default) => Call(Methods.Shutdown, null, ct);

    private Task<JsonNode?> Call(string method, JsonNode? p, CancellationToken ct) =>
        _peer.RequestAsync(method, p, ct, TimeSpan.FromSeconds(15));

    public async ValueTask DisposeAsync()
    {
        await _peer.DisposeAsync().ConfigureAwait(false);
        try { await _readLoop.ConfigureAwait(false); } catch { /* already closed */ }
    }
}

/// <summary>Starts the broker as a detached background process of the current executable.</summary>
public static class BrokerLauncher
{
    public static void Spawn(string pipeName)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("cannot locate the shepherd executable");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = Path.GetTempPath(),
        };
        // A `dotnet shepherd.dll` host (tests / dev runs) must pass the dll first.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        psi.ArgumentList.Add("broker");
        psi.ArgumentList.Add("run");
        psi.Environment["SHEPHERD_PIPE"] = pipeName;
        using var _ = Process.Start(psi);
    }
}
