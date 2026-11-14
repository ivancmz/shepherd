using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Shepherd.Broker;

namespace Shepherd.Cli;

/// <summary>Where <see cref="Setup"/> reads and writes; injectable for tests.</summary>
internal sealed record SetupContext
{
    public required string Home { get; init; }
    /// <summary>Directory holding the running shepherd binaries (the source of the install).</summary>
    public required string SourceDir { get; init; }
    /// <summary>Root of versioned installs, e.g. %LOCALAPPDATA%\shepherd\bin.</summary>
    public required string InstallRoot { get; init; }
    public required ICommandRunner Runner { get; init; }
    public string? ClaudePath { get; init; }
    public string? CodexPath { get; init; }
    public bool Windows { get; init; } = OperatingSystem.IsWindows();
    public string Version { get; init; } = typeof(Setup).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public DateTime Now { get; init; } = DateTime.Now;
    public Action<string> Out { get; init; } = Console.WriteLine;

    public static SetupContext Default() => new()
    {
        Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        SourceDir = Path.GetDirectoryName(Environment.ProcessPath)!,
        InstallRoot = Path.Combine(Protocol.Endpoints.DataDir(), "bin"),
        Runner = new CliProcessRunner(),
        ClaudePath = Tools.Claude(),
        CodexPath = ToolPaths.Codex(),
    };
}

/// <summary>
/// <c>shepherd setup</c> (ADR-0007): installs a versioned copy of the binaries, registers the MCP
/// server with Claude Code and Codex through their own CLIs, pre-approves the shepherd tools in
/// Codex and adds a short coexistence rule to the global instructions. Every file it edits is
/// backed up first; <c>--undo</c> reverses each step.
/// </summary>
internal sealed class Setup(SetupContext ctx)
{
    public const string BlockBegin = "<!-- shepherd:begin -->";
    public const string BlockEnd = "<!-- shepherd:end -->";

    public const string Rule = """
        ## Talking to other agents (shepherd)

        To talk to other coding agents (Claude Code, Codex) use the shepherd MCP tools (`send`, `reply`,
        `read_inbox`, `list_agents`) instead of `herdr agent prompt`, terminal keystrokes or the clipboard.
        Use `herdr agent prompt` only if shepherd is unavailable.
        """;

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private string ClaudeMd => Path.Combine(ctx.Home, ".claude", "CLAUDE.md");
    private string AgentsMd => Path.Combine(ctx.Home, ".codex", "AGENTS.md");
    private string CodexToml => Path.Combine(ctx.Home, ".codex", "config.toml");
    private string ClaudeJson => Path.Combine(ctx.Home, ".claude.json");
    private string ExeName => ctx.Windows ? "shepherd.exe" : "shepherd";
    private string[] NativeLibs => ctx.Windows ? ["e_sqlite3.dll"] : ["libe_sqlite3.so"];

    /// <summary>Human-readable list of what <see cref="ApplyAsync"/> would do.</summary>
    public List<string> Plan()
    {
        var target = InstallDir();
        var plan = new List<string> { $"copy {ExeName} (+ sqlite) to {target}" };
        plan.Add(ctx.ClaudePath is null
            ? "Claude Code: not found, skipped"
            : $"Claude Code: claude mcp add -s user shepherd -- \"{Path.Combine(target, ExeName)}\" mcp  (backup of {ClaudeJson})");
        plan.Add(ctx.CodexPath is null
            ? "Codex: not found, skipped"
            : $"Codex: codex mcp add shepherd -- \"{Path.Combine(target, ExeName)}\" mcp, then default_tools_approval_mode = \"approve\" for shepherd only  (backup of {CodexToml})");
        plan.Add($"rule for other agents in {ClaudeMd}{(ctx.CodexPath is null ? "" : $" and {AgentsMd}")} between {BlockBegin} / {BlockEnd}");
        return plan;
    }

    public async Task<int> ApplyAsync(CancellationToken ct = default)
    {
        var target = InstallDir();
        var exe = Path.Combine(target, ExeName);
        Install(target);
        ctx.Out($"installed {exe}");

        if (ctx.ClaudePath is not null)
        {
            Backup(ClaudeJson);
            await RunAsync(ctx.ClaudePath, ["mcp", "remove", "-s", "user", "shepherd"], ignoreFailure: true, ct);
            await RunAsync(ctx.ClaudePath, ["mcp", "add", "-s", "user", "shepherd", "--", exe, "mcp"], ignoreFailure: false, ct);
            ctx.Out("Claude Code: shepherd registered (user scope)");
            AddBlock(ClaudeMd);
        }

        if (ctx.CodexPath is not null)
        {
            Backup(CodexToml);
            await RunAsync(ctx.CodexPath, ["mcp", "remove", "shepherd"], ignoreFailure: true, ct);
            await RunAsync(ctx.CodexPath, ["mcp", "add", "shepherd", "--", exe, "mcp"], ignoreFailure: false, ct);
            SetCodexApproval();
            ctx.Out("Codex: shepherd registered; its tools are pre-approved");
            AddBlock(AgentsMd);
        }
        ctx.Out("Done. Start agents with `shepherd launch claude|codex --name <name>` to receive messages automatically.");
        return 0;
    }

    public async Task<int> UndoAsync(CancellationToken ct = default)
    {
        if (ctx.ClaudePath is not null)
        {
            Backup(ClaudeJson);
            await RunAsync(ctx.ClaudePath, ["mcp", "remove", "-s", "user", "shepherd"], ignoreFailure: true, ct);
            RemoveBlock(ClaudeMd);
        }
        if (ctx.CodexPath is not null)
        {
            Backup(CodexToml);
            await RunAsync(ctx.CodexPath, ["mcp", "remove", "shepherd"], ignoreFailure: true, ct);
            RemoveBlock(AgentsMd);
        }
        ctx.Out($"shepherd unregistered. Installed binaries stay in {ctx.InstallRoot} (delete them when no agent uses them).");
        return 0;
    }

