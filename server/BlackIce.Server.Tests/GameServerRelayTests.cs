using System.Collections.Generic;
using System.Net;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class GameServerRelayTests
{
    private static (GameServerHandler h, RoomRegistry reg, TestDb db) NewHandler()
    {
        var db = new TestDb();
        db.Context.Realms.Add(new Realm { Name = "co-op", IsEnabled = true });
        db.Context.SaveChanges();
        var reg = new RoomRegistry();
        var h = new GameServerHandler("s", reg, allowAnonymousLan: true, realms: new RealmService(db.Context));
        return (h, reg, db);
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

    private static OperationRequest Join() => new(226, new() { { 255, "co-op" } });
    private static OperationRequest GameplayRpc() => new(253, new()
    {
        { 244, (byte)200 },
        { 245, new Dictionary<object, object> { { (byte)0, 2001 }, { (byte)5, (byte)73 },
                 { (byte)4, new object[] { 1.0 } } } },
    });

    [Fact]
    public void Gameplay_rpc_from_one_actor_is_relayed_to_the_other()
    {
        var (h, _, db) = NewHandler();
        using (db)
        {
            var a = Peer(out var aRaised); var b = Peer(out var bRaised);
            h.OnOperationRequest(a, Join());
            h.OnOperationRequest(b, Join());
            aRaised.Clear(); bRaised.Clear();

            h.OnOperationRequest(a, GameplayRpc());

            Assert.Empty(aRaised);
            Assert.Single(bRaised);
            Assert.Equal(200, bRaised[0].Code);
        }
    }

    [Fact]
    public void Slash_motd_is_still_intercepted_not_relayed()
    {
        var (h, _, db) = NewHandler();
        using (db)
        {
            var a = Peer(out var aRaised); var b = Peer(out var bRaised);
            h.OnOperationRequest(a, Join());
            h.OnOperationRequest(b, Join());
            aRaised.Clear(); bRaised.Clear();

            var motd = new OperationRequest(253, new()
            {
                { 244, (byte)200 },
                { 245, new Dictionary<object, object> { { (byte)5, (byte)7 },
                         { (byte)4, new object[] { "/motd" } } } },
            });
            h.OnOperationRequest(a, motd);

            Assert.Single(aRaised);
            Assert.Equal(199, aRaised[0].Code);
            Assert.Empty(bRaised);
        }
    }

    [Fact]
    public void A_disconnected_actor_no_longer_receives_relayed_events()
    {
        var (h, _, db) = NewHandler();
        using (db)
        {
            var a = Peer(out _); var b = Peer(out var bRaised);
            h.OnOperationRequest(a, Join());
            h.OnOperationRequest(b, Join());
            h.OnDisconnect(b);
            bRaised.Clear();

            h.OnOperationRequest(a, GameplayRpc());
            Assert.Empty(bRaised);
        }
    }

    [Fact]
    public void Existing_actors_are_notified_when_a_new_actor_joins()
    {
        var (h, _, db) = NewHandler();
        using (db)
        {
            var a = Peer(out var aRaised);
            h.OnOperationRequest(a, Join());
            aRaised.Clear();

            var b = Peer(out _);
            h.OnOperationRequest(b, Join());

            Assert.Contains(aRaised, e => e.Code == 255
                && e.Parameters.TryGetValue(254, out var nr) && nr is int i && i == 2);
        }
    }

    [Fact]
    public void Remaining_actors_are_notified_when_an_actor_leaves()
    {
        var (h, _, db) = NewHandler();
        using (db)
        {
            var a = Peer(out var aRaised);
            var b = Peer(out _);
            h.OnOperationRequest(a, Join());
            h.OnOperationRequest(b, Join());
            aRaised.Clear();

            h.OnDisconnect(b);

            Assert.Contains(aRaised, e => e.Code == 254
                && e.Parameters.TryGetValue(254, out var nr) && nr is int i && i == 2);
        }
    }

    [Fact]
    public void Unreliable_gameplay_event_is_relayed_unreliably()
    {
        var (h, _, db) = NewHandler();
        using (db)
        {
            var a = Peer(out _); var b = PeerClassified(out var bRaised);
            h.OnOperationRequest(a, Join());
            h.OnOperationRequest(b, Join());
            a.CurrentInboundUnreliable = true;
            bRaised.Clear();

            h.OnOperationRequest(a, new OperationRequest(253, new()
            {
                { 244, (byte)201 },
                { 245, new Dictionary<object, object> { { (byte)0, 2001 } } },
            }));

            Assert.Single(bRaised);
            Assert.True(bRaised[0].unreliable);
        }
    }

    private static PeerConnection PeerClassified(out List<(EventData ev, bool unreliable)> raised)
    {
        var captured = new List<(EventData, bool)>();
        raised = captured;
        var p = new PeerConnection("GameServer", new IPEndPoint(IPAddress.Loopback, 0), new Null(), (_, _) => { });
        p.OnRaisedClassified = (ev, unrel) => captured.Add((ev, unrel));
        return p;
    }

    private static OperationRequest SetProps(Dictionary<object, object> props, int? actorNr, bool? broadcast) =>
        new(252, BuildSetPropsParams(props, actorNr, broadcast));

    private static Dictionary<byte, object> BuildSetPropsParams(Dictionary<object, object> props, int? actorNr, bool? broadcast)
    {
        var p = new Dictionary<byte, object> { { 251, props } };
        if (actorNr is int nr) p[254] = nr;
        if (broadcast is bool b) p[250] = b;
        return p;
    }

    [Fact]
    public void Broadcast_set_player_properties_is_relayed_to_other_actor_as_event_253()
    {
        var (h, _, db) = NewHandler();
        using (db)
        {
            var a = Peer(out var aRaised); var b = Peer(out var bRaised);
            h.OnOperationRequest(a, Join());   // actor 1
            h.OnOperationRequest(b, Join());   // actor 2
            aRaised.Clear(); bRaised.Clear();

            // Actor 1 broadcasts its appearance/player properties.
            var props = new Dictionary<object, object> { { "PlayerModelIndex", 4 }, { "BackHoloIconIndex", 2 } };
            h.OnOperationRequest(a, SetProps(props, actorNr: 1, broadcast: true));

            // The OTHER actor (2) sees an EvPropertiesChanged (253); the sender (1) does not.
            Assert.Empty(aRaised);
            var ev = Assert.Single(bRaised);
            Assert.Equal(253, ev.Code);
            // Target-actor key (253) identifies whose properties these are.
            Assert.True(ev.Parameters.TryGetValue(253, out var tgt) && tgt is int t && t == 1);
            // Properties key (251) carries the changed values.
            Assert.True(ev.Parameters.TryGetValue(251, out var pp) && pp is System.Collections.IDictionary d
                && d.Contains("PlayerModelIndex") && (int)d["PlayerModelIndex"]! == 4
                && d.Contains("BackHoloIconIndex"));
        }
    }

    [Fact]
    public void Broadcast_set_properties_still_acks_the_sender_with_rc_zero()
    {
        var (h, _, db) = NewHandler();
        using (db)
        {
            h.OnOperationRequest(Peer(out _), Join());

            // SetProperties returns the sender's ack directly (the op handler forwards it via SendResponse).
            var resp = h.SetProperties("co-op", senderActor: 1,
                SetProps(new() { { "PlayerModelIndex", 1 } }, actorNr: 1, broadcast: true));

            Assert.Equal(252, resp.OperationCode);
            Assert.Equal(0, resp.ReturnCode);
        }
    }

    [Fact]
    public void Set_properties_without_broadcast_persists_but_is_not_relayed()
    {
        var (h, reg, db) = NewHandler();
        using (db)
        {
            var a = Peer(out _); var b = Peer(out var bRaised);
            h.OnOperationRequest(a, Join());
            h.OnOperationRequest(b, Join());
            bRaised.Clear();

            h.OnOperationRequest(a, SetProps(new() { { "PlayerModelIndex", 9 } }, actorNr: 1, broadcast: false));

            // Persisted but no event fanned out to the other actor.
            Assert.Empty(bRaised);
            Assert.Equal(9, reg.Find("co-op")!.ActorProperties(1)["PlayerModelIndex"]);
        }
    }
}
