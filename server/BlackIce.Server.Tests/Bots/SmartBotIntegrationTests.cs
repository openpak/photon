using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Authority;
using BlackIce.Server.LoadBalancing.Bots;
using Xunit;

namespace BlackIce.Server.Tests.Bots;

/// <summary>
/// End-to-end wiring of the smart bots: an enemy spawn relayed through a room session (whose evaluate is the
/// authority <see cref="WorldStateObserver"/>) populates the SHARED <see cref="RoomWorldStateRegistry"/>, and
/// the <see cref="BotManager"/> tick then drives a world-aware bot to actually attack it — proving the
/// observer → shared world-state → bot-manager → relay path connects (what the HunterBehavior unit tests,
/// which build the world-state by hand, cannot prove).
/// </summary>
public class SmartBotIntegrationTests
{
    private const string Room = "co-op";

    private sealed class NullHandler : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    private static PeerConnection Peer(List<EventData> sink)
    {
        var p = new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 0), new NullHandler(), (_, _) => { });
        p.OnRaised = sink.Add;
        return p;
    }

    /// <summary>A 202 instantiation for <paramref name="prefab"/> at (x,0,z), shaped like a real spawn:
    /// PData(245) = { 0=prefab, 1=Vector3, 7=viewId }.</summary>
    private static EventData EnemySpawn(string prefab, int viewId, float x, float z)
    {
        var pos = new byte[12];
        BinaryPrimitives.WriteSingleBigEndian(pos.AsSpan(0), x);
        BinaryPrimitives.WriteSingleBigEndian(pos.AsSpan(8), z);
        var pdata = new Dictionary<object, object>
        {
            { (byte)0, prefab },
            { (byte)1, new PhotonCustomData(86, pos) },
            { (byte)7, viewId },
        };
        return new EventData(202, new Dictionary<byte, object> { { (byte)245, pdata } });
    }

    [Fact]
    public void Smart_bot_attacks_an_enemy_the_master_spawned()
    {
        // Shared world-state + a session whose evaluate is the authority observer (mirrors production).
        var worlds = new RoomWorldStateRegistry();
        var observer = new WorldStateObserver(worlds.For(Room));
        var session = new RoomSession(Room, observer.Intercept);

        // A real player in the room receives the relayed traffic (the bot is not a session member).
        var sink = new List<EventData>();
        session.Join(1, Peer(sink));

        // Smart bots reading the shared world-state.
        var bots = new BotManager { Smart = true, Worlds = worlds };
        bots.Spawn(session, new BotIdentityGenerator().Next());   // first bot → spiral index 0 → spawns at (6,0)

        // The master spawns an enemy exactly at the bot's spawn point (in attack range immediately).
        session.RelayFrom(senderActor: 1, EnemySpawn("SpiderEnemy", viewId: 2002, x: 6f, z: 0f));
        Assert.Equal(false, worlds.For(Room).IsAlive(2002) is null);   // observer populated the shared state

        sink.Clear();   // ignore spawn/join chatter; we only care what the tick relays
        bots.Tick();

        // The bot should have relayed a TakeDamage RPC at the enemy's view.
        EventData? attack = null;
        foreach (var ev in sink)
            if (PunRpcInfo.From(ev) is { Method: "TakeDamage" } info && info.ViewId == 2002) attack = ev;

        Assert.True(attack is not null,
            $"expected the smart bot to attack enemy 2002; relayed {sink.Count} events: " +
            string.Join(",", sink.ConvertAll(e => e.Code.ToString())));
    }

    [Fact]
    public void Smart_bot_with_no_world_targets_only_moves()
    {
        // No enemies spawned → the bot has nothing to hunt → it relays a position (201) but no action RPC.
        var worlds = new RoomWorldStateRegistry();
        var session = new RoomSession(Room);
        var sink = new List<EventData>();
        session.Join(1, Peer(sink));

        var bots = new BotManager { Smart = true, Worlds = worlds };
        bots.Spawn(session, new BotIdentityGenerator().Next());
        sink.Clear();
        bots.Tick();

        Assert.Contains(sink, e => e.Code == PhotonCodes.PunEvent.SendSerialize);          // moved
        Assert.DoesNotContain(sink, e => PunRpcInfo.From(e)?.Method == "TakeDamage");       // but did not attack
    }
}
