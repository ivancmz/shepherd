using System.Collections.Concurrent;
using System.Globalization;
using Shepherd.Protocol;

namespace Shepherd.Broker;

/// <summary>Wake-up timings (ADR-0002, ADR-0009). Tests shorten them.</summary>
public sealed record WakeTimings
{
    /// <summary>Back-off between the attempts made right after a message arrives.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
    /// <summary>After those, a failed wake-up is retried this often while the recipient is live.</summary>
    public TimeSpan RetryEvery { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Announced but unread: first reminder this long after the last notice.</summary>
    public TimeSpan FirstReminder { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Second and last reminder.</summary>
    public TimeSpan SecondReminder { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Wakes idle agents that cannot receive a push (ADR-0002):
/// <list type="bullet">
/// <item>Codex with a known thread → <c>codex queue</c> with a fixed notice (never the message content).</item>
/// <item>Claude without channel, live in a herdr pane → <c>herdr agent prompt</c> with the same notice.</item>
/// </list>
/// Requests per recipient are coalesced (one notice for a burst) and retried with back-off. The
/// broker's sweep keeps retrying failed wake-ups and reminds of announced-but-unread mail (ADR-0009).
/// </summary>
internal sealed class Wakeups
{
    public static readonly TimeSpan CoalesceDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    private readonly Store _store;
    private readonly ICommandRunner _runner;
    private readonly Func<string, bool> _isLive;
    private readonly Action<string> _log;
    private readonly string _brokerNode;
    private readonly string? _codex;
    private readonly string? _herdr;
    private readonly string? _wsl;
    private readonly WakeTimings _timings;
    private readonly ConcurrentDictionary<string, WakeState> _states = new();
    private readonly CancellationToken _stopping;

    public Wakeups(Store store, ICommandRunner runner, Func<string, bool> isLive, Action<string> log, string brokerNode,
        string? codexPath, string? herdrPath, string? wslPath, WakeTimings timings, CancellationToken stopping)
    {
        _store = store;
        _runner = runner;
        _isLive = isLive;
        _log = log;
        _brokerNode = brokerNode;
        _codex = codexPath;
        _herdr = herdrPath;
        _wsl = wslPath;
        _timings = timings;
        _stopping = stopping;
    }

    /// <summary>The fixed notice. Only broker-registered names (validated) and a count: no peer-written text.</summary>
    public static string Notice(int count, IReadOnlyCollection<string> senders) => string.Create(CultureInfo.InvariantCulture,
        $"[shepherd] You have {count} new message(s){From(senders)}. Read them with the shepherd read_inbox tool and answer requests with reply.");

    /// <summary>The fixed reminder for mail that was announced but is still unread (ADR-0009).</summary>
    public static string Reminder(int count, IReadOnlyCollection<string> senders) => string.Create(CultureInfo.InvariantCulture,
        $"[shepherd] Reminder: you still have {count} unread message(s){From(senders)}. Read them with the shepherd read_inbox tool and answer requests with reply.");

    private static string From(IReadOnlyCollection<string> senders) => senders.Count switch
    {
        0 => "",
        <= 3 => " from " + string.Join(", ", senders),
        _ => string.Create(CultureInfo.InvariantCulture, $" from {string.Join(", ", senders.Take(3))} and {senders.Count - 3} more"),
    };

    /// <summary>True if this recipient can be woken by one of the strategies.</summary>
    public bool CanWake(AgentRow a) => Strategy(a) is not null;

    /// <summary>
    /// Schedules a coalesced wake-up of <paramref name="address"/>. One loop per recipient: a burst
    /// within <see cref="CoalesceDelay"/> becomes one notice, and messages arriving while a notice
    /// is being sent trigger exactly one more pass afterwards (never two notices at once).
    /// </summary>
    public void Request(string address) => Start(address, Pass.New);

    /// <summary>
    /// Periodic check of a live recipient (ADR-0009): retries a wake-up that failed, or reminds
    /// of mail that was announced and is still unread.
    /// </summary>
    public void Sweep(string address)
    {
        var state = _states.GetOrAdd(address, _ => new WakeState());
        lock (state)
        {
            if (state.Running) return;
        }
        var unread = _store.UnreadFor(address);
        if (unread.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        lock (state)
        {
            if (now - state.LastAttempt < _timings.RetryEvery) return;
        }
        if (unread.Any(u => u.State == RecipientStates.Queued)) Start(address, Pass.Retry);
        else if (ReminderDue(state, unread.Max(u => u.UpdatedAt), now)) Start(address, Pass.Reminder);
    }

    /// <summary>
    /// A session (re)registered: announce what is still queued, or remind once of mail announced
    /// to the previous connection, unless a reminder went out recently.
    /// </summary>
    public void OnRegistered(string address)
    {
        var unread = _store.UnreadFor(address);
        if (unread.Count == 0) return;
        if (unread.Any(u => u.State == RecipientStates.Queued))
        {
            Request(address);
            return;
        }
        var state = _states.GetOrAdd(address, _ => new WakeState());
        bool due;
        lock (state) due = DateTimeOffset.UtcNow - state.LastReminder >= _timings.FirstReminder;
        if (due) Start(address, Pass.Reminder);
    }

    // ------------------------------------------------------------ waiters (ADR-0012)

    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _waiters = new();

    public bool HasWaiter(string address) => _waiters.ContainsKey(address);

    /// <summary>
    /// Parks a <c>shepherd wait</c> for <paramref name="address"/> until there is mail to announce;
    /// returns the fixed notice, or null if a newer wait took its place. One waiter per agent.
    /// </summary>
    public async Task<string?> WaitAsync(string address, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters.AddOrUpdate(address, tcs, (_, old) =>
        {
            old.TrySetResult(null);
            return tcs;
        });
        try
        {
            // Mail that arrived while the agent was busy is announced now that it went idle.
            if (_store.UnreadFor(address).Any(u => u.State == RecipientStates.Queued)) Request(address);
            return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _waiters.TryRemove(new KeyValuePair<string, TaskCompletionSource<string?>>(address, tcs));
        }
    }

    private HashSet<string> _suspects = [];

    /// <summary>
    /// Called on every sweep: a wait whose agent has been offline for two sweeps in a row belongs
    /// to a session that is gone; release it so its process ends. (A broker restart leaves the
    /// agent offline only for the moment its shim takes to reconnect.)
    /// </summary>
    public void ReleaseOrphans()
    {
        var offline = _waiters.Keys.Where(a => !_isLive(a)).ToHashSet();
        foreach (var address in offline.Where(_suspects.Contains))
        {
            if (_waiters.TryRemove(address, out var waiter)) waiter.TrySetResult(null);
            _log($"released the wait of {address}: its session is gone");
        }
        _suspects = offline;
    }

    private bool ReminderDue(WakeState state, DateTimeOffset lastNotice, DateTimeOffset now)
    {
        lock (state)
        {
            if (state.ReminderBase != lastNotice)
            {
                // A newer notice went out (new mail): the count starts again.
                state.ReminderBase = lastNotice;
                state.Reminders = 0;
            }
            var age = now - lastNotice;
            return state.Reminders switch
            {
                0 => age >= _timings.FirstReminder,
                1 => age >= _timings.SecondReminder && now - state.LastReminder >= _timings.SecondReminder - _timings.FirstReminder,
                _ => false,
            };
        }
    }

    private enum Pass
    {
        /// <summary>New mail: coalesce, then attempt with back-off.</summary>
        New,
        /// <summary>Sweep: one more attempt for mail still queued.</summary>
        Retry,
        /// <summary>Sweep or registration: one reminder for announced mail.</summary>
        Reminder,
    }

    private void Start(string address, Pass pass)
    {
        var state = _states.GetOrAdd(address, _ => new WakeState());
        lock (state)
        {
            if (pass == Pass.New) state.Pending = true;
            if (state.Running) return;
            state.Running = true;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                if (pass != Pass.New) await WakeAsync(address, state, pass).ConfigureAwait(false);
                while (true)
                {
                    lock (state)
                    {
                        if (!state.Pending)
                        {
                            state.Running = false;
                            return;
                        }
                    }
                    await Task.Delay(CoalesceDelay, _stopping).ConfigureAwait(false);
                    lock (state) state.Pending = false;
                    await WakeAsync(address, state, Pass.New).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log($"wake {address} failed: {ex.Message}");
            }
            lock (state) state.Running = false;
        });
    }

    private sealed class WakeState
    {
        public bool Pending;
        public bool Running;
        /// <summary>Last time a wake-up command was run for this recipient.</summary>
        public DateTimeOffset LastAttempt = DateTimeOffset.MinValue;
        /// <summary>Time of the notice the reminder count refers to.</summary>
        public DateTimeOffset ReminderBase = DateTimeOffset.MinValue;
        public int Reminders;
        public DateTimeOffset LastReminder = DateTimeOffset.MinValue;
    }

    private async Task WakeAsync(string address, WakeState state, Pass pass)
    {
        var maxAttempts = pass == Pass.New ? _timings.RetryDelays.Count + 1 : 1;
        for (var attempt = 0; ; attempt++)
        {
            var agent = _store.GetAgent(address);
            if (agent is null) return;
            var unread = _store.UnreadFor(address);
            var queued = unread.Where(u => u.State == RecipientStates.Queued).ToList();
            // New mail that arrived meanwhile is announced as such; a reminder is only for the rest.
            var reminder = pass == Pass.Reminder && queued.Count == 0;
            var targets = reminder ? unread : queued;
            if (targets.Count == 0) return; // already read in the meantime
            var strategy = Strategy(agent);
            if (strategy is null && !HasWaiter(address)) return;

            var senders = targets.Select(q => q.From).Distinct(StringComparer.Ordinal).ToList();
            var notice = reminder ? Reminder(targets.Count, senders) : Notice(targets.Count, senders);
            lock (state) state.LastAttempt = DateTimeOffset.UtcNow;
            CommandResult result;
            string via;
            // A parked `shepherd wait` (Claude Stop hook) goes first: it needs no terminal (ADR-0012).
            if (_waiters.TryRemove(address, out var waiter) && waiter.TrySetResult(notice))
            {
                result = new CommandResult(0, "", "");
                via = "hook";
            }
            else if (strategy is { } s)
            {
                result = await _runner.RunAsync(s.File, [.. s.Args, notice], CommandTimeout, _stopping).ConfigureAwait(false);
                via = Path.GetFileNameWithoutExtension(s.File);
            }
            else
            {
                return; // the waiter left meanwhile; its next wait announces what is queued
            }
            if (result.Ok)
            {
                foreach (var q in queued) _store.Advance(q.Id, address, RecipientStates.Notified);
                if (reminder)
                {
                    var now = DateTimeOffset.UtcNow;
                    var lastNotice = targets.Max(t => t.UpdatedAt);
                    lock (state)
                    {
                        if (state.ReminderBase != lastNotice)
                        {
                            state.ReminderBase = lastNotice;
                            state.Reminders = 0;
                        }
                        // Old mail (e.g. after a broker restart) gets one reminder, not both in a row.
                        state.Reminders = now - lastNotice >= _timings.SecondReminder ? 2 : Math.Min(2, state.Reminders + 1);
                        state.LastReminder = now;
                    }
                }
                _log($"{(reminder ? "reminded" : "woke")} {address} via {via} ({targets.Count} message(s))");
                return;
            }
            var why = (result.Stderr.Length > 0 ? result.Stderr : result.Stdout).Trim();
            _log($"wake {address} attempt {attempt + 1} failed (exit {result.ExitCode}): {why}");
            if (attempt + 1 >= maxAttempts) return; // stays queued; the sweep retries while the agent is live
            await Task.Delay(_timings.RetryDelays[attempt], _stopping).ConfigureAwait(false);
        }
    }

    /// <summary>Command (without the trailing notice argument) that wakes <paramref name="a"/>, or null.</summary>
    private (string File, string[] Args)? Strategy(AgentRow a)
    {
        if (a.Kind == AgentKinds.Codex && a.ThreadId is { } thread && _codex is not null && a.Node == _brokerNode)
            return (_codex, ["queue", "--thread", thread, "--message"]);

        if (a.Kind == AgentKinds.Claude && !a.Channel && a.HerdrPaneId is { } pane && _herdr is not null
            && a.Node == _brokerNode && _isLive(a.Address))
            return (_herdr, ["agent", "prompt", pane]);

        // Codex inside WSL: its own codex/daemon live in Linux (ADR-0001). `bash -ic` loads the
        // user's shell setup (fnm/nvm) so `codex` resolves to the Linux build, not the Windows one
        // that WSL's PATH also exposes; "$@" passes the arguments through untouched.
        if (a.Kind == AgentKinds.Codex && a.ThreadId is { } wthread && _wsl is not null && a.Distro is { } distro
            && PathMap.IsWslNode(a.Node))
            return (_wsl, ["-d", distro, "-e", "bash", "-ic", "codex queue \"$@\"", "bash", "--thread", wthread, "--message"]);

        return null;
    }
}
