namespace OpenPak.Server.Core;

/// <summary>A role that can authenticate a peer from the token its web-style Init carried (the
/// Photon C++ SDK's AuthenticateOnce flow: the Master and Game hops get the Name Server's token in
/// the Init and no OpAuthenticate follows).</summary>
public interface IInitTokenHandler
{
    /// <summary>Tries each candidate as a token; true once one authenticates the peer.</summary>
    bool OnInitToken(PeerConnection peer, IEnumerable<string> candidates);
}
