namespace OpenPak.Server.LoadBalancing.Fusion;

/// <summary><c>SimulationMessage</c> flag bits (docs/protocol/08-fusion-shared-simulation.md §4.3).</summary>
[Flags]
public enum SimMessageFlags
{
    None = 0,
    UserMessage = 1,
    Remote = 2,
    Static = 4,
    Unreliable = 8,
    TargetPlayer = 16,
    TargetServer = 32,
    Internal = 64,
    NotTickAligned = 128,
    Dummy = 256,
}

/// <summary>One simulation message as read from or written to the wire (spec §4.3). Player references
/// are <i>indices</i> (= actor numbers on this server); -1 = none.</summary>
public sealed class SimMessage
{
    public int BitLength;
    public int Tick;
    public int Source = -1;
    public int Target = -1;
    public SimMessageFlags Flags;
    public ulong Sequence;
    public byte[] Payload = [];

    public bool IsReliable => (Flags & SimMessageFlags.Unreliable) == 0;
}

/// <summary>
/// The bit-level encodings of Fusion's simulation layer that are not object records (spec §3-§4): the
/// 64-bit packet header, message envelopes, PlayerRef wire form, TimeFeedback and zig-zag helpers.
/// Readers throw <see cref="InvalidDataException"/> or <see cref="EndOfStreamException"/> on malformed
/// input; the caller drops the rest of the packet.
/// </summary>
public static class FusionSimCodec
{
    /// <summary>A message payload is under 512 bytes (spec §4.8); anything longer is malformed.</summary>
    public const int MaxMessagePayloadBytes = 511;
    public const int HeaderBits = 64;

    public static uint ZigZag32(int value) => (uint)((value << 1) ^ (value >> 31));
    public static int UnZigZag32(uint value) => (int)(value >> 1) ^ -(int)(value & 1);
    public static ulong ZigZag64(long value) => (ulong)((value << 1) ^ (value >> 63));
    public static long UnZigZag64(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    /// <summary>Header word / ordinary data word: <c>vb(6)</c> of the sign-extended word, zig-zagged.</summary>
    public static void WriteWord(NetBitWriter writer, int word) => writer.WriteUInt64VarLength(ZigZag64(word), 6);
    public static int ReadWord(NetBitReader reader) => (int)UnZigZag64(reader.ReadUInt64VarLength(6));

    public static (int Messages, int Inputs, int ObjectUpdates, int ObjectDestroys, int Tick) ReadHeader(NetBitReader reader) =>
        (reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadInt32());

    /// <summary>Fills in the header reserved at <paramref name="bitPosition"/> once the counts are known.</summary>
    public static void PatchHeader(NetBitWriter writer, int bitPosition, int messages, int objectUpdates, int objectDestroys, int tick)
    {
        var value = (ulong)(byte)messages | (ulong)(byte)objectUpdates << 16 | (ulong)(byte)objectDestroys << 24 | (ulong)(uint)tick << 32;
        writer.Overwrite(bitPosition, value, HeaderBits);
    }

    /// <summary>PlayerRef wire form: <c>bool isRealPlayer</c>, then the index as a <c>v7</c>. -1 = not a real player.</summary>
    public static void WritePlayer(NetBitWriter writer, int index)
    {
        writer.WriteBoolean(index >= 0);
        if (index >= 0) writer.WriteUInt32VarLength((uint)index);
    }

    public static int ReadPlayer(NetBitReader reader)
    {
        if (!reader.ReadBoolean()) return -1;
        var index = reader.ReadUInt32VarLength();
        // A PlayerRef index is a 31-bit value; anything larger cannot name a player.
        if (index > int.MaxValue - 1) throw new InvalidDataException($"player index {index} out of range");
        return (int)index;
    }

    /// <summary>One message envelope (spec §4.3). The receiver's REMOTE bit is ORed in.</summary>
    public static SimMessage ReadMessage(NetBitReader reader)
    {
        var message = new SimMessage { BitLength = reader.ReadInt32VarLength(10), Tick = reader.ReadInt32VarLength(16) };
        message.Source = ReadPlayer(reader);
        message.Target = ReadPlayer(reader);
        var flags = reader.ReadBoolean() ? reader.ReadUInt16() : 0;
        if ((flags & (int)SimMessageFlags.UserMessage) != 0 && reader.ReadBoolean())
            throw new InvalidDataException("message carries user flags");   // the SDK never sends them
        message.Flags = (SimMessageFlags)flags | SimMessageFlags.Remote;
        if (message.IsReliable) message.Sequence = reader.ReadUInt64VarLength(16);
        var bytes = (message.BitLength + 7) / 8;
        if (message.BitLength < 0 || bytes > MaxMessagePayloadBytes)
            throw new InvalidDataException($"message payload of {message.BitLength} bits");
        // An empty payload has no pad before it (spec §4.3).
        if (bytes > 0) message.Payload = reader.ReadBytesAligned(bytes);
        return message;
    }

    /// <summary>Writes <paramref name="message"/> with the sender's tick and sequence, REMOTE cleared. The server
    /// leaves <paramref name="target"/> at none: a receiver fills in itself when TARGET_PLAYER is set (§12.3).</summary>
    public static void WriteMessage(NetBitWriter writer, SimMessage message, int tick, ulong sequence, int target = -1)
    {
        writer.WriteInt32VarLength(message.BitLength, 10);
        writer.WriteInt32VarLength(tick, 16);
        WritePlayer(writer, message.Source);
        WritePlayer(writer, target);
        var flags = (ushort)(message.Flags & ~SimMessageFlags.Remote);
        writer.WriteBoolean(flags != 0);
        if (flags != 0) writer.WriteUInt16(flags);
        if ((flags & (int)SimMessageFlags.UserMessage) != 0) writer.WriteBoolean(false);
        if (message.IsReliable) writer.WriteUInt64VarLength(sequence, 16);
        if (message.Payload.Length > 0) writer.WriteBytesAligned(message.Payload);
    }

    /// <summary>TimeFeedback (spec §4.4): four seconds values as <c>vb(6)</c> of zig-zagged 1/256 fixed point.</summary>
    public static void WriteTimeFeedback(NetBitWriter writer, double offsetAvg, double offsetDev, double recvDeltaAvg, double recvDeltaDev)
    {
        foreach (var seconds in (ReadOnlySpan<double>)[offsetAvg, offsetDev, recvDeltaAvg, recvDeltaDev])
        {
            var fixedPoint = (int)Math.Clamp(Math.Round(seconds * 256, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);
            writer.WriteUInt32VarLength(ZigZag32(fixedPoint), 6);
        }
    }
}
