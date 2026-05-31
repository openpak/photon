using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests.Authority;

/// <summary>
/// Phase 3a end-to-end: the per-realm <c>ExtraJson</c> strictness flows through <see cref="RoomRegistry"/>
/// into the session's interceptor chain. Default (no resolver) is Observe — a no-op relay. An Enforce
/// realm snap-corrects a teleport so recipients receive the last-good position, not the teleport.
/// </summary>
public class RoomRegistryPolicyTests
{
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

    private static EventData PosEvent(int viewId, float x, float y, float z)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), x);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4), y);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        var view = new object[] { viewId, false, null!, new PhotonCustomData(86, b) };
        return new EventData(201, new() { { 245, new object[] { 0, null!, view } } });
    }

    [Fact]
    public void Default_registry_relays_a_teleport_unchanged_observe_no_op()
    {
        var sink = new List<EventData>();
        var session = new RoomRegistry().Session("co-op");
        session.Join(1, Peer(new List<EventData>()));
        session.Join(2, Peer(sink));

        session.RelayFrom(1, PosEvent(1001, 0, 0, 0));
        System.Threading.Thread.Sleep(50);
        session.RelayFrom(1, PosEvent(1001, 10000, 0, 0));   // teleport, but Observe -> forwarded

        var last = PositionInfo.From(sink[^1]);
        Assert.NotNull(last);
        Assert.Equal(10000f, last!.Value.X, 3);   // teleport delivered as-is
    }

    [Fact]
    public void Enforce_realm_snap_corrects_a_teleport_in_the_relay()
    {
        var sink = new List<EventData>();
        var reg = new RoomRegistry(room => room == "pvp"
            ? "{\"authority\":{\"strictness\":\"Enforce\"}}"
            : "{}");
        var session = reg.Session("pvp");
        session.Join(1, Peer(new List<EventData>()));
        session.Join(2, Peer(sink));

        session.RelayFrom(1, PosEvent(1001, 5, 6, 7));    // last good
        System.Threading.Thread.Sleep(50);
        session.RelayFrom(1, PosEvent(1001, 10000, 0, 0));   // teleport -> snap-corrected

        var last = PositionInfo.From(sink[^1]);
        Assert.NotNull(last);
        Assert.Equal(5f, last!.Value.X, 3);   // recipients see the last-good position, not the teleport
        Assert.Equal(6f, last.Value.Y, 3);
        Assert.Equal(7f, last.Value.Z, 3);
    }
}
