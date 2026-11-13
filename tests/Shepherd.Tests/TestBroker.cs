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

    private TestBroker(string dataDir)
    {
        DataDir = dataDir;
        (_server, _run) = Start();
    }

    public static async Task<TestBroker> StartAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shepherd-tests", Guid.NewGuid().ToString("N")[..12]);
        var b = new TestBroker(dir);
        await b.WaitListeningAsync();
        return b;
    }

    private (BrokerServer, Task) Start()
    {
        var server = new BrokerServer(new BrokerOptions
        {
            PipeName = PipeName,
            DataDir = DataDir,
            Node = "pc-test",
            KeepAlive = false,
            Log = s => { lock (Log) Log.Add(s); },
            Runner = Runner,
            CodexPath = "codex-test",
            HerdrPath = "herdr-test",
        });
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
        return new DuplexPair { Server = server, Client = client };
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Server.DisposeAsync();
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
