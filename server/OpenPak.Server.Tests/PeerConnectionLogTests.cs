using System;
using System.IO;
using System.Net;
using OpenPak.Photon;
using OpenPak.Server.Core;
using Xunit;

namespace OpenPak.Server.Tests;

/// <summary>Raising an event is the per-packet path (Fusion's relay at the send rate): it logs at Debug only.</summary>
[Collection("Log")]
public class PeerConnectionLogTests
{
    private sealed class Null : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    [Theory]
    [InlineData(LogLevel.Info, false)]
    [InlineData(LogLevel.Debug, true)]
    public void A_raised_event_is_logged_only_at_debug(LogLevel level, bool logged)
    {
        var previous = Log.Level;
        var path = Path.Combine(Path.GetTempPath(), $"openpak-log-{Guid.NewGuid():N}.log");
        try
        {
            Log.Level = level;
            Log.ToFile(path);
            var peer = new PeerConnection("GameServer", new IPEndPoint(IPAddress.Loopback, 1), new Null(), (_, _) => { });
            peer.RaiseEvent(new EventData(101, new() { { (byte)245, new byte[] { 1, 2, 3 } } }), unreliable: true);
            Log.Flush();
            Assert.Equal(logged, File.ReadAllText(path).Contains("-> raise"));
        }
        finally
        {
            Log.Level = previous;
            var safe = Path.Combine(Path.GetTempPath(), $"openpak-log-{Guid.NewGuid():N}.log");
            Log.ToFile(safe);
            try { File.Delete(path); } catch (IOException) { }
            try { File.Delete(safe); } catch (IOException) { }
        }
    }
}
