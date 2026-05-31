using System.Collections.Concurrent;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Per-(room, actor) violation accumulator with time decay and a session-scoped kick threshold. This is
/// the one piece of authority state that is legitimately touched cross-thread (counters), so every
/// mutation is atomic (<see cref="Interlocked"/>) and the backing maps are concurrent — closing the
/// latent race the chaos findings documented for the old <c>FlaggedCount++</c>. Counts decay so a laggy
/// spike doesn't snowball into a kick; there are NO persistent bans here (that is Phase 4) — escalation
/// is session-scoped only.
/// </summary>
public sealed class ViolationTracker
{
    private readonly int _kickThreshold;
    private readonly TimeSpan _decay;
    private readonly Func<DateTime> _now;

    // Ring of recent flag timestamps per (room, actor); pruned to the decay window on read/write.
    private readonly ConcurrentDictionary<(string room, int actor), Entry> _entries = new();

    private sealed class Entry
    {
        public readonly object Gate = new();
        public readonly Queue<DateTime> Recent = new();
    }

    public ViolationTracker(int kickThreshold, TimeSpan decay) : this(kickThreshold, decay, () => DateTime.UtcNow) { }

    /// <summary>Test-friendly ctor allowing a deterministic clock.</summary>
    public ViolationTracker(int kickThreshold, TimeSpan decay, Func<DateTime> now)
    {
        _kickThreshold = kickThreshold;
        _decay = decay;
        _now = now;
    }

    /// <summary>
    /// Records one violation for <paramref name="actor"/> in <paramref name="room"/>. Returns true if the
    /// decayed count has reached the kick threshold (the caller decides whether to act on it — only Strict
    /// realms escalate). Atomic and safe to call from any thread.
    /// </summary>
    public bool Flag(string room, int actor)
    {
        var entry = _entries.GetOrAdd((room, actor), _ => new Entry());
        lock (entry.Gate)
        {
            entry.Recent.Enqueue(_now());
            Prune(entry);
            return entry.Recent.Count >= _kickThreshold;
        }
    }

    /// <summary>The current decayed violation count for <paramref name="actor"/> in <paramref name="room"/>.</summary>
    public int CountFor(string room, int actor)
    {
        if (!_entries.TryGetValue((room, actor), out var entry)) return 0;
        lock (entry.Gate)
        {
            Prune(entry);
            return entry.Recent.Count;
        }
    }

    /// <summary>Clears an actor's accumulated flags (e.g. on leave, or after acting on a kick).</summary>
    public void Reset(string room, int actor) => _entries.TryRemove((room, actor), out _);

    /// <summary>Drops flags older than the decay window. Must hold the entry gate.</summary>
    private void Prune(Entry entry)
    {
        var cutoff = _now() - _decay;
        while (entry.Recent.Count > 0 && entry.Recent.Peek() <= cutoff) entry.Recent.Dequeue();
    }
}
