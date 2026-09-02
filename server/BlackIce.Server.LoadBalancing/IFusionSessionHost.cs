namespace BlackIce.Server.LoadBalancing;

/// <summary>
/// Per-server sink that hosts Photon Fusion's own Shared-mode server simulation for rooms whose
/// clients run Fusion (Outbound). Photon Cloud runs this as a closed "Fusion plugin"; self-hosting
/// it lets a room relay real Fusion gameplay between two or more peers rather than only echoing a
/// solo host's traffic back to itself.
///
/// The concrete implementation lives in <c>BlackIce.Server.Fusion</c>, which links Fusion's
/// non-redistributable assemblies and so is an optional build (see docs/fusion-shared-server.md).
/// When it is absent the handler holds a null reference and keeps the legacy solo-host path, so the
/// core server builds and runs without any Fusion DLLs.
///
/// All members are called on the Game listener thread only (Fusion's simulation is single-threaded).
/// </summary>
public interface IFusionSessionHost
{
    /// <summary>A peer joined <paramref name="room"/> as <paramref name="actor"/>. <paramref name="uniqueId"/>
    /// is the 8-byte id the server handed the client in its Fusion Join Confirmation — the same id the
    /// client presents on its event-101 relay Connect, so the simulation must be told it before that
    /// Connect arrives.</summary>
    void PlayerJoined(string room, int actor, byte[] uniqueId);

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
