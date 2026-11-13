using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Shepherd.Protocol;
using Shepherd.Protocol.Wire;

namespace Shepherd.Broker;

public sealed record BrokerOptions
{
    public required string PipeName { get; init; }
    public required string DataDir { get; init; }
    public string Node { get; init; } = Names.LocalNode();
    public string Version { get; init; } = typeof(BrokerServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(90);
    public bool KeepAlive { get; init; } = true;
    public Action<string>? Log { get; init; }
}

/// <summary>
/// The per-user broker (ADR-0004): accepts connections on the local pipe, keeps the registry of
/// live agents (ADR-0003), stores and routes messages (ADR-0006).
/// </summary>
public sealed class BrokerServer : IAsyncDisposable
{
    public const int MaxBodyBytes = 64 * 1024;
    public const int MaxSubjectChars = 120;
    public const int MaxRefs = 20;
    public const int MaxRecipients = 20;

    private readonly BrokerOptions _opt;
    private readonly Store _store;
    private readonly AuditLog _audit;
    private readonly ConcurrentDictionary<string, Connection> _live = new();
    private readonly ConcurrentDictionary<Connection, byte> _connections = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _registryLock = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public BrokerServer(BrokerOptions options)
    {
        _opt = options;
        Directory.CreateDirectory(options.DataDir);
        _store = new Store(Path.Combine(options.DataDir, "shepherd.db"));
        _audit = new AuditLog(options.DataDir);
        var purged = _store.Purge(options.Retention);
        if (purged > 0) Log($"purged {purged} old messages");
    }

    public CancellationToken Stopping => _stop.Token;
    public void Stop() => _stop.Cancel();

    /// <summary>
    /// Guards against a second broker for the same pipe. Two pipe servers can share a name, so
    /// the pipe alone is not enough. Returns null if another broker already holds it.
    /// </summary>
    public static Mutex? TryAcquireSingleInstance(string pipeName)
    {
        var mutex = new Mutex(false, $"Local\\shepherd-broker-{Names.Sanitize(pipeName, "default")}");
        try
        {
            if (mutex.WaitOne(0)) return mutex;
        }
        catch (AbandonedMutexException)
        {
            return mutex; // previous broker crashed; we own it now
        }
        mutex.Dispose();
        return null;
    }

    /// <summary>Accept loop. Returns when <see cref="Stop"/> is called or <paramref name="ct"/> fires.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var token = linked.Token;
        Log($"broker {_opt.Version} listening on pipe '{_opt.PipeName}' as node '{_opt.Node}'");
        while (!token.IsCancellationRequested)
        {
            // CurrentUserOnly: the pipe's ACL admits only this user (ADR-0004).
            var server = new NamedPipeServerStream(_opt.PipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (IOException ex)
            {
                Log($"accept failed: {ex.Message}");
                await server.DisposeAsync().ConfigureAwait(false);
                continue;
            }
            Accept(server, "pipe", requiresToken: false);
        }
        foreach (var c in _connections.Keys) c.Peer.Close();
    }

    private void Accept(Stream stream, string transport, bool requiresToken)
    {
        var peer = new RpcPeer(new LineChannel(stream, stream), $"{transport}#{Environment.TickCount64 % 100000}")
        {
            Log = Log,
        };
        var conn = new Connection(peer, requiresToken);
        _connections[conn] = 0;
        peer.OnRequest = (m, p, meta, ct) => HandleAsync(conn, m, p, ct);
        peer.Closed += (_, err) => OnClosed(conn, err);
        if (_opt.KeepAlive) peer.StartKeepAlive(Endpoints.KeepAliveInterval, Endpoints.KeepAliveTimeout);
        _ = Task.Run(async () =>
        {
            await peer.RunAsync().ConfigureAwait(false);
            await peer.DisposeAsync().ConfigureAwait(false);
        });
    }

