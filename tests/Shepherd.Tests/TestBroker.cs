using System.IO.Pipes;
using Shepherd.Broker;
using Shepherd.Protocol;
using Shepherd.Protocol.Client;
using Shepherd.Protocol.Wire;

namespace Shepherd.Tests;

/// <summary>A real broker on a random pipe and a temporary data directory.</summary>
internal sealed class TestBroker : IAsyncDisposable
{
    // Clients announce their own node in hello; pin it so addresses are predictable.
    static TestBroker() => Environment.SetEnvironmentVariable("SHEPHERD_NODE", "pc-test");

    public string PipeName { get; } = "shepherd-test-" + Guid.NewGuid().ToString("N")[..12];
    public string DataDir { get; }
    public List<string> Log { get; } = [];
    public FakeRunner Runner { get; } = new();
    private BrokerServer _server;
    private Task _run;

    private readonly bool _tcp;

    /// <summary>Adjusts the options of the next (re)start, e.g. shorter timings.</summary>
    public Func<BrokerOptions, BrokerOptions>? Configure { get; set; }

    private TestBroker(string dataDir, bool tcp, Func<BrokerOptions, BrokerOptions>? configure)
    {
        DataDir = dataDir;
        _tcp = tcp;
        Configure = configure;
        (_server, _run) = Start();
    }

    /// <param name="tcp">Also listen on 127.0.0.1 (random port) like the WSL listener.</param>
    public static async Task<TestBroker> StartAsync(bool tcp = false, Func<BrokerOptions, BrokerOptions>? configure = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "shepherd-tests", Guid.NewGuid().ToString("N")[..12]);
        var b = new TestBroker(dir, tcp, configure);
        await b.WaitListeningAsync();
        return b;
    }

    public System.Net.IPEndPoint? TcpEndpoint => _server.TcpEndpoint;
    public string Token => _server.Token;

    /// <summary>Connects over TCP announcing <paramref name="node"/> (simulates a WSL shim).</summary>
    public async Task<BrokerClient> ConnectTcpAsync(string node, string? token = null)
    {
        var ep = TcpEndpoint ?? throw new InvalidOperationException("TCP not enabled");
        return await BrokerClient.TryConnectTcpAsync(ep.Address.ToString(), ep.Port, token ?? Token, "mcp", TimeSpan.FromSeconds(5), default, node)
            ?? throw new InvalidOperationException("tcp connect failed");
    }

    private (BrokerServer, Task) Start()
    {
        var options = new BrokerOptions
        {
            PipeName = PipeName,
            DataDir = DataDir,
            Node = "pc-test",
            KeepAlive = false,
            Log = s => { lock (Log) Log.Add(s); },
            Runner = Runner,
            CodexPath = "codex-test",
            HerdrPath = "herdr-test",
            WslPath = "wsl-test",
            TcpBind = _tcp ? "127.0.0.1:0" : "off",
        };
        var server = new BrokerServer(Configure?.Invoke(options) ?? options);
        return (server, Task.Run(() => server.RunAsync()));
    }

    /// <summary>Stops and starts the broker on the same data directory (persistence tests).</summary>
    public async Task RestartAsync()
    {
        await _server.DisposeAsync();
        await _run;
        (_server, _run) = Start();
        await WaitListeningAsync();
    }

    /// <summary>Connector for an in-process MCP shim.</summary>
    public Task<BrokerClient> Connect(CancellationToken ct) => BrokerClient.ConnectOrSpawnAsync(PipeName, "mcp", ct);

    public async Task<BrokerClient> ConnectAsync(string clientKind = "mcp")
    {
        var c = await BrokerClient.TryConnectPipeAsync(PipeName, clientKind, TimeSpan.FromSeconds(5));
        return c ?? throw new InvalidOperationException("broker not reachable");
    }

    /// <summary>Connects and registers an agent.</summary>
    public async Task<BrokerClient> AgentAsync(string? name, string kind = AgentKinds.Claude, string? sessionKey = null,
        string? cwd = null, bool channel = false, string? herdrPane = null)
    {
        var c = await ConnectAsync();
        await c.RegisterAsync(new RegisterParams
        {
            Name = name, Kind = kind, SessionKey = sessionKey ?? $"{kind}:{Guid.NewGuid():N}", Cwd = cwd,
            Channel = channel, HerdrPaneId = herdrPane,
        });
        return c;
    }

    private async Task WaitListeningAsync()
    {
        for (var i = 0; i < 50; i++)
        {
            await using var c = await BrokerClient.TryConnectPipeAsync(PipeName, "cli", TimeSpan.FromMilliseconds(100));
            if (c is not null) return;
        }
        throw new InvalidOperationException("broker did not start");
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        await _run;
        try { Directory.Delete(DataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        // On Linux, .NET named pipes are Unix sockets that outlive the server.
        if (OperatingSystem.IsLinux()) File.Delete(Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + PipeName));
    }
}

internal static class Assertions
{
    public static async Task<RpcException> ThrowsRpc(int code, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<RpcException>(action);
        Assert.Equal(code, ex.Code);
        return ex;
    }
}

/// <summary>An in-process duplex stream pair (a private named pipe) for driving the MCP shim.</summary>
internal sealed class DuplexPair : IAsyncDisposable
{
    public required NamedPipeServerStream Server { get; init; }
    public required NamedPipeClientStream Client { get; init; }

    public static async Task<DuplexPair> CreateAsync()
    {
        var name = "shepherd-duplex-" + Guid.NewGuid().ToString("N")[..12];
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());
        return new DuplexPair { Server = server, Client = client, Name = name };
    }

    public required string Name { get; init; }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Server.DisposeAsync();
        if (OperatingSystem.IsLinux()) File.Delete(Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + Name));
    }
}

/// <summary>Records external commands instead of running them. Fails the first N calls if asked.</summary>
public sealed class FakeRunner : ICommandRunner
{
    private readonly List<(string File, string[] Args)> _calls = [];
    public int FailFirst { get; set; }

    public IReadOnlyList<(string File, string[] Args)> Calls
    {
        get { lock (_calls) return _calls.ToList(); }
    }

    public Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        lock (_calls)
        {
            _calls.Add((file, args.ToArray()));
            if (FailFirst > 0)
            {
                FailFirst--;
                return Task.FromResult(new CommandResult(1, "", "simulated failure"));
            }
        }
        return Task.FromResult(new CommandResult(0, "", ""));
    }
}
