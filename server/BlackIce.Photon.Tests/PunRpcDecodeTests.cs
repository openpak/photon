using System.Buffers.Binary;
using System.Collections.Generic;
using BlackIce.Photon;
using Xunit;

namespace BlackIce.Photon.Tests;

public class PunRpcDecodeTests
{
    private static PhotonCustomData DamagePacket(float damage)
    {
        var b = new byte[41];                                  // 41-byte DamagePacket; damage at offset 0
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), damage);
        return new PhotonCustomData(68, b);
    }

    [Fact]
    public void Reads_named_rpc_method_and_damage_value()
    {
        var ev = new EventData(200, new()
        {
            { 245, new Dictionary<object, object>
                {
                    { (byte)0, 1001 },
                    { (byte)3, "TakeDamage" },
                    { (byte)4, new object[] { 5, DamagePacket(42.5f) } },
                } },
        });
        var info = PunRpcInfo.From(ev);
        Assert.True(info.HasValue);
        Assert.Equal("TakeDamage", info!.Value.Method);
        Assert.True(info.Value.DamageValue.HasValue);
        Assert.Equal(42.5f, info.Value.DamageValue!.Value, 3);
    }

    [Fact]
    public void Handles_shortcut_rpc_with_null_method_name()
    {
        var ev = new EventData(200, new()
        {
            { 245, new Dictionary<object, object>
                {
                    { (byte)0, 1001 },
                    { (byte)5, (byte)73 },                      // shortcut index, no name
                    { (byte)4, new object[] { DamagePacket(10f) } },
                } },
        });
        var info = PunRpcInfo.From(ev);
        Assert.True(info.HasValue);
        Assert.Null(info!.Value.Method);
        Assert.Equal(10f, info.Value.DamageValue!.Value, 3);
    }

    [Fact]
    public void Returns_null_for_non_rpc_events()
    {
        Assert.Null(PunRpcInfo.From(new EventData(201, new() { { 245, "not-an-rpc" } })));
        Assert.Null(PunRpcInfo.From(new EventData(255, new() { { 254, 1 } })));
    }

    [Fact]
    public void No_damage_value_when_args_have_no_damage_packet()
    {
        var ev = new EventData(200, new()
        {
            { 245, new Dictionary<object, object>
                {
                    { (byte)3, "Move" },
                    { (byte)4, new object[] { 1, 2, "x" } },
                } },
        });
        var info = PunRpcInfo.From(ev);
        Assert.True(info.HasValue);
        Assert.Null(info!.Value.DamageValue);
    }

#if PHOTON_ORACLE
    [Fact]
    public void DamagePacket_damage_float_survives_oracle_roundtrip()
    {
        var dp = DamagePacket(123.25f);
        var ourBytes = new GpBinaryWriter().WriteTyped(dp).ToArray();
        var decoded = Oracle.Deserialize(ourBytes);           // real DLL must accept code-68 slim custom
        Assert.NotNull(decoded);
        // And our reader recovers the damage float from the round-tripped bytes.
        var back = (PhotonCustomData)new GpBinaryReader(ourBytes).ReadTyped()!;
        Assert.Equal(123.25f, System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(back.Data.AsSpan(0, 4)), 3);
    }
#endif
}
