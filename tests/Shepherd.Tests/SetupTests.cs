using Shepherd.Broker;
using Shepherd.Cli;

namespace Shepherd.Tests;

public sealed class SetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shepherd-setup-" + Guid.NewGuid().ToString("N")[..10]);
    private string Home => Path.Combine(_root, "home");
    private string Source => Path.Combine(_root, "src");
    private string Bin => Path.Combine(_root, "bin");
    private readonly ScriptedRunner _runner = new();

    private const string ExistingToml = """
        model = "gpt-6"

        [mcp_servers.rea]
        command = "node"
        args = ["rea.mjs", "mcp"]

        [features]
        hooks = true
        """;

    private const string ExistingClaudeMd = "# My rules\r\n\r\n- be nice\r\n";

    public SetupTests()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "shepherd.exe"), "fake exe v1");
        File.WriteAllText(Path.Combine(Source, "e_sqlite3.dll"), "fake sqlite");
        Directory.CreateDirectory(Path.Combine(Home, ".codex"));
        Directory.CreateDirectory(Path.Combine(Home, ".claude"));
        File.WriteAllText(Path.Combine(Home, ".codex", "config.toml"), ExistingToml.Replace("\r\n", "\n"));
        File.WriteAllText(Path.Combine(Home, ".claude", "CLAUDE.md"), ExistingClaudeMd);
        File.WriteAllText(Path.Combine(Home, ".claude.json"), "{}");
        // `codex mcp add` writes its table; `codex mcp remove` deletes it (simulated).
        _runner.OnRun = (file, args) =>
        {
            var toml = Path.Combine(Home, ".codex", "config.toml");
            if (file == "codex" && args is ["mcp", "add", "shepherd", "--", var exe, "mcp"])
                File.AppendAllText(toml, $"\n[mcp_servers.shepherd]\ncommand = '{exe}'\nargs = [\"mcp\"]\n");
            if (file == "codex" && args is ["mcp", "remove", "shepherd"])
            {
                var t = File.ReadAllText(toml);
                var i = t.IndexOf("\n[mcp_servers.shepherd]", StringComparison.Ordinal);
                if (i >= 0) File.WriteAllText(toml, t[..i] + "\n");
            }
        };
    }

    private Setup NewSetup(DateTime? now = null) => new(new SetupContext
    {
        Home = Home, SourceDir = Source, InstallRoot = Bin, Runner = _runner,
        ClaudePath = "claude", CodexPath = "codex", Windows = true, Version = "0.1.0",
        Now = now ?? new DateTime(2026, 11, 14, 10, 0, 0), Out = _ => { },
    });

    [Fact]
    public async Task Apply_installs_registers_and_adds_the_rule()
    {
        var setup = NewSetup();
        await setup.ApplyAsync();

        var dir = setup.InstallDir();
        Assert.StartsWith(Path.Combine(Bin, "0.1.0-"), dir);
        Assert.Equal("fake exe v1", File.ReadAllText(Path.Combine(dir, "shepherd.exe")));
        Assert.True(File.Exists(Path.Combine(dir, "e_sqlite3.dll")));

        var exe = Path.Combine(dir, "shepherd.exe");
        Assert.Contains(("claude", "mcp add -s user shepherd -- " + exe + " mcp"), _runner.Calls);
        Assert.Contains(("codex", "mcp add shepherd -- " + exe + " mcp"), _runner.Calls);

        var toml = File.ReadAllText(Path.Combine(Home, ".codex", "config.toml"));
        Assert.Contains("[mcp_servers.shepherd]\ndefault_tools_approval_mode = \"approve\"\ncommand", toml);
        Assert.Single(toml.Split("default_tools_approval_mode")[1..]); // only in shepherd's table
        Assert.Contains("[mcp_servers.rea]\ncommand = \"node\"", toml);

        var claudeMd = File.ReadAllText(Path.Combine(Home, ".claude", "CLAUDE.md"));
        Assert.StartsWith(ExistingClaudeMd, claudeMd);
        Assert.Contains(Setup.BlockBegin + "\r\n## Talking to other agents (shepherd)\r\n", claudeMd); // keeps CRLF
        Assert.Contains("herdr agent prompt", File.ReadAllText(Path.Combine(Home, ".codex", "AGENTS.md")));

        Assert.True(File.Exists(Path.Combine(Home, ".codex", "config.toml.bak-shepherd-20261114-100000")));
        Assert.True(File.Exists(Path.Combine(Home, ".claude.json.bak-shepherd-20261114-100000")));
        Assert.Equal(ExistingClaudeMd, File.ReadAllText(Path.Combine(Home, ".claude", "CLAUDE.md.bak-shepherd-20261114-100000")));
    }

    [Fact]
    public async Task Apply_is_idempotent()
    {
        await NewSetup().ApplyAsync();
        await NewSetup(new DateTime(2026, 11, 14, 11, 0, 0)).ApplyAsync();
        var claudeMd = File.ReadAllText(Path.Combine(Home, ".claude", "CLAUDE.md"));
        Assert.Single(claudeMd.Split(Setup.BlockBegin)[1..]);
        var toml = File.ReadAllText(Path.Combine(Home, ".codex", "config.toml"));
        Assert.Single(toml.Split("[mcp_servers.shepherd]")[1..]);
    }

    [Fact]
    public async Task A_new_build_installs_side_by_side()
    {
        var first = NewSetup().InstallDir();
        await NewSetup().ApplyAsync();
        File.WriteAllText(Path.Combine(Source, "shepherd.exe"), "fake exe v2");
        var second = NewSetup().InstallDir();
        await NewSetup().ApplyAsync();
        Assert.NotEqual(first, second);
        Assert.Equal("fake exe v1", File.ReadAllText(Path.Combine(first, "shepherd.exe"))); // a running shim keeps working
    }

    [Fact]
    public async Task Undo_restores_the_files()
    {
        await NewSetup().ApplyAsync();
        await NewSetup(new DateTime(2026, 11, 14, 12, 0, 0)).UndoAsync();

        Assert.Equal(ExistingClaudeMd, File.ReadAllText(Path.Combine(Home, ".claude", "CLAUDE.md")));
        Assert.DoesNotContain("shepherd", File.ReadAllText(Path.Combine(Home, ".codex", "config.toml")));
        Assert.Equal("", File.ReadAllText(Path.Combine(Home, ".codex", "AGENTS.md")));
        Assert.Contains(("claude", "mcp remove -s user shepherd"), _runner.Calls);
    }

    [Fact]
    public async Task Missing_agents_are_skipped()
    {
        var setup = new Setup(new SetupContext
        {
            Home = Home, SourceDir = Source, InstallRoot = Bin, Runner = _runner,
            ClaudePath = null, CodexPath = null, Windows = true, Out = _ => { },
        });
        await setup.ApplyAsync();
        Assert.Empty(_runner.Calls);
        Assert.Equal(ExistingClaudeMd, File.ReadAllText(Path.Combine(Home, ".claude", "CLAUDE.md")));
    }

    [Fact]
    public async Task Windows_puts_a_stable_copy_on_the_user_path_and_undo_removes_it()
    {
        var path = new List<string>();
        bool Edit(string dir, bool add)
        {
            if (add == path.Contains(dir)) return false;
            if (add) path.Add(dir); else path.Remove(dir);
            return true;
        }
        Setup Make() => new(new SetupContext
        {
            Home = Home, SourceDir = Source, InstallRoot = Bin, Runner = _runner, ClaudePath = "claude", CodexPath = "codex",
            Windows = true, Out = _ => { }, EditUserPath = Edit,
        });

        await Make().ApplyAsync();
        Assert.Equal("fake exe v1", File.ReadAllText(Path.Combine(Bin, "shepherd.exe")));
        Assert.True(File.Exists(Path.Combine(Bin, "e_sqlite3.dll")));
        Assert.Equal([Bin], path);

        // A running `launch` keeps the stable copy open: it is moved aside, never blocking the update.
        File.WriteAllText(Path.Combine(Source, "shepherd.exe"), "fake exe v2");
        using (new FileStream(Path.Combine(Bin, "shepherd.exe"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            await Make().ApplyAsync();
        Assert.Equal("fake exe v2", File.ReadAllText(Path.Combine(Bin, "shepherd.exe")));
        Assert.Equal([Bin], path);

        await Make().UndoAsync();
        Assert.Empty(path);
    }

    [Fact]
    public async Task Linux_links_shepherd_into_the_user_bin()
    {
        if (OperatingSystem.IsWindows()) return; // symlinks need privileges on Windows; covered when run in WSL
        File.WriteAllText(Path.Combine(Source, "shepherd"), "fake linux exe");
        File.WriteAllText(Path.Combine(Source, "libe_sqlite3.so"), "fake so");
        var userBin = Path.Combine(_root, "local-bin");
        var setup = new Setup(new SetupContext
        {
            Home = Home, SourceDir = Source, InstallRoot = Bin, Runner = _runner, ClaudePath = null, CodexPath = null,
            Windows = false, Out = _ => { }, UserBinDir = userBin,
        });
        await setup.ApplyAsync();
        var link = new FileInfo(Path.Combine(userBin, "shepherd"));
        Assert.Equal(Path.Combine(setup.InstallDir(), "shepherd"), link.LinkTarget);
        await setup.UndoAsync();
        Assert.False(File.Exists(link.FullName));
    }

    [Theory]
    [InlineData("a\n\n<!-- shepherd:begin -->\nx\n<!-- shepherd:end -->\n", "a\n")]
    [InlineData("<!-- shepherd:begin -->\nx\n<!-- shepherd:end -->\n", "")]
    [InlineData("a\n\n<!-- shepherd:begin -->\nx\n<!-- shepherd:end -->\n\nb\n", "a\n\nb\n")]
    [InlineData("no block\n", "no block\n")]
    public void Strip_removes_only_the_block(string input, string expected) => Assert.Equal(expected, Setup.Strip(input));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class ScriptedRunner : ICommandRunner
    {
        public List<(string File, string Args)> Calls { get; } = [];
        public Action<string, string[]>? OnRun { get; set; }

        public Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((file, string.Join(' ', args)));
            OnRun?.Invoke(file, args.ToArray());
            return Task.FromResult(new CommandResult(0, "", ""));
        }
    }
}

public class LaunchTests
{
    [Fact]
    public void Claude_gets_the_channel_flag_and_identity_through_env()
    {
        var (file, args, env) = Launch.Build(new LaunchOptions { Agent = "claude", Name = "lead", Prompt = "hola", Passthrough = ["--model", "opus"] }, "claude.exe");
        Assert.Equal("claude.exe", file);
        Assert.Equal(["--dangerously-load-development-channels", "server:shepherd", "--model", "opus", "hola"], args);
        Assert.Equal("lead", env["SHEPHERD_NAME"]);
        Assert.Equal("1", env["SHEPHERD_CHANNEL"]);
    }

    [Fact]
    public void Codex_gets_name_and_pre_approval_through_config_overrides()
    {
        var (_, args, env) = Launch.Build(new LaunchOptions { Agent = "codex", Name = "dev", Prompt = "Eres dev." }, "codex.exe");
        Assert.Equal(
            ["-c", "mcp_servers.shepherd.env.SHEPHERD_NAME=\"dev\"", "-c", "mcp_servers.shepherd.default_tools_approval_mode=\"approve\"", "Eres dev."],
            args);
        Assert.False(env.ContainsKey("SHEPHERD_CHANNEL"));
    }

    [Fact]
    public void Invalid_names_and_agents_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Launch.Build(new LaunchOptions { Agent = "claude", Name = "Bad Name" }, "x"));
        Assert.Throws<ArgumentException>(() => Launch.Build(new LaunchOptions { Agent = "gemini" }, "x"));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("> 1. I am using this for local development\n", 1)]
    [InlineData("I am using this for local development ... I am using this for local development", 2)]
    public void Channel_dialog_occurrences(string screen, int n) => Assert.Equal(n, Launch.Occurrences(screen));
}
