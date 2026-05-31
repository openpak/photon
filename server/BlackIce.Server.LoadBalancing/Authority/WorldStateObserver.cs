using System.Buffers.Binary;
using System.Collections;
using BlackIce.Photon;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Feeds the room <see cref="RoomWorldState"/> from authoritative spawn/destroy facts: PUN event 202
/// (instantiation) marks an entity alive — recording its prefab <em>kind</em> and spawn <em>position</em>
/// when present — and PUN event 204 (destroy) marks it dead. It is a pure observer — it NEVER changes the
/// verdict (always <see cref="RelayAction.Forward"/>), so it is safe to place first in the interceptor
/// chain, guaranteeing outcome rules (and world-aware bots) downstream see up-to-date state. Existence is
/// treated as fact — it mirrors the relay's own late-joiner spawn cache — not as a validated claim, so
/// this observer makes no enforcement decision of its own.
/// </summary>
public sealed class WorldStateObserver : IEventInterceptor
{
    private readonly RoomWorldState _world;

    public WorldStateObserver(RoomWorldState world) => _world = world;

    public RelayVerdict Intercept(EventContext ctx)
    {
        var ev = ctx.Event;
        if (ev.Code == PhotonCodes.PunEvent.Instantiation)
        {
            if (TryReadSpawn(ev, out var viewId, out var kind, out var hasPos, out var x, out var y, out var z))
            {
                if (hasPos) _world.ObserveSpawn(viewId, kind, x, y, z);
                else _world.ObserveSpawn(viewId, kind);   // known to exist, position unknown (don't fabricate 0,0,0)
            }
        }
        else if (ev.Code == PhotonCodes.PunEvent.Destroy)
        {
            if (TryReadDestroyViewId(ev, out var viewId)) _world.ObserveDestroy(viewId);
        }
        return RelayVerdict.Forward(ev);   // observer only: never alters the relay decision
    }

    /// <summary>
    /// Reads a 202 instantiation: viewID (key 7, required), prefab name (key 0, optional), and spawn
    /// position (key 1, a Vector3 custom type, optional). <paramref name="hasPos"/> reports whether a real
    /// position was present — when false the entity is tracked without a location. Best-effort: a payload
    /// without a resolvable viewID is not tracked (the entity stays unknown → fail-open).
    /// </summary>
    private static bool TryReadSpawn(EventData ev, out int viewId, out string? kind, out bool hasPos,
                                     out float x, out float y, out float z)
    {
        viewId = 0; kind = null; hasPos = false; x = y = z = 0f;
        if (!ev.Parameters.TryGetValue(PhotonCodes.Param.Data, out var raw) || raw is not IDictionary<object, object> pdata)
            return false;
        if (!(pdata.TryGetValue(PhotonCodes.InstantiationKey.ViewId, out var v) && v is int i)) return false;
        viewId = i;

        if (pdata.TryGetValue(PhotonCodes.InstantiationKey.PrefabName, out var n) && n is string s) kind = s;

        if (pdata.TryGetValue(PhotonCodes.InstantiationKey.Position, out var p)
            && p is PhotonCustomData { Code: PhotonCodes.CustomType.Vector3 } vec && vec.Data.Length >= 12)
        {
            x = BinaryPrimitives.ReadSingleBigEndian(vec.Data.AsSpan(0, 4));
            y = BinaryPrimitives.ReadSingleBigEndian(vec.Data.AsSpan(4, 4));
            z = BinaryPrimitives.ReadSingleBigEndian(vec.Data.AsSpan(8, 4));
            hasPos = true;
        }
        return true;
    }

    /// <summary>Reads the destroyed viewID. A 204's payload carries the viewID at key 0 in live captures
    /// ({0=viewId}); we also accept key 7 for symmetry with the 202 shape. Best-effort.</summary>
    private static bool TryReadDestroyViewId(EventData ev, out int viewId)
    {
        viewId = 0;
        if (!ev.Parameters.TryGetValue(PhotonCodes.Param.Data, out var raw) || raw is not IDictionary<object, object> pdata)
            return false;
        if (pdata.TryGetValue((byte)0, out var v0) && v0 is int i0) { viewId = i0; return true; }
        if (pdata.TryGetValue(PhotonCodes.InstantiationKey.ViewId, out var v7) && v7 is int i7) { viewId = i7; return true; }
        return false;
    }
}
