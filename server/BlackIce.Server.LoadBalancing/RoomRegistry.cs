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
    private readonly Func<EventContext, RelayVerdict>? _relayPolicy;
    private readonly GameModeRegistry _modes;

    /// <param name="relayPolicy">The live relay decision for every in-room event (the plugin manager wires
    /// <c>PluginManager.Evaluate</c> here; authority/anti-cheat and game-mode logic live in plugins). It is
    /// evaluated per event — not baked into the chain — so plugins can be enabled / disabled / loaded /
    /// unloaded at runtime with immediate effect and no lingering references. When null the relay is pure
    /// pass-through — the vanilla server.</param>
    /// <param name="modes">Shared game-mode/team state, used by the game-mode plugin and the bot soak.</param>
    public RoomRegistry(Func<EventContext, RelayVerdict>? relayPolicy = null, GameModeRegistry? modes = null)
    {
        _relayPolicy = relayPolicy;
        _modes = modes ?? new GameModeRegistry();
    }

    /// <summary>The shared game-mode/team state, so the bot soak and game-mode plugin share one map.</summary>
    public GameModeRegistry Modes => _modes;

    public Room GetOrCreate(string name) => _rooms.GetOrAdd(name, n => new Room { Name = n });
    public Room? Find(string name) => _rooms.TryGetValue(name, out var r) ? r : null;
    public IReadOnlyCollection<Room> All => (IReadOnlyCollection<Room>)_rooms.Values;

    /// <summary>Resolves an operator-typed room/realm name to the canonical stored name, tolerating dash
    /// variants (ASCII hyphen vs Unicode en/em-dash — the live room name carries an em-dash a console code
    /// page can't type), case, and whitespace. Returns the canonical name, or null if none matches. Console
    /// commands should resolve through this before <see cref="Find"/>/<see cref="FindSession"/>.</summary>
    public string? ResolveName(string input)
    {
        if (_rooms.ContainsKey(input)) return input;             // exact, fast path
        var want = Normalize(input);
        foreach (var name in _rooms.Keys)
            if (Normalize(name) == want) return name;
        return null;

        static string Normalize(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            bool prevSpace = false;
            foreach (var ch in s.Trim())
            {
                char c = ch is '‐' or '‑' or '‒' or '–' or '—' or '―' ? '-' : ch;
                if (char.IsWhiteSpace(c)) { if (prevSpace) continue; prevSpace = true; sb.Append(' '); }
                else { prevSpace = false; sb.Append(char.ToLowerInvariant(c)); }
            }
            return sb.ToString();
        }
    }

    /// <summary>Room names known to the registry (snapshot), for inspection commands.</summary>
    public IReadOnlyCollection<string> RoomNames => _rooms.Keys.ToArray();

    /// <summary>The relay session for a room WITHOUT creating one — for inspection that must not
    /// materialize empty sessions. Returns null if no session exists yet.</summary>
    public RoomSession? FindSession(string name) => _sessions.TryGetValue(name, out var s) ? s : null;

    /// <summary>The relay session for a room, created on first use. Its chain is a single interceptor that
    /// defers to the live relay policy (the plugin manager) on every event, so toggling / loading /
    /// unloading plugins takes effect without rebuilding the session. With no policy it is pure
    /// pass-through — the vanilla relay.</summary>
    public RoomSession Session(string name) =>
        _sessions.GetOrAdd(name, n =>
        {
            IEventInterceptor relay = _relayPolicy is null
                ? new PassthroughInterceptor()
                : new DelegatingInterceptor(_relayPolicy);
            return new RoomSession(n, new InterceptorChain(new[] { relay }));
        });
}

/// <summary>Adapts a per-event relay policy delegate (the plugin manager's live evaluation) into an
/// <see cref="IEventInterceptor"/>, so the room's chain holds no plugin instances and never needs
/// rebuilding when plugins change.</summary>
internal sealed class DelegatingInterceptor : IEventInterceptor
{
    private readonly Func<EventContext, RelayVerdict> _policy;
    public DelegatingInterceptor(Func<EventContext, RelayVerdict> policy) => _policy = policy;
    public RelayVerdict Intercept(EventContext ctx) => _policy(ctx);
}
