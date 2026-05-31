using System;
using System.Collections.Concurrent;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>
/// Built-in plugin providing <b>spawn protection</b>: for a configurable window after a player joins a room,
/// the relay drops any damage aimed at them, so they can't be spawn-camped — enforced entirely server-side
/// (the attacker's client still thinks it landed the hit; the victim simply never receives the damage RPC).
/// <b>Inert by default</b> (0-second window); an admin arms it live with <c>spawnguard seconds &lt;n&gt;</c>.
/// </summary>
public sealed class SpawnGuardPlugin : IServerPlugin
{
    public string Name => "spawnguard";
    public string Description => "Spawn protection: drops incoming damage to a player for a grace window after they join. Off until armed.";

    public void Configure(PluginBuilder builder)
    {
        var state = new SpawnGuardState();
        builder
            .AddInterceptor(() => new SpawnGuardInterceptor(state))
            .OnActorJoined(ctx => state.Joined(ctx.RoomName, ctx.Actor))
            .OnActorLeft(ctx => state.Left(ctx.RoomName, ctx.Actor))
            .AddCommands(new SpawnGuardCommands(state));
    }
}

/// <summary>Shared spawn-protection state: the grace window plus each player's join time. Join times are
/// written from join/leave hooks and read by the relay (both on the Game listener thread), and the window
/// is written by the console thread — a <see cref="ConcurrentDictionary{TKey,TValue}"/> and an atomic int
/// cover that.</summary>
internal sealed class SpawnGuardState
{
    private readonly ConcurrentDictionary<(string Room, int Actor), DateTime> _joinedAt = new();
    public int Seconds;   // 0 = disabled

    public void Joined(string room, int actor) => _joinedAt[(room, actor)] = DateTime.UtcNow;
    public void Left(string room, int actor) => _joinedAt.TryRemove((room, actor), out _);

    /// <summary>True if <paramref name="actor"/> in <paramref name="room"/> is still inside its spawn-protection window.</summary>
    public bool IsProtected(string room, int actor)
    {
        int secs = Seconds;
        return secs > 0
            && _joinedAt.TryGetValue((room, actor), out var at)
            && (DateTime.UtcNow - at).TotalSeconds < secs;
    }
}

/// <summary>Drops a damage RPC whose target player is still spawn-protected; forwards everything else.</summary>
internal sealed class SpawnGuardInterceptor : IEventInterceptor
{
    private const int MaxViewIdsPerActor = 1000;   // viewID block size: viewID / 1000 = owning actor
    private readonly SpawnGuardState _state;
    public int ProtectedCount { get; private set; }

    public SpawnGuardInterceptor(SpawnGuardState state) => _state = state;

    public RelayVerdict Intercept(EventContext ctx)
    {
        if (_state.Seconds <= 0) return RelayVerdict.Forward(ctx.Event);

        var info = PunRpcInfo.From(ctx.Event);
        if (info is not { DamageValue: not null } dmg) return RelayVerdict.Forward(ctx.Event);

        int targetActor = dmg.ViewId / MaxViewIdsPerActor;
        if (targetActor == ctx.SenderActor || !_state.IsProtected(ctx.RoomName, targetActor))
            return RelayVerdict.Forward(ctx.Event);

        ProtectedCount++;
        Log.Info("SpawnGuard", $"\"{ctx.RoomName}\": dropped damage from actor {ctx.SenderActor} to spawn-protected actor {targetActor}");
        return RelayVerdict.Drop();
    }
}

/// <summary>Console commands to inspect and arm spawn protection live (Admin).</summary>
internal sealed class SpawnGuardCommands
{
    private readonly SpawnGuardState _state;
    public SpawnGuardCommands(SpawnGuardState state) => _state = state;

    [ConsoleCommand("spawnguard", Usage = "[seconds <n> | off]", MinLevel = PlayerLevel.Admin)]
    private string Cmd(CommandLine line)
    {
        if (line.Parts.Count == 1)
            return _state.Seconds > 0 ? $"spawnguard: {_state.Seconds}s grace window" : "spawnguard: off";

        var verb = line.Parts[1].ToLowerInvariant();
        if (verb == "off") { _state.Seconds = 0; return "spawnguard: off"; }
        if (verb == "seconds" && line.Parts.Count >= 3 && int.TryParse(line.Parts[2], out var n) && n >= 0)
        {
            _state.Seconds = n;
            return n > 0 ? $"spawnguard: armed — {n}s grace window after join" : "spawnguard: off";
        }
        return "usage: spawnguard [seconds <n> | off]";
    }
}
