using System.Collections.Generic;
using System.Net;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Authority;
using BlackIce.Server.LoadBalancing.Plugins;
using Xunit;

namespace BlackIce.Server.Tests.Commands;

/// <summary>
/// Covers the game-master manipulation commands (damage/kill/xp/destroy): each must validate the target
/// viewId against the room world-state, refuse an unknown view, and otherwise queue a relay that reaches
/// the room after <see cref="AdminActionQueue.Drain"/>. Wire-byte shapes are validated against the Photon
/// oracle elsewhere — here we assert only that the right event WAS (or was NOT) relayed.
/// </summary>
public class GameMasterCommandsTests
{
    private static (CommandRegistry reg, RoomRegistry rooms, AdminActionQueue admin, RoomWorldStateRegistry worlds) Setup()
    {
        var rooms = new RoomRegistry();
        var admin = new AdminActionQueue();
        var worlds = new RoomWorldStateRegistry();
        var reg = new CommandRegistry().Register(new GameMasterCommands(rooms, admin, worlds));
        return (reg, rooms, admin, worlds);
    }

    private static void RoomWith(RoomRegistry rooms, string name, params (int actor, PeerConnection peer)[] members)
    {
        rooms.GetOrCreate(name);
        var s = rooms.Session(name);
        foreach (var (actor, peer) in members) s.Join(actor, peer);
    }

    private static PeerConnection Peer(out List<EventData> raised)
    {
        var captured = new List<EventData>();
        raised = captured;
        var p = new PeerConnection("GameServer", new IPEndPoint(IPAddress.Loopback, 0), new Null(), (_, _) => { });
        p.OnRaised = captured.Add;
        return p;
    }

    private sealed class Null : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    [Fact]
    public void Damage_relays_an_rpc_to_a_known_viewId_after_drain()
    {
        var (reg, rooms, admin, worlds) = Setup();
        RoomWith(rooms, "co-op", (1, Peer(out var raised)));
        worlds.For("co-op").ObserveSpawn(5005, "SpiderEnemy", 1f, 0f, 1f);

        Assert.True(reg.TryExecute("damage co-op 5005 25", PlayerLevel.Console, out var o));
        Assert.Contains("queued", o);
        Assert.Empty(raised);     // queued onto the listener thread, not sent inline
        admin.Drain();

        Assert.NotEmpty(raised);  // shape is validated elsewhere; here: the event reached the room
    }

    [Fact]
    public void Kill_xp_and_destroy_all_relay_to_a_known_viewId()
    {
        var (reg, rooms, admin, worlds) = Setup();
        RoomWith(rooms, "co-op", (1, Peer(out var raised)));
        worlds.For("co-op").ObserveSpawn(5005, "SpiderEnemy", 1f, 0f, 1f);

        reg.TryExecute("kill co-op 5005", PlayerLevel.Console, out _);
        reg.TryExecute("xp co-op 5005 100", PlayerLevel.Console, out _);
        reg.TryExecute("destroy co-op 5005", PlayerLevel.Console, out _);
        admin.Drain();

        Assert.Equal(3, raised.Count);
    }

    [Fact]
    public void Unknown_viewId_is_reported_and_relays_nothing()
    {
        var (reg, rooms, admin, worlds) = Setup();
        RoomWith(rooms, "co-op", (1, Peer(out var raised)));
        worlds.For("co-op").ObserveSpawn(5005, "SpiderEnemy", 1f, 0f, 1f);

        Assert.True(reg.TryExecute("damage co-op 9999 25", PlayerLevel.Console, out var o));
        Assert.Contains("view not found", o);
        admin.Drain();

        Assert.Empty(raised);     // nothing queued for an unobserved entity
    }

    [Fact]
    public void Unknown_realm_is_reported_not_crashed()
    {
        var (reg, _, _, _) = Setup();
        reg.TryExecute("damage ghost 5005 25", PlayerLevel.Console, out var o);
        Assert.Contains("no such realm", o);
    }

    [Fact]
    public void Damage_requires_admin_level()
    {
        var (reg, _, _, _) = Setup();
        reg.TryExecute("damage co-op 5005 25", PlayerLevel.Mod, out var o);
        Assert.Contains("requires Admin", o);
    }
}
