using BlackIce.Photon.Transport;
using Xunit;

namespace BlackIce.Photon.Tests;

public class TransportTests
{
    [Fact]
    public void PacketHeader_roundtrips()
    {
        var h = new PhotonHeader(PeerId: 7, CrcEnabled: false, CommandCount: 3, ServerTime: 123456, Challenge: -42);
        var buf = new byte[PhotonHeader.Size];
        h.WriteTo(buf);
        Assert.Equal(h, PhotonHeader.ReadFrom(buf));
    }

    [Fact]
    public void PacketHeader_is_big_endian()
    {
        var buf = new byte[PhotonHeader.Size];
        new PhotonHeader(0, false, 0, 0x01020304, 0).WriteTo(buf);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, buf[4..8]); // ServerTime, network order
    }

    [Fact]
    public void Command_roundtrips()
    {
        var c = new NCommand(NCommand.SendReliable, ChannelId: 0, Flags: NCommand.FlagReliable, ReservedByte: 4,
                             ReliableSequenceNumber: 5, Payload: new byte[] { 9, 9, 9 });
        var bytes = c.ToBytes();
        var parsed = NCommand.Parse(bytes, out int consumed);
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(c.CommandType, parsed.CommandType);
        Assert.Equal(c.ReliableSequenceNumber, parsed.ReliableSequenceNumber);
        Assert.Equal(c.Payload, parsed.Payload);
    }

    private static NCommand Reliable(byte type, byte channel, int seq) =>
        new(type, channel, NCommand.FlagReliable, ReservedByte: 4, seq, Array.Empty<byte>());

    private static NCommand? AckFor(IEnumerable<NCommand> outgoing, byte channel, int seq) =>
        outgoing.FirstOrDefault(c => c.CommandType == NCommand.Acknowledge
                                     && c.ChannelId == channel && c.ReliableSequenceNumber == seq);

    [Theory]
    [InlineData((byte)6)]   // CT_SENDRELIABLE
    [InlineData((byte)5)]   // CT_PING
    [InlineData((byte)12)]  // CT_EG_SERVERTIME — the one that was silently killing sessions
    public void Every_reliable_command_is_acknowledged(byte type)
    {
        var peer = new EnetPeer();
        var outgoing = peer.HandleCommand(Reliable(type, channel: 255, seq: 7), incomingSentTime: 42, out _);
        Assert.NotNull(AckFor(outgoing, channel: 255, seq: 7));
    }

    [Fact]
    public void Reliable_connect_is_both_acked_and_verified()
    {
        var peer = new EnetPeer();
        var outgoing = peer.HandleCommand(Reliable(NCommand.Connect, channel: 255, seq: 1), incomingSentTime: 0, out _);
        Assert.NotNull(AckFor(outgoing, channel: 255, seq: 1));
        Assert.Contains(outgoing, c => c.CommandType == NCommand.VerifyConnect);
    }

    [Fact]
    public void Acknowledge_command_is_not_itself_acked()
    {
        var peer = new EnetPeer();
        var ack = new NCommand(NCommand.Acknowledge, ChannelId: 0, Flags: 0, ReservedByte: 4, 3, new byte[8]);
        var outgoing = peer.HandleCommand(ack, incomingSentTime: 0, out _);
        Assert.DoesNotContain(outgoing, c => c.CommandType == NCommand.Acknowledge);
    }

    [Fact]
    public void Unreliable_command_is_not_acked()
    {
        var peer = new EnetPeer();
        var unreliable = new NCommand(NCommand.SendUnreliable, ChannelId: 0, Flags: 0, ReservedByte: 4, 1, new byte[] { 1 });
        var outgoing = peer.HandleCommand(unreliable, incomingSentTime: 0, out _);
        Assert.DoesNotContain(outgoing, c => c.CommandType == NCommand.Acknowledge);
    }

    [Fact]
    public void Ping_is_reliable_on_control_channel_with_incrementing_seq()
    {
        var peer = new EnetPeer();
        var p1 = peer.Ping();
        var p2 = peer.Ping();
        Assert.Equal(NCommand.Ping, p1.CommandType);
        Assert.Equal((byte)0xFF, p1.ChannelId);
        Assert.Equal(NCommand.FlagReliable, (byte)(p1.Flags & NCommand.FlagReliable));
        Assert.True(p2.ReliableSequenceNumber > p1.ReliableSequenceNumber, "control-channel seq must advance");
    }

    [Fact]
    public void Unreliable_command_roundtrips_with_its_own_sequence_field()
    {
        var c = new NCommand(NCommand.SendUnreliable, ChannelId: 0, Flags: 0, ReservedByte: 4,
                             ReliableSequenceNumber: 51, Payload: new byte[] { 0xF3, 0x04, 1, 2, 3 })
                { UnreliableSequenceNumber = 1234 };
        var bytes = c.ToBytes();
        var parsed = NCommand.Parse(bytes, out int consumed);
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(NCommand.SendUnreliable, parsed.CommandType);
        Assert.Equal(51, parsed.ReliableSequenceNumber);
        Assert.Equal(1234, parsed.UnreliableSequenceNumber);
        Assert.Equal(new byte[] { 0xF3, 0x04, 1, 2, 3 }, parsed.Payload);
    }

    [Fact]
    public void Unreliable_header_is_16_bytes_so_payload_is_not_corrupted()
    {
        var payload = new byte[] { 0xF3, 0x04, 9, 9, 9, 9 };
        var unrel = new NCommand(NCommand.SendUnreliable, 0, 0, 4, 50, payload) { UnreliableSequenceNumber = 7 };
        var parsed = NCommand.Parse(unrel.ToBytes(), out _);
        Assert.Equal(payload, parsed.Payload);
    }

    [Fact]
    public void Reliable_command_still_uses_a_12_byte_header()
    {
        var c = new NCommand(NCommand.SendReliable, 0, NCommand.FlagReliable, 4, 5, new byte[] { 1, 2, 3 });
        var parsed = NCommand.Parse(c.ToBytes(), out int consumed);
        Assert.Equal(c.ToBytes().Length, consumed);
        Assert.Equal(new byte[] { 1, 2, 3 }, parsed.Payload);
        Assert.Equal(0, parsed.UnreliableSequenceNumber);
    }

    [Fact]
    public void Inbound_unreliable_command_surfaces_its_payload()
    {
        var peer = new EnetPeer();
        var cmd = new NCommand(NCommand.SendUnreliable, 0, 0, 4, 50, new byte[] { 0xF3, 0x04, 1 }) { UnreliableSequenceNumber = 9 };
        var outgoing = peer.HandleCommand(cmd, incomingSentTime: 0, out var payload);
        Assert.Equal(new byte[] { 0xF3, 0x04, 1 }, payload);
        Assert.DoesNotContain(outgoing, c => c.CommandType == NCommand.Acknowledge);
    }

    [Fact]
    public void WrapUnreliable_stamps_increasing_per_channel_unreliable_seq()
    {
        var peer = new EnetPeer();
        var c1 = peer.WrapUnreliable(new byte[] { 1 }, channel: 0);
        var c2 = peer.WrapUnreliable(new byte[] { 2 }, channel: 0);
        Assert.Equal(NCommand.SendUnreliable, c1.CommandType);
        Assert.Equal((byte)0, c1.Flags);
        Assert.True(c2.UnreliableSequenceNumber > c1.UnreliableSequenceNumber, "per-channel unreliable seq must advance");
    }

    [Fact]
    public void Type7_serializes_to_exact_wire_bytes()
    {
        // Byte-exact against the real Photon3Unity3D transport (verified by interop review):
        // 16-byte header (extra unreliableSeq int32 BE at offset 12), length includes it.
        var c = new NCommand(NCommand.SendUnreliable, ChannelId: 0, Flags: 0, ReservedByte: 4,
                             ReliableSequenceNumber: 0x11223344, Payload: new byte[] { 0xF3, 0x04, 0xAA, 0xBB, 0xCC })
                { UnreliableSequenceNumber = 0x55667788 };
        var expected = new byte[]
        {
            0x07, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x15,
            0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
            0xF3, 0x04, 0xAA, 0xBB, 0xCC,
        };
        Assert.Equal(expected, c.ToBytes());
    }

    [Fact]
    public void Type6_serializes_to_exact_wire_bytes()
    {
        var c = new NCommand(NCommand.SendReliable, ChannelId: 0, Flags: NCommand.FlagReliable, ReservedByte: 4,
                             ReliableSequenceNumber: 5, Payload: new byte[] { 0x01, 0x02, 0x03 });
        var expected = new byte[]
        {
            0x06, 0x00, 0x01, 0x04, 0x00, 0x00, 0x00, 0x0F,
            0x00, 0x00, 0x00, 0x05, 0x01, 0x02, 0x03,
        };
        Assert.Equal(expected, c.ToBytes());
    }
}
