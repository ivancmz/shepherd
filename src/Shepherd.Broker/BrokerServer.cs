using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    /// <summary>Read messages and whole audit months older than this are deleted (ADR-0011).</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(90);
    /// <summary>Automatic-name agents disconnected for this long, without unread mail, are forgotten (ADR-0008).</summary>
    public TimeSpan AgentRetention { get; init; } = TimeSpan.FromDays(14);
    /// <summary>How often the retention rules run, besides at start-up.</summary>
    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromDays(1);
    /// <summary>How often live recipients with unread mail are checked for retries and reminders (ADR-0009).</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(1);
    public WakeTimings Wake { get; init; } = new();
    public bool KeepAlive { get; init; } = true;
    public Action<string>? Log { get; init; }

    /// <summary>Runs codex/herdr for wake-ups (ADR-0002). Tests inject a fake.</summary>
    public ICommandRunner? Runner { get; init; }
    /// <summary>Path of the codex binary; defaults to <see cref="ToolPaths.Codex"/>. Empty disables Codex wake-ups.</summary>
    public string? CodexPath { get; init; }
    /// <summary>Path of the herdr binary; defaults to <see cref="ToolPaths.Herdr"/>. Empty disables herdr wake-ups.</summary>
    public string? HerdrPath { get; init; }
    /// <summary>Path of wsl.exe for WSL wake-ups; defaults to System32. Empty disables them.</summary>
    public string? WslPath { get; init; }

    /// <summary>
    /// TCP listener for WSL nodes (ADR-0001): <c>auto</c> (the "vEthernet (WSL)" interface only),
    /// <c>off</c>, or <c>address:port</c>. Defaults to <c>SHEPHERD_TCP</c>, else <c>auto</c>.
    /// </summary>
    public string? TcpBind { get; init; }

    /// <summary>How often <c>auto</c> re-checks the WSL interface (it may appear after logon or change).</summary>
    public TimeSpan TcpWatchInterval { get; init; } = TimeSpan.FromSeconds(30);
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
    private readonly Wakeups _wakeups;
    private readonly string _token;
    private TcpListener? _tcp;

    /// <summary>The bound TCP endpoint for WSL nodes, if any.</summary>
    public IPEndPoint? TcpEndpoint { get; private set; }
    /// <summary>Shared secret WSL shims present in <c>hello</c> (file <c>node-token</c> in the data dir).</summary>
    internal string Token => _token;

    public BrokerServer(BrokerOptions options)
    {
        _opt = options;
        Directory.CreateDirectory(options.DataDir);
        _store = new Store(Path.Combine(options.DataDir, "shepherd.db"));
        _audit = new AuditLog(options.DataDir);
        Maintain();
        var codex = options.CodexPath ?? ToolPaths.Codex();
        var herdr = options.HerdrPath ?? ToolPaths.Herdr();
        Log($"wake-ups: codex={(string.IsNullOrEmpty(codex) ? "unavailable" : codex)}, herdr={(string.IsNullOrEmpty(herdr) ? "unavailable" : herdr)}");
        var wsl = options.WslPath ?? (OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "wsl.exe") : "");
        _wakeups = new Wakeups(_store, options.Runner ?? new ProcessRunner(), a => _live.ContainsKey(a), Log, options.Node,
            string.IsNullOrEmpty(codex) ? null : codex, string.IsNullOrEmpty(herdr) ? null : herdr,
            string.IsNullOrEmpty(wsl) ? null : wsl, options.Wake, _stop.Token);
        _token = LoadOrCreateToken(options.DataDir);
    }

    /// <summary>
    /// The node token lives in the user's data dir, whose ACL is inherited from the user profile
    /// (%LOCALAPPDATA%: the user, SYSTEM and Administrators). WSL reads it through /mnt/c as the
    /// same user: the same trust boundary (ADR-0001).
    /// </summary>
    private static string LoadOrCreateToken(string dataDir)
    {
        var path = Path.Combine(dataDir, Endpoints.TokenFileName);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32) return existing;
        }
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(path, token);
        return token;
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
        StartTcp(token);
        _ = Task.Run(() => SweepLoopAsync(token), token);
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
        _tcp?.Stop();
        foreach (var c in _connections.Keys) c.Peer.Close();
    }

    // ------------------------------------------------------------------ sweep and retention

    /// <summary>
    /// Every <see cref="BrokerOptions.SweepInterval"/>: retry failed wake-ups and remind live
    /// recipients of unread mail (ADR-0009); once per <see cref="BrokerOptions.MaintenanceInterval"/>,
    /// apply the retention rules (ADR-0011).
    /// </summary>
    private async Task SweepLoopAsync(CancellationToken ct)
    {
        var lastMaintenance = DateTimeOffset.UtcNow;
        while (true)
        {
            try { await Task.Delay(_opt.SweepInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                foreach (var address in _live.Keys) _wakeups.Sweep(address);
                _wakeups.ReleaseOrphans();
                if (DateTimeOffset.UtcNow - lastMaintenance >= _opt.MaintenanceInterval)
                {
                    lastMaintenance = DateTimeOffset.UtcNow;
                    Maintain();
                }
            }
            catch (ObjectDisposedException)
            {
                return; // broker shutting down
            }
            catch (Exception ex)
            {
                Log($"sweep failed: {ex.Message}");
            }
        }
    }

    /// <summary>Retention: old read messages, old audit months (ADR-0011) and stale automatic agents (ADR-0008).</summary>
    private void Maintain()
    {
        var purged = _store.Purge(_opt.Retention);
        if (purged > 0) Log($"purged {purged} old messages");
        var months = _audit.Purge(_opt.Retention);
        if (months > 0) Log($"deleted {months} old audit file(s)");
        lock (_registryLock)
        {
            var stale = _store.StaleAutomaticAgents(_opt.AgentRetention).Where(a => !_live.ContainsKey(a)).ToList();
            foreach (var a in stale) _store.DeleteAgent(a);
            if (stale.Count > 0) Log($"forgot {stale.Count} stale automatic agent(s): {string.Join(", ", stale)}");
        }
    }

    // ------------------------------------------------------------------ TCP (WSL nodes)

    private void StartTcp(CancellationToken ct)
    {
        var bind = _opt.TcpBind ?? Environment.GetEnvironmentVariable("SHEPHERD_TCP") ?? "auto";
        if (bind.Equals("off", StringComparison.OrdinalIgnoreCase)) return;

        if (!bind.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (IPEndPoint.TryParse(bind, out var fixedEp)) Listen(fixedEp, ct);
            else Log($"invalid SHEPHERD_TCP '{bind}'; TCP disabled");
            return;
        }

        // auto: at logon WSL is usually not running yet, and a WSL restart can change the
        // address of 'vEthernet (WSL)'. Watch it and (re)listen whenever it appears or changes.
        _ = Task.Run(async () =>
        {
            var warned = false;
            while (!ct.IsCancellationRequested)
            {
                var ip = WslInterfaceAddress();
                var current = TcpEndpoint?.Address;
                if (ip is null)
                {
                    if (!warned) Log("waiting for the 'vEthernet (WSL)' interface to listen for WSL nodes (WSL not started yet; with networkingMode=mirrored set SHEPHERD_TCP=127.0.0.1:47321)");
                    warned = true;
                }
                else if (!ip.Equals(current))
                {
                    if (current is not null) Log($"WSL interface changed {current} -> {ip}; re-listening");
                    Listen(new IPEndPoint(ip, Endpoints.DefaultTcpPort), ct);
                    warned = false;
                }
                try { await Task.Delay(_opt.TcpWatchInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }, ct);
    }

    /// <summary>Starts (or replaces) the TCP listener and publishes its endpoint for WSL shims.</summary>
    private void Listen(IPEndPoint ep, CancellationToken ct)
    {
        _tcp?.Stop();
        TcpListener listener;
        try
        {
            listener = new TcpListener(ep);
            listener.Start();
        }
        catch (SocketException ex)
        {
            Log($"cannot listen on {ep}: {ex.Message}");
            _tcp = null;
            TcpEndpoint = null;
            return;
        }
        _tcp = listener;
        TcpEndpoint = (IPEndPoint)listener.LocalEndpoint;
        var file = Path.Combine(_opt.DataDir, Endpoints.EndpointFileName);
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(
            new BrokerEndpoint(TcpEndpoint.Address.ToString(), TcpEndpoint.Port), ProtocolJson.Default.BrokerEndpoint));
        Log($"listening for WSL nodes on tcp {TcpEndpoint} (token required)");

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return; // stopped or replaced
                }
                client.NoDelay = true;
                Accept(client.GetStream(), "tcp", requiresToken: true);
            }
        }, ct);
    }

    /// <summary>IPv4 of the Hyper-V switch WSL uses in NAT mode (the WSL default gateway).</summary>
    private static IPAddress? WslInterfaceAddress()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || !nic.Name.Contains("WSL", StringComparison.OrdinalIgnoreCase)) continue;
            var v4 = nic.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (v4 is not null) return v4.Address;
        }
        return null;
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
                try { _store.Touch(a); }
                catch (ObjectDisposedException) { /* broker shutting down */ }
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
            Methods.Ack => Ack(conn, Json.Params(p, ProtocolJson.Default.AckParams)),
            Methods.ListAgents => ListAgents(),
            Methods.Status => Status(),
            Methods.Shutdown => Shutdown(conn),
            Methods.Wait => await WaitAsync(conn, Json.Params(p, ProtocolJson.Default.WaitParams), ct).ConfigureAwait(false),
            _ => throw new RpcException(RpcErrors.MethodNotFound, $"method not found: {method}"),
        };
    }

    private JsonNode Hello(Connection conn, JsonNode? p)
    {
        var h = Json.Params(p, ProtocolJson.Default.HelloParams);
        if (h.Protocol != Endpoints.ProtocolVersion)
            throw new RpcException(RpcErrors.ProtocolMismatch, $"protocol {h.Protocol} not supported",
                new JsonObject { ["supported"] = new JsonArray(Endpoints.ProtocolVersion) });
        if (conn.RequiresToken && !TokenMatches(h.Token))
        {
            Log("tcp hello rejected: bad or missing token");
            _ = Task.Run(async () => { await Task.Delay(100).ConfigureAwait(false); conn.Peer.Close(); });
            throw new RpcException(RpcErrors.Unauthorized, "invalid node token");
        }
        if (!Names.IsValid(h.Node))
            throw new RpcException(RpcErrors.InvalidName, $"invalid node name '{h.Node}'");
        conn.HelloDone = true;
        conn.Node = h.Node;
        conn.ClientKind = h.Client;
        return Json.ToNode(new HelloResult(Endpoints.ProtocolVersion, _opt.Node, _opt.Version), ProtocolJson.Default.HelloResult);
    }

    private bool TokenMatches(string? presented) =>
        presented is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(_token));

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
            bool automatic;
            if (p.Name is null)
            {
                // ADR-0008: a session that asks for no name gets back the one it had, automatic
                // or chosen. Otherwise a new automatic name, never one another session has had.
                if (p.SessionKey is not null && _store.FindBySession(node, p.SessionKey) is { } mine)
                {
                    name = mine.Name;
                    automatic = mine.Automatic;
                }
                // ADR-0013: a new session in a herdr pane takes over the role of the pane's
                // previous occupant (same folder), once that one is gone.
                else if (p.HerdrPaneId is not null
                         && _store.PaneOccupants(node, p.Kind, p.HerdrPaneId, p.Cwd).FirstOrDefault(a => !_live.ContainsKey(a.Address)) is { } occupant)
                {
                    name = occupant.Name;
                    automatic = occupant.Automatic;
                    Log($"{occupant.Address}: new session in herdr pane {p.HerdrPaneId} takes over the name");
                }
                else
                {
                    automatic = true;
                    var baseName = AutomaticBase(p.Cwd, p.Kind);
                    name = baseName;
                    for (var i = 2; _store.GetAgent(Names.Address(name, node)) is not null; i++)
                        name = TrimTo(baseName, Names.MaxLength - 1 - i.ToString(System.Globalization.CultureInfo.InvariantCulture).Length) + "-" + i;
                }
            }
            else
            {
                automatic = false;
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
            if (conn.Address is { } old && old != address && _live.TryGetValue(old, out var held) && ReferenceEquals(held, conn))
            {
                _live.TryRemove(old, out _);
                // The old row stops belonging to this session, or a later registration without a
                // name would get it back. An automatic name that never carried mail just goes away.
                if (_store.GetAgent(old) is { Automatic: true } && !_store.HasMail(old)) _store.DeleteAgent(old);
                else _store.ReleaseSession(old);
            }

            var existing = _store.GetAgent(address);
            _store.UpsertAgent(new AgentRow
            {
                Address = address, Name = name, Node = node, Kind = p.Kind, SessionKey = p.SessionKey,
                Cwd = p.Cwd, HerdrPaneId = p.HerdrPaneId, Distro = p.Distro,
                // A thread id only survives while the row stays with the same session (ADR-0008 §6).
                ThreadId = p.SessionKey is not null && existing?.SessionKey == p.SessionKey ? existing.ThreadId : null,
                Automatic = automatic, Channel = p.Channel,
            });
            _live[address] = conn;
            conn.Address = address;
            conn.Kind = p.Kind;
            conn.Channel = p.Channel;
            Log($"{address} registered ({p.Kind}{(p.Channel ? ", channel" : "")}{(automatic ? ", automatic name" : "")})");
            var unread = _store.CountUnread(address);
            var notice = unread > 0 ? $"{unread} unread message(s) waiting" : null;
            if (unread > 0) _ = Task.Run(() => DeliverPendingAsync(address));
            return Json.ToNode(new RegisterResult(name, address, automatic, notice), ProtocolJson.Default.RegisterResult);
        }
    }

    private JsonNode Identify(Connection conn, IdentifyParams p)
    {
        var address = RequireRegistered(conn);
        if (p.ThreadId is not { } thread)
        {
            _store.Touch(address);
            return new JsonObject();
        }

        bool changed;
        AgentRow? row;
        lock (_registryLock)
        {
            var me = _store.GetAgent(address);
            var others = _store.AgentsByThread(thread).Where(a => a.Address != address && !_live.ContainsKey(a.Address)).ToList();
            // ADR-0008 §5: a Codex session under an automatic name that resumes a known thread
            // takes back the name that thread had; mail sent to the provisional name follows it.
            var previous = others.FirstOrDefault(a => a.Kind == AgentKinds.Codex && a.Node == me?.Node);
            if (me is { Kind: AgentKinds.Codex, Automatic: true } && previous is not null
                && _live.TryGetValue(address, out var cur) && ReferenceEquals(cur, conn))
            {
                _store.UpsertAgent(me with { Address = previous.Address, Name = previous.Name, Automatic = previous.Automatic, ThreadId = thread });
                _store.MoveMail(address, previous.Address);
                _store.DeleteAgent(address);
                _live.TryRemove(address, out _);
                _live[previous.Address] = conn;
                conn.Address = previous.Address;
                Log($"{address} resumed thread {thread}: it is {previous.Address} again");
                address = previous.Address;
                changed = true;
            }
            else
            {
                changed = me?.ThreadId != thread;
                _store.Touch(address, thread);
            }
            // A thread belongs to one row only.
            foreach (var o in others.Where(a => a.Address != address)) _store.ClearThread(o.Address);
            row = _store.GetAgent(address);
        }
        // First time we learn a Codex thread: anything still queued can now be announced.
        if (changed) _wakeups.Request(address);
        return Json.ToNode(new IdentifyResult(row?.Name, address, row?.Automatic ?? false), ProtocolJson.Default.IdentifyResult);
    }

    private JsonNode Ack(Connection conn, AckParams p)
    {
        var me = RequireRegistered(conn);
        foreach (var id in p.MessageIds ?? []) _store.Advance(id, me, RecipientStates.Read);
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
            Kind = p.Kind, Subject = p.Subject, Body = p.Body, Refs = StampRefs(p.Refs, conn.Node!), CreatedAt = DateTimeOffset.UtcNow,
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
            Body = p.Body, Refs = StampRefs(p.Refs, conn.Node!), CreatedAt = DateTimeOffset.UtcNow,
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
        var messages = _store.Inbox(me, p.UnreadOnly, limit).Select(m => Localize(m, me)).ToList();
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
            Wakeable = (a.Channel && _live.ContainsKey(a.Address)) || _wakeups.CanWake(a) || _wakeups.HasWaiter(a.Address),
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

    /// <summary>
    /// ADR-0012: <c>shepherd wait</c> (a Claude Stop hook) parks here until there is mail to
    /// announce to the agent of that session. The connection does not register as anyone.
    /// </summary>
    private async Task<JsonNode> WaitAsync(Connection conn, WaitParams p, CancellationToken ct)
    {
        var row = string.IsNullOrEmpty(p.SessionKey) ? null : _store.FindBySession(conn.Node!, p.SessionKey);
        if (row is null)
            return Json.ToNode(new WaitResult(Reason: "unknown session"), ProtocolJson.Default.WaitResult);
        var notice = await _wakeups.WaitAsync(row.Address, ct).ConfigureAwait(false);
        return Json.ToNode(new WaitResult(notice, notice is null ? "superseded or session gone" : null), ProtocolJson.Default.WaitResult);
    }

    private JsonNode Shutdown(Connection conn)
    {
        if (conn.ClientKind != "cli") throw new RpcException(RpcErrors.Unauthorized, "only the CLI may stop the broker");
        Log("shutdown requested");
        _ = Task.Run(async () => { await Task.Delay(100).ConfigureAwait(false); Stop(); });
        return new JsonObject();
    }

    // ------------------------------------------------------------------ delivery

    /// <summary>
    /// Delivery (ADR-0002, diagram 1). A live recipient with a Claude channel gets the full message
    /// pushed (its shim acks → read). Any other live recipient gets <c>message.new</c> so its next
    /// tool result mentions new mail, and every recipient without a channel is woken if possible
    /// (codex queue / herdr) with a fixed notice. Messages are already stored: a failed push or
    /// wake-up never loses them.
    /// </summary>
    private async Task DeliverAsync(MessageRow msg, List<string> recipients)
    {
        foreach (var r in recipients)
        {
            var hasChannel = false;
            var live = _live.TryGetValue(r, out var target);
            if (live)
            {
                hasChannel = target!.Channel;
                try
                {
                    await target.Peer.NotifyAsync(Methods.MessageNew,
                        Json.ToNode(Localize(msg.ToInbox(RecipientStates.Queued), r), ProtocolJson.Default.InboxMessage)).ConfigureAwait(false);
                    if (hasChannel) _store.Advance(msg.Id, r, RecipientStates.Notified);
                }
                catch (Exception ex)
                {
                    hasChannel = false;
                    Log($"push to {r} failed: {ex.Message}");
                }
            }
            if (!hasChannel) _wakeups.Request(r);
            Log($"msg {msg.Id[^8..]} {msg.Kind} {msg.From} -> {r}: {(hasChannel ? "pushed to channel" : live ? "online, wake-up requested" : "offline, wake-up requested")}");
        }
    }

    /// <summary>On (re)registration: push unread mail to a channel, or wake the agent.</summary>
    private async Task DeliverPendingAsync(string address)
    {
        try
        {
            if (_live.TryGetValue(address, out var target) && target.Channel)
            {
                foreach (var m in _store.Inbox(address, unreadOnly: true, limit: 50))
                {
                    await target.Peer.NotifyAsync(Methods.MessageNew, Json.ToNode(Localize(m, address), ProtocolJson.Default.InboxMessage)).ConfigureAwait(false);
                    _store.Advance(m.Id, address, RecipientStates.Notified);
                }
            }
            else
            {
                _wakeups.OnRegistered(address);
            }
        }
        catch (Exception ex)
        {
            Log($"pending delivery to {address} failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static List<FileRef>? StampRefs(List<FileRef>? refs, string node) =>
        refs?.Select(r => r with { Node = r.Node ?? node }).ToList();

    /// <summary>Rewrites file references into the recipient's path flavour (ADR-0006).</summary>
    private InboxMessage Localize(InboxMessage m, string recipient)
    {
        if (m.Refs is not { Count: > 0 }) return m;
        var to = _store.GetAgent(recipient);
        if (to is null) return m;
        var fromDistro = _store.GetAgent(m.From)?.Distro;
        var refs = m.Refs.Select(r => r with
        {
            Path = PathMap.Translate(r.Path, r.Node ?? to.Node, fromDistro, to.Node, to.Distro),
            Node = to.Node,
        }).ToList();
        return m with { Refs = refs };
    }

    private bool IsHeldByOther(string address, string? sessionKey) =>
        _live.TryGetValue(address, out var c) && !c.Peer.IsClosed && (sessionKey is null || _store.GetAgent(address)?.SessionKey != sessionKey);

    private static string AutomaticBase(string? cwd, string kind)
    {
        // cwd may be a Windows or a Linux (WSL) path whatever OS the broker runs on.
        var folder = string.IsNullOrWhiteSpace(cwd) ? "agent" : cwd.TrimEnd('\\', '/').Split('\\', '/').Last();
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
        // Close every connection before the database goes away; OnClosed still touches the store.
        foreach (var c in _connections.Keys) await c.Peer.DisposeAsync().ConfigureAwait(false);
        lock (_registryLock) _store.Dispose();
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
