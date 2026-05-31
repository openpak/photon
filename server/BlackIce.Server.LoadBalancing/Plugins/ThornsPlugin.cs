using System.Linq;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>
/// Built-in plugin showcasing a customization that is <b>only possible server-side</b>: <b>thorns</b> /
/// damage reflection. When one player damages another, the server originates a fresh <c>TakeDamage</c> RPC
/// aimed at the attacker's own view, returning a configurable percentage of the damage — a brand-new rule
/// no client knows about, synthesized purely on the relay. <b>Inert by default</b> (0%); an admin enables it
/// live with <c>thorns percent &lt;n&gt;</c>. Reflection rides the relay's Originate path, which (unlike a
/// normal relayed event) delivers to the sender too, so the attacker's client applies the reflected hit.
/// Because the reflect is accumulated, a later <see cref="RelayVerdict.Drop"/> (friendly fire, spawn
/// protection) discards it — so only damage that actually lands is reflected.
/// </summary>
public sealed class ThornsPlugin : IServerPlugin
{
    public string Name => "thorns";
    public string Description => "Damage reflection: returns a % of player-dealt damage to the attacker via a server-originated TakeDamage RPC. Off until set.";
    public int Order => 100;   // react AFTER the validators: a dropped hit discards the reflection

    public void Configure(PluginBuilder builder)
    {
        var state = new ThornsState();
        var rooms = (RoomRegistry?)builder.Services.GetService(typeof(RoomRegistry));
        builder
            .AddInterceptor(() => new ThornsInterceptor(state, rooms))
            .AddCommands(new ThornsCommands(state));
    }
}

/// <summary>Live-tunable reflection percentage (0 = disabled). Atomic int across console/relay threads.</summary>
internal sealed class ThornsState { public int Percent; }

/// <summary>Originates a reflected <c>TakeDamage</c> RPC at the attacker for player-vs-player hits.</summary>
internal sealed class ThornsInterceptor : IEventInterceptor
{
    private const int MaxViewIdsPerActor = 1000;   // viewID / 1000 = owning actor; avatar view = actor*1000 + 1
    private const int AvatarViewSlot = 1;
    private readonly ThornsState _state;
    private readonly RoomRegistry? _rooms;
    public int ReflectedCount { get; private set; }

    public ThornsInterceptor(ThornsState state, RoomRegistry? rooms)
    {
        _state = state;
        _rooms = rooms;
    }

    public RelayVerdict Intercept(EventContext ctx)
    {
        int percent = _state.Percent;
        if (percent <= 0) return RelayVerdict.Forward(ctx.Event);

        var info = PunRpcInfo.From(ctx.Event);
        if (info is not { DamageValue: { } dmg } rpc || !float.IsFinite(dmg) || dmg <= 0f)
            return RelayVerdict.Forward(ctx.Event);

        int attacker = ctx.SenderActor;
        int target = rpc.ViewId / MaxViewIdsPerActor;
        // Only reflect player-vs-player: the target must be a different player currently in the room
        // (skips self-damage and damage to world/enemy views, which have no peer to reflect onto).
        if (target == attacker) return RelayVerdict.Forward(ctx.Event);
        var session = _rooms?.FindSession(ctx.RoomName);
        if (session is null || !session.Actors().Contains(target)) return RelayVerdict.Forward(ctx.Event);

        float reflected = dmg * percent / 100f;
        var reflectRpc = DamageData.BuildTakeDamageRpc(attacker * MaxViewIdsPerActor + AvatarViewSlot, reflected);

        ReflectedCount++;
        Log.Info("Thorns", $"\"{ctx.RoomName}\": reflected {reflected:0.#} ({percent}% of {dmg:0.#}) from actor {target} back to attacker {attacker}");
        return RelayVerdict.Originate(ctx.Event, new[] { reflectRpc });
    }
}

/// <summary>Console commands to inspect and set damage reflection live (Admin).</summary>
internal sealed class ThornsCommands
{
    private readonly ThornsState _state;
    public ThornsCommands(ThornsState state) => _state = state;

    [ConsoleCommand("thorns", Usage = "[percent <0-1000> | off]", MinLevel = PlayerLevel.Admin)]
    private string Cmd(CommandLine line)
    {
        if (line.Parts.Count == 1)
            return _state.Percent > 0 ? $"thorns: reflecting {_state.Percent}% of player damage" : "thorns: off";

        var verb = line.Parts[1].ToLowerInvariant();
        if (verb == "off") { _state.Percent = 0; return "thorns: off"; }
        if (verb == "percent" && line.Parts.Count >= 3 && int.TryParse(line.Parts[2], out var p) && p >= 0 && p <= 1000)
        {
            _state.Percent = p;
            return p > 0 ? $"thorns: reflecting {p}% of player damage to the attacker" : "thorns: off";
        }
        return "usage: thorns [percent <0-1000> | off]";
    }
}
