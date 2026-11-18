using Shepherd.Mcp;
using Shepherd.Protocol;

namespace Shepherd.Tests;

/// <summary>Retention (ADR-0008 §7, ADR-0011) and the CLI's agent-shell guard (ADR-0010).</summary>
public class MaintenanceTests
{
    [Fact]
    public async Task Audit_months_older_than_the_retention_are_deleted()
    {
        await using var b = await TestBroker.StartAsync();
        var old = Path.Combine(b.DataDir, "audit-2020-01.jsonl");
        var foreign = Path.Combine(b.DataDir, "audit-notes.jsonl");
        // A month that ended less than 90 days ago, whatever today is.
        var recent = Path.Combine(b.DataDir, $"audit-{DateTime.UtcNow.AddMonths(-2):yyyy-MM}.jsonl");
        File.WriteAllText(old, "{}\n");
        File.WriteAllText(foreign, "{}\n");
        File.WriteAllText(recent, "{}\n");

        await b.RestartAsync();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(foreign)); // not ours to delete
        lock (b.Log) Assert.Contains("deleted 1 old audit file(s)", b.Log);
    }

    [Fact]
    public async Task Maintenance_also_runs_while_the_broker_stays_up()
    {
        await using var b = await TestBroker.StartAsync(configure: o => o with
        {
            SweepInterval = TimeSpan.FromMilliseconds(100),
            MaintenanceInterval = TimeSpan.FromMilliseconds(300),
        });
        var old = Path.Combine(b.DataDir, "audit-2020-01.jsonl");
        File.WriteAllText(old, "{}\n");
        await BrokerTests.WaitUntil(() => !File.Exists(old));
    }

    [Fact]
    public async Task Audit_file_has_no_byte_order_mark()
    {
        await using var b = await TestBroker.StartAsync();
        await using var lead = await b.AgentAsync("lead");
        await using var dev = await b.AgentAsync("dev");
        await lead.SendAsync(new SendParams { To = ["dev"], Body = "plain jsonl" });
        var file = Assert.Single(Directory.GetFiles(b.DataDir, "audit-*.jsonl"));
        Assert.Equal((byte)'{', File.ReadAllBytes(file)[0]);
    }

    [Fact]
    public async Task Stale_automatic_agents_are_forgotten_unless_they_have_unread_mail()
    {
        await using var b = await TestBroker.StartAsync();
        await using (var lead = await b.AgentAsync("lead", sessionKey: "claude:lead"))
        {
            foreach (var (key, cwd) in new[] { ("claude:a", @"C:\w\idle"), ("claude:b", @"C:\w\busy") })
            {
                await using var c = await b.ConnectAsync();
                await c.RegisterAsync(new RegisterParams { Kind = AgentKinds.Claude, SessionKey = key, Cwd = cwd });
            }
            await BrokerTests.WaitUntil(async () => (await lead.ListAgentsAsync()).Agents.Count(a => a.Online) == 1);
            await lead.SendAsync(new SendParams { To = ["busy-claude"], Body = "still waiting for you" });
        }

        b.Configure = o => o with { AgentRetention = TimeSpan.Zero };
        await b.RestartAsync();

        await using var observer = await b.AgentAsync("observer");
        var names = (await observer.ListAgentsAsync()).Agents.Select(a => a.Name).ToList();
        Assert.DoesNotContain("idle-claude", names);
        Assert.Contains("busy-claude", names); // unread mail keeps it
        Assert.Contains("lead", names);        // chosen names are never forgotten
    }

    [Theory]
    [InlineData("1", new string[0], "claude")]
    [InlineData(null, new[] { "bash", "node", "Codex", "explorer" }, "codex")]
    [InlineData(null, new[] { "pwsh", "claude" }, "claude")]
    [InlineData(null, new[] { "pwsh", "WindowsTerminal", "explorer" }, null)]
    [InlineData("", new[] { "bash", "herdr" }, null)]
    public void Cli_detects_an_agent_shell(string? claudeCode, string[] ancestors, string? expected) =>
        Assert.Equal(expected, CallerAgent.Detect(name => name == "CLAUDECODE" ? claudeCode : null, ancestors));
}
