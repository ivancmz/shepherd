using System.Text.Json;
using System.Text.Json.Nodes;
using Shepherd.Protocol;
using Shepherd.Protocol.Client;
using Shepherd.Protocol.Wire;

namespace Shepherd.Cli;

/// <summary>
/// <c>shepherd wait</c> (ADR-0012): run by Claude Code as a Stop hook with <c>asyncRewake</c>.
/// It parks on the broker while the session is idle and exits with code 2 and a fixed notice on
/// stderr when there is mail to announce, which wakes Claude. Every other outcome is a silent
/// exit 0: a hook must never disturb the session with its own failures.
/// </summary>
internal static class Wait
{
    public const int WakeExitCode = 2;

    /// <summary>Claude shows the hook's stderr under "Stop hook blocking error": say what it really is.</summary>
    public const string Suffix = " (This is a mail notice from the shepherd Stop hook, not an error.)";

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private const int MaxReconnects = 60; // about five minutes of broker downtime

    /// <param name="hookInput">The hook's JSON on stdin (has <c>session_id</c>), or null when run by hand.</param>
    /// <param name="connect">How to reach the broker; null when none is running. Defaults to the real one.</param>
    public static async Task<int> RunAsync(TextReader? hookInput, Func<string, string?> env, TextWriter stderr,
        Func<CancellationToken, Task<BrokerClient?>>? connect = null, TimeSpan? retryDelay = null, CancellationToken ct = default)
    {
        try
        {
            var session = SessionId(hookInput is null ? null : await hookInput.ReadToEndAsync(ct).ConfigureAwait(false))
                          ?? env("CLAUDE_CODE_SESSION_ID");
            if (string.IsNullOrWhiteSpace(session)) return 0;
            // The same key the MCP shim of that session registered with (AgentIdentity).
            var key = $"{AgentKinds.Claude}:{session}";
            connect ??= ConnectAsync;

            for (var attempt = 0; attempt <= MaxReconnects; attempt++)
            {
                if (attempt > 0) await Task.Delay(retryDelay ?? RetryDelay, ct).ConfigureAwait(false);
                try
                {
                    await using var client = await connect(ct).ConfigureAwait(false);
                    if (client is null) continue; // broker not running (yet)
                    client.Peer.StartKeepAlive(Endpoints.KeepAliveInterval, Endpoints.KeepAliveTimeout);
                    var r = await client.WaitAsync(new WaitParams(key), ct).ConfigureAwait(false);
                    if (r.Notice is null) return 0; // unknown session, or a newer wait replaced this one
                    await stderr.WriteLineAsync(r.Notice + Suffix).ConfigureAwait(false);
                    return WakeExitCode;
                }
                catch (RpcException ex) when (ex.Code == RpcErrors.BrokerUnavailable)
                {
                    // Broker restarting or connection lost: park again.
                }
            }
            return 0;
        }
        catch (Exception ex) when (ex is RpcException or IOException or OperationCanceledException or JsonException or UnauthorizedAccessException)
        {
            return 0; // e.g. a broker that predates `wait`
        }
    }

    private static string? SessionId(string? hookJson)
    {
        if (string.IsNullOrWhiteSpace(hookJson)) return null;
        try
        {
            return JsonNode.Parse(hookJson) is JsonObject o && o["session_id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Never starts a broker: a hook that outlives its session must not bring one up.</summary>
    private static async Task<BrokerClient?> ConnectAsync(CancellationToken ct) => Endpoints.IsWsl
        ? await BrokerClient.ConnectAsync("cli", ct).ConfigureAwait(false)
        : await BrokerClient.TryConnectPipeAsync(Endpoints.PipeName(), "cli", TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
}
