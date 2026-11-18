namespace Shepherd.Mcp;

/// <summary>
/// Is this process running inside a coding agent's shell? (ADR-0010). The CLI uses it to refuse
/// to act as the user there. Best effort: agents run as the same OS user, so this stops the
/// likely mistake (an agent using the CLI instead of its MCP tools), not a determined process.
/// </summary>
public static class CallerAgent
{
    private const int MaxDepth = 16;

    /// <summary>The agent kind (<c>claude</c>, <c>codex</c>) this process runs under, or null.</summary>
    public static string? Detect() => Detect(Environment.GetEnvironmentVariable, Ancestors(Environment.ProcessId));

    internal static string? Detect(Func<string, string?> env, IEnumerable<string> ancestorNames)
    {
        // Claude Code sets CLAUDECODE in every command it runs.
        if (!string.IsNullOrEmpty(env("CLAUDECODE"))) return "claude";
        foreach (var name in ancestorNames)
        {
            if (name.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) return "claude";
            if (name.StartsWith("codex", StringComparison.OrdinalIgnoreCase)) return "codex";
        }
        return null;
    }

    private static IEnumerable<string> Ancestors(int pid)
    {
        var seen = new HashSet<int> { pid };
        for (var i = 0; i < MaxDepth; i++)
        {
            if (ProcessTree.Parent(pid) is not { } parent || !seen.Add(parent.Pid)) yield break;
            yield return parent.Name;
            pid = parent.Pid;
        }
    }
}
