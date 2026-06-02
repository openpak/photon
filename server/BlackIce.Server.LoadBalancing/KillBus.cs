using System;

namespace BlackIce.Server.LoadBalancing;

/// <summary>A server-modelled kill: the room, who got the kill, who died, and the killer's running streak.</summary>
public readonly record struct KillNotice(string Room, int Killer, int Victim, int KillerStreak);

/// <summary>A server-detected real death: the room and the victim actor. No killer — the death RPC
/// (KilledPlayerRemote) carries only the victim. Kill credit is a future, separately-captured concern.</summary>
public readonly record struct DeathNotice(string Room, int Victim);

/// <summary>
/// A tiny in-process pub/sub between the kill-modelling plugin and the consumers that score on it. The
/// <c>killfeed</c> plugin detects a kill once (from relayed damage) and <see cref="Publish"/>es it; the
/// <c>arena</c> plugin subscribes to <see cref="Killed"/> to keep team scores. When a match ends, arena
/// calls <see cref="RequestReset"/> so killfeed clears its per-player tallies for that room and the next
/// round starts clean. Registered as a singleton in DI and resolved by both plugins from
/// <c>PluginBuilder.Services</c>, so no plugin holds a direct reference to another.
/// </summary>
public sealed class KillBus
{
    /// <summary>Raised when the kill model credits a kill. Handlers run on the Game listener thread.</summary>
    public event Action<KillNotice>? Killed;

    /// <summary>Raised to ask kill-trackers to clear all per-player state for a room (round/match reset).</summary>
    public event Action<string>? RoomReset;

    /// <summary>Raised when the relay detects a real player death. Handlers run on the Game listener thread.</summary>
    public event Action<DeathNotice>? Died;

    public void PublishDeath(DeathNotice notice) => Died?.Invoke(notice);

    public void Publish(KillNotice notice) => Killed?.Invoke(notice);
    public void RequestReset(string room) => RoomReset?.Invoke(room);
}
