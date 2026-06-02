using System.Buffers.Binary;
using System.Collections;
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class ServerRpcTests
{
    private static IDictionary Rpc(EventData ev) =>
        (IDictionary)ev.Parameters[PhotonCodes.Param.Data];

    [Fact]
    public void Teleport_targets_the_actor_pawn_with_a_vector3_arg()
    {
        var ev = ServerRpc.Teleport(actor: 6, 520f, 3f, 469.5f);
        Assert.Equal(PhotonCodes.PunEvent.Rpc, ev.Code);

        var rpc = Rpc(ev);
        Assert.Equal(6 * 1000 + 1, rpc[PhotonCodes.RpcKey.ViewId]);            // pawn viewId
        Assert.Equal("TeleportImmediately", rpc[PhotonCodes.RpcKey.MethodName]);

        var args = (object[])rpc[PhotonCodes.RpcKey.Args]!;
        var pos = Assert.IsType<PhotonCustomData>(args[0]);
        Assert.Equal(PhotonCodes.CustomType.Vector3, pos.Code);
        Assert.Equal(520f, BinaryPrimitives.ReadSingleBigEndian(pos.Data.AsSpan(0)), 3);
        Assert.Equal(3f, BinaryPrimitives.ReadSingleBigEndian(pos.Data.AsSpan(4)), 3);
        Assert.Equal(469.5f, BinaryPrimitives.ReadSingleBigEndian(pos.Data.AsSpan(8)), 3);
    }

    [Fact]
    public void BecomeTangible_targets_the_actor_pawn_with_no_args()
    {
        var ev = ServerRpc.BecomeTangible(actor: 6);
        var rpc = Rpc(ev);
        Assert.Equal(6 * 1000 + 1, rpc[PhotonCodes.RpcKey.ViewId]);
        Assert.Equal("BecomeTangible", rpc[PhotonCodes.RpcKey.MethodName]);
        Assert.Empty((object[])rpc[PhotonCodes.RpcKey.Args]!);
    }
}
