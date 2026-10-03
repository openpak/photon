using OpenPak.Server.LoadBalancing.Fusion;

namespace OpenPak.Server.Tests;

/// <summary>
/// The transport bit buffer and packet codecs against every worked byte example in
/// docs/protocol/07-fusion-relay-transport.md (§3, §4.3) and the captured Connect/Accepted pair.
/// </summary>
public class FusionRelayCodecTests
{
    // Real Photon Cloud capture (Outbound, Steam, room JWN7NE): the client's Connect and the cloud's Accepted.
    // Same bytes as FusionRelayProtocolTests.RealCapturedConnect, laid out by field.
    private static readonly byte[] CapturedConnect = Convert.FromHexString(
        "01010000" + "00000000" + "0000000001000000" + new string('0', 256) + "980CE9BAA728637B");
    private static readonly byte[] CapturedAccepted = Convert.FromHexString(
        "0102000000000000000000000100000000000000010000000100000000000000");

    private static void AssertWrites(Action<NetBitWriter> write, int bits, string hex)
    {
        var w = new NetBitWriter();
        write(w);
        Assert.Equal(bits, w.BitCount);
        Assert.Equal(hex, Convert.ToHexString(w.Bytes));
    }

    // §4.3, one fact per table row.
    [Fact] public void Bool_byte_uint16() => AssertWrites(w => { w.WriteBoolean(true); w.WriteByte(0x5A); w.WriteUInt16(0x1234); }, 25, "B5682400");
    [Fact] public void Int32_minus_two() => AssertWrites(w => w.WriteInt32(-2), 32, "FEFFFFFF");
    [Fact] public void Int32_minus_two_in_8_bits() => AssertWrites(w => w.WriteInt32(-2, 8), 8, "FE");
    [Fact] public void Narrow_uint32_fields() => AssertWrites(w => { w.WriteUInt32(3, 3); w.WriteUInt32(0x1FF, 9); }, 12, "FB0F");
    [Fact] public void Leb_varint_300() => AssertWrites(w => w.WriteUInt32VarLength(300), 16, "AC02");
    [Fact] public void Leb_varint_unaligned() => AssertWrites(w => { w.WriteBoolean(false); w.WriteUInt32VarLength(300); }, 17, "580500");
    [Fact] public void Leb_varint_negative_int32() => AssertWrites(w => w.WriteInt32VarLength(-1), 40, "FFFFFFFF0F");
    [Fact] public void Block_varint_0_size_4() => AssertWrites(w => w.WriteUInt32VarLength(0, 4), 5, "01");
    [Fact] public void Block_varint_5_size_4() => AssertWrites(w => w.WriteUInt32VarLength(5, 4), 5, "0B");
    [Fact] public void Block_varint_300_size_4() => AssertWrites(w => w.WriteUInt32VarLength(300, 4), 15, "6409");
    [Fact] public void Block_varint_300_size_8() => AssertWrites(w => w.WriteUInt32VarLength(300, 8), 18, "B20400");
    [Fact] public void Bool_then_string() => AssertWrites(w => { w.WriteBoolean(true); w.WriteString("Hi"); }, 40, "0900004869");
    [Fact] public void Null_string() => AssertWrites(w => w.WriteString(null), 1, "01");
    [Fact] public void Empty_string_still_pads() => AssertWrites(w => w.WriteString(""), 24, "000000");
    [Fact] public void Bits_then_aligned_bytes() => AssertWrites(w => { w.WriteBits(0b101, 3); w.WriteBytesAligned([0xAA, 0xBB]); }, 24, "05AABB");
    [Fact] public void Single_one() => AssertWrites(w => w.WriteSingle(1.0f), 32, "0000803F");

    [Fact]
    public void Reader_reads_back_every_primitive()
    {
        var w = new NetBitWriter(4);   // small, so the writer has to grow
        w.WriteBoolean(true);
        w.WriteByte(0x5A);
        w.WriteInt32(-2, 8);
        w.WriteInt16(-3);
        w.WriteUInt64(0xDEADBEEFCAFEF00DUL);
        w.WriteSingle(1.5f);
        w.WriteDouble(-2.25);
        w.WriteUInt32VarLength(300);
        w.WriteInt32VarLength(-1);
        w.WriteUInt32VarLength(300, 4);
        w.WriteUInt64VarLength(ulong.MaxValue, 5);   // 65 value bits: one past 64
        w.WriteInt64VarLength(-7, 16);
        w.WriteString("Hé");
        w.WriteString(null);
        w.WriteBytesAligned([1, 2, 3]);

        var r = new NetBitReader(w.Bytes.ToArray());
        Assert.True(r.ReadBoolean());
        Assert.Equal(0x5A, r.ReadByte());
        Assert.Equal(254, r.ReadInt32(8));   // no sign extension
        Assert.Equal(-3, r.ReadInt16());
        Assert.Equal(0xDEADBEEFCAFEF00DUL, r.ReadUInt64());
        Assert.Equal(1.5f, r.ReadSingle());
        Assert.Equal(-2.25, r.ReadDouble());
        Assert.Equal(300u, r.ReadUInt32VarLength());
        Assert.Equal(-1, r.ReadInt32VarLength());
        Assert.Equal(300u, r.ReadUInt32VarLength(4));
        Assert.Equal(ulong.MaxValue, r.ReadUInt64VarLength(5));
        Assert.Equal(-7L, r.ReadInt64VarLength(16));
        Assert.Equal("Hé", r.ReadString());
        Assert.Null(r.ReadString());
        Assert.Equal(new byte[] { 1, 2, 3 }, r.ReadBytesAligned(3));
        Assert.Equal(0, r.BitsRemaining);
    }

