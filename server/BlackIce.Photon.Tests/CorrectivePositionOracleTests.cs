using BlackIce.Photon;
using Xunit;
using PhotonEventData = ExitGames.Client.Photon.EventData;

namespace BlackIce.Photon.Tests;

/// <summary>
/// Phase 3a interop gate: the authority layer snap-corrects a teleport by Rewriting a 201 carrying the
/// last-good position, built via <see cref="PositionInfo.BuildEvent"/>. A corrective event the client
/// cannot decode is worse than forwarding, so this round-trips the builder's output through the real
/// Photon3Unity3D.dll oracle and asserts the oracle reads back the exact viewID + XYZ we wrote. Mirrors
/// the decode pattern in <see cref="BotPayloadOracleTests"/> (which round-trips the bot 201 payload).
/// </summary>
public class CorrectivePositionOracleTests
{
    private const byte PData = 245;

    private static (int viewId, float x, float y, float z) DecodeWithOracle(EventData ev)
    {
        var ours = MessageSerializer.SerializeEvent(ev);
        var decoded = (PhotonEventData)Oracle.DeserializeMessage(ours);

        var batch = Assert.IsType<object[]>(decoded.Parameters[PData]);
        var view = Assert.IsType<object[]>(batch[2]);
        int viewId = Assert.IsType<int>(view[0]);

        foreach (var field in view)
        {
            // The oracle decodes our code-86 custom value as a registered passthrough carrying the raw
            // 12-byte body; recover it by reflecting the stand-in's Data field (same shape the fixture uses).
            if (field is null) continue;
            var dataField = field.GetType().GetField("Data");
            if (dataField?.GetValue(field) is byte[] b && b.Length >= 12)
            {
                float x = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(0, 4));
                float y = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(4, 4));
                float z = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(8, 4));
                return (viewId, x, y, z);
            }
        }
        throw new Xunit.Sdk.XunitException("no Vec3 (code 86) custom value found in the decoded corrective event");
    }

    [Theory]
    [InlineData(1001, 5.0f, 6.0f, 7.0f)]
    [InlineData(42, -12.5f, 0f, 88.75f)]
    public void Oracle_decodes_a_corrective_position_event(int viewId, float x, float y, float z)
    {
        var (dvid, dx, dy, dz) = DecodeWithOracle(PositionInfo.BuildEvent(viewId, x, y, z));
        Assert.Equal(viewId, dvid);
        Assert.Equal(x, dx, 3);
        Assert.Equal(y, dy, 3);
        Assert.Equal(z, dz, 3);
    }

    [Fact]
    public void Builder_output_decodes_with_our_own_position_info()
    {
        var ev = PositionInfo.BuildEvent(1001, 5f, 6f, 7f);
        var info = PositionInfo.From(ev);
        Assert.NotNull(info);
        Assert.Equal(1001, info!.Value.ViewId);
        Assert.Equal(5f, info.Value.X, 3);
        Assert.Equal(6f, info.Value.Y, 3);
        Assert.Equal(7f, info.Value.Z, 3);
    }
}
