using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using OpenPak.Photon;
using OpenPak.Server.Core;
using Xunit;

namespace OpenPak.Server.Tests;

/// <summary>The WebSocket transport carries one whole Photon message per binary frame, with no eNet layer.</summary>
public class WebSocketListenerTests
{
    private sealed class EchoHandler : IOperationHandler
    {
        public int Connects, Disconnects;
        public void OnConnect(PeerConnection peer) => Connects++;
        public void OnDisconnect(PeerConnection peer) => Disconnects++;
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) =>
            peer.SendResponse(new OperationResponse(request.OperationCode, 0, null, new() { { 1, "pong" } }));
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    private static async Task<byte[]> Receive(ClientWebSocket ws)
    {
        var buf = new byte[4096];
        var r = await ws.ReceiveAsync(buf, CancellationToken.None);
        return buf[..r.Count];
    }

    [Fact]
    public async Task Init_ping_and_an_operation_round_trip()
    {
        var port = FreePort();
        var handler = new EchoHandler();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = new WebSocketListener("WsTest", port, handler, new object()).RunAsync(cts.Token);
        using var ws = new ClientWebSocket();
        await Task.Delay(300);
        using (var c1 = new CancellationTokenSource(3000)) await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/?libversion=4&app=x"), c1.Token);
        Assert.Equal(WebSocketState.Open, ws.State);

        await ws.SendAsync(new byte[] { 0xF3, 0x00, 1, 2, 3 }, WebSocketMessageType.Binary, true, cts.Token);
        Assert.Equal(new byte[] { 0xF3, 0x01 }, await Receive(ws));

        await ws.SendAsync(new byte[] { 0xF0, 0, 0, 0, 7 }, WebSocketMessageType.Binary, true, cts.Token);
        var pong = await Receive(ws);
        Assert.Equal(9, pong.Length);
        Assert.Equal(0xF0, pong[0]);
        Assert.Equal(7, pong[8]);

        // OpGetRegions-shaped request: [F3][2][op 220][param count 0].
        await ws.SendAsync(new byte[] { 0xF3, 0x02, 220, 0, 0 }, WebSocketMessageType.Binary, true, cts.Token);
        var resp = await Receive(ws);
        Assert.Equal(0xF3, resp[0]);
        Assert.Equal(WireMessage.OperationResponse, resp[1]);
        Assert.Equal(220, resp[2]);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        for (var i = 0; i < 50 && handler.Disconnects == 0; i++) await Task.Delay(50);
        Assert.Equal(1, handler.Connects);
        Assert.Equal(1, handler.Disconnects);
        cts.Cancel();
        try { await run; } catch { }
    }
}
