using System.Collections;
using BlackIce.Photon;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Feeds the room <see cref="RoomWorldState"/> from authoritative spawn/destroy facts: PUN event 202
/// (instantiation) marks an entity alive; PUN event 204 (destroy) marks it dead. It is a pure observer —
/// it NEVER changes the verdict (always <see cref="RelayAction.Forward"/>), so it is safe to place first
/// in the interceptor chain, guaranteeing outcome rules downstream see up-to-date existence before they
/// judge. Existence is treated as fact — it mirrors the relay's own late-joiner spawn cache — not as a
/// validated claim, so this observer makes no enforcement decision of its own.
/// </summary>
public sealed class WorldStateObserver : IEventInterceptor
{
    private const byte EvInstantiation = 202;
    private const byte EvDestroy = 204;
    private const byte PData = 245;        // RaiseEvent data hashtable
    private const byte KeyViewId = 7;      // viewID slot inside the 202/204 PData hashtable

    private readonly RoomWorldState _world;

    public WorldStateObserver(RoomWorldState world) => _world = world;

    public RelayVerdict Intercept(EventContext ctx)
    {
        var ev = ctx.Event;
        if (ev.Code == EvInstantiation) { if (TryReadViewId(ev, out var v)) _world.ObserveSpawn(v); }
        else if (ev.Code == EvDestroy) { if (TryReadViewId(ev, out var v)) _world.ObserveDestroy(v); }
        return RelayVerdict.Forward(ev);   // observer only: never alters the relay decision
    }

    /// <summary>Reads the viewID from a 202/204 PData(245) hashtable at key 7. Best-effort: a payload
    /// without a resolvable viewID is simply not tracked (the entity stays unknown → fail-open).</summary>
    private static bool TryReadViewId(EventData ev, out int viewId)
    {
        viewId = 0;
        if (!ev.Parameters.TryGetValue(PData, out var raw) || raw is not IDictionary<object, object> pdata) return false;
        if (pdata.TryGetValue(KeyViewId, out var v) && v is int i) { viewId = i; return true; }
        return false;
    }
}
