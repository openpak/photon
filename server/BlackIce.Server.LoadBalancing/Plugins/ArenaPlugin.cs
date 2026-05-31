using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BlackIce.Server.Core;
using BlackIce.Server.Data;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>
/// Built-in plugin that turns a Team-vs-Team realm into a scored, replayable <b>arena match</b>, entirely
/// server-side. It scores on the kills the <c>killfeed</c> plugin publishes on the <see cref="KillBus"/>:
/// each cross-team kill credits the killer's team a point, the running score is broadcast to the room
/// (vanilla chat), and the first team to reach the configurable <see cref="ArenaOptions.ScoreCap"/> wins —
/// the other side loses. When <see cref="ArenaOptions.ResetOnWin"/> is set the match then resets (scores
/// cleared, kill tallies wiped, "new round" announced) so it loops like an arcade arena. Off by default;
/// requires the <c>killfeed</c> plugin enabled (it is the kill source) and a Team-vs-Team realm.
/// </summary>
public sealed class ArenaPlugin : IServerPlugin
{
    public string Name => "arena";
    public string Description => "Team-deathmatch arena for Team-vs-Team realms: scores kills, first team to the score cap wins, then resets to replay. Off by default.";

    public void Configure(PluginBuilder builder)
    {
        var opt = (ArenaOptions?)builder.Services.GetService(typeof(ArenaOptions)) ?? new ArenaOptions();
        var state = new ArenaState { Enabled = opt.Enabled, ScoreCap = opt.ScoreCap, ResetOnWin = opt.ResetOnWin };
        var modes = (GameModeRegistry?)builder.Services.GetService(typeof(GameModeRegistry));
        var rooms = (RoomRegistry?)builder.Services.GetService(typeof(RoomRegistry));
        var bus = (KillBus?)builder.Services.GetService(typeof(KillBus));

        var match = new ArenaMatch(state, modes, rooms, bus);
        if (bus is not null) bus.Killed += match.OnKill;   // score on every published kill

        builder.AddCommands(new ArenaCommands(state, match));
    }
}

/// <summary>Live-tunable arena settings plus per-(room, team) scores and a per-room "match over" flag.</summary>
internal sealed class ArenaState
{
    public bool Enabled;
    public int ScoreCap = 25;
    public bool ResetOnWin = true;

    private readonly ConcurrentDictionary<(string Room, int Team), int> _score = new();
    private readonly ConcurrentDictionary<string, bool> _ended = new();   // room -> match decided, awaiting reset

    public int Add(string room, int team) => _score.AddOrUpdate((room, team), 1, (_, v) => v + 1);
    public int Score(string room, int team) => _score.GetValueOrDefault((room, team));
    public bool Ended(string room) => _ended.GetValueOrDefault(room);
    public void MarkEnded(string room) => _ended[room] = true;

    public void ResetRoom(string room)
    {
        foreach (var key in _score.Keys.Where(k => k.Room == room).ToArray()) _score.TryRemove(key, out _);
        _ended.TryRemove(room, out _);
    }

    /// <summary>Rooms that currently hold any score (for the console reset-all and status).</summary>
    public IReadOnlyList<string> ActiveRooms() => _score.Keys.Select(k => k.Room).Distinct().ToList();
}

/// <summary>The match logic: reacts to published kills, scores teams, declares a winner at the cap, and
/// resets. Runs on the Game listener thread (the kill bus fires from the relay), so its broadcasts and
/// state changes are single-threaded per listener.</summary>
internal sealed class ArenaMatch
{
    private readonly ArenaState _state;
    private readonly GameModeRegistry? _modes;
    private readonly RoomRegistry? _rooms;
    private readonly KillBus? _bus;

    public ArenaMatch(ArenaState state, GameModeRegistry? modes, RoomRegistry? rooms, KillBus? bus)
    {
        _state = state;
        _modes = modes;
        _rooms = rooms;
        _bus = bus;
    }

