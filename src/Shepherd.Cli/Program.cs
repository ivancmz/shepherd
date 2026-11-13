using System.Globalization;
using Shepherd.Broker;
using Shepherd.Mcp;
using Shepherd.Protocol;
using Shepherd.Protocol.Client;
using Shepherd.Protocol.Wire;

namespace Shepherd.Cli;

internal static class Program
{
    private const string Usage = """
        shepherd — message bus between coding agents

        Usage:
          shepherd mcp                          MCP server over stdio (for Claude Code / Codex)
          shepherd send <to>[,<to>...] <text>   Send a message as the user [--request] [--subject <s>]
          shepherd inbox [--all] [--limit <n>]  Read the user's messages (marks them read)
          shepherd reply <message_id> <text>    Answer a message
          shepherd agents                       List agents
          shepherd status                       Broker status
          shepherd broker run                   Run the broker in the foreground
          shepherd broker stop                  Stop the running broker
          shepherd firewall                     Show the Windows Firewall rule WSL agents need (does not apply it)
          shepherd version

        Environment: SHEPHERD_HOME (data dir), SHEPHERD_PIPE (pipe name), SHEPHERD_USER (your name),
        SHEPHERD_TCP (WSL listener: auto | off | addr:port), SHEPHERD_WIN_HOME (from WSL: Windows data dir).
        """;