    private void OnClosed(Connection conn, Exception? error)
    {
        _connections.TryRemove(conn, out _);
        lock (_registryLock)
        {
            if (conn.Address is { } a && _live.TryGetValue(a, out var cur) && ReferenceEquals(cur, conn))
            {
                _live.TryRemove(a, out _);
                _store.Touch(a);
                Log($"{a} disconnected{(error is null ? "" : $" ({error.Message})")}");
            }
        }
    }

    // ------------------------------------------------------------------ dispatch

    private async Task<JsonNode?> HandleAsync(Connection conn, string method, JsonNode? p, CancellationToken ct)
    {
        if (method == Methods.Hello) return Hello(conn, p);
        if (!conn.HelloDone)
        {
            conn.Peer.Close();
            throw new RpcException(RpcErrors.Unauthorized, "hello required");
        }
        return method switch
        {
            Methods.Register => Register(conn, Json.Params(p, ProtocolJson.Default.RegisterParams)),
            Methods.Identify => Identify(conn, Json.Params(p, ProtocolJson.Default.IdentifyParams)),
            Methods.Send => await SendAsync(conn, Json.Params(p, ProtocolJson.Default.SendParams)).ConfigureAwait(false),
            Methods.Reply => await ReplyAsync(conn, Json.Params(p, ProtocolJson.Default.ReplyParams)).ConfigureAwait(false),
            Methods.ReadInbox => ReadInbox(conn, p is null ? new ReadInboxParams() : Json.Params(p, ProtocolJson.Default.ReadInboxParams)),
            Methods.ListAgents => ListAgents(),
            Methods.Status => Status(),
            Methods.Shutdown => Shutdown(conn),
            _ => throw new RpcException(RpcErrors.MethodNotFound, $"method not found: {method}"),
        };
    }

    private JsonNode Hello(Connection conn, JsonNode? p)
    {
        var h = Json.Params(p, ProtocolJson.Default.HelloParams);
        if (h.Protocol != Endpoints.ProtocolVersion)
            throw new RpcException(RpcErrors.ProtocolMismatch, $"protocol {h.Protocol} not supported",
                new JsonObject { ["supported"] = new JsonArray(Endpoints.ProtocolVersion) });
        if (conn.RequiresToken)
            throw new RpcException(RpcErrors.Unauthorized, "token required"); // TCP transport arrives in H3
        if (!Names.IsValid(h.Node))
            throw new RpcException(RpcErrors.InvalidName, $"invalid node name '{h.Node}'");
        conn.HelloDone = true;
        conn.Node = h.Node;
        conn.ClientKind = h.Client;
        return Json.ToNode(new HelloResult(Endpoints.ProtocolVersion, _opt.Node, _opt.Version), ProtocolJson.Default.HelloResult);
    }

