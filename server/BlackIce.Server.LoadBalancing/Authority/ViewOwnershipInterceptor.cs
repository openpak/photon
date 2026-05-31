using System.Collections.Generic;
using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Enforces PUN's viewID ownership invariant: a viewID encodes its owner as <c>viewID / 1000</c> (the
/// MAX_VIEW_IDS block scheme), so an actor driving an RPC or a position update for a viewID outside its
/// own block — and not a scene object (block 0) — is acting on another player's object (the classic
/// "puppeteer" cheat). Flags the mismatch; drops it only when <see cref="AnticheatOptions.Enforce"/> is set.
/// </summary>
public sealed class ViewOwnershipInterceptor : IEventInterceptor
{
    private const int MaxViewIdsPerActor = 1000;   // PUN MAX_VIEW_IDS block size
    private readonly bool _enforce;
    public int FlaggedCount { get; private set; }

    public ViewOwnershipInterceptor(bool enforce = false) => _enforce = enforce;

    public RelayVerdict Intercept(EventContext ctx)
    {
        // Only SERIALIZE (201, position) and INSTANTIATION (202) must reference the sender's OWN view —
        // you sync/spawn your own objects. RPCs (200) are deliberately NOT checked here: many legitimately
        // target another object's view (e.g. TakeDamage is sent to the VICTIM's view), so flagging them on
        // ownership would false-positive on normal play. Cross-actor RPC policy lives elsewhere
        // (e.g. TeamDamageInterceptor for damage).
        int? viewId = PositionInfo.From(ctx.Event)?.ViewId ?? InstantiationViewId(ctx.Event);
        if (viewId is not int vid || vid <= 0) return RelayVerdict.Forward(ctx.Event);   // no view / unparseable

        int owner = vid / MaxViewIdsPerActor;
        // owner 0 = scene object (the scene/master client owns it) — allowed for anyone; otherwise the
        // viewID block must belong to the sending actor.
        if (owner == 0 || owner == ctx.SenderActor) return RelayVerdict.Forward(ctx.Event);

        FlaggedCount++;
        Log.Warn("Authority", $"actor {ctx.SenderActor} in \"{ctx.RoomName}\" acted on view {vid} owned by " +
                              $"actor {owner} -> {(_enforce ? "DROPPED" : "forwarded (log-only)")}");
        return _enforce ? RelayVerdict.Drop() : RelayVerdict.Forward(ctx.Event);
    }

    /// <summary>The viewID a PUN instantiation (202) claims, from key 7 of its PData hashtable.</summary>
    private static int? InstantiationViewId(EventData ev)
    {
        if (ev.Code != PhotonCodes.PunEvent.Instantiation) return null;
        if (!ev.Parameters.TryGetValue(PhotonCodes.Param.Data, out var d) || d is not IDictionary<object, object> pdata)
            return null;
        return pdata.TryGetValue(PhotonCodes.InstantiationKey.ViewId, out var v) && v is int i ? i : null;
    }
}
