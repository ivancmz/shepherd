using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shepherd.Protocol;

namespace Shepherd.Broker;

internal sealed record AgentRow
{
    public required string Address { get; init; }
    public required string Name { get; init; }
    public required string Node { get; init; }
    public required string Kind { get; init; }
    public string? SessionKey { get; init; }
    public string? Cwd { get; init; }
    public string? HerdrPaneId { get; init; }
    public string? ThreadId { get; init; }
    public bool Automatic { get; init; }
    public bool Channel { get; init; }
    public string? Distro { get; init; }
    public DateTimeOffset LastSeen { get; init; }
}

internal sealed record MessageRow
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

    public InboxMessage ToInbox(string state) => new()
    {
        Id = Id, ThreadId = ThreadId, InReplyTo = InReplyTo, From = From, FromHuman = FromHuman,
        Kind = Kind, Subject = Subject, Body = Body, Refs = Refs, CreatedAt = CreatedAt, State = state,
    };
}

/// <summary>
/// SQLite persistence (ADR-0004, ADR-0006). A single connection guarded by a lock: the broker's
/// traffic is a handful of messages per minute, so simplicity beats concurrency here.
/// </summary>
internal sealed class Store : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly SqliteConnection _db;
    private readonly Lock _lock = new();
    private bool _disposed;

    public Store(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        Exec("""
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS agents(
                address TEXT PRIMARY KEY, name TEXT NOT NULL, node TEXT NOT NULL, kind TEXT NOT NULL,
                session_key TEXT, cwd TEXT, herdr_pane_id TEXT, thread_id TEXT,
                automatic INTEGER NOT NULL DEFAULT 0, channel INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL, last_seen TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS messages(
                id TEXT PRIMARY KEY, thread_id TEXT NOT NULL, in_reply_to TEXT, from_addr TEXT NOT NULL,
                from_human INTEGER NOT NULL, kind TEXT NOT NULL, subject TEXT, body TEXT NOT NULL,
                refs TEXT, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS recipients(
                message_id TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                recipient TEXT NOT NULL, state TEXT NOT NULL, updated_at TEXT NOT NULL,
                PRIMARY KEY(message_id, recipient));
            CREATE INDEX IF NOT EXISTS ix_recipients_recipient ON recipients(recipient, state);
            CREATE INDEX IF NOT EXISTS ix_messages_thread ON messages(thread_id);
            CREATE INDEX IF NOT EXISTS ix_messages_from ON messages(from_addr, kind);
            """);
        Exec($"INSERT OR IGNORE INTO meta(key, value) VALUES('schema_version', '{SchemaVersion}');");
        // H3: WSL distro of an agent (wake-ups via wsl.exe, path translation).
        if (!HasColumn("agents", "distro")) Exec("ALTER TABLE agents ADD COLUMN distro TEXT;");
    }

    // ---------------------------------------------------------------- agents

    public void UpsertAgent(AgentRow a)
    {
        lock (_lock)
        {
            using var cmd = NewCommand();
            cmd.CommandText = """
                INSERT INTO agents(address, name, node, kind, session_key, cwd, herdr_pane_id, thread_id, automatic, channel, distro, created_at, last_seen)
                VALUES($address, $name, $node, $kind, $sk, $cwd, $pane, $thread, $auto, $channel, $distro, $now, $now)
                ON CONFLICT(address) DO UPDATE SET
                    kind=excluded.kind, session_key=excluded.session_key, cwd=excluded.cwd, distro=excluded.distro,
                    herdr_pane_id=excluded.herdr_pane_id, thread_id=COALESCE(excluded.thread_id, agents.thread_id),
                    automatic=excluded.automatic, channel=excluded.channel, last_seen=excluded.last_seen;
                """;
            cmd.Parameters.AddWithValue("$address", a.Address);
            cmd.Parameters.AddWithValue("$name", a.Name);
            cmd.Parameters.AddWithValue("$node", a.Node);
            cmd.Parameters.AddWithValue("$kind", a.Kind);
            cmd.Parameters.AddWithValue("$sk", (object?)a.SessionKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cwd", (object?)a.Cwd ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pane", (object?)a.HerdrPaneId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$thread", (object?)a.ThreadId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$auto", a.Automatic ? 1 : 0);
            cmd.Parameters.AddWithValue("$channel", a.Channel ? 1 : 0);
            cmd.Parameters.AddWithValue("$distro", (object?)a.Distro ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", Now());
            cmd.ExecuteNonQuery();
        }
    }

    public void Touch(string address, string? threadId = null)
    {
        lock (_lock)
        {
            using var cmd = NewCommand();
            cmd.CommandText = "UPDATE agents SET last_seen=$now, thread_id=COALESCE($thread, thread_id) WHERE address=$a";
            cmd.Parameters.AddWithValue("$now", Now());
            cmd.Parameters.AddWithValue("$thread", (object?)threadId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$a", address);
            cmd.ExecuteNonQuery();
        }
    }

    public AgentRow? GetAgent(string address)
    {
        lock (_lock)
        {
            using var cmd = NewCommand();
            cmd.CommandText = "SELECT * FROM agents WHERE address=$a";
            cmd.Parameters.AddWithValue("$a", address);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadAgent(r) : null;
        }
    }

    public List<AgentRow> ListAgents()
    {
        lock (_lock)
        {
            using var cmd = NewCommand();
            cmd.CommandText = "SELECT * FROM agents ORDER BY node, name";
            using var r = cmd.ExecuteReader();
            var list = new List<AgentRow>();
            while (r.Read()) list.Add(ReadAgent(r));
            return list;
        }
    }

    private static AgentRow ReadAgent(SqliteDataReader r) => new()
    {
        Address = r.GetString(r.GetOrdinal("address")),
        Name = r.GetString(r.GetOrdinal("name")),
        Node = r.GetString(r.GetOrdinal("node")),
        Kind = r.GetString(r.GetOrdinal("kind")),
        SessionKey = Str(r, "session_key"),
        Cwd = Str(r, "cwd"),
        HerdrPaneId = Str(r, "herdr_pane_id"),
        ThreadId = Str(r, "thread_id"),
        Automatic = r.GetInt32(r.GetOrdinal("automatic")) != 0,
        Channel = r.GetInt32(r.GetOrdinal("channel")) != 0,
        Distro = Str(r, "distro"),
        LastSeen = ParseTime(r.GetString(r.GetOrdinal("last_seen"))),
    };

    // -------------------------------------------------------------- messages

    public void InsertMessage(MessageRow m, IReadOnlyCollection<string> recipients)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var tx = _db.BeginTransaction();
            using (var cmd = NewCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO messages(id, thread_id, in_reply_to, from_addr, from_human, kind, subject, body, refs, created_at)
                    VALUES($id, $thread, $irt, $from, $human, $kind, $subject, $body, $refs, $created)
                    """;
                cmd.Parameters.AddWithValue("$id", m.Id);
                cmd.Parameters.AddWithValue("$thread", m.ThreadId);
                cmd.Parameters.AddWithValue("$irt", (object?)m.InReplyTo ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$from", m.From);
                cmd.Parameters.AddWithValue("$human", m.FromHuman ? 1 : 0);
                cmd.Parameters.AddWithValue("$kind", m.Kind);
                cmd.Parameters.AddWithValue("$subject", (object?)m.Subject ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$body", m.Body);
                cmd.Parameters.AddWithValue("$refs", m.Refs is { Count: > 0 } ? JsonSerializer.Serialize(m.Refs, ProtocolJson.Default.ListFileRef) : DBNull.Value);
                cmd.Parameters.AddWithValue("$created", m.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
                cmd.ExecuteNonQuery();
            }
            foreach (var rcpt in recipients)
            {
                using var cmd = NewCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO recipients(message_id, recipient, state, updated_at) VALUES($id, $r, $s, $now)";
                cmd.Parameters.AddWithValue("$id", m.Id);
                cmd.Parameters.AddWithValue("$r", rcpt);
                cmd.Parameters.AddWithValue("$s", RecipientStates.Queued);
                cmd.Parameters.AddWithValue("$now", Now());
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public MessageRow? GetMessage(string id)
    {
        lock (_lock)
        {
            using var cmd = NewCommand();
            cmd.CommandText = "SELECT * FROM messages WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadMessage(r) : null;
        }
    }

    public string? GetState(string messageId, string recipient)
    {
        lock (_lock)
        {
            using var cmd = NewCommand();
            cmd.CommandText = "SELECT state FROM recipients WHERE message_id=$id AND recipient=$r";
            cmd.Parameters.AddWithValue("$id", messageId);
            cmd.Parameters.AddWithValue("$r", recipient);
            return cmd.ExecuteScalar() as string;
        }
    }

    /// <summary>Moves a recipient's state forward only (queued → notified → read → answered).</summary>
    public void Advance(string messageId, string recipient, string state)
    {
        lock (_lock)
        {
            using var cmd = NewCommand();
            cmd.CommandText = """
                UPDATE recipients SET state=$s, updated_at=$now
                WHERE message_id=$id AND recipient=$r AND
                      (CASE state WHEN 'queued' THEN 0 WHEN 'notified' THEN 1 WHEN 'read' THEN 2 ELSE 3 END) <
                      (CASE $s    WHEN 'queued' THEN 0 WHEN 'notified' THEN 1 WHEN 'read' THEN 2 ELSE 3 END)
                """;
            cmd.Parameters.AddWithValue("$s", state);
            cmd.Parameters.AddWithValue("$now", Now());
            cmd.Parameters.AddWithValue("$id", messageId);
            cmd.Parameters.AddWithValue("$r", recipient);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Inbox of <paramref name="recipient"/>, oldest first.</summary>
    public List<InboxMessage> Inbox(string recipient, bool unreadOnly, int limit)
    {
        lock (_lock)
        {
            using var cmd = NewCommand();
            cmd.CommandText = $"""
                SELECT m.*, r.state AS rstate FROM recipients r JOIN messages m ON m.id = r.message_id
                WHERE r.recipient=$r {(unreadOnly ? "AND r.state IN ('queued','notified')" : "")}
                ORDER BY m.created_at {(unreadOnly ? "ASC" : "DESC")}, m.id LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$r", recipient);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<InboxMessage>();
            while (r.Read()) list.Add(ReadMessage(r).ToInbox(r.GetString(r.GetOrdinal("rstate"))));
            return list;
        }
    }

    /// <summary>Messages still in <c>queued</c> for a recipient (to be announced by a wake-up).</summary>
    public List<(string Id, string From)> QueuedFor(string recipient)
    {
        lock (_lock)
        {
            using var cmd = NewCommand();
            cmd.CommandText = """
                SELECT m.id, m.from_addr FROM recipients r JOIN messages m ON m.id = r.message_id
                WHERE r.recipient=$r AND r.state='queued' ORDER BY m.created_at, m.id
                """;
            cmd.Parameters.AddWithValue("$r", recipient);
            using var r = cmd.ExecuteReader();
            var list = new List<(string, string)>();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
            return list;
        }
    }

    public int CountUnread(string? recipient = null)
    {
        lock (_lock)
        {
            using var cmd = NewCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM recipients WHERE state IN ('queued','notified')" + (recipient is null ? "" : " AND recipient=$r");
            if (recipient is not null) cmd.Parameters.AddWithValue("$r", recipient);
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Recipients of requests sent by <paramref name="from"/> that are still unanswered.</summary>
    public List<string> WaitingOn(string from)
    {
        lock (_lock)
        {
            using var cmd = NewCommand();
            cmd.CommandText = """
                SELECT DISTINCT r.recipient FROM messages m JOIN recipients r ON r.message_id = m.id
                WHERE m.from_addr=$f AND m.kind='request' AND r.state <> 'answered' ORDER BY r.recipient
                """;
            cmd.Parameters.AddWithValue("$f", from);
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    public int CountPendingRequests()
    {
        lock (_lock)
        {
            using var cmd = NewCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM messages m JOIN recipients r ON r.message_id=m.id WHERE m.kind='request' AND r.state <> 'answered'";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Deletes messages older than <paramref name="age"/> whose recipients have all read them (ADR-0006).</summary>
    public int Purge(TimeSpan age)
    {
        lock (_lock)
        {
            using var cmd = NewCommand();
            cmd.CommandText = """
                DELETE FROM messages WHERE created_at < $cut AND NOT EXISTS
                    (SELECT 1 FROM recipients r WHERE r.message_id = messages.id AND r.state IN ('queued','notified'))
                """;
            cmd.Parameters.AddWithValue("$cut", (DateTimeOffset.UtcNow - age).ToString("O", CultureInfo.InvariantCulture));
            return cmd.ExecuteNonQuery();
        }
    }

    private static MessageRow ReadMessage(SqliteDataReader r)
    {
        var refs = Str(r, "refs");
        return new MessageRow
        {
            Id = r.GetString(r.GetOrdinal("id")),
            ThreadId = r.GetString(r.GetOrdinal("thread_id")),
            InReplyTo = Str(r, "in_reply_to"),
            From = r.GetString(r.GetOrdinal("from_addr")),
            FromHuman = r.GetInt32(r.GetOrdinal("from_human")) != 0,
            Kind = r.GetString(r.GetOrdinal("kind")),
            Subject = Str(r, "subject"),
            Body = r.GetString(r.GetOrdinal("body")),
            Refs = refs is null ? null : JsonSerializer.Deserialize(refs, ProtocolJson.Default.ListFileRef),
            CreatedAt = ParseTime(r.GetString(r.GetOrdinal("created_at"))),
        };
    }

    private static string? Str(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static DateTimeOffset ParseTime(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private bool HasColumn(string table, string column)
    {
        lock (_lock)
        {
            using var cmd = NewCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$c";
            cmd.Parameters.AddWithValue("$c", column);
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        }
    }

    private void Exec(string sql)
    {
        lock (_lock)
        {
            using var cmd = NewCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Waits for any in-flight command, then closes. Later calls throw <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _db.Dispose();
        }
    }

    /// <summary>Every command goes through here so a closed store fails uniformly.</summary>
    private SqliteCommand NewCommand()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _db.CreateCommand();
    }
}
