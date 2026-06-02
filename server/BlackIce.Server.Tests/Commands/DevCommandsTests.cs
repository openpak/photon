using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Authority;
using BlackIce.Server.LoadBalancing.Plugins;
using Xunit;

namespace BlackIce.Server.Tests.Commands;

/// <summary>
/// Covers the read-only developer/diagnostic console commands. The commands are reflected through a
/// <see cref="CommandRegistry"/> exactly as the live console drives them, over freshly-constructed
/// thread-safe registries (<see cref="RoomRegistry"/> + <see cref="RoomWorldStateRegistry"/>).
/// </summary>
public class DevCommandsTests
{
    private static (CommandRegistry reg, RoomRegistry rooms, RoomWorldStateRegistry worlds) Setup()
    {
        var rooms = new RoomRegistry();
        var worlds = new RoomWorldStateRegistry();
        var reg = new CommandRegistry().Register(new DevCommands(rooms, worlds));
        return (reg, rooms, worlds);
    }

    [Fact]
    public void Dump_on_an_unknown_realm_reports_no_such_realm()
    {
        var (reg, _, _) = Setup();

        Assert.True(reg.TryExecute("dump ghost-realm", PlayerLevel.Console, out var o));
        Assert.Contains("no such realm", o);
    }

    [Fact]
    public void Dump_lists_a_seeded_world_entity()
    {
        var (reg, rooms, worlds) = Setup();
        rooms.GetOrCreate("co-op");
        worlds.For("co-op").ObserveSpawn(1001, "SpiderEnemy", 10f, 2f, -5f);

        Assert.True(reg.TryExecute("dump co-op", PlayerLevel.Console, out var o));
        Assert.Contains("view 1001", o);
        Assert.Contains("SpiderEnemy", o);
        Assert.Contains("hasPos=True", o);
    }

    [Fact]
    public void Entities_filters_by_kind_substring()
    {
        var (reg, rooms, worlds) = Setup();
        rooms.GetOrCreate("co-op");
        worlds.For("co-op").ObserveSpawn(1, "SpiderEnemy", 0f, 0f, 0f);
        worlds.For("co-op").ObserveSpawn(2, "NetworkLootCube", 0f, 0f, 0f);

        Assert.True(reg.TryExecute("entities co-op spider", PlayerLevel.Console, out var o));
        Assert.Contains("SpiderEnemy", o);
        Assert.DoesNotContain("NetworkLootCube", o);
    }

    [Fact]
    public void Sysinfo_returns_a_nonempty_runtime_summary()
    {
        var (reg, _, _) = Setup();

        Assert.True(reg.TryExecute("sysinfo", PlayerLevel.Console, out var o));
        Assert.False(string.IsNullOrWhiteSpace(o));
        Assert.Contains("uptime=", o);
        Assert.Contains("cpus=", o);
    }

    [Fact]
    public void Find_matches_a_created_realm()
    {
        var (reg, rooms, _) = Setup();
        rooms.GetOrCreate("Black Ice — Co-op");

        Assert.True(reg.TryExecute("find Co-op", PlayerLevel.Console, out var o));
        Assert.Contains("Black Ice — Co-op", o);
    }

    [Fact]
    public void Dev_commands_require_mod_level()
    {
        var (reg, _, _) = Setup();

        reg.TryExecute("sysinfo", PlayerLevel.Player, out var o);
        Assert.Contains("requires Mod", o);
    }
}
