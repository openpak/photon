using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;

namespace OpenPak.Server.Core;

/// <summary>
/// A Photon role over WebSocket: what Photon Realtime's C# client opens for ConnectionProtocol.WebSocket
/// / WebSocketSecure (AuthMode AuthOnceWss sends the Name Server hop this way, wss://ns...:443). TLS is
/// terminated in front; this listens on plain HTTP and upgrades. Each binary frame is one Photon message
/// ([0xF3][type][body]); a frame that starts with the TCP header byte 0xFB has the 7-byte stream header
/// in front and the reply is framed the same way. Ping frames (0xF0) are answered in place.
///
/// The role handler is called under one lock: the UDP listeners call their handler from one thread, and
/// handlers are written for that.
/// </summary>
public sealed class WebSocketListener
{
    private readonly string _role;
    private readonly int _port;
    private readonly IOperationHandler _handler;
    private readonly object _gate;

    public WebSocketListener(string role, int port, IOperationHandler handler, object gate)
    {
        _role = role;
        _port = port;
        _handler = handler;
        _gate = gate;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://*:{_port}/");
        listener.Start();
        Log.Info(_role, $"WebSocket listener on {_port}");
        using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch when (ct.IsCancellationRequested) { break; }
            _ = Task.Run(() => ServeAsync(ctx, ct), ct);
        }
    }

    private async Task ServeAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        if (!ctx.Request.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }
        // The query names the app and the library (?libversion=&sid=&app=...); values are logged by key only.
        var keys = string.Join(",", ctx.Request.QueryString.AllKeys.Where(k => k is not null));
        var sub = ctx.Request.Headers["Sec-WebSocket-Protocol"];
        HttpListenerWebSocketContext wsc;
        try { wsc = await ctx.AcceptWebSocketAsync(string.IsNullOrEmpty(sub) ? null : sub.Split(',')[0].Trim()); }
        catch (Exception ex) { Log.Exception(_role, "WebSocket upgrade failed", ex); return; }
        var ws = wsc.WebSocket;
        var remote = ctx.Request.RemoteEndPoint ?? new IPEndPoint(IPAddress.None, 0);
        var sendLock = new SemaphoreSlim(1, 1);
        bool tcpFraming = false;

        void Send(byte[] message)
        {
            byte[] frame = message;
            if (tcpFraming)
            {
                frame = new byte[message.Length + 7];
                frame[0] = 0xFB;
                BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), frame.Length);
                frame[5] = 0;      // channel
                frame[6] = 1;      // reliable
                message.CopyTo(frame, 7);
            }
            sendLock.Wait();
            try { ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Warn(_role, $"WebSocket send failed: {ex.Message}"); }
            finally { sendLock.Release(); }
        }
        void Close()
        {
            try { ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "server", CancellationToken.None).Wait(1000); } catch { }
        }

        var peer = new PeerConnection(_role, remote, _handler, Send, Close);
        Log.Info(_role, $"{peer.PeerTag} WebSocket open (query keys {keys}{(string.IsNullOrEmpty(sub) ? "" : $", subprotocol {sub}")})");
        lock (_gate) _handler.OnConnect(peer);
        var buf = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) break;
                var data = ms.ToArray();
                if (data.Length == 0) continue;
                if (data[0] == 0xF0)
                {
                    // Ping: [0xF0][client time:int32] -> [0xF0][server time:int32][client time:int32].
                    var pong = new byte[9];
                    pong[0] = 0xF0;
                    BinaryPrimitives.WriteInt32BigEndian(pong.AsSpan(1), Environment.TickCount);
                    if (data.Length >= 5) Array.Copy(data, 1, pong, 5, 4);
                    sendLock.Wait();
                    try { await ws.SendAsync(pong, WebSocketMessageType.Binary, true, ct); } finally { sendLock.Release(); }
                    continue;
                }
                if (data[0] == 0xFB && data.Length > 7)
                {
                    tcpFraming = true;
                    data = data[7..];
                }
                lock (_gate) peer.HandleMessage(data);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            Log.Info(_role, $"{peer.PeerTag} WebSocket ended: {ex.Message}");
        }
        if (ws.State == WebSocketState.CloseReceived) Close();
        Log.Info(_role, $"{peer.PeerTag} WebSocket closed");
        lock (_gate) peer.NotifyDisconnect();
        try { ws.Dispose(); } catch { }
    }
}
