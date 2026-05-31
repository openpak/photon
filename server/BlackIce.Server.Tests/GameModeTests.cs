using System.Collections.Generic;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Authority;
using Xunit;

namespace BlackIce.Server.Tests;

public class GameModeTests
{
    // A player-target damage RPC: target is the player owning viewId = targetActor*1000+1.
    private static EventData PlayerDamage(int targetActor)
    {
        var dp = new byte[41];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(dp.AsSpan(0), 25f);
        return new EventData(200, new()
        {
            { 245, new Dictionary<object, object>
                {
                    { (byte)0, targetActor * 1000 + 1 },          // viewId -> owner = targetActor
                    { (byte)3, "TakeDamage" },
                    { (byte)4, new object[] { new PhotonCustomData(68, dp) } },
                } },
        });
    }

    // --- GameModeRegistry policy -----------------------------------------------------------------

    [Fact]
    public void FreeForAll_blocks_nothing()
    {
        var m = new GameModeRegistry();
        m.SetMode("r", GameMode.FreeForAll);
        m.AssignTeam("r", 1); m.AssignTeam("r", 2);
        Assert.False(m.BlocksDamage("r", 1, 2));
    }

    [Fact]
    public void TeamVsTeam_blocks_same_team_allows_cross_team()
    {
        var m = new GameModeRegistry();
        m.SetMode("r", GameMode.TeamVsTeam);
        int t1 = m.AssignTeam("r", 1);   // team 0
        int t2 = m.AssignTeam("r", 2);   // team 1 (balanced)
        int t3 = m.AssignTeam("r", 3);   // team 0
        Assert.NotEqual(t1, t2);
        Assert.Equal(t1, t3);
        Assert.True(m.BlocksDamage("r", 1, 3));    // same team -> friendly fire blocked
        Assert.False(m.BlocksDamage("r", 1, 2));   // cross team -> allowed
    }

    [Fact]
    public void Coop_blocks_all_player_damage_but_not_enemies()
    {
        var m = new GameModeRegistry();
        m.SetMode("r", GameMode.Coop);
        m.AssignTeam("r", 1); m.AssignTeam("r", 2);
        Assert.True(m.BlocksDamage("r", 1, 2));    // players can't hurt each other
        Assert.False(m.BlocksDamage("r", 1, 9));   // actor 9 isn't a tracked player (enemy/scene) -> allowed
    }

    [Fact]
    public void Leaving_frees_the_team_slot()
    {
        var m = new GameModeRegistry();
        m.SetMode("r", GameMode.TeamVsTeam);
        m.AssignTeam("r", 1);
        Assert.NotNull(m.TeamOf("r", 1));
        m.Remove("r", 1);
        Assert.Null(m.TeamOf("r", 1));
    }

    // --- TeamDamageInterceptor (relay enforcement) -----------------------------------------------

    [Fact]
    public void Interceptor_drops_friendly_fire_in_team_mode()
    {
        var m = new GameModeRegistry();
        m.SetMode("co-op", GameMode.TeamVsTeam);
        m.AssignTeam("co-op", 1); m.AssignTeam("co-op", 2); m.AssignTeam("co-op", 3);  // 1&3 same team
        var i = new TeamDamageInterceptor(m);

        Assert.Equal(RelayAction.Drop, i.Intercept(new EventContext("co-op", 1, PlayerDamage(3))).Action);     // friendly
        Assert.Equal(RelayAction.Forward, i.Intercept(new EventContext("co-op", 1, PlayerDamage(2))).Action);  // enemy team
        Assert.Equal(1, i.DroppedCount);
    }

    [Fact]
    public void Interceptor_is_a_noop_in_free_for_all()
    {
        var m = new GameModeRegistry();
        m.SetMode("co-op", GameMode.FreeForAll);
        var i = new TeamDamageInterceptor(m);
        Assert.Equal(RelayAction.Forward, i.Intercept(new EventContext("co-op", 1, PlayerDamage(2))).Action);
    }

    // --- end-to-end through the room registry / handler ------------------------------------------

    [Fact]
    public void Joining_a_team_realm_assigns_and_broadcasts_a_team()
    {
        using var db = new TestDb();
        db.Context.Realms.Add(new Realm { Name = "Team Battle", IsEnabled = true, Mode = "TeamVsTeam" });
        db.Context.SaveChanges();
        var reg = new RoomRegistry();
        var h = new GameServerHandler("s", reg, allowAnonymousLan: true, realms: new RealmService(db.Context));

        var a = MakePeer(out var aRaised);
        h.OnOperationRequest(a, new OperationRequest(226, new() { { 255, "Team Battle" } }));

        // The newcomer is told its Team via a PropertiesChanged (253) carrying the "Team" property.
        Assert.Contains(aRaised, e => e.Code == 253
            && e.Parameters.TryGetValue(251, out var p) && p is System.Collections.IDictionary d && d.Contains("Team"));
        Assert.NotNull(reg.Modes.TeamOf("Team Battle", 1));
    }

    private static PeerConnection MakePeer(out List<EventData> raised)
    {
        var captured = new List<EventData>();
        raised = captured;
        var p = new PeerConnection("GameServer", new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0), new Null(), (_, _) => { });
        p.OnRaised = captured.Add;
        return p;
    }
    private sealed class Null : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }
}
