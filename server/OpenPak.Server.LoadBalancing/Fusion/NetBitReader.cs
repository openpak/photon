using System.Text;

namespace OpenPak.Server.LoadBalancing.Fusion;

/// <summary>
/// Reads Fusion's transport bit buffer — the mirror of <see cref="NetBitWriter"/> (spec §4). Reading
/// never sign-extends: a signed field written narrower than its type reads back as its unsigned low
/// bits. A read past the end throws <see cref="EndOfStreamException"/>; the transport hands this reader
/// to the simulation layer, whose own handler is expected to catch that and drop the packet (§4.2).
/// Every length taken from the stream is checked against what is left before anything is allocated.
/// </summary>
public sealed class NetBitReader
{
    private readonly byte[] _buffer;
    private readonly int _endBit;

    /// <summary>Reads <paramref name="buffer"/> from <paramref name="startBit"/> to its end.</summary>
    public NetBitReader(byte[] buffer, int startBit = 0)
    {
        _buffer = buffer;
        _endBit = buffer.Length * 8;
        if ((uint)startBit > (uint)_endBit) throw new ArgumentOutOfRangeException(nameof(startBit));
        BitPosition = startBit;
    }

    public int BitPosition { get; private set; }

    /// <summary>Bits left, including up to 7 zero padding bits the writer's last byte may carry.</summary>
    public int BitsRemaining => _endBit - BitPosition;

    /// <summary>Reads <paramref name="bits"/> (0..64) bits, low bit first.</summary>
    public ulong ReadBits(int bits)
    {
        if ((uint)bits > 64) throw new ArgumentOutOfRangeException(nameof(bits));
        if (bits > BitsRemaining) throw new EndOfStreamException($"read of {bits} bits with {BitsRemaining} left");
        ulong value = 0;
        var done = 0;
        while (done < bits)
        {
            var bitOffset = BitPosition & 7;
            var take = Math.Min(8 - bitOffset, bits - done);
            var chunk = (ulong)(_buffer[BitPosition >> 3] >> bitOffset) & ((1UL << take) - 1);
            value |= chunk << done;
            done += take;
            BitPosition += take;
        }
        return value;
    }

    public bool ReadBoolean() => ReadBits(1) != 0;
    public byte ReadByte(int bits = 8) => (byte)ReadBits(CheckWidth(bits, 8));
    public short ReadInt16(int bits = 16) => (short)ReadBits(CheckWidth(bits, 16));
    public ushort ReadUInt16(int bits = 16) => (ushort)ReadBits(CheckWidth(bits, 16));
    public int ReadInt32(int bits = 32) => (int)ReadBits(CheckWidth(bits, 32));
    public uint ReadUInt32(int bits = 32) => (uint)ReadBits(CheckWidth(bits, 32));
    public long ReadInt64(int bits = 64) => (long)ReadBits(CheckWidth(bits, 64));
    public ulong ReadUInt64(int bits = 64) => ReadBits(CheckWidth(bits, 64));
    public float ReadSingle() => BitConverter.UInt32BitsToSingle((uint)ReadBits(32));
    public double ReadDouble() => BitConverter.UInt64BitsToDouble(ReadBits(64));

    /// <summary>LEB128-style varint (see <see cref="NetBitWriter.WriteUInt32VarLength(uint)"/>). Stops
    /// after 5 bytes even if the fifth still says "more follows": a uint32 never needs a sixth.</summary>
    public uint ReadUInt32VarLength()
    {
        uint value = 0;
        for (var i = 0; i < 5; i++)
        {
            var group = (uint)ReadBits(8);
            value |= (group & 0x7F) << (7 * i);
            if ((group & 0x80) == 0) break;
        }
        return value;
    }

    public int ReadInt32VarLength() => (int)ReadUInt32VarLength();

    public uint ReadUInt32VarLength(int blockSize) => (uint)ReadBlockVarLength(blockSize);
    public int ReadInt32VarLength(int blockSize) => (int)ReadBlockVarLength(blockSize);
    public ulong ReadUInt64VarLength(int blockSize) => ReadBlockVarLength(blockSize);
    public long ReadInt64VarLength(int blockSize) => (long)ReadBlockVarLength(blockSize);

    /// <summary>Skips to the next multiple of 8 bits (no-op when already aligned).</summary>
    public void PadToByteBoundary() => Skip((8 - (BitPosition & 7)) & 7);

    /// <summary>Pads to a byte boundary, then copies <paramref name="count"/> bytes.</summary>
    public byte[] ReadBytesAligned(int count)
    {
        PadToByteBoundary();
        if (count < 0 || count > BitsRemaining / 8) throw new EndOfStreamException($"aligned read of {count} bytes with {BitsRemaining / 8} left");
        var bytes = _buffer.AsSpan(BitPosition >> 3, count).ToArray();
        BitPosition += 8 * count;
        return bytes;
    }

    public string? ReadString()
    {
        if (ReadBoolean()) return null;
        var length = ReadUInt16();
        return Encoding.UTF8.GetString(ReadBytesAligned(length));
    }

    /// <summary>Counts 0-bits up to the first 1-bit (that many + 1 blocks), then reads the value. Running
    /// out of buffer while counting yields 0, as the SDK reader does.</summary>
    private ulong ReadBlockVarLength(int blockSize)
    {
        blockSize = Math.Clamp(blockSize, 2, 16);
        var blocks = 1;
        while (true)
        {
            if (BitsRemaining == 0) return 0;
            if (ReadBits(1) != 0) break;
            blocks++;
        }
        var valueBits = (long)blocks * blockSize;
        var value = ReadBits((int)Math.Min(valueBits, 64));
        // Anything past 64 bits cannot carry value; a long zero run only makes this a bounds-checked skip.
        if (valueBits > 64) Skip(valueBits - 64);
        return value;
    }

    private void Skip(long bits)
    {
        if (bits > BitsRemaining) throw new EndOfStreamException($"skip of {bits} bits with {BitsRemaining} left");
        BitPosition += (int)bits;
    }

    private static int CheckWidth(int bits, int max) =>
        bits >= 0 && bits <= max ? bits : throw new ArgumentOutOfRangeException(nameof(bits));
}
