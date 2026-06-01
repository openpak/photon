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
        // Share the per-room world-state via the registry so world-aware playerbots read the same shadow the
        // observer writes. Fall back to a private registry if none is registered (e.g. a unit-test host).
        var worlds = (RoomWorldStateRegistry?)builder.Services.GetService(typeof(RoomWorldStateRegistry)) ?? new RoomWorldStateRegistry();
        builder.AddInterceptor(() => new ServerAuthorityInterceptor(opt.Enforce, worlds));
    }
}

/// <summary>
/// Per-room composition of the server-authority pieces over one <see cref="RoomWorldState"/>: observes
/// spawn/destroy to keep the shadow current, records accepted positions for lag-comp rewind, and runs the
/// zero-trust outcome rules on damage/kill/loot RPCs. The world-state is the room's SHARED instance from
/// <see cref="RoomWorldStateRegistry"/> (resolved lazily on first event by room name), so bots and authority
/// observe the same world. The plugin factory still yields one interceptor per room, so the lazy binding is
/// stable per room.
/// </summary>
internal sealed class ServerAuthorityInterceptor : IEventInterceptor
{
    private readonly bool _enforce;
    private readonly RoomWorldStateRegistry _worlds;
    private RoomWorldState? _world;
    private WorldStateObserver? _observer;
    private OutcomeValidationInterceptor? _outcome;

    public ServerAuthorityInterceptor(bool enforce, RoomWorldStateRegistry worlds)
    {
        _enforce = enforce;
        _worlds = worlds;
    }

    public RelayVerdict Intercept(EventContext ctx)
    {
        // Lazily bind to this room's shared world-state on the first event we see (the instance is per-room).
        if (_world is null)
        {
            _world = _worlds.For(ctx.RoomName);
            _observer = new WorldStateObserver(_world);
            _outcome = new OutcomeValidationInterceptor(_world, new IOutcomeRule[] { new DeadTargetOutcomeRule() }, _enforce);
        }

        // Keep the shadow current from spawn/destroy facts (observer always forwards), and record accepted
        // positions for the lag-comp rewind timeline. Then let the outcome validator judge a 200 RPC.
        _observer!.Intercept(ctx);

        if (ctx.Event.Code == PhotonCodes.PunEvent.SendSerialize && PositionInfo.From(ctx.Event) is { } p)
            _world.RecordPosition(p.ViewId, p.X, p.Y, p.Z, DateTime.UtcNow);

        return _outcome!.Intercept(ctx);
    }
}
