using BlackIce.Server.Data;

namespace BlackIce.Server.Core;

/// <summary>
/// Tunables for the server-authority / anti-cheat validators on the relay. Detection-only by default
/// (<see cref="Enforce"/> false): violations are logged and the event is still forwarded, matching the
/// project's detect-first posture. Set <see cref="Enforce"/> once thresholds are tuned against live play
/// to also drop the offending event. Thresholds are generous to avoid false positives on legitimate play.
/// </summary>
public sealed class AnticheatOptions
{
    /// <summary>When true, behavioral validators drop the offending event instead of only logging it.</summary>
    public bool Enforce { get; set; } = false;

    /// <summary>Minimum account level a player must hold to be EXEMPT from movement enforcement — i.e. allowed
    /// to use the client fly/speed plugin. The exemption is honored only for a Steam-VERIFIED identity
    /// (<see cref="PeerConnection.IsVerified"/>); an unverified/asserted identity is never exempt, no matter
    /// what level its (spoofable) SteamID claims. Default <see cref="PlayerLevel.Admin"/>.</summary>
    public PlayerLevel AdminExemptLevel { get; set; } = PlayerLevel.Admin;

    // --- Per-hit / per-step ceilings ---
    /// <summary>Single-hit damage ceiling (a DamagePacket above this is suspicious).</summary>
    public float MaxDamagePerHit { get; set; } = 100_000f;
    /// <summary>Implied movement speed ceiling, units/second between two position updates.</summary>
    public float MaxSpeedUnitsPerSecond { get; set; } = 200f;
    /// <summary>Single-step position jump ceiling, units (teleport detection independent of timing).</summary>
    public float MaxTeleportDistance { get; set; } = 500f;

    // --- Sliding-window rate ceilings (the "too many X in Y seconds" checks) ---
    /// <summary>Length of the sliding window for the per-actor rate checks below.</summary>
    public double RateWindowSeconds { get; set; } = 1.0;
    /// <summary>Per-actor relayed-event ceiling per window (flood / DoS).</summary>
    public int MaxEventsPerWindow { get; set; } = 200;
    /// <summary>Per-actor damage-RPC ceiling per window (rapid-fire / aimbot fire rate).</summary>
    public int MaxHitsPerWindow { get; set; } = 30;
    /// <summary>Per-actor cumulative damage ceiling per window.</summary>
    public float MaxDamagePerWindow { get; set; } = 5_000f;
    /// <summary>Per-actor headshot ceiling per window (only checked when <see cref="HeadshotFlagOffset"/> is set).</summary>
    public int MaxHeadshotsPerWindow { get; set; } = 8;

    /// <summary>
    /// Byte offset of the headshot flag inside the game's DamagePacket custom type, if known. The exact
    /// layout is game-specific and must be confirmed from a local capture; until set, headshot-rate
    /// checking is inert (the rest of the rate checks still run). A headshot is counted when
    /// <c>(packet[HeadshotFlagOffset] &amp; HeadshotFlagMask) != 0</c>.
    /// </summary>
    public int? HeadshotFlagOffset { get; set; } = null;

    /// <summary>
    /// Bit mask applied at <see cref="HeadshotFlagOffset"/> to isolate the headshot/weak-point bit from
    /// other flags sharing that byte. Default 0xFF = "any non-zero byte". For Black Ice's DamagePacket
    /// the "combined" bitfield packs Crit=bit0 and WeakPoint=bit1, so offset 39 + mask 0x02 isolates
    /// weak-point hits — PENDING live-capture confirmation (see docs/protocol/live-verification.md).
    /// </summary>
    public byte HeadshotFlagMask { get; set; } = 0xFF;

    public TimeSpan RateWindow => TimeSpan.FromSeconds(RateWindowSeconds);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (RateWindowSeconds <= 0) errors.Add("Anticheat.RateWindowSeconds must be greater than 0.");
        if (MaxEventsPerWindow <= 0) errors.Add("Anticheat.MaxEventsPerWindow must be greater than 0.");
        if (MaxHitsPerWindow <= 0) errors.Add("Anticheat.MaxHitsPerWindow must be greater than 0.");
        if (MaxSpeedUnitsPerSecond <= 0) errors.Add("Anticheat.MaxSpeedUnitsPerSecond must be greater than 0.");
        if (MaxTeleportDistance <= 0) errors.Add("Anticheat.MaxTeleportDistance must be greater than 0.");
        if (HeadshotFlagOffset is < 0) errors.Add("Anticheat.HeadshotFlagOffset must be non-negative.");
        return errors;
    }
}
