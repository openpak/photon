using System.Buffers.Binary;
using BlackIce.Photon;
using Xunit;

namespace BlackIce.Photon.Tests;

public class PositionDecodeTests
{
    private static PhotonCustomData Vec3(float x, float y, float z)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), x);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4), y);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        return new PhotonCustomData(86, b);
    }

    [Fact]
    public void Reads_viewid_and_xyz_from_a_201_batch()
    {
        var view = new object[] { 2001, false, null!, Vec3(10f, 0f, -5f), new PhotonCustomData(81, new byte[16]) };
        var batch = new object[] { 123, null!, view };
        var ev = new EventData(201, new() { { 245, batch } });

        var p = PositionInfo.From(ev);
        Assert.True(p.HasValue);
        Assert.Equal(2001, p!.Value.ViewId);
        Assert.Equal(10f, p.Value.X, 3);
        Assert.Equal(-5f, p.Value.Z, 3);
    }

    [Fact]
    public void Returns_null_for_non_201_or_malformed()
    {
        Assert.Null(PositionInfo.From(new EventData(200, new() { { 245, "x" } })));
        Assert.Null(PositionInfo.From(new EventData(201, new() { { 245, "not-a-batch" } })));
    }
}
