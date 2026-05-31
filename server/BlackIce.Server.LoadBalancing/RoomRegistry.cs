using BlackIce.Server.LoadBalancing.Authority;

namespace BlackIce.Server.LoadBalancing;

/// <summary>
/// Process-wide registry of <see cref="RoomSession"/>s keyed by room name. The relay path looks up (or
/// lazily creates) a session per room. Each session is built with the authority interceptor chain.
/// Thread-safety: a simple lock around the dictionary; sessions outlive individual operations.
///
/// Phase 3a: each session's authority interceptors are driven by an <see cref="AuthorityPolicy"/>
/// resolved per room from the realm's <c>ExtraJson</c> (via the optional <c>extraJsonResolver</c>). With
/// no resolver — the default — every room resolves to <see cref="AuthorityStrictness.Observe"/>, so the
/// authority layer is a pure no-op in production until a realm explicitly opts in.
/// </summary>
public sealed class RoomRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RoomSession> _sessions = new();
    private readonly Func<string, string?>? _extraJsonResolver;

    // Shared across rooms so an actor's violation tally is process-wide for the session.
    private readonly ViolationTracker _violations;

    // Authority thresholds. Generous defaults (tuned later). Whether they ACT depends on the per-realm
    // strictness; at the default Observe they only ever forward.
    private const float MaxUnitsPerSecond = 50f;
    private const float MaxDamage = 1000f;
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

    public RoomSession Session(string roomName)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(roomName, out var s))
            {
                var policy = ResolvePolicy(roomName);
                var chain = new InterceptorChain(new IEventInterceptor[]
                {
                    new DamageValidationInterceptor(MaxDamage, policy, _violations),
                    new MovementValidationInterceptor(MaxUnitsPerSecond, policy, _violations),
                    new PassthroughInterceptor(),
                });
                _sessions[roomName] = s = new RoomSession(roomName, chain);
            }
            return s;
        }
    }

    private AuthorityPolicy ResolvePolicy(string roomName)
    {
        if (_extraJsonResolver is null) return AuthorityPolicy.Default;
        return AuthorityPolicy.FromExtraJson(_extraJsonResolver(roomName));
    }
}
