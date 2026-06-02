using System.Net;
using BlackIce.Photon;
using BlackIce.Photon.Crypto;
using BlackIce.Photon.Transport;

namespace BlackIce.Server.Core;

/// <summary>
/// One connected client. Owns the eNet transport state and (once established) the DH crypto
/// provider. Parses inbound application messages, transparently handles the InitEncryption
/// handshake, and dispatches operations to the role handler. Outbound responses/events are
/// framed and sent through the listener's send callback.
/// </summary>
public sealed class PeerConnection
{
    private readonly EnetPeer _enet = new();
    private readonly IOperationHandler _handler;
    private readonly Action<IReadOnlyList<NCommand>, int> _send;
    private readonly string _role;
    private DiffieHellmanCryptoProvider? _crypto;

    public IPEndPoint Remote { get; }
    /// <summary>Per-peer slot for role handlers to stash state (e.g. authenticated userId).</summary>
    public object? Tag { get; set; }

    /// <summary>The connection's resolved SteamID — verified when <see cref="IsVerified"/>, otherwise the
    /// client-asserted or anonymous id. Null before authentication.</summary>
    public string? SteamId { get; set; }

    /// <summary>True once a Steam game-server ticket proved this identity (see the auth flow). Admin/anti-cheat
    /// gating trusts the SteamID/level ONLY when this is true; an asserted or anonymous-LAN identity is not.</summary>
    public bool IsVerified { get; set; }

    /// <summary>
    /// Where this peer is in the connect flow. Role handlers advance it (Authenticated after a
    /// successful auth, InRoom after a join); the <see cref="OperationRouter"/> uses it to flag
    /// operations that arrive out of order.
    /// </summary>
    public SessionStatus Status { get; set; } = SessionStatus.Connected;

    /// <summary>UTC of the last datagram received from this peer — drives keepalive + dead-peer eviction.</summary>
    public DateTime LastInboundUtc { get; private set; } = DateTime.UtcNow;
    private DateTime _lastPingSentUtc = DateTime.UtcNow;

    public PeerConnection(string role, IPEndPoint remote, IOperationHandler handler, Action<IReadOnlyList<NCommand>, int> send)
    {
        _role = role;
        Remote = remote;
        _handler = handler;
        _send = send;
    }

    public void HandlePacket(PhotonHeader header, IReadOnlyList<NCommand> commands)
    {
        LastInboundUtc = DateTime.UtcNow;
        _enet.NoteChallenge(header.Challenge);
        var control = new List<NCommand>();
        foreach (var cmd in commands)
        {
            control.AddRange(_enet.HandleCommand(cmd, header.ServerTime, out var payload));
            if (cmd.CommandType == NCommand.Connect)
            {
                Log.Info(_role, $"{Remote} eNet CONNECT");
                _handler.OnConnect(this);
            }
            if (cmd.CommandType == NCommand.Disconnect)
            {
                // The client is telling us it's leaving. We don't tear down transport state yet
                // (Phase 1), but record it — a Disconnect here vs. a silent timeout are very
                // different failure modes when diagnosing "kicked out of the game".
                Log.Info(_role, $"{Remote} eNet DISCONNECT received");
                _handler.OnDisconnect(this);
            }
            if (payload is not null)
            {
                CurrentInboundUnreliable = cmd.CommandType == NCommand.SendUnreliable;
                HandleAppPayload(payload);
                CurrentInboundUnreliable = false;
            }
        }
        if (control.Count > 0) _send(control, _enet.Challenge);
    }

    private void HandleAppPayload(byte[] payload)
    {
        if (!WireMessage.TryPeekType(payload, out var type, out var encrypted))
        {
            Log.Warn(_role, $"{Remote} dropped non-Photon payload ({payload.Length}B: {PhotonNames.Hex(payload, 32)})");
            return;
        }

        // Init handshake: a fixed 41-byte blob naming the target app. The client only needs a
        // 2-byte InitResponse back before it proceeds to encryption + authentication.
        if (type == WireMessage.Init)
        {
            Log.Info(_role, $"{Remote} Init -> InitResponse");
            SendRaw(WireMessage.InitResponseMessage());
            return;
        }

        WireMessage.Parsed msg;
        try
        {
            msg = WireMessage.Parse(payload, _crypto is not null ? b => _crypto!.Decrypt(b) : null);
        }
        catch (Exception ex)
        {
            // Deliberate boundary (nothing better): the GpBinary wire codec signals malformed/undecryptable
            // input by throwing, so we translate that to a log-and-drop here rather than letting one bad
            // datagram tear down the peer. Expected-failure paths in our own code use Result instead.
            Log.Exception(_role, $"{Remote} dropped malformed {PhotonNames.MessageType(type)} message " +
                                 $"(enc={encrypted}, {payload.Length}B)", ex);
            return;
        }

        if (msg is { MessageType: WireMessage.InternalOperationRequest, Code: 0 })
        {
            Log.Info(_role, $"{Remote} InitEncryption -> deriving shared key");
            EstablishEncryption(msg.Parameters);
            return;
        }

        if (msg.MessageType == WireMessage.Operation)
            Log.Info(_role, $"{Remote} <- {PhotonNames.Op(msg.Code)} [{PhotonNames.Params(msg.Parameters)}]");
        else
            Log.Info(_role, $"{Remote} <- {PhotonNames.MessageType(msg.MessageType)} code={msg.Code} " +
                            $"[{PhotonNames.Params(msg.Parameters)}]");

        try
        {
            _handler.OnOperationRequest(this, new OperationRequest(msg.Code, msg.Parameters));
        }
        catch (Exception ex)
        {
            // A handler throwing on one operation must not kill the peer/listener silently. Log it
            // with the offending op so we can see exactly which message broke processing.
            Log.Exception(_role, $"{Remote} handler threw on {PhotonNames.Op(msg.Code)}", ex);
        }
    }

