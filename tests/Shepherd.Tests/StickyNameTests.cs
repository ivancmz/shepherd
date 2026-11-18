using Shepherd.Broker;
using Shepherd.Protocol;

namespace Shepherd.Tests;

/// <summary>ADR-0008: a session that asks for no name gets back the one it had.</summary>
public class StickyNameTests
{
    private const string Repo = @"C:\work\repo";

    private static async Task<(Shepherd.Protocol.Client.BrokerClient Client, RegisterResult Result)> AutoAsync(
        TestBroker b, string sessionKey, string kind = AgentKinds.Claude, string? herdrPane = null)
    {
        var c = await b.ConnectAsync();
        var r = await c.RegisterAsync(new RegisterParams { Kind = kind, SessionKey = sessionKey, Cwd = Repo, HerdrPaneId = herdrPane });
        return (c, r);
    }

    private static async Task WaitOfflineAsync(Shepherd.Protocol.Client.BrokerClient observer, params string[] names) =>
        await BrokerTests.WaitUntil(async () =>
            (await observer.ListAgentsAsync()).Agents.Where(a => names.Contains(a.Name)).All(a => !a.Online));

    [Fact]
    public async Task Automatic_names_do_not_reshuffle_when_sessions_come_back_in_another_order()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        var (c1, r1) = await AutoAsync(b, "claude:s1");
        var (c2, r2) = await AutoAsync(b, "claude:s2");
        Assert.Equal("repo-claude", r1.Name);
        Assert.Equal("repo-claude-2", r2.Name);
        await c1.DisposeAsync();
        await c2.DisposeAsync();
        await WaitOfflineAsync(lead, "repo-claude", "repo-claude-2");

