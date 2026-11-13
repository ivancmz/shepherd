using System.Text;
using Shepherd.Protocol;
using Shepherd.Protocol.Wire;

namespace Shepherd.Tests;

public class ProtocolTests
{
    [Theory]
    [InlineData("dev", true)]
    [InlineData("lead-2", true)]
    [InlineData("a", false)]
    [InlineData("-dev", false)]
    [InlineData("dev-", false)]
    [InlineData("Dev", false)]
    [InlineData("dev name", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456", false)]
    public void Name_validation(string name, bool valid) => Assert.Equal(valid, Names.IsValid(name));

    [Theory]
    [InlineData("My Repo", "my-repo")]
    [InlineData("Año_2026!!", "ano-2026")]
    [InlineData("  ", "agent")]
    [InlineData("C++", "agent")]
    public void Sanitize(string input, string expected) => Assert.Equal(expected, Names.Sanitize(input));

    [Theory]
    [InlineData("dev", "dev", "pc-a")]
    [InlineData("@dev", "dev", "pc-a")]
    [InlineData("dev@wsl-ubuntu", "dev", "wsl-ubuntu")]
    [InlineData("DEV@PC-A", "dev", "pc-a")]
    public void Address_parsing(string text, string name, string node)
    {
        Assert.True(Names.TryParseAddress(text, "pc-a", out var n, out var nd));
        Assert.Equal(name, n);
        Assert.Equal(node, nd);
    }

    [Fact]
    public async Task Line_channel_splits_lines_and_skips_oversized_ones()
    {
        var big = new string('x', 300);
        var input = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"a\":1}}\r\n\n{big}\n{{\"b\":\"ñ\"}}\n{{\"tail\":true}}"));
        var ch = new LineChannel(input, Stream.Null, maxLineBytes: 100);
        Assert.Equal("{\"a\":1}", (await ch.ReadLineAsync(default))!.Text);
        Assert.True((await ch.ReadLineAsync(default))!.IsTooLarge);
        Assert.Equal("{\"b\":\"ñ\"}", (await ch.ReadLineAsync(default))!.Text);
        Assert.Equal("{\"tail\":true}", (await ch.ReadLineAsync(default))!.Text);
        Assert.Null(await ch.ReadLineAsync(default));
    }

    [Fact]
    public async Task Rpc_peers_talk_both_ways()
    {
        await using var pair = await DuplexPair.CreateAsync();
        await using var a = new RpcPeer(new LineChannel(pair.Server, pair.Server), "a");
        await using var b = new RpcPeer(new LineChannel(pair.Client, pair.Client), "b");
        a.OnRequest = (m, p, _, _) => m == "echo" ? Task.FromResult(p) : throw new RpcException(RpcErrors.MethodNotFound, m);
        b.OnRequest = (m, _, _, _) => Task.FromResult<System.Text.Json.Nodes.JsonNode?>(m);
        _ = Task.Run(a.RunAsync);
        _ = Task.Run(b.RunAsync);

        Assert.Equal("{\"x\":1}", (await b.RequestAsync("echo", new System.Text.Json.Nodes.JsonObject { ["x"] = 1 }))!.ToJsonString());
        Assert.Equal("\"hey\"", (await a.RequestAsync("hey", null))!.ToJsonString());
        await Assertions.ThrowsRpc(RpcErrors.MethodNotFound, () => b.RequestAsync("nope", null));
        Assert.NotNull(await b.RequestAsync("ping", null)); // built-in

        b.Close();
        await Assertions.ThrowsRpc(RpcErrors.BrokerUnavailable, () => b.RequestAsync("echo", null));
    }
}
