using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Watches relayed position updates (PUN event 201) and LOGS implied speeds above a sane maximum
/// (teleport / speedhack). Tracks the last position + wall-clock time per (room, viewId). Phase 2b is
/// detection-only: always forwards. A later phase can clamp/drop once tuned against real play. Driven
/// from the single listener thread, so the per-view state needs no locking.
/// </summary>
public sealed class MovementValidationInterceptor : IEventInterceptor
{
    private readonly float _maxUnitsPerSecond;
    private readonly Dictionary<(string room, int viewId), (float x, float y, float z, DateTime t)> _last = new();
    public int FlaggedCount { get; private set; }

    public MovementValidationInterceptor(float maxUnitsPerSecond) => _maxUnitsPerSecond = maxUnitsPerSecond;

    public RelayVerdict Intercept(EventContext ctx)
    {
        var pos = PositionInfo.From(ctx.Event);
        if (pos is { } p)
        {
            var key = (ctx.RoomName, p.ViewId);
            var now = DateTime.UtcNow;
            if (_last.TryGetValue(key, out var prev))
            {
                var dt = (now - prev.t).TotalSeconds;
                if (dt > 0.001)
                {
                    double dx = p.X - prev.x, dy = p.Y - prev.y, dz = p.Z - prev.z;
                    double speed = Math.Sqrt(dx * dx + dy * dy + dz * dz) / dt;
                    if (speed > _maxUnitsPerSecond)
                    {
                        FlaggedCount++;
                        Log.Warn("Authority", $"actor {ctx.SenderActor} view {p.ViewId} in \"{ctx.RoomName}\" " +
                                              $"moved {speed:F0} u/s (> max {_maxUnitsPerSecond:F0}) -> forwarded (log-only)");
                    }
                }
            }
            _last[key] = (p.X, p.Y, p.Z, now);
        }
        return RelayVerdict.Forward(ctx.Event);
    }
}
