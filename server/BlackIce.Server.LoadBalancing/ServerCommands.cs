using System.Collections.Generic;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing.Bots;

namespace BlackIce.Server.LoadBalancing;

/// <summary>
/// Live server inspection, debug, and direct client-manipulation commands. Read-only inspection runs
/// inline (thread-safe snapshots); anything that sends packets to clients is queued onto the Game
/// listener thread via <see cref="AdminActionQueue"/> (only that thread may touch per-peer transport),
/// so those commands report "queued" and take effect on the next maintenance tick.
/// </summary>
public sealed class ServerCommands
{
    private readonly RoomRegistry _rooms;
    private readonly AdminActionQueue _admin;
    private readonly BotManager _bots;
    private readonly BotIdentityGenerator _botIds;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    public ServerCommands(RoomRegistry rooms, AdminActionQueue admin, BotManager bots, BotIdentityGenerator botIds)
    {
        _rooms = rooms;
        _admin = admin;
        _bots = bots;
        _botIds = botIds;
    }

    // --- Inspection / debug (read-only, runs inline) ---------------------------------------------

    [ConsoleCommand("rooms", MinLevel = PlayerLevel.Mod)]
    private string Rooms(CommandLine line)
    {
        var names = _rooms.RoomNames;
        if (names.Count == 0) return "(no rooms)";
        return string.Join('\n', names.OrderBy(n => n).Select(n => $"{n} ({_rooms.FindSession(n)?.Count ?? 0})"));
    }

    [ConsoleCommand("room", Usage = "<name>", MinParts = 2, MinLevel = PlayerLevel.Mod)]
    private string Room(CommandLine line)
    {
        var name = Arg(line, 1);
        var room = _rooms.Find(name);
        if (room is null) return $"no such room: {name}";
        var actors = _rooms.FindSession(name)?.Actors() ?? System.Array.Empty<int>();
        return $"room \"{name}\": actors=[{string.Join(",", actors)}]\nproperties: {PhotonNames.Value(SnapshotProps(room))}";
    }

    [ConsoleCommand("getprop", Usage = "<room>", MinParts = 2, MinLevel = PlayerLevel.Mod)]
    private string GetProp(CommandLine line)
    {
        var room = _rooms.Find(Arg(line, 1));
        return room is null ? $"no such room: {Arg(line, 1)}" : PhotonNames.Value(SnapshotProps(room));
    }

    [ConsoleCommand("stats", MinLevel = PlayerLevel.Mod)]
    private string Stats(CommandLine line)
    {
        var names = _rooms.RoomNames;
        int players = names.Sum(n => _rooms.FindSession(n)?.Count ?? 0);
        var up = DateTime.UtcNow - _startedUtc;
        return $"rooms={names.Count} players={players} uptime={up:d\\.hh\\:mm\\:ss} loglevel={Log.Level}";
    }

    [ConsoleCommand("loglevel", Usage = "<trace|debug|info|warn|error>", MinParts = 2, MinLevel = PlayerLevel.Admin)]
    private string LogLevelCmd(CommandLine line)
    {
        if (!Enum.TryParse<LogLevel>(Arg(line, 1), ignoreCase: true, out var lvl))
            return $"unknown level '{Arg(line, 1)}' (trace|debug|info|warn|error)";
        Log.Level = lvl;
        return $"log level -> {lvl}";
    }

    // --- Direct client manipulation (queued onto the listener thread) ----------------------------

    [ConsoleCommand("say", Usage = "<room> <text>", MinParts = 3, MinLevel = PlayerLevel.Mod)]
    private string Say(CommandLine line)
    {
        var room = Arg(line, 1);
        var text = AfterArg(line, 1);
        if (_rooms.Find(room) is null) return $"no such room: {room}";
        var ev = ChatCommandHandler.ServerMessageEvent(text);
        _admin.Enqueue(() => _rooms.FindSession(room)?.SendToAll(ev));
        return $"queued broadcast to \"{room}\": {text}";
    }

