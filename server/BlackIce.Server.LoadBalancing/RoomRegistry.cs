using System.Collections.Concurrent;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing;

/// <summary>An in-memory game room and its membership. Persistence is out of scope for Phase 1.</summary>
public sealed class Room
{
    public required string Name { get; init; }
    public Dictionary<byte, object> Properties { get; } = new();
    public List<int> ActorNumbers { get; } = new();
    private int _nextActor;

    private readonly object _propsLock = new();
    private readonly Dictionary<object, object> _gameProps = new();
    private readonly Dictionary<int, Dictionary<object, object>> _actorProps = new();

    public int AddActor()
    {
        var actor = Interlocked.Increment(ref _nextActor);
        // Real actor numbers must stay below the bot range so their viewID blocks (actor*1000) never
        // overlap; this also catches the (absurd, ~2^31 joins) wraparound to a negative number.
        if (actor <= 0 || actor >= Bots.BotManager.BotActorBase)
            throw new InvalidOperationException($"Room '{Name}' exhausted its actor-number space ({actor})");
        lock (ActorNumbers) ActorNumbers.Add(actor);
        return actor;
    }

    /// <summary>
    /// Merges a property set into the room. With <paramref name="actorNr"/>, the values are that
    /// actor's player properties; without it, they are the shared game properties. This is how
    /// OpSetProperties (op 252) persists in-room state so later joiners / GetProperties see it.
    /// Keys/values are stored as-is (Photon's loosely-typed hashtable entries).
    /// </summary>
    public void SetProperties(int? actorNr, System.Collections.IDictionary props)
    {
        lock (_propsLock)
        {
            var target = actorNr is int nr
                ? (_actorProps.TryGetValue(nr, out var ap) ? ap : _actorProps[nr] = new())
                : _gameProps;
            foreach (System.Collections.DictionaryEntry e in props)
                if (e.Key is not null) target[e.Key] = e.Value!;
        }
    }

    /// <summary>Snapshot of the game (shared) properties.</summary>
    public IReadOnlyDictionary<object, object> GameProperties
    {
        get { lock (_propsLock) return new Dictionary<object, object>(_gameProps); }
    }

    /// <summary>Snapshot of one actor's player properties, or empty if none set.</summary>
    public IReadOnlyDictionary<object, object> ActorProperties(int actorNr)
    {
        lock (_propsLock)
            return _actorProps.TryGetValue(actorNr, out var ap)
                ? new Dictionary<object, object>(ap) : new Dictionary<object, object>();
    }
}

/// <summary>Tracks rooms shared across the Master and Game server roles.</summary>
public sealed class RoomRegistry
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly ConcurrentDictionary<string, RoomSession> _sessions = new();
    private readonly AnticheatOptions _anticheat;
    private readonly GameModeRegistry _modes;

    public RoomRegistry(AnticheatOptions? anticheat = null, GameModeRegistry? modes = null)
    {
        _anticheat = anticheat ?? new AnticheatOptions();
        _modes = modes ?? new GameModeRegistry();
    }

    /// <summary>The shared game-mode/team state, so handlers can assign teams and set modes per room.</summary>
    public GameModeRegistry Modes => _modes;

    public Room GetOrCreate(string name) => _rooms.GetOrAdd(name, n => new Room { Name = n });
    public Room? Find(string name) => _rooms.TryGetValue(name, out var r) ? r : null;
    public IReadOnlyCollection<Room> All => (IReadOnlyCollection<Room>)_rooms.Values;

    /// <summary>Room names known to the registry (snapshot), for inspection commands.</summary>
    public IReadOnlyCollection<string> RoomNames => _rooms.Keys.ToArray();

    /// <summary>The relay session for a room WITHOUT creating one — for inspection that must not
    /// materialize empty sessions. Returns null if no session exists yet.</summary>
    public RoomSession? FindSession(string name) => _sessions.TryGetValue(name, out var s) ? s : null;

    /// <summary>The relay session for a room, created on first use with the authority/anti-cheat
    /// interceptor chain built from the configured <see cref="AnticheatOptions"/>. Detection-only unless
    /// <see cref="AnticheatOptions.Enforce"/> is set; thresholds are generous to avoid false positives.</summary>
    public RoomSession Session(string name) =>
        _sessions.GetOrAdd(name, n => new RoomSession(n, new InterceptorChain(new IEventInterceptor[]
        {
            new Authority.EventRateInterceptor(_anticheat),
            new Authority.MovementValidationInterceptor(_anticheat.MaxSpeedUnitsPerSecond, _anticheat.MaxTeleportDistance, _anticheat.Enforce),
            new Authority.DamageValidationInterceptor(_anticheat.MaxDamagePerHit, _anticheat.Enforce),
            new Authority.HitRateInterceptor(_anticheat),
            new Authority.ViewOwnershipInterceptor(_anticheat.Enforce),
            // Game-mode policy: drops friendly-fire / PvE-forbidden player damage (no-op in FreeForAll).
            new Authority.TeamDamageInterceptor(_modes),
            new PassthroughInterceptor(),
        })));
}
