using System.Collections.Generic;
using System.Linq;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>
/// Built-in plugin that keeps a <b>server-authoritative kill feed / killstreak</b> with zero client support.
/// The server doesn't know real hit points, so it models them: it sums the damage each player receives and,
/// when the running total crosses an assumed max-HP, credits the attacker with a kill, resets the victim,
/// and announces it (plus milestone streaks) to the room via a <c>ReceiveChatMessage</c> RPC — the same
/// vanilla chat channel the client already renders, so the feed shows up with no mod. It is an approximation
/// (the assumed HP won't match the game exactly) and is <b>off by default</b>; an admin enables it with
/// <c>killfeed on</c> and tunes the model with <c>killfeed hp &lt;n&gt;</c>.
/// </summary>
public sealed class KillfeedPlugin : IServerPlugin
{
    public string Name => "killfeed";
    public string Description => "Server-authoritative killstreak feed: models HP from relayed damage and announces kills/streaks via vanilla chat. Off by default.";

    public void Configure(PluginBuilder builder)
    {
        var state = new KillfeedState();
        var rooms = (RoomRegistry?)builder.Services.GetService(typeof(RoomRegistry));
        var modes = (GameModeRegistry?)builder.Services.GetService(typeof(GameModeRegistry));
        builder
            .AddInterceptor(() => new KillfeedInterceptor(state, rooms, modes))
            .AddCommands(new KillfeedCommands(state));
    }
}

/// <summary>Global kill-feed settings. Atomic bool/int across the console and relay threads.</summary>
internal sealed class KillfeedState
{
    public bool On;
    public int AssumedMaxHp = 100;
    public int StreakAnnounceThreshold = 3;   // first streak size that earns its own "on fire" line
}

/// <summary>
/// Per-room kill tracker: accumulates received damage per victim and credits a kill (and streak) to the
/// attacker that crosses the assumed max-HP. One instance per room (the relay is single-threaded per
/// listener, so its plain dictionaries need no locking). Announcements ride the Originate path so they reach
/// every member including the killer.
/// </summary>
internal sealed class KillfeedInterceptor : IEventInterceptor
{
    private const int MaxViewIdsPerActor = 1000;   // viewID / 1000 = owning actor; avatar view = actor*1000 + 1
    private const int AvatarViewSlot = 1;

    private readonly KillfeedState _state;
    private readonly RoomRegistry? _rooms;
    private readonly GameModeRegistry? _modes;
    private readonly Dictionary<int, float> _damageTaken = new();   // victim actor -> accumulated damage
    private readonly Dictionary<int, int> _streak = new();          // attacker actor -> current killstreak

    public KillfeedInterceptor(KillfeedState state, RoomRegistry? rooms, GameModeRegistry? modes)
    {
        _state = state;
        _rooms = rooms;
        _modes = modes;
    }

    public RelayVerdict Intercept(EventContext ctx)
    {
        if (!_state.On) return RelayVerdict.Forward(ctx.Event);

        var info = PunRpcInfo.From(ctx.Event);
        if (info is not { DamageValue: { } dmg } rpc || !float.IsFinite(dmg) || dmg <= 0f)
            return RelayVerdict.Forward(ctx.Event);

        int attacker = ctx.SenderActor;
        int victim = rpc.ViewId / MaxViewIdsPerActor;
        if (victim == attacker) return RelayVerdict.Forward(ctx.Event);

        // Count only player-vs-player damage that actually lands: the victim must be in the room, and the
        // game mode must not forbid the hit (friendly fire is dropped by the game-mode plugin, so it would
        // never reach the victim — don't let it inflate the feed).
        var session = _rooms?.FindSession(ctx.RoomName);
        if (session is null || !session.Actors().Contains(victim)) return RelayVerdict.Forward(ctx.Event);
        if (_modes?.BlocksDamage(ctx.RoomName, attacker, victim) == true) return RelayVerdict.Forward(ctx.Event);

        float total = _damageTaken.GetValueOrDefault(victim) + dmg;
        if (total < _state.AssumedMaxHp) { _damageTaken[victim] = total; return RelayVerdict.Forward(ctx.Event); }

        // Kill: credit the attacker, reset the victim's pool and streak.
        _damageTaken[victim] = 0f;
        _streak[victim] = 0;
        int streak = _streak.GetValueOrDefault(attacker) + 1;
        _streak[attacker] = streak;

        var announce = new List<EventData>
        {
            ChatRpc(attacker, $"☠ Actor {attacker} eliminated Actor {victim}"),
        };
        if (streak >= _state.StreakAnnounceThreshold)
            announce.Add(ChatRpc(attacker, $"\U0001F525 Actor {attacker} is on a {streak} kill streak!"));

        Log.Info("Killfeed", $"\"{ctx.RoomName}\": actor {attacker} killed actor {victim} (streak {streak})");
        return RelayVerdict.Originate(ctx.Event, announce);
    }

    /// <summary>A <c>ReceiveChatMessage</c> RPC on the actor's avatar view — the vanilla chat channel, so the
    /// line renders without any client mod.</summary>
    private static EventData ChatRpc(int actor, string text) =>
        new(PhotonCodes.PunEvent.Rpc, new Dictionary<byte, object>
        {
            { PhotonCodes.Param.Code, PhotonCodes.PunEvent.Rpc },
            { PhotonCodes.Param.Data, new Dictionary<object, object>
                {
                    { PhotonCodes.RpcKey.ViewId, actor * MaxViewIdsPerActor + AvatarViewSlot },
                    { PhotonCodes.RpcKey.MethodName, "ReceiveChatMessage" },
                    { PhotonCodes.RpcKey.Args, new object[] { text } },
                } },
        });
}

/// <summary>Console commands to toggle and tune the kill feed live (Admin).</summary>
internal sealed class KillfeedCommands
{
    private readonly KillfeedState _state;
    public KillfeedCommands(KillfeedState state) => _state = state;

    [ConsoleCommand("killfeed", Usage = "[on|off|hp <n>]", MinLevel = PlayerLevel.Admin)]
    private string Cmd(CommandLine line)
    {
        if (line.Parts.Count == 1)
            return $"killfeed: {(_state.On ? "on" : "off")}, assumed max-HP {_state.AssumedMaxHp}";

        var verb = line.Parts[1].ToLowerInvariant();
        switch (verb)
        {
            case "on": _state.On = true; return $"killfeed: on (assumed max-HP {_state.AssumedMaxHp})";
            case "off": _state.On = false; return "killfeed: off";
            case "hp":
                if (line.Parts.Count >= 3 && int.TryParse(line.Parts[2], out var hp) && hp > 0)
                {
                    _state.AssumedMaxHp = hp;
                    return $"killfeed: assumed max-HP {hp}";
                }
                return "usage: killfeed hp <n>   (n > 0)";
            default:
                return "usage: killfeed [on|off|hp <n>]";
        }
    }
}
