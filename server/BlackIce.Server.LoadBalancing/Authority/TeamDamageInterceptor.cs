using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Enforces a room's <see cref="GameMode"/> friendly-fire / PvE policy on the relay: a player-target
/// damage RPC that the mode forbids (same-team in Team-vs-Team, any player target in Co-op) is dropped
/// before it reaches the victim, so the damage is never applied — turning a free-for-all room into TvT
/// or Co-op without touching the client. Damage to enemies/world objects always passes.
///
/// The target player is identified by the RPC's viewID block (viewID / 1000), the same MAX_VIEW_IDS
/// invariant the ownership checks use; if that block isn't a tracked player the event passes.
/// </summary>
public sealed class TeamDamageInterceptor : IEventInterceptor
{
    private const int MaxViewIdsPerActor = 1000;
    private readonly GameModeRegistry _modes;
    public int DroppedCount { get; private set; }

    public TeamDamageInterceptor(GameModeRegistry modes) => _modes = modes;

    public RelayVerdict Intercept(EventContext ctx)
    {
        // Only damage-carrying RPCs are subject to the policy.
        var info = PunRpcInfo.From(ctx.Event);
        if (info is not { DamageValue: not null } dmg) return RelayVerdict.Forward(ctx.Event);

        int targetActor = dmg.ViewId / MaxViewIdsPerActor;
        if (!_modes.BlocksDamage(ctx.RoomName, attacker: ctx.SenderActor, target: targetActor))
            return RelayVerdict.Forward(ctx.Event);

        DroppedCount++;
        Log.Info("GameMode", $"{_modes.ModeOf(ctx.RoomName)} in \"{ctx.RoomName}\": dropped disallowed damage " +
                             $"from actor {ctx.SenderActor} to actor {targetActor} (friendly-fire/PvE rule)");
        return RelayVerdict.Drop();
    }
}
