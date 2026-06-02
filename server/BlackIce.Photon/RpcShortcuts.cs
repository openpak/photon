using System.Collections.Generic;

namespace BlackIce.Photon;

/// <summary>
/// PUN's ordered RPC method list (the project's <c>RpcList</c>), captured live from the game client.
/// A client may send a frequently-used RPC as a byte <b>index</b> into this list (Photon RPC key 5,
/// the "method shortcut") instead of by name — e.g. <c>KilledPlayerRemote</c> arrives as index 32.
/// This table resolves that index back to a method name so the relay can recognize the call.
/// Mirrors <c>docs/protocol/generated/rpc-shortcuts.csv</c>; regenerate both from a fresh
/// <c>BlackIce.OpLogger</c> "rpclist" capture after a game update (indices are not version-stable).
/// </summary>
public static class RpcShortcuts
{
    public static readonly IReadOnlyList<string> Methods = new[]
    {
        "AddAggro", "AddBuffRPC", "AddImpact", "AddImpactNetwork", "AddRAMNetwork", "AddSpawnedEnemies",
        "AddXP", "AddXPRPC", "BarrierSetup", "BecomeTangible", "CancelHackSecondary", "ChangeColor",
        "ClickRpc", "Cloak", "DestroyRpc", "Die", "DieByViewID", "DropLoot", "DropLootForLocalPlayer",
        "EggHatchEarly", "EndHack", "ExplodeObjects", "ExplosionParticlesNetwork", "FinishEndingHack",
        "GetColorCallback", "GetLock", "GhostIntangible", "GoIntangible", "GrapplingHookOther",
        "InitializeFromNetwork", "KickRemote", "KilledPlayer", "KilledPlayerRemote", "KillEnemyPhase",
        "KillProjectileOther", "LoadRemoteModIcon", "LockGranted", "NotifyMine", "ParentToThisNetwork",
        "ReceiveChatMessage", "RefreshModel", "RequestModIcon", "RequestParent", "ResetAtNetworkTime",
        "ReturnActual", "SetActiveAtNetworkTime", "SetColor", "SetDamageTaken", "SetDying", "SetHealth",
        "SetItemRPC", "SetLinkedPrimaryPawn", "SetMaxShield", "SetShieldValues", "SetupHack",
        "SetupLinkRequest", "SetupMineNetwork", "SetupRequest", "SetupXP", "Shatter", "SpawnDisc",
        "SpawnProjectile", "SyncUnhackServerRPC", "TakeDamage", "TakeDamageOwner", "Teleport",
        "TeleportImmediately", "TriggerGrenade", "Uncloak", "Unlock", "UpdateDifficultyRPC",
        "UpdateHighestServerHackedRPC", "UpdatePVPRPC", "WakeEnemyAfterDelay", "SpawnProjectileLocal",
        "SpawnProjectileRemote", "AddTempHP", "KilledPlayerBuildingSecondary", "NonexplosionParticlesNetwork",
        "SetHostWorldState", "SetWorldStateFlag", "ShareHostWorldStateMaster", "SetCreditsRPC",
        "NotifySeen", "RemoveDebuffsRPC", "DiscoGetLock", "DiscoLocked", "DiscoUnlockPawn",
    };

    /// <summary>The method name at a shortcut index, or null if the index is out of range.</summary>
    public static string? Name(int index) => index >= 0 && index < Methods.Count ? Methods[index] : null;
}
