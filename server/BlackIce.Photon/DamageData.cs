using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;

namespace BlackIce.Photon;

/// <summary>
/// Single source of truth for the game's DamagePacket layout (custom type 68) and helpers to build or
/// rewrite damage-carrying RPCs. The layout comes from the project's protocol recon (docs/protocol):
/// a 41-byte packet whose first 4 bytes are a big-endian float damage value, and whose "combined"
/// bitfield at byte 39 carries Crit (bit0) and WeakPoint (bit1) — WeakPoint being Black Ice's headshot
/// equivalent. Centralizing this keeps the bot action script, the authority decoders, and the
/// server-side gameplay plugins all reading/writing one agreed format.
/// </summary>
public static class DamageData
{
    public const int PacketLength = 41;
    public const int DamageOffset = 0;
    public const int CombinedFlagsOffset = 39;
    public const byte CritBit = 0x01;
    public const byte WeakPointBit = 0x02;

    /// <summary>Builds a DamagePacket custom-data blob: big-endian float damage at offset 0, Crit/WeakPoint
    /// flags in the combined byte (39).</summary>
    public static PhotonCustomData BuildPacket(float damage, bool crit = false, bool weakPoint = false)
    {
        var b = new byte[PacketLength];
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(DamageOffset), damage);
        b[CombinedFlagsOffset] = (byte)((crit ? CritBit : 0) | (weakPoint ? WeakPointBit : 0));
        return new PhotonCustomData(PhotonCodes.CustomType.DamagePacket, b);
    }

    /// <summary>Builds a server-authored <c>TakeDamage</c> RPC (PUN event 200) targeting <paramref name="viewId"/>
    /// — the same shape the client sends, so a plugin can originate damage (e.g. reflection) the vanilla
    /// client applies. Args are <c>(viewId, DamagePacket)</c>, matching the observed call.</summary>
    public static EventData BuildTakeDamageRpc(int viewId, float damage, bool crit = false, bool weakPoint = false) =>
        new(PhotonCodes.PunEvent.Rpc, new Dictionary<byte, object>
        {
            { PhotonCodes.Param.Code, PhotonCodes.PunEvent.Rpc },
            { PhotonCodes.Param.Data, new Dictionary<object, object>
                {
                    { PhotonCodes.RpcKey.ViewId, viewId },
                    { PhotonCodes.RpcKey.MethodName, "TakeDamage" },
                    { PhotonCodes.RpcKey.Args, new object[] { viewId, BuildPacket(damage, crit, weakPoint) } },
                } },
        });

    /// <summary>
    /// Rewrites the damage value (and optionally the Crit/WeakPoint flags) of the first DamagePacket
    /// argument of a PUN RPC <b>in place</b>: the freshly-decoded event is owned by this relay pass, so
    /// mutating its args array is safe and avoids reconstructing the payload. Returns false (no change) if
    /// the event isn't an RPC carrying a DamagePacket. A null flag leaves that flag untouched.
    /// </summary>
    public static bool TryRewriteDamage(EventData ev, Func<float, float> scale, bool? forceCrit = null, bool? forceWeakPoint = null)
    {
        if (ev.Code != PhotonCodes.PunEvent.Rpc) return false;
        if (!ev.Parameters.TryGetValue(PhotonCodes.Param.Data, out var d) || d is not IDictionary rpc) return false;
        if (rpc[PhotonCodes.RpcKey.Args] is not object[] args) return false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is not PhotonCustomData { Code: PhotonCodes.CustomType.DamagePacket } p || p.Data.Length < 4) continue;

            // Clone before writing so we never mutate a byte[] another reference might share.
            var bytes = (byte[])p.Data.Clone();
            float dmg = BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(DamageOffset, 4));
            BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(DamageOffset, 4), scale(dmg));
            if (bytes.Length > CombinedFlagsOffset)
            {
                if (forceCrit == true) bytes[CombinedFlagsOffset] |= CritBit;
                else if (forceCrit == false) bytes[CombinedFlagsOffset] &= unchecked((byte)~CritBit);
                if (forceWeakPoint == true) bytes[CombinedFlagsOffset] |= WeakPointBit;
                else if (forceWeakPoint == false) bytes[CombinedFlagsOffset] &= unchecked((byte)~WeakPointBit);
            }
            args[i] = new PhotonCustomData(p.Code, bytes);
            return true;
        }
        return false;
    }
}