    [ConsoleCommand("tell", Usage = "<room> <actor> <text>", MinParts = 4, MinLevel = PlayerLevel.Mod)]
    private string Tell(CommandLine line)
    {
        var room = Arg(line, 1);
        if (!int.TryParse(Arg(line, 2), out var actor)) return "actor must be a number.";
        var text = AfterArg(line, 2);
        if (_rooms.Find(room) is null) return $"no such room: {room}";
        var ev = ChatCommandHandler.ServerMessageEvent(text);
        _admin.Enqueue(() => _rooms.FindSession(room)?.SendToActor(actor, ev));
        return $"queued message to {room}#{actor}: {text}";
    }

    [ConsoleCommand("setprop", Usage = "<room> <key> <value>", MinParts = 4, MinLevel = PlayerLevel.Admin)]
    private string SetProp(CommandLine line)
    {
        var name = Arg(line, 1);
        var key = Arg(line, 2);
        object value = ParseValue(AfterArg(line, 2));
        if (_rooms.Find(name) is null) return $"no such room: {name}";
        _admin.Enqueue(() =>
        {
            var room = _rooms.Find(name);
            if (room is null) return;
            room.SetProperties(null, new Dictionary<object, object> { { key, value } });   // shared game prop
            _rooms.FindSession(name)?.SendToAll(new EventData(PhotonCodes.Event.PropertiesChanged, new()
            {
                { PhotonCodes.Param.Properties, new Dictionary<object, object> { { key, value } } },
                { PhotonCodes.Param.TargetActorNr, 0 },   // 0 = shared game properties
            }));
        });
        return $"queued setprop {name}.{key} = {value}";
    }

    // --- Moderation ------------------------------------------------------------------------------

    [ConsoleCommand("kick", Usage = "<room> <actor> [reason]", MinParts = 3, MinLevel = PlayerLevel.Mod)]
    private string Kick(CommandLine line)
    {
        var room = Arg(line, 1);
        if (!int.TryParse(Arg(line, 2), out var actor)) return "actor must be a number.";
        var reason = line.Parts.Count > 3 ? AfterArg(line, 2) : "Kicked by an administrator.";
        if (_rooms.Find(room) is null) return $"no such room: {room}";
        _admin.Enqueue(() => _rooms.FindSession(room)?.Kick(actor, reason));
        return $"queued kick of {room}#{actor}";
    }

    [ConsoleCommand("raise", Usage = "<room> <eventCode> [text]", MinParts = 3, MinLevel = PlayerLevel.Admin)]
    private string Raise(CommandLine line)
    {
        var room = Arg(line, 1);
        if (!byte.TryParse(Arg(line, 2), out var code)) return "eventCode must be 0-255.";
        if (_rooms.Find(room) is null) return $"no such room: {room}";
        // Generic "send any direct packet": an arbitrary event code to the whole room, with an optional
        // string payload under Data(245). Admin authority deliberately bypasses the client reserved-code
        // rule (the server may originate any event); use for protocol debugging / scripted manipulation.
        var ev = line.Parts.Count > 3
            ? new EventData(code, new() { { PhotonCodes.Param.Data, AfterArg(line, 2) } })
            : new EventData(code, new());
        _admin.Enqueue(() => _rooms.FindSession(room)?.SendToAll(ev));
        return $"queued raw event {code} ({PhotonNames.Event(code)}) to \"{room}\"";
    }

    [ConsoleCommand("bot", Usage = "<realm>", MinParts = 2, MinLevel = PlayerLevel.Admin)]
    private string Bot(CommandLine line)
    {
        var realm = AfterArg(line, 0);   // realm name may contain spaces
        _rooms.GetOrCreate(realm);
        _bots.RequestSpawn(_rooms.Session(realm), _botIds.Next());
        return $"queued bot spawn for \"{realm}\" (runs on next listener tick)";
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static string Arg(CommandLine line, int index) => index < line.Parts.Count ? line.Parts[index] : "";

    /// <summary>Everything after the token at <paramref name="index"/> (so trailing text can contain spaces).</summary>
    private static string AfterArg(CommandLine line, int index)
    {
        var s = line.Raw;
        int pos = 0;
        for (int i = 0; i <= index; i++)
        {
            pos = s.IndexOf(' ', pos);
            if (pos < 0) return "";
            pos++;
        }
        return s[pos..].Trim();
    }

    private static object ParseValue(string s)
        => bool.TryParse(s, out var b) ? b : int.TryParse(s, out var i) ? i : s;

    private static Dictionary<object, object> SnapshotProps(Room room) =>
        new(room.GameProperties);
}
