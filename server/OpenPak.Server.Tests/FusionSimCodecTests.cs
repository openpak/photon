using OpenPak.Server.LoadBalancing.Fusion;

namespace OpenPak.Server.Tests;

/// <summary>Message envelope bounds in <see cref="FusionSimCodec"/> (docs/protocol/08 §4.3).</summary>
public class FusionSimCodecTests
{
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(0x7FFFFFF9)]
    [InlineData(512 * 8)]
    public void A_message_length_beyond_511_bytes_is_malformed_even_where_rounding_would_overflow(int bits)
    {
        var writer = new NetBitWriter();
        writer.WriteInt32VarLength(bits, 10);
        writer.WriteInt32VarLength(0, 16);
        writer.WriteBits(0, 2);        // Source, Target: none
        writer.WriteBoolean(true);
        writer.WriteUInt16((ushort)SimMessageFlags.Unreliable);
        writer.WriteBytesAligned(new byte[16]);
        Assert.Throws<InvalidDataException>(() => FusionSimCodec.ReadMessage(new NetBitReader(writer.Bytes.ToArray())));
    }

    [Fact]
    public void A_511_byte_message_round_trips()
    {
        var message = new SimMessage { BitLength = 511 * 8, Payload = new byte[511], Flags = SimMessageFlags.Unreliable };
        message.Payload[510] = 0x5A;
        var writer = new NetBitWriter();
        FusionSimCodec.WriteMessage(writer, message, 7, 0);
        var read = FusionSimCodec.ReadMessage(new NetBitReader(writer.Bytes.ToArray()));
        Assert.Equal((511 * 8, 0x5A), (read.BitLength, read.Payload[510]));
    }
}
