using System.Buffers.Binary;
using System.Collections;

namespace BlackIce.Photon;

/// <summary>
/// Decoded view of a PUN RPC event (Photon event code 200) for authority checks: the target view id,
/// the method name (null when sent as a shortcut index), and — if any argument is a DamagePacket
/// custom type (code 68) — its damage value (the first 4 bytes, a big-endian float) plus the raw packet
/// bytes (so a validator can inspect game-specific fields like a headshot flag at a known offset).
/// Pure read over an already-decoded EventData; no transport parsing.
/// </summary>
public readonly record struct PunRpcInfo(int ViewId, string? Method, float? DamageValue, byte[]? DamagePacket)
{
    /// <summary>True when a non-zero byte sits at <paramref name="offset"/> in the DamagePacket (caller supplies the game-specific offset).</summary>
    public bool IsHeadshot(int offset) =>
        DamagePacket is { } p && offset >= 0 && offset < p.Length && p[offset] != 0;

    /// <summary>Decodes <paramref name="ev"/> as a PUN RPC, or null if it is not event 200 with an RPC table.</summary>
    public static PunRpcInfo? From(EventData ev)
    {
        if (ev.Code != PhotonCodes.PunEvent.Rpc) return null;
        if (!ev.Parameters.TryGetValue(PhotonCodes.Param.Data, out var d) || d is not IDictionary rpc) return null;

        int viewId = rpc.Contains(PhotonCodes.RpcKey.ViewId) && rpc[PhotonCodes.RpcKey.ViewId] is int v ? v : 0;
        string? method = rpc.Contains(PhotonCodes.RpcKey.MethodName) ? rpc[PhotonCodes.RpcKey.MethodName] as string : null;

        float? damage = null;
        byte[]? packet = null;
        if (rpc.Contains(PhotonCodes.RpcKey.Args) && rpc[PhotonCodes.RpcKey.Args] is object[] args)
        {
            foreach (var a in args)
                if (a is PhotonCustomData { Code: PhotonCodes.CustomType.DamagePacket } dp && dp.Data.Length >= 4)
                {
                    damage = BinaryPrimitives.ReadSingleBigEndian(dp.Data.AsSpan(0, 4));
                    packet = dp.Data;
                    break;
                }
        }
        return new PunRpcInfo(viewId, method, damage, packet);
    }
}