    [Fact]
    public void Reader_throws_past_the_end_and_bounds_wire_lengths()
    {
        Assert.Throws<EndOfStreamException>(() => new NetBitReader([0xFF]).ReadBits(9));
        // A string claiming 0xFFFF bytes with none behind it must not allocate or read past the end.
        var w = new NetBitWriter();
        w.WriteBoolean(false);
        w.WriteUInt16(0xFFFF);
        Assert.Throws<EndOfStreamException>(() => new NetBitReader(w.Bytes.ToArray()).ReadString());
        // Block varint: running out while counting the unary prefix yields 0 (spec §4.2).
        Assert.Equal(0u, new NetBitReader([0x00, 0x00]).ReadUInt32VarLength(4));
    }

    [Fact]
    public void Captured_connect_decodes()
    {
        Assert.True(FusionRelayCodec.TryDecodeConnect(CapturedConnect, out var connect));
        Assert.Equal(new NetConnectionId(0, 0, 1), connect.ConnectionId);
        Assert.Empty(connect.Token);
        Assert.Equal(0x7B6328A7BAE90C98UL, connect.UniqueId);   // the 8 bytes as a little-endian uint64
        Assert.Equal(CapturedConnect, FusionRelayCodec.EncodeConnect(connect.ConnectionId, connect.Token, connect.UniqueId));
    }

    [Fact]
    public void Captured_accepted_encodes_and_decodes()
    {
        var id = new NetConnectionId(0, 0, 1);
        Assert.Equal(CapturedAccepted, FusionRelayCodec.EncodeAccepted(id, id, 1));
        Assert.True(FusionRelayCodec.TryDecodeAccepted(CapturedAccepted, out var local, out var remote, out var counter));
        Assert.Equal((id, id, 1u), (local, remote, counter));
    }

    [Fact]
    public void Refused_and_disconnect_examples()
    {
        Assert.Equal("010302", Convert.ToHexString(FusionRelayCodec.EncodeRefused(NetConnectFailedReason.ServerFull)));
        var disconnect = FusionRelayCodec.EncodeDisconnect(NetDisconnectReason.Requested);
        Assert.Equal("0104030000000000" + new string('0', 256), Convert.ToHexString(disconnect));
        Assert.True(FusionRelayCodec.TryDecodeRefused(Convert.FromHexString("010302"), out var refused));
        Assert.Equal(NetConnectFailedReason.ServerFull, refused);
    }

    [Fact]
    public void Commands_read_short_input_as_zero_padded_and_clamp_token_length()
    {
        // The SDK reads commands into a zeroed buffer without a length check.
        Assert.True(FusionRelayCodec.TryDecodeDisconnect(Convert.FromHexString("010403"), out var reason, out var token));
        Assert.Equal(NetDisconnectReason.Requested, reason);
        Assert.Empty(token);

        var oversized = FusionRelayCodec.EncodeConnect(new NetConnectionId(0, 0, 1), new byte[128], 5);
        BitConverter.GetBytes(int.MaxValue).CopyTo(oversized, 4);
        Assert.True(FusionRelayCodec.TryDecodeConnect(oversized, out var connect));
        Assert.Equal(128, connect.Token.Length);
        BitConverter.GetBytes(-5).CopyTo(oversized, 4);
        Assert.True(FusionRelayCodec.TryDecodeConnect(oversized, out connect));
        Assert.Empty(connect.Token);
    }

    [Fact]
    public void Notify_header_examples()
    {
        // §3.2: server NotifyData seq 1 acking client 1..5 with one payload byte, then the same acks ack-only.
        var data = new byte[15];
        FusionRelayCodec.WriteNotifyHeader(data, new NotifyHeader(NetPacketType.NotifyData, 0, 1, 5, 0x1F));
        Assert.Equal("030001000500" + "1F00000000000000" + "00", Convert.ToHexString(data));
        Assert.Equal("040000000500" + "1F00000000000000", Convert.ToHexString(FusionRelayCodec.EncodeNotifyAcks(5, 0x1F)));
        Assert.True(FusionRelayCodec.TryReadNotifyHeader(data, out var header));
        Assert.Equal(new NotifyHeader(NetPacketType.NotifyData, 0, 1, 5, 0x1F), header);
        Assert.False(FusionRelayCodec.TryReadNotifyHeader(data.AsSpan(0, 13), out _));
    }

    [Fact]
    public void Reliable_id_round_trips_with_a_32_bit_sequence()
    {
        var key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var id = new ReliableId(0xFFFFFFFF, 1088, 5000, -1, 7, 3, key);
        var bytes = new byte[FusionRelayCodec.ReliableIdSize];
        FusionRelayCodec.WriteReliableId(bytes, id);
        Assert.Equal("FFFFFFFF00000000" + "40040000" + "88130000" + "FFFFFFFF" + "07000000" + "03000000",
            Convert.ToHexString(bytes, 0, 28));
        Assert.True(FusionRelayCodec.TryReadReliableId(bytes, out var back));
        Assert.Equal((id.Sequence, id.SliceLength, id.TotalLength, id.Source, id.SourceSend, id.Target), (back.Sequence, back.SliceLength, back.TotalLength, back.Source, back.SourceSend, back.Target));
        Assert.Equal(key, back.Key);
        Assert.False(FusionRelayCodec.TryReadReliableId(bytes.AsSpan(0, 47), out _));
    }
}
