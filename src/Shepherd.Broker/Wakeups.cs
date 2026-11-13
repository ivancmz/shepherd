using System.Collections.Concurrent;
using System.Globalization;
using Shepherd.Protocol;

namespace Shepherd.Broker;

/// <summary>
/// Wakes idle agents that cannot receive a push (ADR-0002):
/// <list type="bullet">
/// <item>Codex with a known thread → <c>codex queue</c> with a fixed notice (never the message content).</item>
/// <item>Claude without channel, live in a herdr pane → <c>herdr agent prompt</c> with the same notice.</item>
/// </list>
/// Requests per recipient are coalesced (one notice for a burst) and retried with back-off.
/// </summary>
internal sealed class Wakeups
{
    public static readonly TimeSpan CoalesceDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    private readonly Store _store;
    private readonly ICommandRunner _runner;
    private readonly Func<string, bool> _isLive;
    private readonly Action<string> _log;
    private readonly string _brokerNode;
    private readonly string? _codex;
    private readonly string? _herdr;
    private readonly ConcurrentDictionary<string, WakeState> _states = new();
    private readonly CancellationToken _stopping;

    public Wakeups(Store store, ICommandRunner runner, Func<string, bool> isLive, Action<string> log, string brokerNode,
        string? codexPath, string? herdrPath, CancellationToken stopping)
    {
        _store = store;
        _runner = runner;
        _isLive = isLive;
        _log = log;
        _brokerNode = brokerNode;
        _codex = codexPath;
        _herdr = herdrPath;
        _stopping = stopping;
    }

    /// <summary>The fixed notice. Only broker-registered names (validated) and a count: no peer-written text.</summary>
    public static string Notice(int count, IReadOnlyCollection<string> senders)
    {
        var from = senders.Count switch
        {
            0 => "",
            <= 3 => " from " + string.Join(", ", senders),
            _ => string.Create(CultureInfo.InvariantCulture, $" from {string.Join(", ", senders.Take(3))} and {senders.Count - 3} more"),
        };
        return string.Create(CultureInfo.InvariantCulture,
            $"[shepherd] You have {count} new message(s){from}. Read them with the shepherd read_inbox tool and answer requests with reply.");
    }

    /// <summary>True if this recipient can be woken by one of the strategies.</summary>
    public bool CanWake(AgentRow a) => Strategy(a) is not null;

    /// <summary>
    /// Schedules a coalesced wake-up of <paramref name="address"/>. One loop per recipient: a burst
    /// within <see cref="CoalesceDelay"/> becomes one notice, and messages arriving while a notice
    /// is being sent trigger exactly one more pass afterwards (never two notices at once).
    /// </summary>
    public void Request(string address)
    {
        var state = _states.GetOrAdd(address, _ => new WakeState());
        lock (state)
        {
            state.Pending = true;
            if (state.Running) return;
            state.Running = true;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(CoalesceDelay, _stopping).ConfigureAwait(false);
                    lock (state) state.Pending = false;
                    await WakeWithRetriesAsync(address).ConfigureAwait(false);
                    lock (state)
                    {
                        if (!state.Pending)
                        {
                            state.Running = false;
                            return;
                        }
                    }
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
    }

    private async Task WakeWithRetriesAsync(string address)
    {
        for (var attempt = 0; ; attempt++)
        {
            var agent = _store.GetAgent(address);
            if (agent is null) return;
            var queued = _store.QueuedFor(address);
            if (queued.Count == 0) return; // already read in the meantime
            var strategy = Strategy(agent);
            if (strategy is null) return;

            var senders = queued.Select(q => q.From).Distinct(StringComparer.Ordinal).ToList();
            var notice = Notice(queued.Count, senders);
            var (file, args) = strategy.Value;
            args = [.. args, notice];
            var result = await _runner.RunAsync(file, args, CommandTimeout, _stopping).ConfigureAwait(false);
            if (result.Ok)
            {
                foreach (var q in queued) _store.Advance(q.Id, address, RecipientStates.Notified);
                _log($"woke {address} via {Path.GetFileNameWithoutExtension(file)} ({queued.Count} message(s))");
                return;
            }
            var why = (result.Stderr.Length > 0 ? result.Stderr : result.Stdout).Trim();
            _log($"wake {address} attempt {attempt + 1} failed (exit {result.ExitCode}): {why}");
            if (attempt >= RetryDelays.Length) return; // stays queued; read_inbox still works
            await Task.Delay(RetryDelays[attempt], _stopping).ConfigureAwait(false);
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

        return null; // WSL nodes arrive in H3
    }
}
