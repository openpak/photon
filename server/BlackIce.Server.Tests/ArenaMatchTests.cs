using System.Collections.Generic;
using System.Linq;
using System.Net;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Plugins;
using Xunit;

namespace BlackIce.Server.Tests;

public class ArenaMatchTests
{
    private static PeerConnection Peer(out List<EventData> raised)
    {
        var captured = new List<EventData>();
        raised = captured;
        var p = new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 0), new NullHandler(), (_, _) => { });
        p.OnRaised = captured.Add;
        return p;
    }

    private sealed class NullHandler : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    [Fact]
    public void A_death_scores_the_victims_opposing_team()
    {
        var modes = new GameModeRegistry();
        modes.SetMode("co-op", GameMode.TeamVsTeam);
        int victimTeam = modes.AssignTeam("co-op", 6);          // team 0
        var state = new ArenaState { Enabled = true, ScoreCap = 25 };
        var match = new ArenaMatch(state, modes, rooms: null, bus: null);

        match.OnDeath(new DeathNotice("co-op", 6));

        Assert.Equal(1, state.Score("co-op", 1 - victimTeam));   // opponent scored
        Assert.Equal(0, state.Score("co-op", victimTeam));
    }

    [Fact]
    public void Reaching_the_cap_wins_and_resets_the_score()
    {
        var modes = new GameModeRegistry();
        modes.SetMode("co-op", GameMode.TeamVsTeam);
        int victimTeam = modes.AssignTeam("co-op", 6);
        var state = new ArenaState { Enabled = true, ScoreCap = 1, ResetOnWin = true };
        var match = new ArenaMatch(state, modes, rooms: null, bus: null);

        match.OnDeath(new DeathNotice("co-op", 6));               // opponent hits the cap -> win -> reset

        Assert.Equal(0, state.Score("co-op", 1 - victimTeam));   // reset cleared the score
        Assert.False(state.Ended("co-op"));
    }

    [Fact]
    public void Non_team_modes_do_not_score()
    {
        var modes = new GameModeRegistry();
        modes.SetMode("co-op", GameMode.Coop);
        var state = new ArenaState { Enabled = true };
        var match = new ArenaMatch(state, modes, rooms: null, bus: null);

        match.OnDeath(new DeathNotice("co-op", 6));

        Assert.Equal(0, state.Score("co-op", 0));
        Assert.Equal(0, state.Score("co-op", 1));
    }

    [Fact]
    public void RespawnAll_sends_teleport_and_tangible_for_every_participant()
    {
        var state = new ArenaState { SpawnX = 520f, SpawnY = 3f, SpawnZ = 469.5f };
        var match = new ArenaMatch(state, modes: null, rooms: null, bus: null);
        var session = new RoomSession("co-op", new InterceptorChain(System.Array.Empty<IEventInterceptor>()));
        var p6 = Peer(out var r6); session.Join(6, p6);
        var p7 = Peer(out var r7); session.Join(7, p7);

        match.RespawnAll(session);

        // Each participant gets a Teleport + BecomeTangible; every member receives them (4 events total).
        foreach (var raised in new[] { r6, r7 })
        {
            Assert.Equal(4, raised.Count);
            var methods = raised.Select(e =>
                (string)((System.Collections.IDictionary)e.Parameters[PhotonCodes.Param.Data])[PhotonCodes.RpcKey.MethodName]!).ToList();
            Assert.Equal(2, methods.Count(m => m == "TeleportImmediately"));
            Assert.Equal(2, methods.Count(m => m == "BecomeTangible"));
        }
    }
}