    /// <summary>bin/&lt;version&gt;-&lt;hash&gt;: a new build never overwrites a binary a running shim uses.</summary>
    public string InstallDir()
    {
        using var sha = SHA256.Create();
        using var f = File.OpenRead(Path.Combine(ctx.SourceDir, ExeName));
        var hash = Convert.ToHexStringLower(sha.ComputeHash(f))[..8];
        return Path.Combine(ctx.InstallRoot, $"{ctx.Version}-{hash}");
    }

    private void Install(string target)
    {
        Directory.CreateDirectory(target);
        foreach (var name in NativeLibs.Prepend(ExeName))
        {
            var src = Path.Combine(ctx.SourceDir, name);
            var dst = Path.Combine(target, name);
            if (!File.Exists(src)) throw new FileNotFoundException($"missing {src}");
            if (Path.GetFullPath(src) == Path.GetFullPath(dst)) continue;
            if (File.Exists(dst) && new FileInfo(dst).Length == new FileInfo(src).Length) continue; // same build, maybe in use
            File.Copy(src, dst, overwrite: true);
        }
        if (!ctx.Windows && !OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(target, ExeName),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private async Task RunAsync(string file, string[] args, bool ignoreFailure, CancellationToken ct)
    {
        var r = await ctx.Runner.RunAsync(file, args, CommandTimeout, ct);
        if (!r.Ok && !ignoreFailure)
            throw new InvalidOperationException($"{Path.GetFileName(file)} {string.Join(' ', args)} failed: {(r.Stderr + r.Stdout).Trim()}");
    }

    /// <summary>Copies <paramref name="path"/> to <c>path.bak-shepherd-yyyyMMdd-HHmmss</c> (if it exists).</summary>
    private void Backup(string path)
    {
        if (!File.Exists(path)) return;
        var bak = $"{path}.bak-shepherd-{ctx.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        if (!File.Exists(bak)) File.Copy(path, bak);
    }

    /// <summary>Adds <c>default_tools_approval_mode = "approve"</c> to <c>[mcp_servers.shepherd]</c> only.</summary>
    private void SetCodexApproval()
    {
        if (!File.Exists(CodexToml)) return;
        var lines = File.ReadAllLines(CodexToml).ToList();
        var start = lines.FindIndex(l => l.Trim() == "[mcp_servers.shepherd]");
        if (start < 0) throw new InvalidOperationException("codex mcp add did not create [mcp_servers.shepherd]");
        var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;
        var existing = lines.FindIndex(start + 1, end - start - 1, l => l.TrimStart().StartsWith("default_tools_approval_mode", StringComparison.Ordinal));
        const string line = "default_tools_approval_mode = \"approve\"";
        if (existing >= 0) lines[existing] = line;
        else lines.Insert(start + 1, line);
        WriteKeepingNewlines(CodexToml, lines);
    }

    private void AddBlock(string path)
    {
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        if (text.Contains(BlockBegin, StringComparison.Ordinal)) text = Strip(text);
        Backup(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var block = $"{BlockBegin}{nl}{Rule.Replace("\n", nl, StringComparison.Ordinal)}{nl}{BlockEnd}{nl}";
        var sep = text.Length == 0 ? "" : text.EndsWith(nl + nl, StringComparison.Ordinal) ? "" : text.EndsWith(nl, StringComparison.Ordinal) ? nl : nl + nl;
        File.WriteAllText(path, text + sep + block, new UTF8Encoding(false));
    }

    private void RemoveBlock(string path)
    {
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        if (!text.Contains(BlockBegin, StringComparison.Ordinal)) return;
        Backup(path);
        File.WriteAllText(path, Strip(text), new UTF8Encoding(false));
    }

    /// <summary>Removes the marked block and the blank line that introduced it.</summary>
    internal static string Strip(string text)
    {
        var b = text.IndexOf(BlockBegin, StringComparison.Ordinal);
        var e = text.IndexOf(BlockEnd, StringComparison.Ordinal);
        if (b < 0 || e < b) return text;
        var after = e + BlockEnd.Length;
        while (after < text.Length && text[after] is '\r' or '\n') after++;
        var before = b;
        while (before > 0 && text[before - 1] is '\r' or '\n') before--;
        var head = text[..before];
        var tail = text[after..];
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (head.Length == 0) return tail;
        return tail.Length == 0 ? head + nl : head + nl + nl + tail;
    }

    private static void WriteKeepingNewlines(string path, List<string> lines)
    {
        var original = File.ReadAllText(path);
        var nl = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        File.WriteAllText(path, string.Join(nl, lines) + nl, new UTF8Encoding(false));
    }
}

/// <summary>Locates agent CLIs for setup and launch.</summary>
internal static class Tools
{
    public static string? Claude()
    {
        var env = Environment.GetEnvironmentVariable("SHEPHERD_CLAUDE");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe" } : ["claude"];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in names)
            {
                var p = Path.Combine(dir.Trim('"'), n);
                if (File.Exists(p)) return p;
            }
        // WSL: the native installer puts claude in ~/.local/bin, which only login shells add to PATH.
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude");
        return !OperatingSystem.IsWindows() && File.Exists(local) ? local : null;
    }
}

/// <summary>Command runner for the CLI (same contract as the broker's).</summary>
internal sealed class CliProcessRunner : ICommandRunner
{
    private readonly ICommandRunner _inner = BrokerRunners.Process();

    public Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
        _inner.RunAsync(file, args, timeout, ct);
}
