using System.Text.Json;
using Shepherd.Broker;
using Shepherd.Protocol;
using Shepherd.Protocol.Wire;

namespace Shepherd.Tests;

public class WslTests
{
    [Theory]
    [InlineData(@"C:\repo\src\x.c", "pc-a", null, "wsl-ubuntu", "Ubuntu", "/mnt/c/repo/src/x.c")]
    [InlineData(@"D:/data/f.txt", "pc-a", null, "wsl-ubuntu", "Ubuntu", "/mnt/d/data/f.txt")]
    [InlineData(@"C:\", "pc-a", null, "wsl-ubuntu", "Ubuntu", "/mnt/c")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\u\p.md", "pc-a", null, "wsl-ubuntu", "Ubuntu", "/home/u/p.md")]
    [InlineData(@"\\wsl$\Ubuntu\home\u\p.md", "pc-a", null, "wsl-ubuntu", "Ubuntu", "/home/u/p.md")]
    [InlineData(@"\\wsl.localhost\Debian\home\u\p.md", "pc-a", null, "wsl-ubuntu", "Ubuntu", @"\\wsl.localhost\Debian\home\u\p.md")]
    [InlineData("/mnt/c/repo/x.c", "wsl-ubuntu", "Ubuntu", "pc-a", null, @"C:\repo\x.c")]
    [InlineData("/home/u/notes.md", "wsl-ubuntu", "Ubuntu", "pc-a", null, @"\\wsl.localhost\Ubuntu\home\u\notes.md")]
    [InlineData("relative/path.c", "wsl-ubuntu", "Ubuntu", "pc-a", null, "relative/path.c")]
    [InlineData(@"C:\same\node.c", "pc-a", null, "pc-a", null, @"C:\same\node.c")]
    [InlineData(@"C:\other\pc.c", "pc-a", null, "pc-b", null, @"C:\other\pc.c")]
    public void Paths_are_translated_between_windows_and_wsl(string path, string from, string? fromDistro, string to, string? toDistro, string expected) =>
        Assert.Equal(expected, PathMap.Translate(path, from, fromDistro, to, toDistro));

    [Fact]
    public async Task Tcp_requires_the_node_token()
    {
        await using var b = await TestBroker.StartAsync(tcp: true);
        await Assertions.ThrowsRpc(RpcErrors.Unauthorized, () => b.ConnectTcpAsync("wsl-ubuntu", token: "wrong-token-wrong-token-wrong-token"));

        await using var ok = await b.ConnectTcpAsync("wsl-ubuntu");
        var r = await ok.RegisterAsync(new RegisterParams { Name = "dev", Kind = AgentKinds.Codex, SessionKey = "codex:pid:7", Distro = "Ubuntu" });
        Assert.Equal("dev@wsl-ubuntu", r.Address);
    }

    [Fact]
    public async Task Endpoint_file_and_token_are_published_for_wsl()
    {
        await using var b = await TestBroker.StartAsync(tcp: true);
        var ep = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(b.DataDir, Endpoints.EndpointFileName)), ProtocolJson.Default.BrokerEndpoint)!;
        Assert.Equal("127.0.0.1", ep.Address);
        Assert.Equal(b.TcpEndpoint!.Port, ep.Port);
        Assert.Equal(b.Token, File.ReadAllText(Path.Combine(b.DataDir, Endpoints.TokenFileName)).Trim());
        Assert.True(b.Token.Length >= 32);
    }

    [Fact]
    public async Task Windows_and_wsl_agents_talk_with_translated_refs()
    {
        await using var b = await TestBroker.StartAsync(tcp: true);
        await using var lead = await b.AgentAsync("lead");
        await using var dev = await b.ConnectTcpAsync("wsl-ubuntu");
        await dev.RegisterAsync(new RegisterParams { Name = "dev", Kind = AgentKinds.Claude, SessionKey = "claude:wsl", Distro = "Ubuntu" });

        var sent = await lead.SendAsync(new SendParams
        {
            To = ["dev@wsl-ubuntu"], Body = "review", Kind = MessageKinds.Request, Refs = [new FileRef(@"C:\repo\x.c", Lines: "1-9")],
        });
        var msg = Assert.Single((await dev.ReadInboxAsync(new ReadInboxParams())).Messages);
        Assert.Equal("lead@pc-test", msg.From);
        Assert.Equal("/mnt/c/repo/x.c", msg.Refs!.Single().Path);
        Assert.Equal("1-9", msg.Refs!.Single().Lines);

        // The reply goes back by plain name resolution: lead is on another node, so the address is full.
        await dev.ReplyAsync(new ReplyParams(sent.MessageId, "see notes", [new FileRef("/home/u/notes.md")]));
        var back = Assert.Single((await lead.ReadInboxAsync(new ReadInboxParams())).Messages);
        Assert.Equal("dev@wsl-ubuntu", back.From);
        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\u\notes.md", back.Refs!.Single().Path);

        // Bare names resolve on the sender's own node: 'lead' from WSL is unknown there.
        await Assertions.ThrowsRpc(RpcErrors.UnknownAgent, () => dev.SendAsync(new SendParams { To = ["lead"], Body = "x" }));
        await dev.SendAsync(new SendParams { To = ["lead@pc-test"], Body = "x" });
    }

    [Fact]
    public async Task Codex_in_wsl_is_woken_through_wsl_exe()
    {
        await using var b = await TestBroker.StartAsync(tcp: true);
        await using var lead = await b.AgentAsync("lead");
        await using var dev = await b.ConnectTcpAsync("wsl-ubuntu");
        await dev.RegisterAsync(new RegisterParams { Name = "dev", Kind = AgentKinds.Codex, SessionKey = "codex:pid:9", Distro = "Ubuntu-24.04" });
        await dev.IdentifyAsync(new IdentifyParams("thread-wsl"));

        await lead.SendAsync(new SendParams { To = ["dev@wsl-ubuntu"], Body = "hi" });
        await BrokerTests.WaitUntil(() => b.Runner.Calls.Count == 1);
        var call = b.Runner.Calls[0];
        Assert.Equal("wsl-test", call.File);
        Assert.Equal(["-d", "Ubuntu-24.04", "-e", "bash", "-ic", "codex queue \"$@\"", "bash", "--thread", "thread-wsl", "--message"], call.Args[..^1]);
        Assert.StartsWith("[shepherd] You have 1 new message(s) from lead@pc-test.", call.Args[^1]);
    }
}
