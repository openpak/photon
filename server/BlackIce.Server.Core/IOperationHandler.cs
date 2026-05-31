using BlackIce.Photon;

namespace BlackIce.Server.Core;

/// <summary>A server role (Name / Master / Game) reacts to a peer's lifecycle and operations.</summary>
public interface IOperationHandler
{
    void OnConnect(PeerConnection peer);
    void OnOperationRequest(PeerConnection peer, OperationRequest request);
    void OnDisconnect(PeerConnection peer);
}
