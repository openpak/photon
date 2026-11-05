using OpenPak.Server.Core;

namespace OpenPak.Server.LoadBalancing.Quantum;

/// <summary>One round of a Stumble Guys match: a level the client already ships (data.unity3d LevelDef,
/// its Quantum map named in MapCollection).</summary>
public sealed record StumbleRound(string LevelId, string MapName, string FriendlyName, long MapGuid);

/// <summary>
/// The room-property half of Stumble Guys' Photon Cloud plugin. Read from the game's own flow code
/// (Multiplayer.Core / Multiplayer.Implementation, IL2CPP; RoomConstants holds the key names):
/// after joining, the client waits for room property ROUNDS_NUMBER (byte) and, for round i from 1, reads
/// ROUND{i}_MAP_ID (string, the LevelDef id) and ROUND{i}_MAP (string, the MapCollection name whose guid
/// it resolves); the creator side also writes ROUND{i}_MAP_NAME (string) and ROUND{i}_MAP_GUID (long).
/// It then waits for room property LOAD (bool), loads the scene, sets its own player property LOAD_READY
/// (bool) and waits for room property START (bool) before starting the Quantum simulation, which
/// <see cref="QuantumHost"/> then serves. Nothing here simulates.
/// State is per live room and dies with it; players, accounts and tickets persist in the stumble-guys
/// service (Postgres), not here.
/// </summary>
public sealed class StumbleLobbyHost
{
    public const string KeyRoundsNumber = "ROUNDS_NUMBER", KeyLoad = "LOAD", KeyLoadReady = "LOAD_READY",
        KeyStart = "START", KeyGameId = "GAME_ID", KeyPool = "P", KeyPluginVersion = "PLUGIN_VERSION",
        KeyPlayWithBots = "PLAY_WITH_BOTS";

    public static string RoundKey(int round, string suffix) => $"ROUND{round}_{suffix}";

    public static readonly IReadOnlyList<StumbleRound> DefaultRounds = new[]
    {
        new StumbleRound("IPL_047_Flame", "ipl_047_flame", "Hot Wheels Yeti Run", 3684277463294234336L),
        new StumbleRound("SSL_015_slides", "ssl_015_slides", "Super Waterpark", 3091314977753144095L),
    };

    private enum Phase { Joining, Loading, Started }

    private sealed class Lobby
    {
        public double FirstJoin, LoadSent;
        public Phase Phase = Phase.Joining;
        public readonly HashSet<int> Actors = new(), Ready = new();
    }

    /// <summary>(room key, properties): store them on the room and broadcast them to every member.</summary>
    private readonly Action<string, IDictionary<object, object>> _setRoomProperties;
    private readonly Dictionary<string, Lobby> _lobbies = new();
    private readonly object _gate = new();

    public StumbleLobbyHost(Action<string, IDictionary<object, object>> setRoomProperties) => _setRoomProperties = setRoomProperties;

    /// <summary>Seconds after the first join that the room waits for the rest of the matched lobby.</summary>
    public double JoinWindow { get; set; } = 6;
    /// <summary>Seconds a loading room waits for every player's LOAD_READY before starting anyway.</summary>
    public double LoadTimeout { get; set; } = 45;
    /// <summary>How many rounds a match has (clamped to the rounds configured).</summary>
    public int RoundsNumber { get; set; } = 1;
    public IReadOnlyList<StumbleRound> Rounds { get; set; } = DefaultRounds;
    public string PluginVersion { get; set; } = "openpak-1";
    /// <summary>The optional properties set besides ROUNDS_NUMBER, ROUND{i}_MAP_ID and ROUND{i}_MAP (all by default); trimmed from the environment to find which one a client rejects.</summary>
    public ISet<string> Extras { get; set; } = new HashSet<string> { KeyGameId, KeyPool, KeyPluginVersion, KeyPlayWithBots, "MAP_NAME", "MAP_GUID" };

