using System.Collections.Concurrent;
using BlackIce.Server.LoadBalancing.Authority;

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

/// <summary>
/// Tracks rooms shared across the Master and Game server roles, and the per-room relay sessions.
///
/// Phase 3a: each session's authority interceptors are driven by an <see cref="AuthorityPolicy"/>
/// resolved per room from the realm's <c>ExtraJson</c> (via the optional resolver passed to the ctor).
/// With no resolver — the default — every room resolves to <see cref="AuthorityStrictness.Observe"/>,
/// so the authority layer is a pure no-op in production until a realm explicitly opts in. One
/// <see cref="ViolationTracker"/> is shared across rooms so an actor's violation tally is process-wide.
/// </summary>
public sealed class RoomRegistry
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly ConcurrentDictionary<string, RoomSession> _sessions = new();

    // Optional resolver from room name -> realm ExtraJson; null = every room is Observe (no-op).
    private readonly Func<string, string?>? _extraJsonResolver;
    private readonly ViolationTracker _violations;

    // Authority thresholds. Generous (tuned later); whether they ACT depends on per-realm strictness.
    private const float MaxUnitsPerSecond = 200f;
    private const float MaxDamage = 100000f;
    private const int KickThreshold = 20;
    private static readonly TimeSpan ViolationDecay = TimeSpan.FromMinutes(5);

    public RoomRegistry() : this(null) { }

    /// <param name="extraJsonResolver">Optional: maps a room name to its realm's <c>ExtraJson</c> so the
    /// session can resolve per-realm authority strictness. Null = every room is Observe (no-op).</param>
    public RoomRegistry(Func<string, string?>? extraJsonResolver)
    {
        _extraJsonResolver = extraJsonResolver;
        _violations = new ViolationTracker(KickThreshold, ViolationDecay);
    }

    public Room GetOrCreate(string name) => _rooms.GetOrAdd(name, n => new Room { Name = n });
    public Room? Find(string name) => _rooms.TryGetValue(name, out var r) ? r : null;
    public IReadOnlyCollection<Room> All => (IReadOnlyCollection<Room>)_rooms.Values;

    /// <summary>The relay session for a room, created on first use with the authority interceptor chain.
    /// The chain's strictness comes from the realm's ExtraJson (via the resolver); at the default Observe
    /// the validators log only and always forward, so relay behavior is unchanged.</summary>
    public RoomSession Session(string name) =>
        _sessions.GetOrAdd(name, n =>
        {
            var policy = ResolvePolicy(n);

            // Phase 3b: a per-room shadow world-state, fed by the observer (first in the chain) from
            // authoritative spawn/destroy facts, and consulted by the zero-trust outcome validator.
            var world = new RoomWorldState();
            var outcomeRules = new IOutcomeRule[] { new DeadTargetOutcomeRule() };

            return new RoomSession(n, new InterceptorChain(new IEventInterceptor[]
            {
                new WorldStateObserver(world),
                new DamageValidationInterceptor(MaxDamage, policy, _violations),
                new OutcomeValidationInterceptor(world, outcomeRules, policy, _violations),
                new MovementValidationInterceptor(MaxUnitsPerSecond, policy, _violations, world),
                new PassthroughInterceptor(),
            }));
        });

    private AuthorityPolicy ResolvePolicy(string roomName) =>
        _extraJsonResolver is null ? AuthorityPolicy.Default
                                   : AuthorityPolicy.FromExtraJson(_extraJsonResolver(roomName));
}