    /// <summary>InitEncryption: derive the shared key from the client's public key, return ours.</summary>
    private void EstablishEncryption(Dictionary<byte, object> parameters)
    {
        // The client key (param 1) is untrusted: it may be missing or the wrong type. Reject cleanly
        // rather than throwing a KeyNotFound/InvalidCast out of an unchecked cast.
        if (!parameters.TryGetValue(1, out var raw) || raw is not byte[] clientPublicKey)   // PhotonCodes.ClientKey
        {
            Log.Warn(_role, $"{Remote} InitEncryption without a valid ClientKey (param 1) — ignored");
            return;
        }

        // Derive first; only adopt the provider once the (validating) key agreement succeeds, so a bad
        // key leaves the peer unencrypted rather than half-initialised.
        var crypto = new DiffieHellmanCryptoProvider();
        crypto.DeriveSharedKey(clientPublicKey);
        _crypto = crypto;
        var serverPublicKey = crypto.PublicKey;
        Log.Info(_role, $"{Remote} shared key derived (clientKey {clientPublicKey.Length}B, serverKey {serverPublicKey.Length}B)");
        var response = new OperationResponse(0, 0, null, new() { { 1, serverPublicKey } }); // ServerKey
        SendRaw(WireMessage.Response(response, WireMessage.InternalOperationResponse));
    }

    /// <summary>
    /// Sends a keepalive Ping if the peer has been inbound-silent for at least <paramref name="quietFor"/>
    /// and we haven't already pinged within that window (so we probe at most once per window, not every
    /// maintenance tick). A live client answers with an ack, refreshing <see cref="LastInboundUtc"/>.
    /// </summary>
    public void MaybePing(DateTime now, TimeSpan quietFor)
    {
        if (now - LastInboundUtc < quietFor || now - _lastPingSentUtc < quietFor) return;
        _lastPingSentUtc = now;
        Log.Trace(_role, $"{Remote} -> keepalive Ping (quiet {(now - LastInboundUtc).TotalSeconds:F1}s)");
        _send(new[] { _enet.Ping() }, _enet.Challenge);
    }

    /// <summary>Set once the server has decided to drop this peer (a hard kick); the listener evicts it
    /// on the next maintenance pass. The eNet Disconnect command is sent immediately by <see cref="Disconnect"/>.</summary>
    public bool WantsDisconnect { get; private set; }

    /// <summary>
    /// Hard-disconnects this peer: sends the client a reliable eNet Disconnect so it tears down promptly,
    /// and flags the connection for eviction by the listener's next maintenance pass. Must be called on
    /// the listener thread (it sends through the peer's transport state).
    /// </summary>
    public void Disconnect()
    {
        if (WantsDisconnect) return;
        WantsDisconnect = true;
        try { _send(new[] { _enet.Disconnect() }, _enet.Challenge); }
        catch (Exception ex) { Log.Exception(_role, $"{Remote} disconnect send failed", ex); }
        Log.Info(_role, $"{Remote} hard-disconnected (server kick)");
    }

    /// <summary>Notifies the role handler that this peer is being dropped (graceful quit or eviction).</summary>
    public void NotifyDisconnect() => _handler.OnDisconnect(this);

    public void SendResponse(OperationResponse response)
    {
        Log.Info(_role, $"{Remote} -> {PhotonNames.Op(response.OperationCode)} response rc={response.ReturnCode}" +
                        $"{(response.DebugMessage is null ? "" : $" \"{response.DebugMessage}\"")} " +
                        $"[{PhotonNames.Params(response.Parameters)}]");
        SendRaw(WireMessage.Response(response));
    }

    /// <summary>Test/diagnostic observation hook: when set, every raised event is also handed here
    /// before being sent. Production leaves this null; relay tests use it to assert fan-out without a socket.</summary>
    public System.Action<EventData>? OnRaised { get; set; }

    /// <summary>Test/diagnostic hook reporting each raised event AND whether it was sent unreliably.</summary>
    public System.Action<EventData, bool>? OnRaisedClassified { get; set; }

    /// <summary>Delivery class of the operation currently being dispatched to the handler — set by the
    /// transport as it unwraps each command so the handler can relay an event with matching semantics.</summary>
    public bool CurrentInboundUnreliable { get; set; }

    public void RaiseEvent(EventData ev) => RaiseEvent(ev, unreliable: false);

    public void RaiseEvent(EventData ev, bool unreliable)
    {
        Log.Info(_role, $"{Remote} -> raise {PhotonNames.Event(ev.Code)} ({(unreliable ? "unreliable" : "reliable")}) [{PhotonNames.Params(ev.Parameters)}]");
        OnRaised?.Invoke(ev);
        OnRaisedClassified?.Invoke(ev, unreliable);
        var msg = WireMessage.EventMessage(ev);
        if (unreliable) _send(new[] { _enet.WrapUnreliable(msg) }, _enet.Challenge);
        else SendRaw(msg);
    }

    private void SendRaw(byte[] appMessage) =>
        _send(new[] { _enet.WrapReliable(appMessage) }, _enet.Challenge);
}
