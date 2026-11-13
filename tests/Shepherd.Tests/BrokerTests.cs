using System.Text.Json.Nodes;
using Shepherd.Protocol;
using Shepherd.Protocol.Wire;

namespace Shepherd.Tests;

public class BrokerTests
{
    [Fact]
    public async Task Request_reply_round_trip()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        await using var dev = await b.AgentAsync("dev", AgentKinds.Codex);

        var sent = await lead.SendAsync(new SendParams
        {
            To = ["dev"], Body = "Please review x.c", Kind = MessageKinds.Request, Subject = "review",
            Refs = [new FileRef(@"C:\repo\x.c", Lines: "10-20")],
        });
        Assert.Equal(["dev@pc-test"], sent.To);

        var agents = await lead.ListAgentsAsync();
        Assert.Equal(["dev@pc-test"], agents.Agents.Single(a => a.Name == "lead").WaitingOn);
        Assert.Equal(1, agents.Agents.Single(a => a.Name == "dev").Unread);

        var inbox = await dev.ReadInboxAsync(new ReadInboxParams());
        var msg = Assert.Single(inbox.Messages);
        Assert.Equal("lead@pc-test", msg.From);
        Assert.Equal(MessageKinds.Request, msg.Kind);
        Assert.Equal("review", msg.Subject);
        Assert.Equal(@"C:\repo\x.c", msg.Refs!.Single().Path);
        Assert.Equal(0, inbox.Remaining);
        Assert.Empty((await dev.ReadInboxAsync(new ReadInboxParams())).Messages); // marked read

        var reply = await dev.ReplyAsync(new ReplyParams(msg.Id, "LGTM"));
        Assert.Equal(msg.ThreadId, reply.ThreadId);

        var back = Assert.Single((await lead.ReadInboxAsync(new ReadInboxParams())).Messages);
        Assert.Equal(MessageKinds.Reply, back.Kind);
        Assert.Equal(msg.Id, back.InReplyTo);
        Assert.Equal("Re: review", back.Subject);
        Assert.Equal("dev@pc-test", back.From);