    private static int Firewall()
    {
        if (!OperatingSystem.IsWindows()) return Fail("run this on Windows");
        var exe = Environment.ProcessPath ?? "shepherd.exe";
        return Print($"""
            WSL agents reach the Windows broker over TCP on the WSL virtual interface (ADR-0001).
            If they cannot connect, allow it with this rule, from an elevated PowerShell:

              New-NetFirewallRule -DisplayName "shepherd broker (WSL)" -Direction Inbound -Action Allow `
                -Protocol TCP -LocalPort {Endpoints.DefaultTcpPort} -InterfaceAlias "vEthernet (WSL*)" `
                -Program "{exe}"

            It only admits TCP {Endpoints.DefaultTcpPort} on the WSL interface and only for this executable;
            connections still need the node token. Remove it with:

              Remove-NetFirewallRule -DisplayName "shepherd broker (WSL)"
            """);
    }

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            return args.FirstOrDefault() switch
            {
                "mcp" => await RunMcpAsync(),
                "broker" => await BrokerAsync(args.Skip(1).ToArray()),
                "send" => await SendAsync(args.Skip(1).ToArray()),
                "inbox" => await InboxAsync(args.Skip(1).ToArray()),
                "reply" => await ReplyAsync(args.Skip(1).ToArray()),
                "agents" => await AgentsAsync(),
                "status" => await StatusAsync(),
                "firewall" => Firewall(),
                "version" or "--version" => Print(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"),
                null or "help" or "--help" or "-h" => Print(Usage),
                var other => Fail($"unknown command '{other}'\n\n{Usage}"),
            };
        }
        catch (RpcException ex)
        {
            return Fail(ex.Message);
        }
    }

    // ------------------------------------------------------------------ mcp

    private static async Task<int> RunMcpAsync()
    {
        // stdout carries JSON-RPC only; diagnostics go to stderr (Claude/Codex keep it in their logs).
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        void Log(string s) => Console.Error.WriteLine($"[shepherd {DateTime.Now:HH:mm:ss}] {s}");
        await using var server = new McpServer(stdin, stdout, ct => BrokerClient.ConnectAsync("mcp", ct), Log);
        await server.RunAsync();
        return 0;
    }

    // --------------------------------------------------------------- broker

    private static async Task<int> BrokerAsync(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "run":
            {
                var pipe = Endpoints.PipeName();
                using var mutex = BrokerServer.TryAcquireSingleInstance(pipe);
                if (mutex is null) return Fail($"a broker is already running for pipe '{pipe}'");
                var dataDir = Endpoints.DataDir();
                Directory.CreateDirectory(dataDir);
                var logPath = Path.Combine(dataDir, "broker.log");
                await using var logFile = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                var logLock = new Lock();
                void Log(string s)
                {
                    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {s}";
                    lock (logLock)
                    {
                        logFile.WriteLine(line);
                        if (!Console.IsErrorRedirected) Console.Error.WriteLine(line);
                    }
                }
                await using var broker = new BrokerServer(new BrokerOptions { PipeName = pipe, DataDir = dataDir, Log = Log });
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                await broker.RunAsync(cts.Token);
                Log("broker stopped");
                return 0;
            }
            case "stop":
            {
                await using var c = await BrokerClient.TryConnectPipeAsync(Endpoints.PipeName(), "cli", TimeSpan.FromSeconds(1));
                if (c is null) return Print("No broker running.");
                await c.ShutdownAsync();
                return Print("Broker stopping.");
            }
            default:
                return Fail("usage: shepherd broker run|stop");
        }
    }

    // ------------------------------------------------------------ user CLI

    private static async Task<BrokerClient> ConnectAsUserAsync()
    {
        var c = await BrokerClient.ConnectAsync("cli");
        await c.RegisterAsync(new RegisterParams { Kind = AgentKinds.Human, Name = Endpoints.HumanName() });
        return c;
    }

    private static async Task<int> SendAsync(string[] args)
    {
        var (pos, flags) = Parse(args, "--subject");
        if (pos.Count < 2) return Fail("usage: shepherd send <to>[,<to>...] <text> [--request] [--subject <s>]");
        await using var c = await ConnectAsUserAsync();
        var r = await c.SendAsync(new SendParams
        {
            To = pos[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            Body = string.Join(' ', pos.Skip(1)),
            Kind = flags.ContainsKey("--request") ? MessageKinds.Request : MessageKinds.Note,
            Subject = flags.GetValueOrDefault("--subject"),
        });
        return Print($"Sent to {string.Join(", ", r.To)} ({r.MessageId}).");
    }

    private static async Task<int> InboxAsync(string[] args)
    {
        var (_, flags) = Parse(args, "--limit");
        var limit = flags.TryGetValue("--limit", out var l) && int.TryParse(l, CultureInfo.InvariantCulture, out var n) ? n : 20;
        await using var c = await ConnectAsUserAsync();
        var unreadOnly = !flags.ContainsKey("--all");
        var r = await c.ReadInboxAsync(new ReadInboxParams(unreadOnly, limit));
        return Print(Format.Inbox(r, unreadOnly));
    }

    private static async Task<int> ReplyAsync(string[] args)
    {
        var (pos, _) = Parse(args);
        if (pos.Count < 2) return Fail("usage: shepherd reply <message_id> <text>");
        await using var c = await ConnectAsUserAsync();
        var r = await c.ReplyAsync(new ReplyParams(pos[0], string.Join(' ', pos.Skip(1))));
        return Print($"Reply sent to {string.Join(", ", r.To)} ({r.MessageId}).");
    }

    private static async Task<int> AgentsAsync()
    {
        await using var c = await ConnectAsUserAsync();
        return Print(Format.Agents(await c.ListAgentsAsync(), null));
    }

    private static async Task<int> StatusAsync()
    {
        // Status never starts a broker; inside WSL it asks the Windows one over TCP.
        await using var c = Endpoints.IsWsl
            ? await BrokerClient.ConnectAsync("cli")
            : await BrokerClient.TryConnectPipeAsync(Endpoints.PipeName(), "cli", TimeSpan.FromSeconds(1));
        if (c is null) return Print($"No broker running (pipe '{Endpoints.PipeName()}').");
        var s = await c.StatusAsync();
        return Print($"""
            broker {s.BrokerVersion} on node {s.Node}, up since {s.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}
            data: {s.DataDir}
            agents: {s.Online} online / {s.Agents} known
            unread messages: {s.Unread}, pending requests: {s.PendingRequests}
            """);
    }

    // -------------------------------------------------------------- helpers

    /// <summary>Splits positionals from <c>--flags</c>; flags listed in <paramref name="valued"/> take a value.</summary>
    private static (List<string> Positional, Dictionary<string, string> Flags) Parse(string[] args, params string[] valued)
    {
        var pos = new List<string>();
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (valued.Contains(args[i]) && i + 1 < args.Length) flags[args[i]] = args[++i];
                else flags[args[i]] = "";
            }
            else pos.Add(args[i]);
        }
        return (pos, flags);
    }

    private static int Print(string s)
    {
        Console.WriteLine(s);
        return 0;
    }

    private static int Fail(string s)
    {
        Console.Error.WriteLine($"shepherd: {s}");
        return 1;
    }
}
