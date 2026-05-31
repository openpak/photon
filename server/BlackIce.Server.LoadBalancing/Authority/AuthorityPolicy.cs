using System.Text.Json;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Per-realm authority posture. The rollout ladder: <see cref="Observe"/> (default, today's behavior —
/// a pure no-op in production until a realm opts in), <see cref="Warn"/> (log + count, still forward,
/// for tuning thresholds against live play), <see cref="Enforce"/> (the real anti-cheat: snap-correct
/// movement, drop bad outcomes), <see cref="Strict"/> (Enforce + session-scoped escalation to kick).
/// </summary>
public enum AuthorityStrictness { Observe, Warn, Enforce, Strict }

/// <summary>The class of authority violation, which selects the corrective action.</summary>
public enum ViolationKind
{
    /// <summary>Position/movement anomaly (teleport / speedhack / OOB). Corrected via snap-correct (Rewrite).</summary>
    Movement,
    /// <summary>Consequential outcome (damage/kill/loot/XP) that fails validation. Rejected via Drop.</summary>
    Outcome,
}

/// <summary>
/// Maps a realm's <see cref="AuthorityStrictness"/> and a <see cref="ViolationKind"/> to the concrete
/// <see cref="RelayAction"/> the relay should take. This is the single place strictness policy lives, so
/// interceptors stay free of level-specific branching. Fail-open by construction: anything below
/// <see cref="AuthorityStrictness.Enforce"/> resolves to <see cref="RelayAction.Forward"/>.
/// </summary>
public sealed class AuthorityPolicy
{
    public AuthorityStrictness Strictness { get; }

    public AuthorityPolicy(AuthorityStrictness strictness) => Strictness = strictness;

    /// <summary>The shared default policy: <see cref="AuthorityStrictness.Observe"/> (no-op).</summary>
    public static AuthorityPolicy Default { get; } = new(AuthorityStrictness.Observe);

    /// <summary>True once we log + tally violations (Warn and above). Observe is a pure forward.</summary>
    public bool CountsViolations => Strictness >= AuthorityStrictness.Warn;

    /// <summary>True when violations escalate to suppression/kick — Strict only, session-scoped.</summary>
    public bool Escalates => Strictness == AuthorityStrictness.Strict;

    /// <summary>The relay action for a violation of <paramref name="kind"/> at this strictness.</summary>
    public RelayAction ActionFor(ViolationKind kind)
    {
        if (Strictness < AuthorityStrictness.Enforce) return RelayAction.Forward;   // Observe/Warn: log only
        return kind switch
        {
            ViolationKind.Movement => RelayAction.Rewrite,   // snap-correct, don't discard
            ViolationKind.Outcome => RelayAction.Drop,        // zero-trust: reject the bad outcome
            _ => RelayAction.Forward,
        };
    }

    /// <summary>
    /// Parses the per-realm strictness from a <c>Realm.ExtraJson</c> string of the shape
    /// <c>{ "authority": { "strictness": "Enforce" } }</c>. Fail-open: null/empty/garbage JSON, a missing
    /// key, or an unknown level all resolve to <see cref="AuthorityStrictness.Observe"/> so a config typo
    /// can never accidentally start punishing players.
    /// </summary>
    public static AuthorityPolicy FromExtraJson(string? extraJson)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return Default;
        try
        {
            using var doc = JsonDocument.Parse(extraJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("authority", out var auth) &&
                auth.ValueKind == JsonValueKind.Object &&
                auth.TryGetProperty("strictness", out var s) &&
                s.ValueKind == JsonValueKind.String &&
                Enum.TryParse<AuthorityStrictness>(s.GetString(), ignoreCase: true, out var level))
            {
                return new AuthorityPolicy(level);
            }
        }
        catch (JsonException)
        {
            // Malformed JSON -> fail open to Observe.
        }
        return Default;
    }
}
