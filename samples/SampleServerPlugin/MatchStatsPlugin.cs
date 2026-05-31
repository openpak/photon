using System.Collections.Concurrent;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Plugins;

namespace SampleServerPlugin;

/// <summary>
/// A complete, harmless example of an external server plugin — the kind a community author would write
/// and drop into <c>server-plugins/</c> as a single DLL. It exercises all three contribution points
/// without changing gameplay:
/// <list type="bullet">
///   <item>an <b>interceptor</b> that counts relayed events per room and always forwards (never drops);</item>
///   <item><b>join/leave hooks</b> that track live occupancy and log it;</item>
///   <item>a <b>console command</b> (<c>matchstats</c>) that prints the collected per-room tallies.</item>
/// </list>
/// Because it only ever returns <see cref="RelayVerdict.Forward"/>, enabling or disabling it is purely
/// observational — a safe thing to demo <c>plugin load</c> / <c>plugin unload</c> against on a live server.
/// </summary>
public sealed class MatchStatsPlugin : IServerPlugin
{
    public string Name => "matchstats";
    public string Description => "Sample external plugin: per-room event counts + live occupancy (observational, never drops).";

    // Shared across the per-room interceptor instances, the hooks, and the console command.
    private readonly MatchStats _stats = new();

    public void Configure(PluginBuilder builder)
    {
        builder
            .AddInterceptor(() => new CountingInterceptor(_stats))
            .AddCommands(new MatchStatsCommands(_stats))
            .OnActorJoined(ctx =>
            {
                int n = _stats.Joined(ctx.RoomName);
                Log.Info("MatchStats", $"\"{ctx.RoomName}\": actor {ctx.Actor} joined — {n} present");
            })
            .OnActorLeft(ctx =>
            {
                int n = _stats.Left(ctx.RoomName);
                Log.Info("MatchStats", $"\"{ctx.RoomName}\": actor {ctx.Actor} left — {n} present");
            });
    }
}

/// <summary>Thread-safe per-room tallies shared by the plugin's contributions.</summary>
internal sealed class MatchStats
{
    private readonly ConcurrentDictionary<string, long> _events = new();
    private readonly ConcurrentDictionary<string, int> _present = new();

    public void CountEvent(string room) => _events.AddOrUpdate(room, 1, (_, v) => v + 1);
    public int Joined(string room) => _present.AddOrUpdate(room, 1, (_, v) => v + 1);
    public int Left(string room) => _present.AddOrUpdate(room, 0, (_, v) => v > 0 ? v - 1 : 0);

    public IReadOnlyList<(string Room, long Events, int Present)> Snapshot()
    {
        var rooms = _events.Keys.Union(_present.Keys);
        return rooms
            .Select(r => (r, _events.TryGetValue(r, out var e) ? e : 0, _present.TryGetValue(r, out var p) ? p : 0))
            .OrderBy(t => t.r)
            .ToList();
    }
}

/// <summary>Counts every event it sees for the room, then forwards unchanged.</summary>
internal sealed class CountingInterceptor : IEventInterceptor
{
    private readonly MatchStats _stats;
    public CountingInterceptor(MatchStats stats) => _stats = stats;

    public RelayVerdict Intercept(EventContext ctx)
    {
        _stats.CountEvent(ctx.RoomName);
        return RelayVerdict.Forward(ctx.Event);
    }
}

/// <summary>Console command exposing the sample plugin's tallies (Admin).</summary>
internal sealed class MatchStatsCommands
{
    private readonly MatchStats _stats;
    public MatchStatsCommands(MatchStats stats) => _stats = stats;

    [ConsoleCommand("matchstats", MinLevel = PlayerLevel.Admin)]
    private string Show(CommandLine line)
    {
        var rows = _stats.Snapshot();
        return rows.Count == 0
            ? "matchstats: no rooms seen yet"
            : "matchstats:\n" + string.Join('\n', rows.Select(r => $"  {r.Room}: {r.Events} events, {r.Present} present"));
    }
}
