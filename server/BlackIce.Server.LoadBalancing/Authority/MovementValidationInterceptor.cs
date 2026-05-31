using System.Collections.Concurrent;
using System.Diagnostics;
using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Authority over relayed position updates (PUN event 201). Tracks the last ACCEPTED position + wall-clock
/// time per (room, viewId) and flags implied speeds above a sane maximum (teleport / speedhack). What it
/// does with a violation is the realm's <see cref="AuthorityPolicy"/>:
/// <list type="bullet">
/// <item>Observe — forward, no opinion (default; production no-op).</item>
/// <item>Warn — forward, but log + tally via <see cref="ViolationTracker"/>.</item>
/// <item>Enforce/Strict — snap-correct: <see cref="RelayAction.Rewrite"/> a 201 carrying the last-good
/// position, so the cheater rubber-bands instead of teleporting. The event is never silently dropped —
/// dropping movement would break client prediction.</item>
/// </list>
/// Governing invariants:
/// <list type="bullet">
/// <item><b>apply-after-validate</b> — the shadow position only advances to positions we ACCEPTED; a
/// rejected teleport never poisons the next frame's speed calculation.</item>
/// <item><b>fail-open</b> — an unparseable event, or a view with no baseline yet, is forwarded untouched
/// (can't judge, never punish).</item>
/// <item><b>single-threaded</b> — authority is designed to run on the single listener thread. The shadow
/// map is a <see cref="ConcurrentDictionary{TKey,TValue}"/> as defense-in-depth (matching the EnetPeer
/// hardening in commit 8d49495), and an opt-in <see cref="Debug.Assert"/> tripwire (see
/// <see cref="EnableSingleThreadGuard"/>) catches accidental cross-thread use in production builds. The
/// only intentionally cross-thread state — the violation tally — lives in the thread-safe
/// <see cref="ViolationTracker"/>.</item>
/// </list>
/// </summary>
public sealed class MovementValidationInterceptor : IEventInterceptor
{
    private readonly float _maxUnitsPerSecond;
    private readonly AuthorityPolicy _policy;
    private readonly ViolationTracker _violations;
    private readonly RoomWorldState? _world;   // optional: records accepted positions for lag-comp rewind (3c)
    private readonly ConcurrentDictionary<(string room, int viewId), (float x, float y, float z, DateTime t)> _last = new();

    // Single-thread tripwire. Off by default so concurrency-hardening tests (which deliberately drive the
    // relay from many threads as defense-in-depth) don't trip it; production wiring opts in.
    private int _ownerThreadId;
    public bool EnableSingleThreadGuard { get; set; }

    private int _flaggedCount;
    public int FlaggedCount => _flaggedCount;

    public MovementValidationInterceptor(float maxUnitsPerSecond)
        : this(maxUnitsPerSecond, AuthorityPolicy.Default, new ViolationTracker(int.MaxValue, TimeSpan.FromMinutes(5))) { }

    public MovementValidationInterceptor(float maxUnitsPerSecond, AuthorityPolicy policy, ViolationTracker violations,
                                         RoomWorldState? world = null)
    {
        _maxUnitsPerSecond = maxUnitsPerSecond;
        _policy = policy;
        _violations = violations;
        _world = world;
    }

    public RelayVerdict Intercept(EventContext ctx)
    {
        AssertSingleThread();

        var parsed = PositionInfo.From(ctx.Event);
        if (parsed is not { } p) return RelayVerdict.Forward(ctx.Event);   // fail-open: not a position

        var key = (ctx.RoomName, p.ViewId);
        var now = DateTime.UtcNow;

        if (!_last.TryGetValue(key, out var prev))
        {
            // No baseline yet — can't judge speed. Accept this position as the new baseline and forward.
            AcceptPosition(key, p, now);
            return RelayVerdict.Forward(ctx.Event);
        }

        var dt = (now - prev.t).TotalSeconds;
        bool violation = false;
        if (dt > 0.001)
        {
            double dx = p.X - prev.x, dy = p.Y - prev.y, dz = p.Z - prev.z;
            double speed = Math.Sqrt(dx * dx + dy * dy + dz * dz) / dt;
            violation = speed > _maxUnitsPerSecond;
        }

        if (!violation)
        {
            // Accepted: advance the shadow to this position (apply-after-validate).
            AcceptPosition(key, p, now);
            return RelayVerdict.Forward(ctx.Event);
        }

        // Violation. Tally/log from Warn upward; escalation (Strict) is handled by the session.
        if (_policy.CountsViolations)
        {
            System.Threading.Interlocked.Increment(ref _flaggedCount);
            _violations.Flag(ctx.RoomName, ctx.SenderActor);
            Log.Warn("Authority", $"actor {ctx.SenderActor} view {p.ViewId} in \"{ctx.RoomName}\" " +
                                  $"exceeded max speed (> {_maxUnitsPerSecond:F0} u/s) -> {_policy.Strictness}");
        }

        var action = _policy.ActionFor(ViolationKind.Movement);
        if (action == RelayAction.Rewrite)
        {
            // Snap-correct: relay a 201 with the LAST GOOD position. The shadow stays at last-good — it
            // does NOT advance to the rejected teleport target (apply-after-validate).
            var corrected = PositionInfo.BuildEvent(p.ViewId, prev.x, prev.y, prev.z);
            return RelayVerdict.Rewrite(corrected);
        }

        // Observe/Warn: forward the original. The shadow advances to the (tolerated) reported position so
        // the next frame's delta is measured from where the client now believes it is.
        AcceptPosition(key, p, now);
        return RelayVerdict.Forward(ctx.Event);
    }

    /// <summary>
    /// Commits an accepted position: advances the per-view baseline AND (if a world-state is attached)
    /// records the sample for lag-comp rewind. Called only on positions we accept — a snap-corrected
    /// teleport is never committed here, keeping both the baseline and the rewind history apply-after-validate.
    /// </summary>
    private void AcceptPosition((string room, int viewId) key, PositionInfo p, DateTime now)
    {
        _last[key] = (p.X, p.Y, p.Z, now);
        _world?.RecordPosition(p.ViewId, p.X, p.Y, p.Z, now);
    }

    [Conditional("DEBUG")]
    private void AssertSingleThread()
    {
        if (!EnableSingleThreadGuard) return;
        int tid = Environment.CurrentManagedThreadId;
        if (_ownerThreadId == 0) _ownerThreadId = tid;
        Debug.Assert(_ownerThreadId == tid,
            "MovementValidationInterceptor authority state must only be touched on the listener thread");
    }
}
