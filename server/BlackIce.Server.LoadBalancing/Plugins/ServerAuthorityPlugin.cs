using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing.Authority;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>
/// Built-in plugin packaging the Phase 3 <b>server-authority</b> layer that goes beyond the rate/movement
/// behavioural checks in <see cref="AnticheatPlugin"/>: a per-room authoritative <em>shadow</em> world-state
/// and <b>zero-trust outcome validation</b>. The shadow tracks entity existence/alive from observed
/// spawn (202) / destroy (204) facts; outcome RPCs (200) are then checked against it by pluggable
/// <see cref="IOutcomeRule"/>s (currently <see cref="DeadTargetOutcomeRule"/> — "can't damage a corpse").
/// Accepted positions are also recorded into the room's lag-comp rewind history.
///
/// <para>Disabling it (config or <c>plugins disable authority</c>) removes the whole layer. Detection-only
/// by default; set <see cref="AnticheatOptions.Enforce"/> to drop rejected outcomes. Each room gets its own
/// interceptor instance (per-room shadow state) via the factory, matching the plugin model.</para>
/// </summary>
public sealed class ServerAuthorityPlugin : IServerPlugin
{
    public string Name => "authority";
    public string Description => "Server-authority: shadow world-state, zero-trust outcome validation (dead-target), lag-comp position history.";

    public void Configure(PluginBuilder builder)
    {
        var opt = (AnticheatOptions?)builder.Services.GetService(typeof(AnticheatOptions)) ?? new AnticheatOptions();
        builder.AddInterceptor(() => new ServerAuthorityInterceptor(opt.Enforce));
    }
}

/// <summary>
/// Per-room composition of the server-authority pieces over one <see cref="RoomWorldState"/>: observes
/// spawn/destroy to keep the shadow current, records accepted positions for lag-comp rewind, and runs the
/// zero-trust outcome rules on damage/kill/loot RPCs. A fresh instance per room (the plugin factory is
/// invoked per room) gives each room its own isolated shadow state without any cross-room sharing.
/// </summary>
internal sealed class ServerAuthorityInterceptor : IEventInterceptor
{
    private readonly RoomWorldState _world = new();
    private readonly WorldStateObserver _observer;
    private readonly OutcomeValidationInterceptor _outcome;

    public ServerAuthorityInterceptor(bool enforce)
    {
        _observer = new WorldStateObserver(_world);
        _outcome = new OutcomeValidationInterceptor(_world, new IOutcomeRule[] { new DeadTargetOutcomeRule() }, enforce);
    }

    public RelayVerdict Intercept(EventContext ctx)
    {
        // Keep the shadow current from spawn/destroy facts (observer always forwards), and record accepted
        // positions for the lag-comp rewind timeline. Then let the outcome validator judge a 200 RPC.
        _observer.Intercept(ctx);

        if (ctx.Event.Code == PhotonCodes.PunEvent.SendSerialize && PositionInfo.From(ctx.Event) is { } p)
            _world.RecordPosition(p.ViewId, p.X, p.Y, p.Z, DateTime.UtcNow);

        return _outcome.Intercept(ctx);
    }
}
