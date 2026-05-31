using System;
using System.Threading.Tasks;
using BlackIce.Server.LoadBalancing.Authority;
using Xunit;

namespace BlackIce.Server.Tests.Authority;

/// <summary>
/// Phase 3a: <see cref="ViolationTracker"/> is the per-(room, actor) flag accumulator. It is the one
/// piece of authority state legitimately touched cross-thread (counters), so its increments are atomic
/// (Interlocked). Counts decay over time, and a configurable threshold raises a kick signal — but only
/// when escalation is enabled (Strict). No persistent bans: escalation is session-scoped.
/// </summary>
public class ViolationTrackerTests
{
    [Fact]
    public void Flag_increments_per_actor_count()
    {
        var t = new ViolationTracker(kickThreshold: 5, decay: TimeSpan.FromMinutes(1));
        t.Flag("co-op", actor: 1);
        t.Flag("co-op", actor: 1);
        t.Flag("co-op", actor: 2);
        Assert.Equal(2, t.CountFor("co-op", 1));
        Assert.Equal(1, t.CountFor("co-op", 2));
    }

    [Fact]
    public void Counts_are_isolated_per_room()
    {
        var t = new ViolationTracker(kickThreshold: 5, decay: TimeSpan.FromMinutes(1));
        t.Flag("co-op", 1);
        t.Flag("pvp", 1);
        Assert.Equal(1, t.CountFor("co-op", 1));
        Assert.Equal(1, t.CountFor("pvp", 1));
    }

    [Fact]
    public void Parallel_flags_are_counted_exactly_via_interlocked()
    {
        // The whole point of the Interlocked hardening: N concurrent Flag()s yield EXACTLY N, never less.
        const int threads = 16, perThread = 5000;
        var t = new ViolationTracker(kickThreshold: int.MaxValue, decay: TimeSpan.FromHours(1));

        Parallel.For(0, threads, _ =>
        {
            for (int i = 0; i < perThread; i++) t.Flag("co-op", actor: 1);
        });

        Assert.Equal(threads * perThread, t.CountFor("co-op", 1));
    }

    [Fact]
    public void Counts_decay_to_zero_after_the_decay_window()
    {
        // A laggy spike must not accumulate forever. With a zero-length decay window every prior flag is
        // already stale, so a fresh read sees only flags inside the window.
        var t = new ViolationTracker(kickThreshold: 100, decay: TimeSpan.Zero);
        t.Flag("co-op", 1);
        t.Flag("co-op", 1);
        // Nothing flagged "recently" within a zero window -> decayed away.
        Assert.Equal(0, t.CountFor("co-op", 1));
    }

    [Fact]
    public void Recent_flags_survive_a_generous_decay_window()
    {
        var t = new ViolationTracker(kickThreshold: 100, decay: TimeSpan.FromHours(1));
        t.Flag("co-op", 1);
        t.Flag("co-op", 1);
        Assert.Equal(2, t.CountFor("co-op", 1));
    }

    [Fact]
    public void Flag_returns_true_when_kick_threshold_is_reached()
    {
        var t = new ViolationTracker(kickThreshold: 3, decay: TimeSpan.FromHours(1));
        Assert.False(t.Flag("co-op", 1));   // count 1
        Assert.False(t.Flag("co-op", 1));   // count 2
        Assert.True(t.Flag("co-op", 1));    // count 3 -> kick
    }

    [Fact]
    public void Reset_clears_an_actors_count()
    {
        var t = new ViolationTracker(kickThreshold: 3, decay: TimeSpan.FromHours(1));
        t.Flag("co-op", 1);
        t.Reset("co-op", 1);
        Assert.Equal(0, t.CountFor("co-op", 1));
    }
}
