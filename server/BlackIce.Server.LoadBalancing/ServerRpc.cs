using System.Collections.Generic;
using BlackIce.Photon;

namespace BlackIce.Server.LoadBalancing;

/// <summary>Builders for server-authored RPCs the vanilla client already renders, used by the gameplay
/// plugins to speak to clients without a mod.</summary>
public static class ServerRpc
{
    private const int MaxViewIdsPerActor = 1000;
    private const int AvatarViewSlot = 1;

    /// <summary>A <c>ReceiveChatMessage(string)</c> RPC on <paramref name="actor"/>'s avatar view — the
    /// vanilla chat channel, so the line shows up without any client mod. Used for kill feeds, score
    /// updates and match announcements (attributed to that actor's view).</summary>
    public static EventData Chat(int actor, string text) =>
        new(PhotonCodes.PunEvent.Rpc, new Dictionary<byte, object>
        {
            { PhotonCodes.Param.Code, PhotonCodes.PunEvent.Rpc },
            { PhotonCodes.Param.Data, new Dictionary<object, object>
                {
                    { PhotonCodes.RpcKey.ViewId, actor * MaxViewIdsPerActor + AvatarViewSlot },
                    { PhotonCodes.RpcKey.MethodName, "ReceiveChatMessage" },
                    { PhotonCodes.RpcKey.Args, new object[] { text } },
                } },
        });

    /// <summary>A <c>TeleportImmediately(Vector3)</c> RPC on <paramref name="actor"/>'s pawn view — used to
    /// respawn a participant to a spawn point at round reset (captured respawn sequence step 1 of 2).</summary>
    public static EventData Teleport(int actor, float x, float y, float z) =>
        new(PhotonCodes.PunEvent.Rpc, new Dictionary<byte, object>
        {
            { PhotonCodes.Param.Code, PhotonCodes.PunEvent.Rpc },
            { PhotonCodes.Param.Data, new Dictionary<object, object>
                {
                    { PhotonCodes.RpcKey.ViewId, actor * MaxViewIdsPerActor + AvatarViewSlot },
                    { PhotonCodes.RpcKey.MethodName, "TeleportImmediately" },
                    { PhotonCodes.RpcKey.Args, new object[] { PhotonCustomData.Vector3(x, y, z) } },
                } },
        });

    /// <summary>A <c>BecomeTangible()</c> RPC on <paramref name="actor"/>'s pawn view — the second half of
    /// the captured respawn sequence (re-enables collision / "alive").</summary>
    public static EventData BecomeTangible(int actor) =>
        new(PhotonCodes.PunEvent.Rpc, new Dictionary<byte, object>
        {
            { PhotonCodes.Param.Code, PhotonCodes.PunEvent.Rpc },
            { PhotonCodes.Param.Data, new Dictionary<object, object>
                {
                    { PhotonCodes.RpcKey.ViewId, actor * MaxViewIdsPerActor + AvatarViewSlot },
                    { PhotonCodes.RpcKey.MethodName, "BecomeTangible" },
                    { PhotonCodes.RpcKey.Args, System.Array.Empty<object>() },
                } },
        });
}
