using BlackIce.Photon;

namespace BlackIce.Server.Core;

/// <summary>How far along the connect flow a peer is. Ordered so a higher value implies the lower ones.</summary>
public enum SessionStatus
{
    Connected = 0,      // transport up, not yet authenticated
    Authenticated = 1,  // passed Authenticate on this role
    InRoom = 2,         // joined a room (Game server only)
}

/// <summary>
/// Maps an operation code to its handler and the session status it expects, replacing the per-role
/// switch statements. Registering an op is a single <see cref="On"/> call; an unknown op gets a
/// consistent rc=-2 response, and an op that arrives before its prerequisite status is logged (but
/// still processed) — detection-only, matching the Phase 2b authority validators, so a real client
/// flow can be observed before this is hardened into a rejection. Mirrors TrinityCore's OpcodeTable
/// (handler + status) and MangosSharp's dictionary dispatch.
/// </summary>
public sealed class OperationRouter
{
    public delegate void Handler(PeerConnection peer, OperationRequest request);

    private readonly string _role;
    private readonly string _unknownMessage;
    private readonly Dictionary<byte, (Handler Handler, SessionStatus Required)> _table = new();

    public OperationRouter(string role, string unknownMessage = "Unknown operation")
    {
        _role = role;
        _unknownMessage = unknownMessage;
    }

    /// <summary>Registers <paramref name="handler"/> for <paramref name="op"/>, requiring at least <paramref name="required"/> status.</summary>
    public OperationRouter On(byte op, Handler handler, SessionStatus required = SessionStatus.Connected)
    {
        _table[op] = (handler, required);
        return this;
    }

    /// <summary>Routes one request: unknown op -> rc=-2; out-of-state op -> logged then processed; otherwise handled.</summary>
    public void Dispatch(PeerConnection peer, OperationRequest request)
    {
        if (!_table.TryGetValue(request.OperationCode, out var entry))
        {
            Log.Warn(_role, $"{peer.Remote} unhandled {PhotonNames.Op(request.OperationCode)} " +
                            $"[{PhotonNames.Params(request.Parameters)}] -> rc=-2");
            peer.SendResponse(new OperationResponse(request.OperationCode, -2, _unknownMessage, new()));
            return;
        }

        if (peer.Status < entry.Required)
            Log.Warn(_role, $"{peer.Remote} {PhotonNames.Op(request.OperationCode)} arrived before " +
                            $"{entry.Required} (peer status={peer.Status}) -> processed (log-only)");

        entry.Handler(peer, request);
    }
}