        agents = await lead.ListAgentsAsync();
        Assert.Empty(agents.Agents.Single(a => a.Name == "lead").WaitingOn);
    }

    [Fact]
    public async Task Unknown_recipient_is_rejected()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        await Assertions.ThrowsRpc(RpcErrors.UnknownAgent, () => lead.SendAsync(new SendParams { To = ["ghost"], Body = "hi" }));
    }

    [Fact]
    public async Task Cannot_send_to_self_or_use_reply_kind()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        await using var dev = await b.AgentAsync("dev");
        await Assertions.ThrowsRpc(RpcErrors.InvalidParams, () => lead.SendAsync(new SendParams { To = ["lead"], Body = "hi" }));
        await Assertions.ThrowsRpc(RpcErrors.InvalidParams, () => lead.SendAsync(new SendParams { To = ["dev"], Body = "hi", Kind = MessageKinds.Reply }));
    }

    [Fact]
    public async Task Live_name_cannot_be_stolen_but_same_session_takes_over()
    {
        await using var b = await TestBroker.StartAsync();
        await using var first = await b.AgentAsync("dev", sessionKey: "claude:aaa");

        await using var thief = await b.ConnectAsync();
        await Assertions.ThrowsRpc(RpcErrors.NameTaken, () =>
            thief.RegisterAsync(new RegisterParams { Name = "dev", Kind = AgentKinds.Claude, SessionKey = "claude:bbb" }));

        // Same session key (a reconnect) reclaims the name immediately; the old link is closed.
        await using var again = await b.ConnectAsync();
        var r = await again.RegisterAsync(new RegisterParams { Name = "dev", Kind = AgentKinds.Claude, SessionKey = "claude:aaa" });
        Assert.Equal("dev@pc-test", r.Address);
        await WaitUntil(() => !first.IsConnected);
    }

    [Fact]
    public async Task Name_is_released_on_disconnect_and_mailbox_is_inherited()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        var dev = await b.AgentAsync("dev", sessionKey: "codex:pid:1");
        await dev.DisposeAsync();
        await WaitUntil(async () => !(await lead.ListAgentsAsync()).Agents.Single(a => a.Name == "dev").Online);

        await lead.SendAsync(new SendParams { To = ["dev"], Body = "for whoever is dev next" });

        await using var dev2 = await b.AgentAsync("dev", AgentKinds.Codex, sessionKey: "codex:pid:2");
        var msg = Assert.Single((await dev2.ReadInboxAsync(new ReadInboxParams())).Messages);
        Assert.Equal("for whoever is dev next", msg.Body);
    }

    [Fact]
    public async Task Automatic_names_follow_folder_and_kind()
    {
        await using var b = await TestBroker.StartAsync();
        await using var a1 = await b.ConnectAsync();
        var r1 = await a1.RegisterAsync(new RegisterParams { Kind = AgentKinds.Codex, SessionKey = "codex:pid:10", Cwd = @"C:\work\My Repo" });
        Assert.Equal("my-repo-codex", r1.Name);
        Assert.True(r1.Automatic);

        await using var a2 = await b.ConnectAsync();
        var r2 = await a2.RegisterAsync(new RegisterParams { Kind = AgentKinds.Codex, SessionKey = "codex:pid:11", Cwd = "/home/u/my-repo" });
        Assert.Equal("my-repo-codex-2", r2.Name);
    }

    [Fact]
    public async Task Invalid_names_are_rejected()
    {
        await using var b = await TestBroker.StartAsync();
        await using var c = await b.ConnectAsync();
        await Assertions.ThrowsRpc(RpcErrors.InvalidName, () => c.RegisterAsync(new RegisterParams { Name = "Bad Name!", Kind = AgentKinds.Claude }));
        await Assertions.ThrowsRpc(RpcErrors.InvalidName, () => c.RegisterAsync(new RegisterParams { Name = "x", Kind = AgentKinds.Claude }));
    }

    [Fact]
    public async Task Oversized_body_is_rejected()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        await using var dev = await b.AgentAsync("dev");
        await Assertions.ThrowsRpc(RpcErrors.TooLarge, () =>
            lead.SendAsync(new SendParams { To = ["dev"], Body = new string('x', 64 * 1024 + 1) }));
    }

    [Fact]
    public async Task Requests_require_hello_and_registration()
    {
        await using var b = await TestBroker.StartAsync();
        await using var c = await b.ConnectAsync();
        await Assertions.ThrowsRpc(RpcErrors.NotRegistered, () => c.ReadInboxAsync(new ReadInboxParams()));
    }

    [Fact]
    public async Task Protocol_mismatch_is_reported()
    {
        await using var b = await TestBroker.StartAsync();
        await using var c = await b.ConnectAsync();
        var bad = Json.ToNode(new HelloParams(99, "pc-test", "mcp"), ProtocolJson.Default.HelloParams);
        await Assertions.ThrowsRpc(RpcErrors.ProtocolMismatch, () => c.Peer.RequestAsync(Methods.Hello, bad));
    }

    [Fact]
    public async Task Human_user_sends_and_receives()
    {
        await using var b = await TestBroker.StartAsync();
        await using var dev = await b.AgentAsync("dev");
        await using var human = await b.ConnectAsync("cli");
        var me = await human.RegisterAsync(new RegisterParams { Kind = AgentKinds.Human, Name = "alice" });
        Assert.Equal("alice@pc-test", me.Address);

        await human.SendAsync(new SendParams { To = ["dev"], Body = "status?", Kind = MessageKinds.Request });
        var msg = Assert.Single((await dev.ReadInboxAsync(new ReadInboxParams())).Messages);
        Assert.True(msg.FromHuman);
        await dev.ReplyAsync(new ReplyParams(msg.Id, "all green"));

        // A second console of the same user shares the same mailbox.
        await using var human2 = await b.ConnectAsync("cli");
        await human2.RegisterAsync(new RegisterParams { Kind = AgentKinds.Human, Name = "alice" });
        var reply = Assert.Single((await human2.ReadInboxAsync(new ReadInboxParams())).Messages);
        Assert.Equal("all green", reply.Body);
    }

    [Fact]
    public async Task Only_recipients_can_reply()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        await using var dev = await b.AgentAsync("dev");
        await using var other = await b.AgentAsync("other");
        var sent = await lead.SendAsync(new SendParams { To = ["dev"], Body = "x", Kind = MessageKinds.Request });
        await Assertions.ThrowsRpc(RpcErrors.NotFound, () => other.ReplyAsync(new ReplyParams(sent.MessageId, "not mine")));
    }

    [Fact]
    public async Task Live_recipient_gets_message_new_push()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        await using var dev = await b.AgentAsync("dev");
        var got = new TaskCompletionSource<JsonNode?>();
        dev.Peer.OnNotification = (m, p) => { if (m == Methods.MessageNew) got.TrySetResult(p); return Task.CompletedTask; };

        await lead.SendAsync(new SendParams { To = ["dev"], Body = "ping!" });
        var pushed = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ping!", Json.Result(pushed, ProtocolJson.Default.InboxMessage).Body);
    }

    [Fact]
    public async Task Messages_survive_a_broker_restart()
    {
        await using var b = await TestBroker.StartAsync();
        await using (var lead = await b.AgentAsync("lead"))
        await using (var dev = await b.AgentAsync("dev", sessionKey: "claude:dev"))
        {
            await lead.SendAsync(new SendParams { To = ["dev"], Body = "persist me" });
        }
        await b.RestartAsync();
        await using var dev2 = await b.AgentAsync("dev", sessionKey: "claude:dev");
        Assert.Equal("persist me", Assert.Single((await dev2.ReadInboxAsync(new ReadInboxParams())).Messages).Body);
    }

    [Fact]
    public async Task Audit_log_records_sends()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        await using var dev = await b.AgentAsync("dev");
        await lead.SendAsync(new SendParams { To = ["dev"], Body = "audited" });
        var file = Assert.Single(Directory.GetFiles(b.DataDir, "audit-*.jsonl"));
        var line = JsonNode.Parse(File.ReadAllLines(file).Last())!;
        Assert.Equal("send", line["event"]!.GetValue<string>());
        Assert.Equal("audited", line["body"]!.GetValue<string>());
    }

    internal static async Task WaitUntil(Func<bool> cond) => await WaitUntil(() => Task.FromResult(cond()));

    internal static async Task WaitUntil(Func<Task<bool>> cond)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await cond()) return;
            await Task.Delay(50);
        }
        Assert.Fail("condition not met in time");
    }
}