        // The second session comes back first: it must not take the first one's name.
        var (c2b, r2b) = await AutoAsync(b, "claude:s2");
        var (c1b, r1b) = await AutoAsync(b, "claude:s1");
        await using var _1 = c1b;
        await using var _2 = c2b;
        Assert.Equal("repo-claude-2", r2b.Name);
        Assert.Equal("repo-claude", r1b.Name);
        Assert.True(r1b.Automatic);
    }

    [Fact]
    public async Task Automatic_names_survive_a_broker_restart()
    {
        await using var b = await TestBroker.StartAsync();
        var (c1, _) = await AutoAsync(b, "claude:s1");
        var (c2, _) = await AutoAsync(b, "claude:s2");
        await c1.DisposeAsync();
        await c2.DisposeAsync();
        await b.RestartAsync();

        var (c2b, r2b) = await AutoAsync(b, "claude:s2");
        await using var _2 = c2b;
        Assert.Equal("repo-claude-2", r2b.Name);
    }

    [Fact]
    public async Task A_new_session_never_gets_the_automatic_name_or_mail_of_another_one()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        var (c1, _) = await AutoAsync(b, "claude:s1");
        await c1.DisposeAsync();
        await WaitOfflineAsync(lead, "repo-claude");
        await lead.SendAsync(new SendParams { To = ["repo-claude"], Body = "for the first session only" });

        var (c3, r3) = await AutoAsync(b, "claude:s3");
        await using var _3 = c3;
        Assert.Equal("repo-claude-2", r3.Name);
        Assert.Empty((await c3.ReadInboxAsync(new ReadInboxParams())).Messages);

        var (c1b, r1b) = await AutoAsync(b, "claude:s1");
        await using var _1 = c1b;
        Assert.Equal("repo-claude", r1b.Name);
        Assert.Equal("for the first session only", Assert.Single((await c1b.ReadInboxAsync(new ReadInboxParams())).Messages).Body);
    }

    [Fact]
    public async Task A_chosen_name_comes_back_when_the_session_registers_without_one()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        var (c1, _) = await AutoAsync(b, "claude:s1");
        await c1.RegisterAsync(new RegisterParams { Name = "reviewer", Kind = AgentKinds.Claude, SessionKey = "claude:s1", Cwd = Repo });
        await c1.DisposeAsync();
        await WaitOfflineAsync(lead, "reviewer");

        // A restarted shim has forgotten the name the agent chose with `register`.
        var (c1b, r1b) = await AutoAsync(b, "claude:s1");
        await using var _1 = c1b;
        Assert.Equal("reviewer", r1b.Name);
        Assert.False(r1b.Automatic);
    }

    [Fact]
    public async Task An_explicit_name_still_belongs_to_the_role()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        await (await b.AgentAsync("dev", sessionKey: "claude:old")).DisposeAsync();
        await WaitOfflineAsync(lead, "dev");
        await using var dev2 = await b.AgentAsync("dev", sessionKey: "claude:new");

        // The old session lost the role: without a name it gets an automatic one, not "dev".
        var (old, r) = await AutoAsync(b, "claude:old");
        await using var _ = old;
        Assert.Equal("repo-claude", r.Name);
    }

    // ------------------------------------------------------------ ADR-0013

    [Fact]
    public async Task A_new_session_in_the_same_herdr_pane_inherits_the_name_and_mail()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        var (old, r1) = await AutoAsync(b, "claude:old", herdrPane: "w7:p1");
        await old.DisposeAsync();
        await WaitOfflineAsync(lead, r1.Name);
        await lead.SendAsync(new SendParams { To = [r1.Name], Body = "for whoever works in this pane" });

        var (fresh, r2) = await AutoAsync(b, "claude:fresh", herdrPane: "w7:p1");
        await using var _ = fresh;
        Assert.Equal("repo-claude", r2.Name);
        Assert.True(r2.Automatic);
        Assert.Equal("for whoever works in this pane", Assert.Single((await fresh.ReadInboxAsync(new ReadInboxParams())).Messages).Body);
    }

    [Fact]
    public async Task A_chosen_name_is_inherited_through_the_pane_too()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        var (old, _) = await AutoAsync(b, "claude:old", herdrPane: "w7:p1");
        await old.RegisterAsync(new RegisterParams { Name = "reviewer", Kind = AgentKinds.Claude, SessionKey = "claude:old", Cwd = Repo, HerdrPaneId = "w7:p1" });
        await old.DisposeAsync();
        await WaitOfflineAsync(lead, "reviewer");

        var (fresh, r) = await AutoAsync(b, "claude:fresh", herdrPane: "w7:p1");
        await using var _ = fresh;
        Assert.Equal("reviewer", r.Name);
        Assert.False(r.Automatic);
    }

    [Fact]
    public async Task The_pane_is_not_inherited_from_a_live_occupant_another_folder_or_another_pane()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        var (live, r0) = await AutoAsync(b, "claude:live", herdrPane: "w7:p1");
        await using var _0 = live;
        var (second, r1) = await AutoAsync(b, "claude:second", herdrPane: "w7:p1");
        await using var _1 = second;
        Assert.NotEqual(r0.Name, r1.Name); // the occupant is still connected

        // Same pane id, another project (e.g. another herdr workspace): no inheritance.
        await using (var c = await b.ConnectAsync())
            await c.RegisterAsync(new RegisterParams { Kind = AgentKinds.Claude, SessionKey = "claude:other", Cwd = @"C:\work\other", HerdrPaneId = "w9:p9" });
        await WaitOfflineAsync(lead, "other-claude");
        await using var elsewhere = await b.ConnectAsync();
        var r2 = await elsewhere.RegisterAsync(new RegisterParams { Kind = AgentKinds.Claude, SessionKey = "claude:x", Cwd = Repo, HerdrPaneId = "w9:p9" });
        Assert.StartsWith("repo-claude", r2.Name);

        // Another pane: no inheritance either.
        var r3 = await (await b.ConnectAsync()).RegisterAsync(new RegisterParams { Kind = AgentKinds.Claude, SessionKey = "claude:y", Cwd = @"C:\work\other", HerdrPaneId = "w9:p8" });
        Assert.Equal("other-claude-2", r3.Name);
    }

    [Fact]
    public async Task A_session_keeps_its_own_name_even_in_another_occupants_pane()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        var (a, ra) = await AutoAsync(b, "claude:a", herdrPane: "w7:p1");
        var (c, rc) = await AutoAsync(b, "claude:c", herdrPane: "w7:p2");
        await a.DisposeAsync();
        await c.DisposeAsync();
        await WaitOfflineAsync(lead, ra.Name, rc.Name);

        // Session c is resumed in pane p1: its own row wins over the pane's.
        var (c2, rc2) = await AutoAsync(b, "claude:c", herdrPane: "w7:p1");
        await using var _ = c2;
        Assert.Equal(rc.Name, rc2.Name);
    }

    [Fact]
    public async Task Renaming_frees_an_automatic_name_that_never_carried_mail()
    {
        await using var b = await TestBroker.StartAsync();
        var (c1, _) = await AutoAsync(b, "claude:s1");
        await using var _1 = c1;
        await c1.RegisterAsync(new RegisterParams { Name = "reviewer", Kind = AgentKinds.Claude, SessionKey = "claude:s1", Cwd = Repo });
        Assert.DoesNotContain((await c1.ListAgentsAsync()).Agents, a => a.Name == "repo-claude");
    }

    [Fact]
    public async Task Codex_resuming_a_thread_gets_its_previous_name_and_mail()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        var (first, r1) = await AutoAsync(b, "codex:pid:1", AgentKinds.Codex);
        Assert.Equal("repo-codex", r1.Name);
        await first.IdentifyAsync(new IdentifyParams("thread-x"));
        await first.DisposeAsync();
        await WaitOfflineAsync(lead, "repo-codex");
        await lead.SendAsync(new SendParams { To = ["repo-codex"], Body = "to the old name" });

        // A new Codex process: provisional name until its first tool call reveals the thread.
        var (second, r2) = await AutoAsync(b, "codex:pid:2", AgentKinds.Codex);
        await using var _2 = second;
        Assert.Equal("repo-codex-2", r2.Name);
        await lead.SendAsync(new SendParams { To = ["repo-codex-2"], Body = "to the provisional name" });

        var id = await second.IdentifyAsync(new IdentifyParams("thread-x"));
        Assert.Equal("repo-codex", id.Name);
        Assert.Equal("repo-codex@pc-test", id.Address);
        Assert.True(id.Automatic);

        var bodies = (await second.ReadInboxAsync(new ReadInboxParams())).Messages.Select(m => m.Body).ToList();
        Assert.Equal(["to the old name", "to the provisional name"], bodies);
        var agents = (await lead.ListAgentsAsync()).Agents;
        Assert.DoesNotContain(agents, a => a.Name == "repo-codex-2");
        Assert.True(agents.Single(a => a.Name == "repo-codex").Online);
    }

    [Fact]
    public async Task Codex_with_a_chosen_name_keeps_it_when_its_thread_is_known()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        var (first, _) = await AutoAsync(b, "codex:pid:1", AgentKinds.Codex);
        await first.IdentifyAsync(new IdentifyParams("thread-y"));
        await first.DisposeAsync();
        await WaitOfflineAsync(lead, "repo-codex");

        await using var named = await b.AgentAsync("dev", AgentKinds.Codex, sessionKey: "codex:pid:2");
        var id = await named.IdentifyAsync(new IdentifyParams("thread-y"));
        Assert.Equal("dev", id.Name);
    }

    [Fact]
    public async Task A_stale_codex_thread_is_not_inherited_with_the_name()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        var first = await b.AgentAsync("dev", AgentKinds.Codex, sessionKey: "codex:pid:1");
        await first.IdentifyAsync(new IdentifyParams("thread-old"));
        await first.DisposeAsync();
        await WaitOfflineAsync(lead, "dev");

        // Another Codex takes the role; its thread is unknown until its first tool call.
        await using var second = await b.AgentAsync("dev", AgentKinds.Codex, sessionKey: "codex:pid:2");
        await lead.SendAsync(new SendParams { To = ["dev"], Body = "hello" });
        await Task.Delay(Wakeups.CoalesceDelay * 3);
        Assert.Empty(b.Runner.Calls); // never `codex queue --thread thread-old`
    }
}
