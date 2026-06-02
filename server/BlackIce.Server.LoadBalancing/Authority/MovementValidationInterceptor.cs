using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Watches relayed position updates (PUN event 201) for movement anomalies: a non-finite (NaN/Inf)
/// coordinate from garbage bytes, a single-step jump beyond <c>maxTeleportDistance</c> (teleport), or an
/// implied speed above <c>maxUnitsPerSecond</c> (speedhack). Tracks the last position + wall-clock time
/// per (room, viewId). Detection-only unless <c>enforce</c> is set, then the offending update drops. At
/// most one flag per event regardless of how many sub-checks trip. Driven from the single listener
/// thread, so the per-view state needs no locking.
/// </summary>
public sealed class MovementValidationInterceptor : IEventInterceptor
{
    private readonly float _maxUnitsPerSecond;
    private readonly float _maxTeleportDistance;
    private readonly bool _enforce;
    private readonly Func<string, int, bool>? _isExempt;
    private readonly Dictionary<(string room, int viewId), (float x, float y, float z, DateTime t)> _last = new();
    public int FlaggedCount { get; private set; }

    /// <param name="isExempt">Optional (room, senderActor) predicate; when it returns true the actor's movement
    /// is never flagged/dropped (a verified admin allowed to fly/speed). The position baseline is still tracked
    /// so a later non-exempt delta stays sane. Null = no one is exempt (everyone enforced).</param>
    public MovementValidationInterceptor(float maxUnitsPerSecond,
        float maxTeleportDistance = float.PositiveInfinity, bool enforce = false,
        Func<string, int, bool>? isExempt = null)
    {
        _maxUnitsPerSecond = maxUnitsPerSecond;
        _maxTeleportDistance = maxTeleportDistance;
        _enforce = enforce;
        _isExempt = isExempt;
    }

    public RelayVerdict Intercept(EventContext ctx)
    {
        var pos = PositionInfo.From(ctx.Event);
        if (pos is not { } p) return RelayVerdict.Forward(ctx.Event);

        // Verified admins (the fly/speed exemption) bypass enforcement — but we still refresh their position
        // baseline so revoking the exemption later doesn't produce a bogus first delta.
        if (_isExempt is not null && _isExempt(ctx.RoomName, ctx.SenderActor))
        {
            if (float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z))
                _last[(ctx.RoomName, p.ViewId)] = (p.X, p.Y, p.Z, DateTime.UtcNow);
            return RelayVerdict.Forward(ctx.Event);
        }

        string? reason = null;
        if (!(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)))
        {
            reason = $"non-finite position ({p.X},{p.Y},{p.Z})";
            // Deliberately do NOT record a non-finite baseline — it would poison the next delta.
        }
        else
        {
            var key = (ctx.RoomName, p.ViewId);
            var now = DateTime.UtcNow;
            if (_last.TryGetValue(key, out var prev))
            {
                double dx = p.X - prev.x, dy = p.Y - prev.y, dz = p.Z - prev.z;
                double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                var dt = (now - prev.t).TotalSeconds;
                if (dist > _maxTeleportDistance)
                    reason = $"teleported {dist:F0}u (> max {_maxTeleportDistance:F0})";
                else if (dt > 0.001 && dist / dt > _maxUnitsPerSecond)
                    reason = $"moved {dist / dt:F0} u/s (> max {_maxUnitsPerSecond:F0})";
            }
            _last[key] = (p.X, p.Y, p.Z, now);
        }

        if (reason is null) return RelayVerdict.Forward(ctx.Event);
        FlaggedCount++;
        Log.Warn("Authority", $"actor {ctx.SenderActor} view {p.ViewId} in \"{ctx.RoomName}\" {reason} " +
                              $"-> {(_enforce ? "DROPPED" : "forwarded (log-only)")}");
        return _enforce ? RelayVerdict.Drop() : RelayVerdict.Forward(ctx.Event);
    }
}