    private JsonNode Register(Connection conn, RegisterParams p)
    {
        if (p.Kind is not (AgentKinds.Claude or AgentKinds.Codex or AgentKinds.Human or AgentKinds.Other))
            throw new RpcException(RpcErrors.InvalidParams, $"unknown kind '{p.Kind}'");
        var node = conn.Node!;

        if (p.Kind == AgentKinds.Human)
        {
            // Humans are not exclusive: several consoles may act as the same user at once.
            var hname = p.Name is null ? Endpoints.HumanName() : RequireValidName(p.Name);
            var haddr = Names.Address(hname, node);
            _store.UpsertAgent(new AgentRow { Address = haddr, Name = hname, Node = node, Kind = AgentKinds.Human });
            conn.Address = haddr;
            conn.Kind = AgentKinds.Human;
            return Json.ToNode(new RegisterResult(hname, haddr, false), ProtocolJson.Default.RegisterResult);
        }

        lock (_registryLock)
        {
            string name;
            var automatic = p.Name is null;
            if (automatic)
            {
                var baseName = AutomaticBase(p.Cwd, p.Kind);
                name = baseName;
                for (var i = 2; IsHeldByOther(Names.Address(name, node), p.SessionKey); i++)
                    name = TrimTo(baseName, Names.MaxLength - 1 - i.ToString(System.Globalization.CultureInfo.InvariantCulture).Length) + "-" + i;
            }
            else
            {
                name = RequireValidName(p.Name!);
                if (IsHeldByOther(Names.Address(name, node), p.SessionKey))
                    throw new RpcException(RpcErrors.NameTaken, $"the name '{name}' is in use by another live session");
            }

            var address = Names.Address(name, node);

            // Same technical identity reconnecting: it takes over immediately (ADR-0005 §5).
            if (_live.TryGetValue(address, out var previous) && !ReferenceEquals(previous, conn))
            {
                Log($"{address}: reconnect takes over a previous connection");
                previous.Address = null;
                previous.Peer.Close();
            }
            // A connection re-registering under a new name releases its old one.
            if (conn.Address is { } old && old != address && _live.TryGetValue(old, out var mine) && ReferenceEquals(mine, conn))
                _live.TryRemove(old, out _);

            var existing = _store.GetAgent(address);
            _store.UpsertAgent(new AgentRow
            {
                Address = address, Name = name, Node = node, Kind = p.Kind, SessionKey = p.SessionKey,
                Cwd = p.Cwd, HerdrPaneId = p.HerdrPaneId,
                ThreadId = existing?.SessionKey == p.SessionKey ? existing?.ThreadId : null,
                Automatic = automatic, Channel = p.Channel,
            });
            _live[address] = conn;
            conn.Address = address;
            conn.Kind = p.Kind;
            conn.Channel = p.Channel;
            Log($"{address} registered ({p.Kind}{(p.Channel ? ", channel" : "")}{(automatic ? ", automatic name" : "")})");
            var unread = _store.CountUnread(address);
            var notice = unread > 0 ? $"{unread} unread message(s) waiting" : null;
            return Json.ToNode(new RegisterResult(name, address, automatic, notice), ProtocolJson.Default.RegisterResult);
        }
    }

    private JsonNode Identify(Connection conn, IdentifyParams p)
    {
        var address = RequireRegistered(conn);
        _store.Touch(address, p.ThreadId);
        return new JsonObject();
    }

    private async Task<JsonNode> SendAsync(Connection conn, SendParams p)
    {
        var from = RequireRegistered(conn);
        if (!MessageKinds.IsSendable(p.Kind))
            throw new RpcException(RpcErrors.InvalidParams, $"kind must be '{MessageKinds.Note}' or '{MessageKinds.Request}' (use reply to answer)");
        ValidateContent(p.Body, p.Subject, p.Refs);
        if (p.To is not { Count: > 0 })
            throw new RpcException(RpcErrors.InvalidParams, "at least one recipient is required");
        if (p.To.Count > MaxRecipients)
            throw new RpcException(RpcErrors.InvalidParams, $"at most {MaxRecipients} recipients");

        var recipients = new List<string>();
        foreach (var t in p.To)
        {
            if (!Names.TryParseAddress(t, conn.Node!, out var n, out var nd))
                throw new RpcException(RpcErrors.InvalidName, $"invalid address '{t}'");
            var addr = Names.Address(n, nd);
            if (_store.GetAgent(addr) is null)
                throw new RpcException(RpcErrors.UnknownAgent, $"unknown agent '{addr}' (see list_agents)");
            if (addr == from)
                throw new RpcException(RpcErrors.InvalidParams, "cannot send a message to yourself");
            if (!recipients.Contains(addr)) recipients.Add(addr);
        }

        var id = Guid.CreateVersion7().ToString();
        var msg = new MessageRow
        {
            Id = id, ThreadId = id, From = from, FromHuman = conn.Kind == AgentKinds.Human,
            Kind = p.Kind, Subject = p.Subject, Body = p.Body, Refs = p.Refs, CreatedAt = DateTimeOffset.UtcNow,
        };
        _store.InsertMessage(msg, recipients);
        Audit("send", msg, recipients);
        await DeliverAsync(msg, recipients).ConfigureAwait(false);
        return Json.ToNode(new SendResult(id, id, recipients), ProtocolJson.Default.SendResult);
    }

