using System.Text.Json.Nodes;
using Shepherd.Mcp;
using Shepherd.Protocol;
using Shepherd.Protocol.Wire;

namespace Shepherd.Tests;

public class McpServerTests
{
    /// <summary>An MCP client (test side) connected to a shim running in-process.</summary>
    private sealed class Shim : IAsyncDisposable
    {
        private readonly DuplexPair _pair;
        private readonly McpServer _server;
        private readonly Task _serverRun;
        public RpcPeer Client { get; }
        private readonly Task _clientRun;

        private Shim(DuplexPair pair, McpServer server, RpcPeer client)
        {
            _pair = pair;
            _server = server;
            Client = client;
            _serverRun = Task.Run(server.RunAsync);
            _clientRun = Task.Run(client.RunAsync);
        }

        public static async Task<Shim> StartAsync(TestBroker broker, Dictionary<string, string?> env, string clientName = "claude-code")
        {
            var pair = await DuplexPair.CreateAsync();
            var server = new McpServer(pair.Server, pair.Server, broker.PipeName, _ => { }, k => env.GetValueOrDefault(k));
            var client = new RpcPeer(new LineChannel(pair.Client, pair.Client), "test-client");
            var shim = new Shim(pair, server, client);
            await client.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-11-25",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = clientName, ["version"] = "1" },
            });
            await client.NotifyAsync("notifications/initialized", null);
            return shim;
        }

        public async Task<(string Text, bool IsError)> CallAsync(string tool, JsonObject args, JsonObject? meta = null)
        {
            var p = new JsonObject { ["name"] = tool, ["arguments"] = args };
            if (meta is not null) p["_meta"] = meta.DeepClone();
            var r = await Client.RequestAsync("tools/call", p);
            return (r!["content"]![0]!["text"]!.GetValue<string>(), r["isError"]!.GetValue<bool>());
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _server.DisposeAsync();
            await _pair.DisposeAsync();
            try { await Task.WhenAll(_serverRun, _clientRun); } catch { }
        }
    }

    [Fact]
    public async Task Initialize_negotiates_protocol_and_lists_tools()
    {
        await using var b = await TestBroker.StartAsync();
        var pair = await DuplexPair.CreateAsync();
        await using var server = new McpServer(pair.Server, pair.Server, b.PipeName, _ => { }, _ => null);
        _ = Task.Run(server.RunAsync);
        await using var client = new RpcPeer(new LineChannel(pair.Client, pair.Client), "c");
        _ = Task.Run(client.RunAsync);

        // The 2026-07-28 discovery probe must fail with -32601 so clients fall back to initialize.
        await Assertions.ThrowsRpc(RpcErrors.MethodNotFound, () => client.RequestAsync("server/discover", new JsonObject()));

        var init = await client.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-11-25",
            ["clientInfo"] = new JsonObject { ["name"] = "claude-code" },
        });
        Assert.Equal("2025-11-25", init!["protocolVersion"]!.GetValue<string>());
        Assert.Contains("herdr agent prompt", init["instructions"]!.GetValue<string>());

        var old = await client.RequestAsync("initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" });
        Assert.Equal("2025-06-18", old!["protocolVersion"]!.GetValue<string>());

        var tools = (await client.RequestAsync("tools/list", null))!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["send", "reply", "read_inbox", "list_agents", "register", "whoami"], tools);
    }

    [Fact]
    public async Task Two_shims_exchange_a_request_and_reply()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await Shim.StartAsync(b, new() { ["CLAUDE_CODE_SESSION_ID"] = "s-lead", ["SHEPHERD_NAME"] = "lead" });
        await using var dev = await Shim.StartAsync(b, new() { ["SHEPHERD_NAME"] = "dev" }, clientName: "codex-mcp-client");

        // Both registered (wait for the background links).
        var (who, _) = await dev.CallAsync("whoami", new JsonObject());
        Assert.Contains("'dev' (dev@", who);
        (who, _) = await lead.CallAsync("whoami", new JsonObject());
        Assert.Contains("'lead'", who);

        var (sent, err) = await lead.CallAsync("send", new JsonObject
        {
            ["to"] = new JsonArray("dev"), ["body"] = "run the tests", ["kind"] = "request",
            ["refs"] = new JsonArray(new JsonObject { ["path"] = @"C:\repo\t.c", ["lines"] = "1-5" }),
        });
        Assert.False(err, sent);
        Assert.Contains("Sent to dev@", sent);

        var codexMeta = new JsonObject { ["x-codex-turn-metadata"] = new JsonObject { ["thread_id"] = "thread-123" } };
        var (inbox, _) = await dev.CallAsync("read_inbox", new JsonObject(), codexMeta);
        Assert.Contains("run the tests", inbox);
        Assert.Contains("from: lead@", inbox);
        Assert.Contains("expects a reply", inbox);
        Assert.Contains(@"ref: C:\repo\t.c (lines 1-5)", inbox);
        var messageId = inbox.Split('\n').First(l => l.StartsWith("message_id: ", StringComparison.Ordinal))["message_id: ".Length..];

        var (replied, rerr) = await dev.CallAsync("reply", new JsonObject { ["message_id"] = messageId, ["body"] = "all green" }, codexMeta);
        Assert.False(rerr, replied);

        var (leadInbox, _) = await lead.CallAsync("read_inbox", new JsonObject());
        Assert.Contains("all green", leadInbox);
        Assert.Contains("kind: reply", leadInbox);

        var (agents, _) = await lead.CallAsync("list_agents", new JsonObject());
        Assert.Contains("lead@pc-test [claude, online, you]", agents);
        Assert.Contains("dev@pc-test [codex, online]", agents);
    }

    [Fact]
    public async Task Taken_name_falls_back_to_automatic_and_tells_the_agent()
    {
        await using var b = await TestBroker.StartAsync();
        await using var holder = await b.AgentAsync("dev", sessionKey: "claude:holder");
        await using var shim = await Shim.StartAsync(b, new() { ["CLAUDE_CODE_SESSION_ID"] = "s2", ["SHEPHERD_NAME"] = "dev" });

        var (who, _) = await shim.CallAsync("whoami", new JsonObject());
        Assert.Contains("automatic name", who);
        Assert.Contains("The name 'dev' is not available", who);

        var (renamed, err) = await shim.CallAsync("register", new JsonObject { ["name"] = "dev-2" });
        Assert.False(err, renamed);
        Assert.Contains("You are now 'dev-2'", renamed);
    }

    [Fact]
    public async Task Tool_errors_are_reported_as_tool_results()
    {
        await using var b = await TestBroker.StartAsync();
        await using var shim = await Shim.StartAsync(b, new() { ["CLAUDE_CODE_SESSION_ID"] = "s3", ["SHEPHERD_NAME"] = "lead" });
        var (text, isError) = await shim.CallAsync("send", new JsonObject { ["to"] = new JsonArray("nobody"), ["body"] = "hi" });
        Assert.True(isError);
        Assert.Contains("unknown agent 'nobody@pc-test'", text);
    }

    [Fact]
    public async Task New_mail_is_mentioned_in_the_next_tool_result()
    {
        await using var b = await TestBroker.StartAsync();
        await using var shim = await Shim.StartAsync(b, new() { ["CLAUDE_CODE_SESSION_ID"] = "s4", ["SHEPHERD_NAME"] = "dev" });
        await shim.CallAsync("whoami", new JsonObject());
        await using var lead = await b.AgentAsync("lead");
        await lead.SendAsync(new SendParams { To = ["dev"], Body = "hello" });

        await BrokerTests.WaitUntil(async () => (await shim.CallAsync("list_agents", new JsonObject())).Text.Contains("new message", StringComparison.Ordinal)
                                               || (await shim.CallAsync("whoami", new JsonObject())).Text.Contains("new message", StringComparison.Ordinal));
    }
}
