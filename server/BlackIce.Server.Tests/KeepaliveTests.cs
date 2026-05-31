using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using BlackIce.Photon;
using BlackIce.Photon.Transport;
using BlackIce.Server.Core;
using Xunit;

namespace BlackIce.Server.Tests;

/// <summary>
/// Keepalive + dead-peer handling on <see cref="PeerConnection"/>. The listener's maintenance loop
/// (ping quiet peers, evict silent ones) is exercised live; these pin the per-peer decisions it relies on.
/// </summary>
public class KeepaliveTests
{
    private sealed class CountingHandler : IOperationHandler
    {
        public int Disconnects;
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) => Disconnects++;
    }

    private static PeerConnection NewPeer(out List<NCommand> sent, out CountingHandler handler)
    {
        var captured = new List<NCommand>();
        sent = captured;
        handler = new CountingHandler();
        return new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 1234), handler,
                                  (cmds, _) => captured.AddRange(cmds));
    }

    private static int Pings(IEnumerable<NCommand> sent) => sent.Count(c => c.CommandType == NCommand.Ping);

    [Fact]
    public void MaybePing_does_not_ping_a_recently_active_peer()
    {
        var peer = NewPeer(out var sent, out _);
        peer.MaybePing(DateTime.UtcNow, TimeSpan.FromSeconds(3));
        Assert.Equal(0, Pings(sent));
    }

    [Fact]
    public void MaybePing_pings_a_quiet_peer()
    {
        var peer = NewPeer(out var sent, out _);
        peer.MaybePing(DateTime.UtcNow + TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3));
        Assert.Equal(1, Pings(sent));
    }

    [Fact]
    public void MaybePing_pings_at_most_once_per_quiet_window()
    {
        var peer = NewPeer(out var sent, out _);
        var t = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        peer.MaybePing(t, TimeSpan.FromSeconds(3));                              // pings
        peer.MaybePing(t + TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(3)); // throttled
        Assert.Equal(1, Pings(sent));
    }

    [Fact]
    public void NotifyDisconnect_invokes_the_role_handler()
    {
        var peer = NewPeer(out _, out var handler);
        peer.NotifyDisconnect();
        Assert.Equal(1, handler.Disconnects);
    }
}
