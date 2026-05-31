using System.Collections.Concurrent;
using System.Linq;

namespace BlackIce.Server.LoadBalancing;

/// <summary>Server-enforced game mode for a room.</summary>
public enum GameMode
{
    /// <summary>No teams; player-vs-player damage is unrestricted (the realm's Pvp flag still applies client-side).</summary>
    FreeForAll,
    /// <summary>Two balanced teams; same-team (friendly-fire) damage is dropped server-side.</summary>
    TeamVsTeam,
    /// <summary>Co-op / PvE; all player-vs-player damage is dropped (players can only hurt enemies).</summary>
    Coop,
}

/// <summary>
/// Tracks each room's <see cref="GameMode"/> and its per-actor team assignments, and decides whether a
/// player-vs-player damage event should be blocked. This is how a free-for-all realm becomes Team-vs-Team
/// or Co-op purely server-side: the server assigns teams (broadcast via the standard "Team" player
/// property the client already renders) and the relay drops disallowed damage RPCs before they reach the
/// target — no client modification required.
///
/// Mutated only from the Game listener thread (join/leave + the relay), but uses concurrent maps as
/// defense-in-depth and so inspection from other threads is safe.
/// </summary>
public sealed class GameModeRegistry
{
    public const int TeamCount = 2;

    private readonly ConcurrentDictionary<string, GameMode> _mode = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, int>> _teams = new();   // room -> actor -> team

    public GameMode ModeOf(string room) => _mode.TryGetValue(room, out var m) ? m : GameMode.FreeForAll;
    public void SetMode(string room, GameMode mode) => _mode[room] = mode;

    /// <summary>Assigns the actor to the smaller team (balanced) and records it; returns the team index.</summary>
    public int AssignTeam(string room, int actor)
    {
        var teams = _teams.GetOrAdd(room, _ => new ConcurrentDictionary<int, int>());
        int team = Enumerable.Range(0, TeamCount)
            .OrderBy(t => teams.Values.Count(v => v == t))
            .First();
        teams[actor] = team;
        return team;
    }

    public int? TeamOf(string room, int actor) =>
        _teams.TryGetValue(room, out var teams) && teams.TryGetValue(actor, out var team) ? team : null;

    public void Remove(string room, int actor)
    {
        if (_teams.TryGetValue(room, out var teams)) teams.TryRemove(actor, out _);
    }

    /// <summary>
    /// True when player-vs-player damage from <paramref name="attacker"/> to <paramref name="target"/>
    /// must be dropped under the room's mode. Damage to a non-player (enemy/scene/unknown) is never
    /// blocked here; FreeForAll never blocks; Coop blocks all player targets; TeamVsTeam blocks same-team.
    /// </summary>
    public bool BlocksDamage(string room, int attacker, int target)
    {
        var mode = ModeOf(room);
        if (mode == GameMode.FreeForAll) return false;
        if (TeamOf(room, target) is not int targetTeam) return false;   // target isn't a tracked player
        if (mode == GameMode.Coop) return true;                         // PvE: no player-vs-player damage
        return TeamOf(room, attacker) is int attackerTeam && attackerTeam == targetTeam;  // TvT: friendly fire off
    }

    /// <summary>Parses a realm's Mode string; unknown/empty falls back to FreeForAll.</summary>
    public static GameMode Parse(string? mode) =>
        Enum.TryParse<GameMode>(mode, ignoreCase: true, out var m) ? m : GameMode.FreeForAll;
}
