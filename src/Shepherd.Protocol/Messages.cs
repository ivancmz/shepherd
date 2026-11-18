using System.Text.Json.Serialization;

namespace Shepherd.Protocol;

/// <summary>Broker protocol methods (ADR-0005).</summary>
public static class Methods
{
    public const string Hello = "hello";
    public const string Register = "register";
    public const string Identify = "identify";
    public const string Send = "send";
    public const string Reply = "reply";
    public const string ReadInbox = "read_inbox";
    public const string ListAgents = "list_agents";
    public const string Status = "status";
    public const string Ack = "ack";
    public const string Shutdown = "shutdown";
    public const string Wait = "wait";

    // Broker → client notifications.
    public const string MessageNew = "message.new";
}

public static class AgentKinds
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Human = "human";
    public const string Other = "other";
}

public static class MessageKinds
{
    public const string Note = "note";
    public const string Request = "request";
    public const string Reply = "reply";

    public static bool IsSendable(string kind) => kind is Note or Request;
}

public static class RecipientStates
{
    public const string Queued = "queued";
    public const string Notified = "notified";
    public const string Read = "read";
    public const string Answered = "answered";
}

public sealed record HelloParams(int Protocol, string Node, string Client, string? Token = null);

/// <summary>Where WSL shims reach the Windows broker (ADR-0001); written by the broker next to its data.</summary>
public sealed record BrokerEndpoint(string Address, int Port);

public sealed record HelloResult(int Protocol, string Node, string BrokerVersion);

public sealed record RegisterParams
{
    /// <summary>Requested name; null asks the broker for an automatic one (ADR-0003).</summary>
    public string? Name { get; init; }
    public required string Kind { get; init; }
    /// <summary>Stable technical identity (Claude session id, Codex process, ...). Same key may reclaim its name.</summary>
    public string? SessionKey { get; init; }
    public string? Cwd { get; init; }
    public string? HerdrPaneId { get; init; }
    /// <summary>The client can push messages straight into the agent (Claude channel).</summary>
    public bool Channel { get; init; }
    public int? Pid { get; init; }
    /// <summary>WSL distro name (exact, e.g. <c>Ubuntu-24.04</c>) for WSL agents: wake-ups and path translation.</summary>
    public string? Distro { get; init; }
}

public sealed record RegisterResult(string Name, string Address, bool Automatic, string? Notice = null);

/// <summary>Late identity details, e.g. the Codex thread id seen in the first tools/call.</summary>
public sealed record IdentifyParams(string? ThreadId);

/// <summary>
/// The agent's name after <c>identify</c>: a Codex session that resumes a known thread gets back
/// the name that thread had (ADR-0008). Empty from brokers that predate it.
/// </summary>
public sealed record IdentifyResult(string? Name = null, string? Address = null, bool Automatic = false);

/// <summary>
/// Blocks until the broker has mail to announce to the agent of this session (ADR-0012).
/// Sent by <c>shepherd wait</c>, which Claude Code runs as a background Stop hook.
/// </summary>
public sealed record WaitParams(string SessionKey);

/// <summary><see cref="Notice"/> is the fixed text to show the agent; null means "nothing to announce" (see <see cref="Reason"/>).</summary>
public sealed record WaitResult(string? Notice = null, string? Reason = null);

public sealed record FileRef(string Path, string? Node = null, string? Lines = null);

public sealed record SendParams
{
    public required List<string> To { get; init; }
    public required string Body { get; init; }
    public string Kind { get; init; } = MessageKinds.Note;
    public string? Subject { get; init; }
    public List<FileRef>? Refs { get; init; }
}

public sealed record ReplyParams(string MessageId, string Body, List<FileRef>? Refs = null);

public sealed record SendResult(string MessageId, string ThreadId, List<string> To);

/// <summary>Marks messages read after the client surfaced them to the agent (Claude channel).</summary>
public sealed record AckParams(List<string> MessageIds);

public sealed record ReadInboxParams(bool UnreadOnly = true, int Limit = 20);

public sealed record InboxMessage
{
    public required string Id { get; init; }
    public required string ThreadId { get; init; }
    public string? InReplyTo { get; init; }
    public required string From { get; init; }
    public bool FromHuman { get; init; }
    public required string Kind { get; init; }
    public string? Subject { get; init; }
    public required string Body { get; init; }
    public List<FileRef>? Refs { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string State { get; init; } = RecipientStates.Queued;
}

public sealed record ReadInboxResult(List<InboxMessage> Messages, int Remaining);

public sealed record AgentInfo
{
    public required string Name { get; init; }
    public required string Address { get; init; }
    public required string Node { get; init; }
    public required string Kind { get; init; }
    public bool Online { get; init; }
    public bool Automatic { get; init; }
    public string? Cwd { get; init; }
    public string? HerdrPaneId { get; init; }
    public bool Channel { get; init; }
    /// <summary>A new message can reach the agent without it polling (channel, codex queue, herdr).</summary>
    public bool Wakeable { get; init; }
    public int Unread { get; init; }
    /// <summary>Addresses this agent sent requests to that are still unanswered.</summary>
    public List<string> WaitingOn { get; init; } = [];
    public DateTimeOffset LastSeen { get; init; }
}

public sealed record ListAgentsResult(List<AgentInfo> Agents);

public sealed record StatusResult
{
    public required string Node { get; init; }
    public required string BrokerVersion { get; init; }
    public required string DataDir { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public int Online { get; init; }
    public int Agents { get; init; }
    public int Unread { get; init; }
    public int PendingRequests { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HelloParams))]
[JsonSerializable(typeof(HelloResult))]
[JsonSerializable(typeof(RegisterParams))]
[JsonSerializable(typeof(RegisterResult))]
[JsonSerializable(typeof(IdentifyParams))]
[JsonSerializable(typeof(IdentifyResult))]
[JsonSerializable(typeof(WaitParams))]
[JsonSerializable(typeof(WaitResult))]
[JsonSerializable(typeof(SendParams))]
[JsonSerializable(typeof(ReplyParams))]
[JsonSerializable(typeof(SendResult))]
[JsonSerializable(typeof(ReadInboxParams))]
[JsonSerializable(typeof(AckParams))]
[JsonSerializable(typeof(ReadInboxResult))]
[JsonSerializable(typeof(InboxMessage))]
[JsonSerializable(typeof(ListAgentsResult))]
[JsonSerializable(typeof(StatusResult))]
[JsonSerializable(typeof(List<FileRef>))]
[JsonSerializable(typeof(BrokerEndpoint))]
public sealed partial class ProtocolJson : JsonSerializerContext;
