using System.Collections.Generic;
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing.Authority;

namespace BlackIce.Server.LoadBalancing.Bots;

/// <summary>One step of bot decision-making, producing where the bot now is. Movement first; combat later.</summary>
public readonly record struct BotPositionUpdate(float X, float Y, float Z);

/// <summary>A move-only bot: decides where it is next, with no world awareness. (e.g. <see cref="WanderBehavior"/>.)</summary>
public interface IBotBehavior
{
    BotPositionUpdate Tick();
}

/// <summary>The result of a world-aware bot's think: where it moves to, plus any RPC/events it emits this
/// tick (e.g. a damage/hack/loot RPC), and a short human label for logging.</summary>
public readonly record struct BotStep(BotPositionUpdate Position, IReadOnlyList<EventData> Actions, string Label);

/// <summary>
/// A world-aware bot ("brain"): given the room's authoritative <see cref="RoomWorldState"/>, it picks a real
/// target (enemy / hack node / loot the master has spawned), steps toward it, and emits the matching captured
/// RPC when in range — acting like a player rather than a blind wanderer. Extends <see cref="IBotBehavior"/>
/// so a move-only fallback still works where no world-state is available.
/// </summary>
public interface IBotBrain : IBotBehavior
{
    BotStep Think(RoomWorldState world);
}
