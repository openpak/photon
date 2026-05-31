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
}
