using System.Buffers.Binary;

namespace BlackIce.Photon.Transport;

/// <summary>
/// One eNet command. The common header is 12 bytes (type, channel, flags, reserved, length int32,
/// reliableSeq int32). Unreliable (7) and unsequenced (11) commands carry one extra int32 after that
/// (unreliableSeq / unsequencedGroup), making their header 16 bytes; CommandLength includes it.
/// All multi-byte fields are big-endian. Fragments (8/15) are not handled yet.
/// </summary>
public sealed record NCommand(byte CommandType, byte ChannelId, byte Flags, byte ReservedByte,
                              int ReliableSequenceNumber, byte[] Payload)
{
    public const int ReliableHeaderSize = 12;
    public const int UnreliableHeaderSize = 16;
    public const int HeaderSize = ReliableHeaderSize;   // back-compat alias for reliable callers

    public const byte Acknowledge = 1, Connect = 2, VerifyConnect = 3, Disconnect = 4,
                      Ping = 5, SendReliable = 6, SendUnreliable = 7, SendFragment = 8,
                      SendUnsequenced = 11;

    public const byte FlagReliable = 1, FlagUnsequenced = 2;

    /// <summary>For type 7 the per-channel unreliable sequence; for type 11 the unsequenced group.
    /// Zero/unused for reliable commands.</summary>
    public int UnreliableSequenceNumber { get; init; }

    private static bool HasExtraSeq(byte type) => type is SendUnreliable or SendUnsequenced;

    public byte[] ToBytes()
    {
        int headerSize = HasExtraSeq(CommandType) ? UnreliableHeaderSize : ReliableHeaderSize;
        int total = headerSize + Payload.Length;
        var b = new byte[total];
        b[0] = CommandType; b[1] = ChannelId; b[2] = Flags; b[3] = ReservedByte;
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), total);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8), ReliableSequenceNumber);
        if (HasExtraSeq(CommandType))
            BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(12), UnreliableSequenceNumber);
        Payload.CopyTo(b.AsSpan(headerSize));
        return b;
    }

    public static NCommand Parse(ReadOnlySpan<byte> b, out int consumed)
    {
        byte type = b[0], channel = b[1], flags = b[2], reserved = b[3];
        int length = BinaryPrimitives.ReadInt32BigEndian(b[4..]);
        int reliableSeq = BinaryPrimitives.ReadInt32BigEndian(b[8..]);
        int headerSize = HasExtraSeq(type) ? UnreliableHeaderSize : ReliableHeaderSize;
        int extraSeq = HasExtraSeq(type) ? BinaryPrimitives.ReadInt32BigEndian(b[12..]) : 0;
        var payload = b[headerSize..length].ToArray();
        consumed = length;
        return new NCommand(type, channel, flags, reserved, reliableSeq, payload) { UnreliableSequenceNumber = extraSeq };
    }
}