    private async Task<JsonNode> ReplyAsync(Connection conn, ReplyParams p)
    {
        var from = RequireRegistered(conn);
        ValidateContent(p.Body, null, p.Refs);
        var original = _store.GetMessage(p.MessageId)
            ?? throw new RpcException(RpcErrors.NotFound, $"message '{p.MessageId}' not found");
        if (_store.GetState(original.Id, from) is null)
            throw new RpcException(RpcErrors.NotFound, $"message '{p.MessageId}' was not addressed to {from}");
        if (_store.GetAgent(original.From) is null)
            throw new RpcException(RpcErrors.UnknownAgent, $"unknown agent '{original.From}'");

        var id = Guid.CreateVersion7().ToString();
        var msg = new MessageRow
        {
            Id = id, ThreadId = original.ThreadId, InReplyTo = original.Id, From = from,
            FromHuman = conn.Kind == AgentKinds.Human, Kind = MessageKinds.Reply,
            Subject = original.Subject is null ? null : (original.Subject.StartsWith("Re: ", StringComparison.Ordinal) ? original.Subject : "Re: " + original.Subject),
            Body = p.Body, Refs = p.Refs, CreatedAt = DateTimeOffset.UtcNow,
        };
        var recipients = new List<string> { original.From };
        _store.InsertMessage(msg, recipients);
        _store.Advance(original.Id, from, RecipientStates.Answered);
        Audit("reply", msg, recipients);
        await DeliverAsync(msg, recipients).ConfigureAwait(false);
        return Json.ToNode(new SendResult(id, original.ThreadId, recipients), ProtocolJson.Default.SendResult);
    }

    private JsonNode ReadInbox(Connection conn, ReadInboxParams p)
    {
        var me = RequireRegistered(conn);
        var limit = Math.Clamp(p.Limit, 1, 100);
        var messages = _store.Inbox(me, p.UnreadOnly, limit);
        foreach (var m in messages)
            if (m.State is RecipientStates.Queued or RecipientStates.Notified)
                _store.Advance(m.Id, me, RecipientStates.Read);
        _store.Touch(me);
        var remaining = _store.CountUnread(me);
        return Json.ToNode(new ReadInboxResult(messages, remaining), ProtocolJson.Default.ReadInboxResult);
    }

    private JsonNode ListAgents()
    {
        var agents = _store.ListAgents().Select(a => new AgentInfo
        {
            Name = a.Name, Address = a.Address, Node = a.Node, Kind = a.Kind,
            Online = _live.ContainsKey(a.Address) || (a.Kind == AgentKinds.Human && _connections.Keys.Any(c => c.Address == a.Address)),
            Automatic = a.Automatic, Cwd = a.Cwd, HerdrPaneId = a.HerdrPaneId, Channel = a.Channel,
            Unread = _store.CountUnread(a.Address), WaitingOn = _store.WaitingOn(a.Address), LastSeen = a.LastSeen,
        }).ToList();
        return Json.ToNode(new ListAgentsResult(agents), ProtocolJson.Default.ListAgentsResult);
    }

    private JsonNode Status() => Json.ToNode(new StatusResult
    {
        Node = _opt.Node, BrokerVersion = _opt.Version, DataDir = _opt.DataDir, StartedAt = _startedAt,
        Online = _live.Count, Agents = _store.ListAgents().Count(a => a.Kind != AgentKinds.Human),
        Unread = _store.CountUnread(), PendingRequests = _store.CountPendingRequests(),
    }, ProtocolJson.Default.StatusResult);

    private JsonNode Shutdown(Connection conn)
    {
        if (conn.ClientKind != "cli") throw new RpcException(RpcErrors.Unauthorized, "only the CLI may stop the broker");
        Log("shutdown requested");
        _ = Task.Run(async () => { await Task.Delay(100).ConfigureAwait(false); Stop(); });
        return new JsonObject();
    }

