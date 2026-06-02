using System;
using System.Buffers.Binary;
using BlackIce.Photon;
using Xunit;

namespace BlackIce.Photon.Tests;

public class PhotonCustomDataTests
{
    [Fact]
    public void Vector3_factory_encodes_three_big_endian_floats_with_the_vector3_code()
    {
        var v = PhotonCustomData.Vector3(520f, 3f, 469.5f);
        Assert.Equal(PhotonCodes.CustomType.Vector3, v.Code);
        Assert.Equal(12, v.Data.Length);
        Assert.Equal(520f, BinaryPrimitives.ReadSingleBigEndian(v.Data.AsSpan(0)), 3);
        Assert.Equal(3f, BinaryPrimitives.ReadSingleBigEndian(v.Data.AsSpan(4)), 3);
        Assert.Equal(469.5f, BinaryPrimitives.ReadSingleBigEndian(v.Data.AsSpan(8)), 3);
    }
}