    /// <summary>The room properties the plugin has set before the first player's join answers, so the join response
    /// carries them: the client reads the rounds (and picks its scenes) right after joining, not when LOAD comes.</summary>
    public Dictionary<object, object> InitialProperties(string room)
    {
        int n = Math.Clamp(RoundsNumber, 0, Rounds.Count);
        var props = new Dictionary<object, object> { [KeyRoundsNumber] = (byte)n };
        if (Extras.Contains(KeyGameId)) props[KeyGameId] = room;
        if (Extras.Contains(KeyPool)) props[KeyPool] = 0;
        if (Extras.Contains(KeyPluginVersion)) props[KeyPluginVersion] = PluginVersion;
        if (Extras.Contains(KeyPlayWithBots)) props[KeyPlayWithBots] = true;
        for (int i = 1; i <= n; i++)
        {
            var r = Rounds[i - 1];
            props[RoundKey(i, "MAP_ID")] = r.LevelId;
            props[RoundKey(i, "MAP")] = r.MapName;
            if (Extras.Contains("MAP_NAME")) props[RoundKey(i, "MAP_NAME")] = r.FriendlyName;
            if (Extras.Contains("MAP_GUID")) props[RoundKey(i, "MAP_GUID")] = r.MapGuid;
        }
        Log.Info("Stumble", $"{Redact.Room(room)} room created with {n} round(s)");
        return props;
    }

    public void Joined(string room, int actor, double now)
    {
        lock (_gate)
        {
            if (!_lobbies.TryGetValue(room, out var l))
            {
                _lobbies[room] = l = new Lobby { FirstJoin = now };
                Log.Info("Stumble", $"{Redact.Room(room)} lobby opened by actor {actor}");
            }
            l.Actors.Add(actor);
        }
    }

    public void Left(string room, int actor)
    {
        lock (_gate)
        {
            if (!_lobbies.TryGetValue(room, out var l)) return;
            l.Actors.Remove(actor); l.Ready.Remove(actor);
            if (l.Actors.Count == 0) _lobbies.Remove(room);
        }
    }

    /// <summary>An actor's player properties changed; LOAD_READY=true is the load handshake.</summary>
    public void PlayerProperties(string room, int actor, System.Collections.IDictionary props)
    {
        if (!props.Contains(KeyLoadReady) || props[KeyLoadReady] is not true) return;
        lock (_gate)
        {
            if (!_lobbies.TryGetValue(room, out var l)) return;
            l.Ready.Add(actor);
            Log.Info("Stumble", $"{Redact.Room(room)} actor {actor} loaded ({l.Ready.Count}/{l.Actors.Count})");
        }
    }

    public void Tick(double now)
    {
        List<(string Room, Lobby L)> due;
        lock (_gate) due = _lobbies.Select(kv => (kv.Key, kv.Value)).ToList();
        foreach (var (room, l) in due)
        {
            bool sendLoad = false, sendStart = false;
            lock (_gate)
            {
                if (l.Phase == Phase.Joining && now - l.FirstJoin >= JoinWindow) { l.Phase = Phase.Loading; l.LoadSent = now; sendLoad = true; }
                else if (l.Phase == Phase.Loading
                         && (l.Actors.All(l.Ready.Contains) && l.Actors.Count > 0 || now - l.LoadSent >= LoadTimeout))
                { l.Phase = Phase.Started; sendStart = true; }
            }
            if (sendLoad)
            {
                Log.Info("Stumble", $"{Redact.Room(room)} {l.Actors.Count} player(s): LOAD");
                _setRoomProperties(room, new Dictionary<object, object> { [KeyLoad] = true });
            }
            if (sendStart)
            {
                Log.Info("Stumble", $"{Redact.Room(room)} {l.Ready.Count}/{l.Actors.Count} loaded: START");
                _setRoomProperties(room, new Dictionary<object, object> { [KeyStart] = true });
            }
        }
    }
}