    // ------------------------------------------------------------------ delivery

    /// <summary>
    /// Pushes <c>message.new</c> to live recipients. A recipient becomes <c>notified</c> only when
    /// its client can surface the message to the agent (Claude channel). Wake-ups for agents
    /// without a channel (codex queue, herdr) arrive in H2.
    /// </summary>
    private async Task DeliverAsync(MessageRow msg, List<string> recipients)
    {
        foreach (var r in recipients)
        {
            if (!_live.TryGetValue(r, out var target)) continue;
            try
            {
                await target.Peer.NotifyAsync(Methods.MessageNew,
                    Json.ToNode(msg.ToInbox(RecipientStates.Queued), ProtocolJson.Default.InboxMessage)).ConfigureAwait(false);
                if (target.Channel) _store.Advance(msg.Id, r, RecipientStates.Notified);
            }
            catch (Exception ex)
            {
                Log($"push to {r} failed: {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private bool IsHeldByOther(string address, string? sessionKey) =>
        _live.TryGetValue(address, out var c) && !c.Peer.IsClosed && (sessionKey is null || _store.GetAgent(address)?.SessionKey != sessionKey);

    private static string AutomaticBase(string? cwd, string kind)
    {
        var folder = string.IsNullOrWhiteSpace(cwd) ? "agent" : Path.GetFileName(cwd.TrimEnd('\\', '/'));
        var suffix = "-" + kind;
        return TrimTo(Names.Sanitize(folder), Names.MaxLength - suffix.Length) + suffix;
    }

    private static string TrimTo(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd('-');

    private static string RequireValidName(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        if (!Names.IsValid(n))
            throw new RpcException(RpcErrors.InvalidName, $"invalid name '{name}': 2-32 chars, lowercase letters, digits and dashes");
        return n;
    }

    private static string RequireRegistered(Connection conn) =>
        conn.Address ?? throw new RpcException(RpcErrors.NotRegistered, "register first");

    private static void ValidateContent(string body, string? subject, List<FileRef>? refs)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new RpcException(RpcErrors.InvalidParams, "empty body");
        if (Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
            throw new RpcException(RpcErrors.TooLarge, $"body exceeds {MaxBodyBytes / 1024} KiB; reference a file instead");
        if (subject is { Length: > MaxSubjectChars })
            throw new RpcException(RpcErrors.InvalidParams, $"subject exceeds {MaxSubjectChars} characters");
        if (refs is { Count: > MaxRefs })
            throw new RpcException(RpcErrors.InvalidParams, $"at most {MaxRefs} file references");
        if (refs?.Any(r => string.IsNullOrWhiteSpace(r.Path)) == true)
            throw new RpcException(RpcErrors.InvalidParams, "file reference without path");
    }

    private void Audit(string evt, MessageRow m, List<string> to) => _audit.Write(evt, new JsonObject
    {
        ["id"] = m.Id, ["thread"] = m.ThreadId, ["in_reply_to"] = m.InReplyTo, ["from"] = m.From,
        ["to"] = new JsonArray(to.Select(t => (JsonNode)t).ToArray()), ["kind"] = m.Kind, ["subject"] = m.Subject,
        ["bytes"] = Encoding.UTF8.GetByteCount(m.Body), ["body"] = m.Body,
    });

    private void Log(string s) => _opt.Log?.Invoke(s);

    public async ValueTask DisposeAsync()
    {
        Stop();
        foreach (var c in _connections.Keys) await c.Peer.DisposeAsync().ConfigureAwait(false);
        _store.Dispose();
    }

    private sealed class Connection(RpcPeer peer, bool requiresToken)
    {
        public RpcPeer Peer { get; } = peer;
        public bool RequiresToken { get; } = requiresToken;
        public bool HelloDone { get; set; }
        public string? Node { get; set; }
        public string? ClientKind { get; set; }
        public string? Address { get; set; }
        public string? Kind { get; set; }
        public bool Channel { get; set; }
    }
}