    public void OnKill(KillNotice n)
    {
        if (!_state.Enabled || _state.Ended(n.Room)) return;
        if (_modes?.ModeOf(n.Room) != GameMode.TeamVsTeam) return;
        if (_modes.TeamOf(n.Room, n.Killer) is not int killerTeam) return;
        if (_modes.TeamOf(n.Room, n.Victim) is not int victimTeam || victimTeam == killerTeam) return;  // cross-team only

        int score = _state.Add(n.Room, killerTeam);
        Announce(n.Room, n.Killer, $"⚔ Team {Name(killerTeam)} scores — {ScoreLine(n.Room)} (first to {_state.ScoreCap})");

        if (score >= _state.ScoreCap)
        {
            int loser = 1 - killerTeam;
            Announce(n.Room, n.Killer,
                $"\U0001F3C6 Team {Name(killerTeam)} WINS {_state.Score(n.Room, killerTeam)}–{_state.Score(n.Room, loser)} — Team {Name(loser)} loses!");
            if (_state.ResetOnWin) Reset(n.Room, n.Killer);
            else _state.MarkEnded(n.Room);
        }
    }

    /// <summary>Resets a room's match: clears scores, wipes the kill tallies (via the bus), starts a new round.</summary>
    public void Reset(string room, int? announceActor = null)
    {
        _state.ResetRoom(room);
        _bus?.RequestReset(room);   // tell killfeed to clear this room's HP/streak tallies
        int actor = announceActor ?? _rooms?.FindSession(room)?.Actors().FirstOrDefault() ?? 0;
        Announce(room, actor, "\U0001F504 New round — fight!");
    }

    /// <summary>Resets every room that currently has a score (console <c>arena reset</c>).</summary>
    public int ResetAll()
    {
        var rooms = _state.ActiveRooms();
        foreach (var room in rooms) Reset(room);
        return rooms.Count;
    }

    private void Announce(string room, int actor, string text)
    {
        _rooms?.FindSession(room)?.SendToAll(ServerRpc.Chat(actor, text));
        Log.Info("Arena", $"\"{room}\": {text}");
    }

    private string ScoreLine(string room) => $"Team A {_state.Score(room, 0)} – Team B {_state.Score(room, 1)}";
    private static char Name(int team) => (char)('A' + team);
}

/// <summary>Console commands to inspect and run the arena match live (Admin).</summary>
internal sealed class ArenaCommands
{
    private readonly ArenaState _state;
    private readonly ArenaMatch _match;
    public ArenaCommands(ArenaState state, ArenaMatch match)
    {
        _state = state;
        _match = match;
    }

    [ConsoleCommand("arena", Usage = "[on|off|scorecap <n>|reset]", MinLevel = PlayerLevel.Admin)]
    private string Cmd(CommandLine line)
    {
        if (line.Parts.Count == 1)
            return $"arena: {(_state.Enabled ? "on" : "off")}, first to {_state.ScoreCap}, reset-on-win {(_state.ResetOnWin ? "on" : "off")} " +
                   "(scores Team-vs-Team realms; needs the killfeed plugin on)";

        var verb = line.Parts[1].ToLowerInvariant();
        switch (verb)
        {
            case "on": _state.Enabled = true; return $"arena: on — first team to {_state.ScoreCap} wins";
            case "off": _state.Enabled = false; return "arena: off";
            case "scorecap":
                if (line.Parts.Count >= 3 && int.TryParse(line.Parts[2], out var cap) && cap >= 1)
                {
                    _state.ScoreCap = cap;
                    return $"arena: score cap {cap}";
                }
                return "usage: arena scorecap <n>   (n >= 1)";
            case "reset":
                int n = _match.ResetAll();
                return n > 0 ? $"arena: reset {n} room(s)" : "arena: no active matches to reset";
            default:
                return "usage: arena [on|off|scorecap <n>|reset]";
        }
    }
}
