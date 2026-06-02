using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>
/// Built-in plugin that detects <b>real player deaths</b> from the relay and announces them, with zero
/// client support. The game broadcasts a player's death as a <c>KilledPlayerRemote</c> RPC carrying the
/// victim's pawn viewId (captured live — see <c>docs/protocol/03-rpc-catalog.md</c>); this plugin watches
/// for it, announces the elimination over vanilla chat, and publishes a <see cref="DeathNotice"/> on the
/// <see cref="KillBus"/> so the <c>arena</c> match plugin can score it. (It replaces an earlier model that
/// summed <c>TakeDamage</c> toward an assumed max-HP — which never fired, since damage is resolved
/// master-side and no <c>TakeDamage</c> transits the wire.) Off by default; an admin runs <c>killfeed on</c>.
/// </summary>
public sealed class KillfeedPlugin : IServerPlugin
{
    public string Name => "killfeed";
    public string Description => "Real-death elimination feed: detects KilledPlayerRemote, announces it via vanilla chat, and publishes deaths for the arena scorer. Off by default.";
    public int Order => 100;   // react AFTER the validators

    public void Configure(PluginBuilder builder)
    {
        var state = new KillfeedState();
        var bus = (KillBus?)builder.Services.GetService(typeof(KillBus));

        if (bus is not null) bus.RoomReset += state.ClearDead;   // a new round lets everyone die again

        builder
            .AddInterceptor(() => new KillfeedInterceptor(state, bus))
            .OnActorLeft(ctx => state.Forget(ctx.RoomName, ctx.Actor))
            .AddCommands(new KillfeedCommands(state));
    }
}

/// <summary>Kill-feed on/off plus the per-room "currently dead" set used to debounce repeated death RPCs
/// (the game may resend). Accessed on the Game listener thread with the flag also written from the console
/// thread; a concurrent map and an atomic bool cover that.</summary>
internal sealed class KillfeedState
{
    public bool On;

    private readonly ConcurrentDictionary<(string Room, int Actor), bool> _dead = new();

    /// <summary>Marks a victim dead; returns true only the first time (so the caller scores once per death).</summary>
    public bool MarkDead(string room, int actor) => _dead.TryAdd((room, actor), true);

    /// <summary>Drops a departed player's dead-flag (called from the leave hook).</summary>
    public void Forget(string room, int actor) => _dead.TryRemove((room, actor), out _);

    /// <summary>Clears every dead-flag for a room (called on a round/match reset).</summary>
    public void ClearDead(string room)
    {
        foreach (var key in _dead.Keys.Where(k => k.Room == room).ToArray()) _dead.TryRemove(key, out _);
    }
}

/// <summary>
/// Per-relay death detector: when a <c>KilledPlayerRemote</c> RPC passes through, derives the victim actor
/// from the pawn viewId in the first arg, debounces, publishes a <see cref="DeathNotice"/>, and announces
/// the elimination (forwarded alongside the original RPC so clients still process the death).
/// </summary>
internal sealed class KillfeedInterceptor : IEventInterceptor
{
    private const int MaxViewIdsPerActor = 1000;   // viewId / 1000 = owning actor
    private readonly KillfeedState _state;
    private readonly KillBus? _bus;

    public KillfeedInterceptor(KillfeedState state, KillBus? bus)
    {
        _state = state;
        _bus = bus;
    }

    public RelayVerdict Intercept(EventContext ctx)
    {
        if (!_state.On) return RelayVerdict.Forward(ctx.Event);

        var info = PunRpcInfo.From(ctx.Event);
        if (info is not { Method: "KilledPlayerRemote" } rpc) return RelayVerdict.Forward(ctx.Event);
        if (rpc.Args is not { Length: > 0 } || rpc.Args[0] is not int victimView) return RelayVerdict.Forward(ctx.Event);

        int victim = victimView / MaxViewIdsPerActor;
        if (!_state.MarkDead(ctx.RoomName, victim)) return RelayVerdict.Forward(ctx.Event);   // already dead -> debounce

        _bus?.PublishDeath(new DeathNotice(ctx.RoomName, victim));
        Log.Info("Killfeed", $"\"{ctx.RoomName}\": actor {victim} was eliminated");
        return RelayVerdict.Originate(ctx.Event, new List<EventData> { ServerRpc.Chat(victim, $"☠ Actor {victim} was eliminated") });
    }
}

/// <summary>Console command to toggle the kill feed live (Admin).</summary>
internal sealed class KillfeedCommands
{
    private readonly KillfeedState _state;
    public KillfeedCommands(KillfeedState state) => _state = state;

    [ConsoleCommand("killfeed", Usage = "[on|off]", MinLevel = PlayerLevel.Admin)]
    private string Cmd(CommandLine line)
    {
        if (line.Parts.Count == 1) return $"killfeed: {(_state.On ? "on" : "off")} (announces real deaths)";

        var verb = line.Parts[1].ToLowerInvariant();
        switch (verb)
        {
            case "on": _state.On = true; return "killfeed: on";
            case "off": _state.On = false; return "killfeed: off";
            default: return "usage: killfeed [on|off]";
        }
    }
}
