using System;
using System.Buffers.Binary;

namespace BlackIce.Photon;

/// <summary>
/// A registered Photon custom type as it appears on the wire: a 1-byte type code plus its raw
/// serialized bytes (e.g. PUN's Vector3 = code 86, three big-endian floats). Phase 1 preserves
/// these verbatim so the stream stays aligned; decoding specific custom types is a later concern.
/// </summary>
public sealed record PhotonCustomData(byte Code, byte[] Data)
{
    /// <summary>Builds a PUN Vector3 (custom type 86): three big-endian floats. The single Vector3
    /// encoder shared by server-authored RPCs (respawn teleports) and the soak bots.</summary>
    public static PhotonCustomData Vector3(float x, float y, float z)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), x);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4), y);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        return new PhotonCustomData(PhotonCodes.CustomType.Vector3, b);
    }
}
