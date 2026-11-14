using System.Diagnostics;
using Shepherd.Broker;
using Shepherd.Protocol;

namespace Shepherd.Cli;

internal sealed record LaunchOptions
{
    public required string Agent { get; init; }   // claude | codex
    public string? Name { get; init; }
    public string? Prompt { get; init; }
    public bool AutoConfirm { get; init; }
    public List<string> Passthrough { get; init; } = [];
}

/// <summary>
/// <c>shepherd launch</c> (ADR-0007): starts Claude Code or Codex wired to the bus. The agent runs
/// in the current terminal; shepherd only prepares arguments and environment and waits.
/// </summary>
internal static class Launch
{
    public const string ChannelDialog = "I am using this for local development";

    /// <summary>Executable, arguments and extra environment for the agent (pure; unit-tested).</summary>
    public static (string File, List<string> Args, Dictionary<string, string> Env) Build(LaunchOptions o, string executable)
    {
        var args = new List<string>();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (o.Name is not null)
        {
            if (!Names.IsValid(o.Name)) throw new ArgumentException($"invalid name '{o.Name}': 2-32 chars, lowercase letters, digits and dashes");
            env["SHEPHERD_NAME"] = o.Name;
        }

        switch (o.Agent)
        {
            case "claude":
                // Claude passes its environment to MCP servers; the shim reads SHEPHERD_*.
                env["SHEPHERD_CHANNEL"] = "1";
                args.Add("--dangerously-load-development-channels");
                args.Add("server:shepherd");
                break;
            case "codex":
                // Codex filters the environment of MCP servers: pass the name through its config,
                // and pre-approve the shepherd tools for this session even without `setup`.
                if (o.Name is not null) args.AddRange(["-c", $"mcp_servers.shepherd.env.SHEPHERD_NAME=\"{o.Name}\""]);
                args.AddRange(["-c", "mcp_servers.shepherd.default_tools_approval_mode=\"approve\""]);
                break;
            default:
                throw new ArgumentException($"unknown agent '{o.Agent}' (claude or codex)");
        }
        args.AddRange(o.Passthrough);
        if (o.Prompt is not null) args.Add(o.Prompt); // first message: creates the Codex thread right away
        return (executable, args, env);
    }

    public static async Task<int> RunAsync(LaunchOptions o)
    {
        var exe = o.Agent switch
        {
            "claude" => Tools.Claude(),
            "codex" => ToolPaths.Codex(),
            _ => null,
        } ?? throw new ArgumentException($"{o.Agent} not found on PATH");

        var (file, args, env) = Build(o, exe);
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;

        using var cts = new CancellationTokenSource();
        if (o.AutoConfirm && o.Agent == "claude") _ = AutoConfirmAsync(cts.Token);

        // The agent owns the terminal; Ctrl+C belongs to it, not to us.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"cannot start {file}");
        await p.WaitForExitAsync();
        await cts.CancelAsync();
        return p.ExitCode;
    }

    /// <summary>
    /// Inside herdr, accepts the development-channels warning once (Enter on its default option).
    /// It never answers the folder-trust question: that is the user's security decision.
    /// </summary>
    private static async Task AutoConfirmAsync(CancellationToken ct)
    {
        var pane = Environment.GetEnvironmentVariable("HERDR_PANE_ID");
        var herdr = ToolPaths.Herdr();
        if (pane is null || herdr is null) return;
        var runner = BrokerRunners.Process();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        try
        {
            // Old screens may still show a previous warning: only a *new* occurrence counts.
            var baseline = Occurrences((await Read(runner, herdr, pane, ct)).Stdout);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                var screen = (await Read(runner, herdr, pane, ct)).Stdout;
                if (Occurrences(screen) <= baseline || !screen.Contains("Enter to confirm", StringComparison.Ordinal)) continue;
                await runner.RunAsync(herdr, ["pane", "send-keys", pane, "enter"], TimeSpan.FromSeconds(5), ct);
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static Task<CommandResult> Read(ICommandRunner runner, string herdr, string pane, CancellationToken ct) =>
        runner.RunAsync(herdr, ["pane", "read", pane, "--lines", "200"], TimeSpan.FromSeconds(5), ct);

    internal static int Occurrences(string screen)
    {
        var n = 0;
        for (var i = screen.IndexOf(ChannelDialog, StringComparison.Ordinal); i >= 0; i = screen.IndexOf(ChannelDialog, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }
}
