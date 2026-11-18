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
            var server = new McpServer(pair.Server, pair.Server, broker.Connect, _ => { }, k => env.GetValueOrDefault(k));
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
        await using var server = new McpServer(pair.Server, pair.Server, b.Connect, _ => { }, _ => null);
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
    public async Task Channel_session_gets_messages_pushed_and_acked()
    {
        await using var b = await TestBroker.StartAsync();
        var pushed = new TaskCompletionSource<JsonNode?>();
        await using var dev = await Shim.StartAsync(b, new()
        {
            ["CLAUDE_CODE_SESSION_ID"] = "s-dev", ["SHEPHERD_NAME"] = "dev", ["SHEPHERD_CHANNEL"] = "1",
        });
        dev.Client.OnNotification = (m, p) =>
        {
            if (m == "notifications/claude/channel") pushed.TrySetResult(p);
            return Task.CompletedTask;
        };
        await dev.CallAsync("whoami", new JsonObject()); // registered

        await using var lead = await b.AgentAsync("lead");
        var sent = await lead.SendAsync(new SendParams
        {
            To = ["dev"], Body = "please check", Kind = MessageKinds.Request, Subject = "check",
            Refs = [new FileRef(@"C:\r\a.c", Lines: "3")],
        });

        var ev = (await pushed.Task.WaitAsync(TimeSpan.FromSeconds(5)))!;
        Assert.Equal("please check\n\nFiles referenced:\n- C:\\r\\a.c (lines 3)", ev["content"]!.GetValue<string>());
        var meta = ev["meta"]!;
        Assert.Equal("lead@pc-test", meta["from"]!.GetValue<string>());
        Assert.Equal("request", meta["kind"]!.GetValue<string>());
        Assert.Equal(sent.MessageId, meta["message_id"]!.GetValue<string>());
        Assert.Equal("check", meta["subject"]!.GetValue<string>());
        Assert.All(meta.AsObject(), kv => Assert.Matches("^[a-z_]+$", kv.Key)); // Claude drops other keys

        // Acked by the shim → read, so it does not show up again in read_inbox.
        await BrokerTests.WaitUntil(async () => (await lead.ListAgentsAsync()).Agents.Single(a => a.Name == "dev").Unread == 0);
        Assert.Empty(b.Runner.Calls);
    }

    [Fact]
    public async Task Channel_capability_only_for_claude_with_channel_flag()
    {
        await using var b = await TestBroker.StartAsync();
        foreach (var (env, client, expected) in new (Dictionary<string, string?>, string, bool)[]
        {
            (new() { ["CLAUDE_CODE_SESSION_ID"] = "a", ["SHEPHERD_CHANNEL"] = "1" }, "claude-code", true),
            (new() { ["CLAUDE_CODE_SESSION_ID"] = "b" }, "claude-code", false),
            (new() { ["SHEPHERD_CHANNEL"] = "1" }, "codex-mcp-client", false),
        })
        {
            var pair = await DuplexPair.CreateAsync();
            await using var server = new McpServer(pair.Server, pair.Server, b.Connect, _ => { }, k => env.GetValueOrDefault(k));
            _ = Task.Run(server.RunAsync);
            await using var c = new RpcPeer(new LineChannel(pair.Client, pair.Client), "c");
            _ = Task.Run(c.RunAsync);
            var init = await c.RequestAsync("initialize", new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = client } });
            Assert.Equal(expected, init!["capabilities"]!["experimental"]?["claude/channel"] is not null);
            Assert.Equal(expected, init["instructions"]!.GetValue<string>().Contains("<channel", StringComparison.Ordinal));
        }
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
    public async Task Mail_waiting_at_registration_is_mentioned_once_in_a_tool_result()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        await (await b.AgentAsync("dev", sessionKey: "claude:s5")).DisposeAsync();
        await lead.SendAsync(new SendParams { To = ["dev"], Body = "while you were away" });

        // No channel and no herdr pane: nothing can wake this session (ADR-0009 §5).
        await using var shim = await Shim.StartAsync(b, new() { ["CLAUDE_CODE_SESSION_ID"] = "s5" });
        var (first, _) = await shim.CallAsync("whoami", new JsonObject());
        Assert.Contains("You are 'dev'", first); // ADR-0008: the name follows the session
        Assert.Contains("1 unread message(s) waiting; use read_inbox.", first);
        var (second, _) = await shim.CallAsync("whoami", new JsonObject());
        Assert.DoesNotContain("unread", second);
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
