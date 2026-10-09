using System.Collections.Generic;
using OpenPak.Photon;
using OpenPak.Server.Core;
using OpenPak.Server.Data;
using OpenPak.Server.LoadBalancing.Bots;

namespace OpenPak.Server.LoadBalancing;

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
        var rooms = _rooms.All;
        if (rooms.Count == 0) return "(no rooms)";
        // Operator view: always break out real players vs bots, independent of the lobby CountInLobby toggle.
        // Rooms of different game builds can share a name; the build's AppVersion tells them apart.
        return string.Join('\n', rooms.OrderBy(r => r.Name).ThenBy(r => r.AppVersion).Select(r =>
        {
            int players = _rooms.FindSession(r.Key)?.Count ?? 0;
            int bots = _bots.CountIn(r.Key);
            var version = r.AppVersion.Length > 0 ? $" [{r.AppVersion}]" : "";
            return bots > 0 ? $"{r.Name}{version} ({players} + {bots} bots)" : $"{r.Name}{version} ({players})";
        }));
    }

    [ConsoleCommand("room", Usage = "<name>", MinParts = 2, MinLevel = PlayerLevel.Mod)]
    private string Room(CommandLine line)
    {
        var name = Arg(line, 1);
        var room = _rooms.ResolveName(name) is { } key ? _rooms.Find(key) : null;
        if (room is null) return $"no such room: {name}";
        var actors = _rooms.FindSession(room.Key)?.Actors() ?? System.Array.Empty<int>();
        return $"room \"{room.Name}\": actors=[{string.Join(",", actors)}]\nproperties: {PhotonNames.Value(SnapshotProps(room))}";
    }

    [ConsoleCommand("getprop", Usage = "<room>", MinParts = 2, MinLevel = PlayerLevel.Mod)]
    private string GetProp(CommandLine line)
    {
        var room = _rooms.ResolveName(Arg(line, 1)) is { } key ? _rooms.Find(key) : null;
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
        var key = _rooms.ResolveName(room);
        var text = AfterArg(line, 1);
        if (key is null) return $"no such room: {room}";
        var ev = ChatCommandHandler.ServerMessageEvent(text);
        _admin.Enqueue(() => _rooms.FindSession(key)?.SendToAll(ev));
        return $"queued broadcast to \"{room}\": {text}";
    }

    [ConsoleCommand("tell", Usage = "<room> <actor> <text>", MinParts = 4, MinLevel = PlayerLevel.Mod)]
    private string Tell(CommandLine line)
    {
        var room = Arg(line, 1);
        var key = _rooms.ResolveName(room);
        if (!int.TryParse(Arg(line, 2), out var actor)) return "actor must be a number.";
        var text = AfterArg(line, 2);
        if (key is null) return $"no such room: {room}";
        var ev = ChatCommandHandler.ServerMessageEvent(text);
        _admin.Enqueue(() => _rooms.FindSession(key)?.SendToActor(actor, ev));
        return $"queued message to {room}#{actor}: {text}";
    }

    [ConsoleCommand("setprop", Usage = "<room> <key> <value>", MinParts = 4, MinLevel = PlayerLevel.Admin)]
    private string SetProp(CommandLine line)
    {
        var name = Arg(line, 1);
        var key = Arg(line, 2);
        object value = ParseValue(AfterArg(line, 2));
        if (_rooms.ResolveName(name) is not { } roomKey) return $"no such room: {name}";
        _admin.Enqueue(() =>
        {
            var room = _rooms.Find(roomKey);
            if (room is null) return;
            room.SetProperties(null, new Dictionary<object, object> { { key, value } });   // shared game prop
            _rooms.FindSession(roomKey)?.SendToAll(new EventData(PhotonCodes.Event.PropertiesChanged, new()
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
        var key = _rooms.ResolveName(room);
        if (!int.TryParse(Arg(line, 2), out var actor)) return "actor must be a number.";
        var reason = line.Parts.Count > 3 ? AfterArg(line, 2) : "Kicked by an administrator.";
        if (key is null) return $"no such room: {room}";
        _admin.Enqueue(() => _rooms.FindSession(key)?.Kick(actor, reason));
        return $"queued kick of {room}#{actor}";
    }

    [ConsoleCommand("raise", Usage = "<room> <eventCode> [text]", MinParts = 3, MinLevel = PlayerLevel.Admin)]
    private string Raise(CommandLine line)
    {
        var room = Arg(line, 1);
        var key = _rooms.ResolveName(room);
        if (!byte.TryParse(Arg(line, 2), out var code)) return "eventCode must be 0-255.";
        if (key is null) return $"no such room: {room}";
        // Generic "send any direct packet": an arbitrary event code to the whole room, with an optional
        // string payload under Data(245). Admin authority deliberately bypasses the client reserved-code
        // rule (the server may originate any event); use for protocol debugging / scripted manipulation.
        var ev = line.Parts.Count > 3
            ? new EventData(code, new() { { PhotonCodes.Param.Data, AfterArg(line, 2) } })
            : new EventData(code, new());
        _admin.Enqueue(() => _rooms.FindSession(key)?.SendToAll(ev));
        return $"queued raw event {code} ({PhotonNames.Event(code)}) to \"{room}\"";
    }

    [ConsoleCommand("bot", Usage = "<realm>", MinParts = 2, MinLevel = PlayerLevel.Admin)]
    private string Bot(CommandLine line)
    {
        var realm = AfterArg(line, 0);   // realm name may contain spaces
        var key = _rooms.ResolveName(realm) ?? realm;   // a live room by that name, else a new one
        _rooms.GetOrCreate(key);
        _bots.RequestSpawn(_rooms.Session(key), _botIds.Next());
        return $"queued bot spawn for \"{RoomRegistry.PublicName(key)}\" (runs on next listener tick)";
    }

    [ConsoleCommand("summon", Usage = "<realm>", MinParts = 2, MinLevel = PlayerLevel.Admin)]
    private string Summon(CommandLine line)
    {
        // Teleport every bot in the realm to the player's position so you can reach them instantly. The
        // server can't move YOUR character (it's client-owned, and the SteamID gate blocks networked control
        // of a real player) — but moving the server-owned bots to you achieves the same "get to them" goal,
        // clean. Queued onto the listener thread (it relays per-peer); takes effect on the next tick.
        var typed = AfterArg(line, 0);   // realm name may contain spaces
        // Resolve tolerantly: the live game's room name uses a Unicode em-dash ("Black Ice — Co-op"), which
        // can't be typed through a console whose input code page is legacy OEM. Match dash-variant/case/space-
        // insensitively so a plain-ASCII "Black Ice - Co-op" still finds it.
        var realm = _rooms.ResolveName(typed);
        if (realm is null) return $"no such room: {typed}";
        _admin.Enqueue(() =>
        {
            int n = _bots.SummonAll(realm);
            if (n < 0) Log.Warn("Bots", $"summon \"{RoomRegistry.PublicName(realm)}\": no player position known yet (is someone in the realm?)");
        });
        return $"queued summon of all bots to the player in \"{RoomRegistry.PublicName(realm)}\" (runs on next listener tick)";
    }

    [ConsoleCommand("walkmap", Usage = "<realm>", MinParts = 2, MinLevel = PlayerLevel.Mod)]
    private string Walkmap(CommandLine line)
    {
        // Report + persist the walkable map learned from real players' movement in a realm. Black Ice's world
        // is procedural (no static navmesh matches), so we map it from where players actually walk. Runs on the
        // listener thread (where the map is written each tick) so the read/save can't race the recorder.
        if (_bots.Walkable is null) return "walkable mapping is not enabled on this server.";
        var typed = AfterArg(line, 0);
        var realm = _rooms.ResolveName(typed) ?? typed;   // record/report under the live room name when known
        _admin.Enqueue(() =>
        {
            var map = _bots.Walkable.For(realm);
            var b = map.Bounds();
            _bots.Walkable.Save(realm);
            Log.Info("Walkmap", $"\"{RoomRegistry.PublicName(realm)}\": {map.Count} cells (cell {map.CellSize:F0}u) " +
                                $"X[{b.minX:F0},{b.maxX:F0}] Y[{b.minY:F0},{b.maxY:F0}] Z[{b.minZ:F0},{b.maxZ:F0}]");
        });
        return $"queued walkmap report+save for \"{RoomRegistry.PublicName(realm)}\" (runs on next tick; see the [Walkmap] log line)";
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
