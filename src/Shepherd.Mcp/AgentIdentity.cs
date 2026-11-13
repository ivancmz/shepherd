using Shepherd.Protocol;

namespace Shepherd.Mcp;

/// <summary>
/// Who launched this shim (ADR-0003). The agent never states its own identity: it is derived
/// from the environment and the process tree, plus the Codex thread id seen in <c>_meta</c>.
/// </summary>
internal sealed record AgentIdentity
{
    public required string Kind { get; init; }
    public required string SessionKey { get; init; }
    public string? RequestedName { get; init; }
    public string? Cwd { get; init; }
    public string? HerdrPaneId { get; init; }
    public int? AgentPid { get; init; }
    public bool Channel { get; init; }
    public string? Distro { get; init; }

    /// <param name="clientName">MCP <c>clientInfo.name</c> from <c>initialize</c> (e.g. <c>claude-code</c>, <c>codex-mcp-client</c>).</param>
    public static AgentIdentity Detect(string? clientName, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var parent = ProcessTree.Parent(Environment.ProcessId);
        var claudeSession = env("CLAUDE_CODE_SESSION_ID");
        var client = clientName?.ToLowerInvariant() ?? "";
        var parentName = parent?.Name.ToLowerInvariant() ?? "";

        string kind;
        string key;
        if (!string.IsNullOrEmpty(claudeSession) || client.Contains("claude", StringComparison.Ordinal))
        {
            kind = AgentKinds.Claude;
            key = !string.IsNullOrEmpty(claudeSession) ? $"claude:{claudeSession}" : $"claude:pid:{parent?.Pid}";
        }
        else if (client.Contains("codex", StringComparison.Ordinal) || parentName.StartsWith("codex", StringComparison.Ordinal))
        {
            // Codex does not pass its environment to MCP servers; the thread id arrives later
            // in tools/call _meta. Until then the codex process is the identity.
            kind = AgentKinds.Codex;
            key = $"codex:pid:{parent?.Pid}";
        }
        else
        {
            kind = AgentKinds.Other;
            key = $"other:pid:{parent?.Pid}";
        }

        var requested = env("SHEPHERD_NAME")?.Trim().ToLowerInvariant();
        return new AgentIdentity
        {
            Kind = kind,
            SessionKey = key,
            RequestedName = Names.IsValid(requested) ? requested : null,
            Cwd = env("CLAUDE_PROJECT_DIR") is { Length: > 0 } pd ? pd : Environment.CurrentDirectory,
            HerdrPaneId = env("HERDR_PANE_ID") is { Length: > 0 } pane ? pane : null,
            AgentPid = parent?.Pid,
            Channel = env("SHEPHERD_CHANNEL") == "1",
            Distro = env("WSL_DISTRO_NAME") is { Length: > 0 } d ? d : Wsl.Distro,
        };
    }
}
