using System.Buffers.Binary;

namespace OpenPak.Server.LoadBalancing.Fusion;

/// <summary>Byte 0 of every Fusion relay datagram (spec §3).</summary>
public enum NetPacketType : byte
{
    Command = 1,
    UnreliableData = 2,
    NotifyData = 3,
    NotifyAcks = 4,
    Unconnected = 5,
    MtuDiscoveryReq = 6,
    MtuDiscoveryRep = 7,
    NotifyReliableData = 8,
}

/// <summary>Byte 1 of a <see cref="NetPacketType.Command"/> datagram (spec §3.1).</summary>
public enum NetCommand : byte { Connect = 1, Accepted = 2, Refused = 3, Disconnect = 4, Ping = 5 }

public enum NetDisconnectReason : byte
{
    Unknown = 1,
    Timeout = 2,
    Requested = 3,
    SequenceOutOfBounds = 4,
    SendWindowFull = 5,
    ByRemote = 6,
}

public enum NetConnectFailedReason : byte { Timeout = 1, ServerFull = 2, ServerRefused = 3 }

/// <summary>An 8-byte <c>NetConnectionId</c>: Group int16 @0, GroupIndex int16 @2, Generation uint32 @4.</summary>
public readonly record struct NetConnectionId(short Group, short GroupIndex, uint Generation)
{
    public static NetConnectionId Read(ReadOnlySpan<byte> b) => new(
        BinaryPrimitives.ReadInt16LittleEndian(b), BinaryPrimitives.ReadInt16LittleEndian(b[2..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[4..]));

    public void Write(Span<byte> b)
    {
        BinaryPrimitives.WriteInt16LittleEndian(b, Group);
        BinaryPrimitives.WriteInt16LittleEndian(b[2..], GroupIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], Generation);
    }
}

/// <summary>The 14-byte header of types 3, 4 and 8 (spec §3.2). <see cref="Fragment"/> is 0, or a
/// 1-based piece index with 0x80 set on the last piece.</summary>
public readonly record struct NotifyHeader(NetPacketType Type, byte Fragment, ushort Sequence, ushort AckSequence, ulong AckMask);

/// <summary>The 48-byte <c>ReliableId</c> that prefixes every reliable-channel slice (spec §8). Only the
/// low 32 bits of the wire's 64-bit Sequence are significant. <see cref="Key"/> is 16 opaque bytes.</summary>
public sealed record ReliableId(uint Sequence, int SliceLength, int TotalLength, int Source, int SourceSend, int Target, byte[] Key);

/// <summary>A decoded Connect (spec §3.1). <see cref="UniqueId"/> is the 8 bytes as a little-endian uint64.</summary>
public sealed record ConnectCommand(NetConnectionId ConnectionId, byte[] Token, ulong UniqueId);

/// <summary>
/// Encoders/decoders for every Fusion relay transport structure (docs/protocol/07-fusion-relay-transport.md).
/// All fields little-endian. Decoders never throw on short or garbage input: commands are read the way
/// the SDK reads them (a short command reads zeros for its missing tail), and every length taken from
/// the wire is clamped to its fixed field.
/// </summary>
public static class FusionRelayCodec
{
    public const int ConnectSize = 152, AcceptedSize = 32, RefusedSize = 3, DisconnectSize = 136;
    public const int MaxTokenLength = 128, ReliableKeyLength = 16;
    public const int NotifyHeaderSize = 14, ReliableIdSize = 48;

    /// <summary>Largest unfragmented datagram, and the SDK's fragmentation threshold (§7).</summary>
    public const int MaxDatagramBytes = 1280;
    /// <summary>Payload bytes per fragment piece, as the SDK splits (§7).</summary>
    public const int FragmentChunkBytes = 1122;
    /// <summary>Largest notify payload (40 × 1122) and the receiver's reassembly buffer size (§7).</summary>
    public const int MaxNotifyPayloadBytes = 44_880, MaxReassemblyBytes = 51_200;
    public const byte FragmentEndBit = 0x80;
    /// <summary>Largest reliable slice: 14 + 48 + 1088 = 1150 keeps a slice under the fragment threshold.</summary>
    public const int MaxReliableSliceBytes = 1088;

