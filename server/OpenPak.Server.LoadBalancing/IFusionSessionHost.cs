namespace OpenPak.Server.LoadBalancing;

/// <summary>
/// Per-server sink that hosts a Fusion 2 Shared-mode server simulation for rooms whose clients run
/// Fusion (Outbound). Photon Cloud runs this as a closed "Fusion plugin"; self-hosting it lets a room
/// relay real Fusion gameplay between two or more peers rather than only echoing a solo host's
/// traffic back to itself.
///
/// The implementation is the clean-room <see cref="Fusion.FusionSharedHost"/>
/// (docs/protocol/08-fusion-shared-simulation.md). Members are called from Game listener loops; the
/// host keeps each room's simulation on one loop.
/// </summary>
public interface IFusionSessionHost
{
    /// <summary>A peer joined <paramref name="room"/> as <paramref name="actor"/>. <paramref name="uniqueId"/>
    /// is the 8-byte id the server handed the client in its Fusion Join Confirmation — the same id the
    /// client presents on its event-101 relay Connect, so the simulation must be told it before that
    /// Connect arrives.</summary>
    /// <param name="clientServer">True when the client asked for Fusion's <c>ClientServer</c>
    /// topology (JoinGame parameter 213) rather than <c>Shared</c>. The two are different simulations:
    /// ClientServer is a dedicated-server session, which on Photon Cloud the plugin hosts, and a
    /// client that asks for it and is given a Shared simulation exchanges a few seconds of traffic and
    /// then drops the room.</param>
    /// <param name="maxPlayers">The Photon room's MaxPlayers, or 0 when it has none.</param>
    void PlayerJoined(string room, int actor, byte[] uniqueId, bool clientServer = false, int maxPlayers = 0);

    /// <summary>A peer left the room; drop its simulation connection.</summary>
    void PlayerLeft(string room, int actor);

    /// <summary>An inbound Fusion relay packet (Photon event code 101) from <paramref name="actor"/>.
    /// Returns true if the host consumed it (the caller must then NOT run the legacy solo echo/relay for
    /// this event); false if there is no simulation for the room and the caller should fall back.</summary>
    bool FeedRelay(string room, int actor, byte[] payload);

    /// <summary>Advance every live room simulation by real elapsed time and flush each one's outbound
    /// relay packets through the send callback supplied at construction. Call once per listener tick.</summary>
    void Tick();
}
