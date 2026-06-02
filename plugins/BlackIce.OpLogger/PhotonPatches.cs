using System.Collections.Generic;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Realtime;

namespace BlackIce.OpLogger;

/// <summary>Logs every incoming event (decoded, post-decryption).</summary>
[HarmonyPatch(typeof(LoadBalancingClient), nameof(LoadBalancingClient.OnEvent))]
internal static class OnEventPatch
{
    static void Prefix(EventData photonEvent) =>
        Plugin.Write("event", new { code = photonEvent.Code, parameters = Describe(photonEvent.Parameters) });

    /// <summary>Re-keys a Photon parameter dictionary by string (the byte code as text) but keeps the values
    /// intact, so <see cref="SimpleJson"/> can recurse into nested RPC payloads, argument arrays, and the raw
    /// bytes of custom types (DamagePacket etc.) rather than flattening everything to <c>ToString()</c>.</summary>
    internal static Dictionary<string, object?> Describe(Dictionary<byte, object> p)
    {
        var d = new Dictionary<string, object?>();
        if (p != null)
            foreach (var kv in p)
                d[kv.Key.ToString()] = kv.Value;
        return d;
    }
}

/// <summary>Logs every operation response from the server.</summary>
[HarmonyPatch(typeof(LoadBalancingClient), nameof(LoadBalancingClient.OnOperationResponse))]
internal static class OnOperationResponsePatch
{
    static void Prefix(OperationResponse operationResponse) =>
        Plugin.Write("response", new
        {
            code = operationResponse.OperationCode,
            returnCode = operationResponse.ReturnCode,
            debug = operationResponse.DebugMessage,
            parameters = OnEventPatch.Describe(operationResponse.Parameters)
        });
}

/// <summary>Logs every outgoing operation the client sends to the server.</summary>
[HarmonyPatch(typeof(PhotonPeer), nameof(PhotonPeer.SendOperation))]
internal static class SendOperationPatch
{
    static void Prefix(byte operationCode, Dictionary<byte, object> operationParameters, SendOptions sendOptions) =>
        Plugin.Write("send", new
        {
            code = operationCode,
            channel = sendOptions.Channel,
            reliable = sendOptions.Reliability,
            parameters = OnEventPatch.Describe(operationParameters)
        });
}