    public static byte[] EncodeConnect(NetConnectionId connectionId, ReadOnlySpan<byte> token, ulong uniqueId)
    {
        if (token.Length > MaxTokenLength) throw new ArgumentException("token longer than 128 bytes", nameof(token));
        var b = NewCommand(ConnectSize, NetCommand.Connect);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), token.Length);
        connectionId.Write(b.AsSpan(8));
        token.CopyTo(b.AsSpan(16));
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(144), uniqueId);
        return b;
    }

    public static bool TryDecodeConnect(ReadOnlySpan<byte> datagram, out ConnectCommand connect)
    {
        connect = null!;
        if (!IsCommand(datagram, NetCommand.Connect)) return false;
        Span<byte> b = stackalloc byte[ConnectSize];
        CopyZeroPadded(datagram, b);
        var tokenLength = ClampTokenLength(BinaryPrimitives.ReadInt32LittleEndian(b[4..]));
        connect = new ConnectCommand(NetConnectionId.Read(b[8..]), b.Slice(16, tokenLength).ToArray(),
            BinaryPrimitives.ReadUInt64LittleEndian(b[144..]));
        return true;
    }

    public static byte[] EncodeAccepted(NetConnectionId localId, NetConnectionId remoteId, uint counter)
    {
        var b = NewCommand(AcceptedSize, NetCommand.Accepted);
        localId.Write(b.AsSpan(8));
        remoteId.Write(b.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), counter);
        return b;
    }

    public static bool TryDecodeAccepted(ReadOnlySpan<byte> datagram, out NetConnectionId localId, out NetConnectionId remoteId, out uint counter)
    {
        localId = remoteId = default;
        counter = 0;
        if (!IsCommand(datagram, NetCommand.Accepted)) return false;
        Span<byte> b = stackalloc byte[AcceptedSize];
        CopyZeroPadded(datagram, b);
        localId = NetConnectionId.Read(b[8..]);
        remoteId = NetConnectionId.Read(b[16..]);
        counter = BinaryPrimitives.ReadUInt32LittleEndian(b[24..]);
        return true;
    }

    public static byte[] EncodeRefused(NetConnectFailedReason reason)
    {
        var b = NewCommand(RefusedSize, NetCommand.Refused);
        b[2] = (byte)reason;
        return b;
    }

    public static bool TryDecodeRefused(ReadOnlySpan<byte> datagram, out NetConnectFailedReason reason)
    {
        reason = default;
        if (!IsCommand(datagram, NetCommand.Refused)) return false;
        reason = datagram.Length > 2 ? (NetConnectFailedReason)datagram[2] : 0;
        return true;
    }

    public static byte[] EncodeDisconnect(NetDisconnectReason reason, ReadOnlySpan<byte> token = default)
    {
        if (token.Length > MaxTokenLength) throw new ArgumentException("token longer than 128 bytes", nameof(token));
        var b = NewCommand(DisconnectSize, NetCommand.Disconnect);
        b[2] = (byte)reason;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), token.Length);
        token.CopyTo(b.AsSpan(8));
        return b;
    }

    public static bool TryDecodeDisconnect(ReadOnlySpan<byte> datagram, out NetDisconnectReason reason, out byte[] token)
    {
        reason = default;
        token = [];
        if (!IsCommand(datagram, NetCommand.Disconnect)) return false;
        Span<byte> b = stackalloc byte[DisconnectSize];
        CopyZeroPadded(datagram, b);
        reason = (NetDisconnectReason)b[2];
        token = b.Slice(8, ClampTokenLength(BinaryPrimitives.ReadInt32LittleEndian(b[4..]))).ToArray();
        return true;
    }

    public static void WriteNotifyHeader(Span<byte> b, NotifyHeader h)
    {
        b[0] = (byte)h.Type;
        b[1] = h.Fragment;
        BinaryPrimitives.WriteUInt16LittleEndian(b[2..], h.Sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(b[4..], h.AckSequence);
        BinaryPrimitives.WriteUInt64LittleEndian(b[6..], h.AckMask);
    }

    /// <summary>Reads the 14-byte notify header; false if the datagram is shorter than that.</summary>
    public static bool TryReadNotifyHeader(ReadOnlySpan<byte> datagram, out NotifyHeader header)
    {
        header = default;
        if (datagram.Length < NotifyHeaderSize) return false;
        header = new NotifyHeader((NetPacketType)datagram[0], datagram[1],
            BinaryPrimitives.ReadUInt16LittleEndian(datagram[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(datagram[4..]),
            BinaryPrimitives.ReadUInt64LittleEndian(datagram[6..]));
        return true;
    }

    /// <summary>The 14-byte ack-only packet (type 4): Sequence 0, nothing after the header.</summary>
    public static byte[] EncodeNotifyAcks(ushort ackSequence, ulong ackMask)
    {
        var b = new byte[NotifyHeaderSize];
        WriteNotifyHeader(b, new NotifyHeader(NetPacketType.NotifyAcks, 0, 0, ackSequence, ackMask));
        return b;
    }

    public static void WriteReliableId(Span<byte> b, ReliableId id)
    {
        b[..ReliableIdSize].Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(b, id.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(b[8..], id.SliceLength);
        BinaryPrimitives.WriteInt32LittleEndian(b[12..], id.TotalLength);
        BinaryPrimitives.WriteInt32LittleEndian(b[16..], id.Source);
        BinaryPrimitives.WriteInt32LittleEndian(b[20..], id.SourceSend);
        BinaryPrimitives.WriteInt32LittleEndian(b[24..], id.Target);
        id.Key.AsSpan(0, Math.Min(id.Key.Length, ReliableKeyLength)).CopyTo(b[28..]);
    }

    /// <summary>Reads a <c>ReliableId</c>; false if fewer than 48 bytes are available.</summary>
    public static bool TryReadReliableId(ReadOnlySpan<byte> b, out ReliableId id)
    {
        id = null!;
        if (b.Length < ReliableIdSize) return false;
        id = new ReliableId(
            (uint)BinaryPrimitives.ReadUInt64LittleEndian(b),   // only the low 32 bits are significant
            BinaryPrimitives.ReadInt32LittleEndian(b[8..]), BinaryPrimitives.ReadInt32LittleEndian(b[12..]),
            BinaryPrimitives.ReadInt32LittleEndian(b[16..]), BinaryPrimitives.ReadInt32LittleEndian(b[20..]),
            BinaryPrimitives.ReadInt32LittleEndian(b[24..]), b.Slice(28, ReliableKeyLength).ToArray());
        return true;
    }

    private static byte[] NewCommand(int size, NetCommand command)
    {
        var b = new byte[size];
        b[0] = (byte)NetPacketType.Command;
        b[1] = (byte)command;
        return b;
    }

    private static bool IsCommand(ReadOnlySpan<byte> datagram, NetCommand command) =>
        datagram.Length >= 2 && datagram[0] == (byte)NetPacketType.Command && datagram[1] == (byte)command;

    /// <summary>The SDK reads commands into a zeroed buffer without a length check, so a short command
    /// reads zeros for its tail; bytes beyond the struct are ignored.</summary>
    private static void CopyZeroPadded(ReadOnlySpan<byte> datagram, Span<byte> fixedSize)
    {
        fixedSize.Clear();
        datagram[..Math.Min(datagram.Length, fixedSize.Length)].CopyTo(fixedSize);
    }

    private static int ClampTokenLength(int length) => Math.Clamp(length, 0, MaxTokenLength);
}
