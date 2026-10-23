using System.Net;
using System.Net.Sockets;

namespace OpenPak.Server.Core;

/// <summary>
/// Photon's region ping, as the client libraries send it when they pick the best region (RegionPinger /
/// PingMono): 13 bytes, twelve 0x7D and a random id, to UDP 5055 of each region's host; the server echoes
/// the datagram and the client matches the id and the length. Every <see cref="UdpListener"/> answers it;
/// <see cref="RegionPing.RunResponderAsync"/> is the bare responder a node runs on 5055 when its Game role
/// listens elsewhere.
/// </summary>
public static class RegionPing
{
    public const int Length = 13;
    private const byte Fill = 0x7D;

    public static bool IsPing(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length != Length) return false;
        for (var i = 0; i < Length - 1; i++)
            if (datagram[i] != Fill) return false;
        return true;
    }

    /// <summary>Echoes region pings on <paramref name="port"/> until cancelled; anything else is dropped.</summary>
    public static async Task RunResponderAsync(int port, CancellationToken ct)
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        Log.Info("RegionPing", $"answering region pings on UDP :{port}");
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await socket.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            if (IsPing(r.Buffer))
            {
                try { await socket.SendAsync(r.Buffer, r.Buffer.Length, r.RemoteEndPoint).ConfigureAwait(false); }
                catch (SocketException) { }
            }
        }
    }
}
