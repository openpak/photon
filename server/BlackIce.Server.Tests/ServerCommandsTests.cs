using System.Collections.Generic;
using System.Net;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Bots;
using Xunit;

namespace BlackIce.Server.Tests;

public class ServerCommandsTests
{
    private static (CommandRegistry reg, RoomRegistry rooms, AdminActionQueue admin) Setup()
    {
        var rooms = new RoomRegistry();
        var admin = new AdminActionQueue();
        var reg = new CommandRegistry().Register(new ServerCommands(rooms, admin, new BotManager(), new BotIdentityGenerator()));
        return (reg, rooms, admin);
    }

    private static RoomSession RoomWith(RoomRegistry rooms, string name, params (int actor, PeerConnection peer)[] members)
    {
        rooms.GetOrCreate(name);
        var s = rooms.Session(name);
        foreach (var (actor, peer) in members) s.Join(actor, peer);
        return s;
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
    public void Say_broadcasts_a_server_message_to_the_room_after_drain()
    {
        var (reg, rooms, admin) = Setup();
        RoomWith(rooms, "co-op", (1, Peer(out var raised)));

        Assert.True(reg.TryExecute("say co-op hello world", PlayerLevel.Console, out var o));
        Assert.Contains("queued", o);
        Assert.Empty(raised);                 // queued, not sent yet
        admin.Drain();                        // listener-thread step

        var ev = Assert.Single(raised);
        Assert.Equal(199, ev.Code);           // ServerMessage
        Assert.Equal("hello world", ev.Parameters[245]);
    }

    [Fact]
    public void Tell_messages_a_single_actor()
    {
        var (reg, rooms, admin) = Setup();
        RoomWith(rooms, "co-op", (1, Peer(out var a)), (2, Peer(out var b)));

        reg.TryExecute("tell co-op 2 just you", PlayerLevel.Console, out _);
        admin.Drain();

        Assert.Empty(a);
        Assert.Equal("just you", Assert.Single(b).Parameters[245]);
    }

    [Fact]
    public void Setprop_sets_a_game_property_and_broadcasts_event_253()
    {
        var (reg, rooms, admin) = Setup();
        RoomWith(rooms, "co-op", (1, Peer(out var raised)));

        reg.TryExecute("setprop co-op PVP true", PlayerLevel.Console, out _);
        admin.Drain();

        Assert.Equal(true, rooms.Find("co-op")!.GameProperties["PVP"]);
        Assert.Equal(253, Assert.Single(raised).Code);
    }

    [Fact]
    public void Kick_hard_disconnects_the_actor_and_notifies_both_sides()
    {
        var (reg, rooms, admin) = Setup();
        var kicked = Peer(out var aRaised);
        var s = RoomWith(rooms, "co-op", (1, kicked), (2, Peer(out var bRaised)));

        reg.TryExecute("kick co-op 1 cheating", PlayerLevel.Console, out _);
        admin.Drain();

        Assert.Equal(1, s.Count);                                  // actor 1 gone from the relay
        Assert.True(kicked.WantsDisconnect);                       // and flagged for transport teardown
        Assert.Contains(aRaised, e => e.Code == 199);              // kicked player got the reason
        Assert.Contains(bRaised, e => e.Code == 254);              // others got a Leave for actor 1
    }

    [Fact]
    public void Rooms_and_stats_report_live_membership()
    {
        var (reg, rooms, _) = Setup();
        RoomWith(rooms, "co-op", (1, Peer(out _)), (2, Peer(out _)));

        reg.TryExecute("rooms", PlayerLevel.Console, out var roomsOut);
        Assert.Contains("co-op (2)", roomsOut);

        reg.TryExecute("stats", PlayerLevel.Console, out var statsOut);
        Assert.Contains("players=2", statsOut);
    }

    [Fact]
    public void Loglevel_changes_the_active_level()
    {
        var (reg, _, _) = Setup();
        var prev = Log.Level;
        try
        {
            reg.TryExecute("loglevel debug", PlayerLevel.Console, out _);
            Assert.Equal(LogLevel.Debug, Log.Level);
        }
        finally { Log.Level = prev; }
    }

    [Fact]
    public void Say_requires_mod_level()
    {
        var (reg, _, _) = Setup();
        reg.TryExecute("say co-op hi", PlayerLevel.Player, out var o);
        Assert.Contains("requires Mod", o);
    }

    [Fact]
    public void Setprop_requires_admin_level()
    {
        var (reg, _, _) = Setup();
        reg.TryExecute("setprop co-op PVP true", PlayerLevel.Mod, out var o);
        Assert.Contains("requires Admin", o);
    }

    [Fact]
    public void Raise_sends_an_arbitrary_event_to_the_room()
    {
        var (reg, rooms, admin) = Setup();
        RoomWith(rooms, "co-op", (1, Peer(out var raised)));

        reg.TryExecute("raise co-op 42 payload", PlayerLevel.Console, out var o);
        Assert.Contains("queued", o);
        admin.Drain();

        var ev = Assert.Single(raised);
        Assert.Equal(42, ev.Code);
        Assert.Equal("payload", ev.Parameters[245]);
    }

    [Fact]
    public void Unknown_room_is_reported_not_crashed()
    {
        var (reg, _, _) = Setup();
        reg.TryExecute("say ghost hi there", PlayerLevel.Console, out var o);
        Assert.Contains("no such room", o);
    }
}
