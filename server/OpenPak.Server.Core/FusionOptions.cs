namespace OpenPak.Server.Core;

/// <summary>Switches for the Fusion 2 Shared-mode simulation the Game server hosts per room
/// (docs/protocol/08-fusion-shared-simulation.md).</summary>
public sealed class FusionOptions
{
    /// <summary>
    /// Create SceneInfo/PhysicsInfo with StateAuthority = MasterClient rather than None. A stock master
    /// client asserts on None when it consumes an initial scene, but Outbound's join sync was live-tested
    /// with None, and a past build that changed it stopped working (spec §11.5, §19), so it is opt-in.
    /// </summary>
    public bool SceneAuthorityMasterClient { get; set; }
}
