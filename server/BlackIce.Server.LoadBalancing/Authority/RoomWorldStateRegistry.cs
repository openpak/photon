using System.Collections.Concurrent;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// One shared <see cref="RoomWorldState"/> per room, so every consumer sees the same authoritative shadow:
/// the authority plugin's <c>WorldStateObserver</c> WRITES it from the relay (spawn/destroy/position), and
/// world-aware playerbots READ it to find real, reachable targets. Without this shared registry each
/// per-room interceptor instance would own a private world-state the bots couldn't see.
///
/// <para>A process-wide singleton (DI). The backing map is concurrent: created on the listener thread when a
/// room first sees traffic, read from the same thread by the bot tick — concurrent as defense-in-depth,
/// matching the rest of the authority layer.</para>
/// </summary>
public sealed class RoomWorldStateRegistry
{
    private readonly ConcurrentDictionary<string, RoomWorldState> _byRoom = new();

    /// <summary>The shared world-state for <paramref name="room"/>, created on first use.</summary>
    public RoomWorldState For(string room) => _byRoom.GetOrAdd(room, _ => new RoomWorldState());

    /// <summary>The world-state for <paramref name="room"/> if one exists, else null (no allocation).</summary>
    public RoomWorldState? Find(string room) => _byRoom.TryGetValue(room, out var w) ? w : null;
}
