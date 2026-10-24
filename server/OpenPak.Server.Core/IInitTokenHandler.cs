using OpenPak.Photon;

namespace OpenPak.Server.Core;

/// <summary>A role that can authenticate a peer from the token its web-style Init carried (the
/// Photon C++ SDK's AuthenticateOnce flow: the Master and Game hops get the Name Server's token in
/// the Init and no OpAuthenticate follows).</summary>
public interface IInitTokenHandler
{
    /// <summary>Tries each candidate as a token. Returns the Authenticate response the client waits
    /// for in place of the OpAuthenticate it never sends (rc 0 once one authenticates the peer).</summary>
    OperationResponse OnInitToken(PeerConnection peer, IEnumerable<string> candidates);
}
