using System.Buffers.Binary;

namespace BlackIce.Photon;

/// <summary>
/// The absolute position of one networked view, decoded from a PUN serialize batch (Photon event 201).
/// The batch under PData(245) is object[] { networkTime, prefix, perViewEntry... }; a per-view entry is
/// object[] { viewId, bool, null, Vector3(custom 86 = 3 big-endian floats), Quaternion(81), ... }.
/// </summary>
public readonly record struct PositionInfo(int ViewId, float X, float Y, float Z)
{
    public static PositionInfo? From(EventData ev)
    {
        if (ev.Code != PhotonCodes.PunEvent.SendSerialize) return null;
        if (!ev.Parameters.TryGetValue(PhotonCodes.Param.Data, out var d) || d is not object[] batch) return null;

        // Per-view entries start at index 2 (after networkTime + prefix).
        for (int i = 2; i < batch.Length; i++)
        {
            if (batch[i] is not object[] view || view.Length < 4) continue;
            int viewId = view[0] is int v ? v : 0;
            foreach (var field in view)
                if (field is PhotonCustomData { Code: PhotonCodes.CustomType.Vector3 } vec && vec.Data.Length >= 12)
                {
                    float x = BinaryPrimitives.ReadSingleBigEndian(vec.Data.AsSpan(0, 4));
                    float y = BinaryPrimitives.ReadSingleBigEndian(vec.Data.AsSpan(4, 4));
                    float z = BinaryPrimitives.ReadSingleBigEndian(vec.Data.AsSpan(8, 4));
                    return new PositionInfo(viewId, x, y, z);
                }
        }
        return null;
    }
}
