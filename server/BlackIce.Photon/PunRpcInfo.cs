using System.Buffers.Binary;
using System.Collections;

namespace BlackIce.Photon;

/// <summary>
/// Decoded view of a PUN RPC event (Photon event code 200) for authority checks: the target view id,
/// the method name (null when sent as a shortcut index), and — if any argument is a DamagePacket
/// custom type (code 68) — its damage value (the first 4 bytes, a big-endian float).
/// Pure read over an already-decoded EventData; no transport parsing.
/// </summary>
public readonly record struct PunRpcInfo(int ViewId, string? Method, float? DamageValue)
{
    private const byte PunRpcEventCode = 200;
    private const byte PData = 245, RpcViewId = 0, RpcMethodName = 3, RpcArgs = 4;
    private const byte DamagePacketCode = 68;

    /// <summary>Decodes <paramref name="ev"/> as a PUN RPC, or null if it is not event 200 with an RPC table.</summary>
    public static PunRpcInfo? From(EventData ev)
    {
        if (ev.Code != PunRpcEventCode) return null;
        if (!ev.Parameters.TryGetValue(PData, out var d) || d is not IDictionary rpc) return null;

        int viewId = rpc.Contains(RpcViewId) && rpc[RpcViewId] is int v ? v : 0;
        string? method = rpc.Contains(RpcMethodName) ? rpc[RpcMethodName] as string : null;

        float? damage = null;
        if (rpc.Contains(RpcArgs) && rpc[RpcArgs] is object[] args)
        {
            foreach (var a in args)
                if (a is PhotonCustomData { Code: DamagePacketCode } dp && dp.Data.Length >= 4)
                {
                    damage = BinaryPrimitives.ReadSingleBigEndian(dp.Data.AsSpan(0, 4));
                    break;
                }
        }
        return new PunRpcInfo(viewId, method, damage);
    }
}
