using System.Numerics;
using System.Text;

namespace OpenPak.Server.LoadBalancing.Fusion;

/// <summary>
/// Writes Fusion's transport bit buffer (<c>NetBitBuffer</c>): fields packed LSB-first, low bit of
/// each field first, so stream bit <i>k</i> is bit <c>k % 8</c> of byte <c>k / 8</c>. A value written
/// in <i>n</i> bits is masked to its low <i>n</i> bits. The encoded length is <see cref="ByteCount"/>;
/// the bit count itself is never transmitted. See docs/protocol/07-fusion-relay-transport.md §4.
///
/// Not the event-100 serializer (<c>FusionBitWriter</c>): this one pads to a byte boundary before
/// string bytes and aligned byte runs (§4.4).
/// </summary>
public sealed class NetBitWriter
{
    private byte[] _buffer;

    public NetBitWriter(int initialCapacityBytes = 64) => _buffer = new byte[Math.Max(initialCapacityBytes, 8)];

    /// <summary>Bits written so far.</summary>
    public int BitCount { get; private set; }

    /// <summary>Encoded length: <c>ceil(BitCount / 8)</c>.</summary>
    public int ByteCount => (BitCount + 7) >> 3;

    /// <summary>The encoded bytes; unused high bits of the last byte are zero.</summary>
    public ReadOnlySpan<byte> Bytes => _buffer.AsSpan(0, ByteCount);

    /// <summary>Writes the low <paramref name="bits"/> (0..64) of <paramref name="value"/>, low bit first.</summary>
    public void WriteBits(ulong value, int bits)
    {
        if ((uint)bits > 64) throw new ArgumentOutOfRangeException(nameof(bits));
        if (bits == 0) return;
        EnsureCapacity(BitCount + bits);
        if (bits < 64) value &= (1UL << bits) - 1;
        var remaining = bits;
        while (remaining > 0)
        {
            var byteIndex = BitCount >> 3;
            var bitOffset = BitCount & 7;
            var take = Math.Min(8 - bitOffset, remaining);
            // OR is safe: bytes past the write position are always zero (fresh or resized array).
            _buffer[byteIndex] |= (byte)((value & ((1UL << take) - 1)) << bitOffset);
            value >>= take;
            BitCount += take;
            remaining -= take;
        }
    }

    public void WriteBoolean(bool value) => WriteBits(value ? 1UL : 0UL, 1);
    public void WriteByte(byte value, int bits = 8) => WriteBits(value, CheckWidth(bits, 8));
    public void WriteInt16(short value, int bits = 16) => WriteBits((ushort)value, CheckWidth(bits, 16));
    public void WriteUInt16(ushort value, int bits = 16) => WriteBits(value, CheckWidth(bits, 16));
    public void WriteInt32(int value, int bits = 32) => WriteBits((uint)value, CheckWidth(bits, 32));
    public void WriteUInt32(uint value, int bits = 32) => WriteBits(value, CheckWidth(bits, 32));
    public void WriteInt64(long value, int bits = 64) => WriteBits((ulong)value, CheckWidth(bits, 64));
    public void WriteUInt64(ulong value, int bits = 64) => WriteBits(value, CheckWidth(bits, 64));
    public void WriteSingle(float value) => WriteBits(BitConverter.SingleToUInt32Bits(value), 32);
    public void WriteDouble(double value) => WriteBits(BitConverter.DoubleToUInt64Bits(value), 64);

    /// <summary>LEB128-style varint: 7 value bits per byte, low group first, bit 7 = "more follows".
    /// The 1..5 bytes go out as one (8·count)-bit field at the current position, not byte-aligned.</summary>
    public void WriteUInt32VarLength(uint value)
    {
        ulong packed = 0;
        var count = 0;
        do
        {
            var group = value & 0x7F;
            value >>= 7;
            if (value != 0) group |= 0x80;
            packed |= (ulong)group << (8 * count);
            count++;
        } while (value != 0);
        WriteBits(packed, 8 * count);
    }

    /// <summary>Signed value reinterpreted as unsigned — no zig-zag, so a negative value costs 5 bytes.</summary>
    public void WriteInt32VarLength(int value) => WriteUInt32VarLength((uint)value);

    public void WriteUInt32VarLength(uint value, int blockSize) => WriteBlockVarLength(value, blockSize);
    public void WriteInt32VarLength(int value, int blockSize) => WriteBlockVarLength((uint)value, blockSize);
    public void WriteUInt64VarLength(ulong value, int blockSize) => WriteBlockVarLength(value, blockSize);
    public void WriteInt64VarLength(long value, int blockSize) => WriteBlockVarLength((ulong)value, blockSize);

    /// <summary>Writes 0-bits until the position is a multiple of 8.</summary>
    public void PadToByteBoundary() => WriteBits(0, (8 - (BitCount & 7)) & 7);

    /// <summary>Pads to a byte boundary, then copies <paramref name="bytes"/> verbatim.</summary>
    public void WriteBytesAligned(ReadOnlySpan<byte> bytes)
    {
        PadToByteBoundary();
        EnsureCapacity(BitCount + 8 * bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(BitCount >> 3));
        BitCount += 8 * bytes.Length;
    }

    /// <summary>Boolean isNull; if not null, UInt16 UTF-8 byte length, pad, then the bytes. An empty
    /// string still pads.</summary>
    public void WriteString(string? value)
    {
        WriteBoolean(value is null);
        if (value is null) return;
        var utf8 = Encoding.UTF8.GetBytes(value);
        if (utf8.Length > ushort.MaxValue) throw new ArgumentException("string longer than 65535 UTF-8 bytes", nameof(value));
        WriteUInt16((ushort)utf8.Length);
        WriteBytesAligned(utf8);
    }

    /// <summary>Block varint: unary block count (blocks−1 zero bits, then a 1 bit), then the value in
    /// blocks·blockSize bits. <paramref name="blockSize"/> is clamped to 2..16 as the SDK does.</summary>
    private void WriteBlockVarLength(ulong value, int blockSize)
    {
        blockSize = Math.Clamp(blockSize, 2, 16);
        var highestBit = value == 0 ? 0 : 63 - BitOperations.LeadingZeroCount(value);
        var blocks = (highestBit + blockSize) / blockSize;
        WriteBits(0, blocks - 1);
        WriteBits(1, 1);
        var valueBits = blocks * blockSize;
        // A 64-bit value with an awkward block size can need a few bits past 64; those are zero.
        WriteBits(value, Math.Min(valueBits, 64));
        if (valueBits > 64) WriteBits(0, valueBits - 64);
    }

    /// <summary>Discards everything written after <paramref name="bitCount"/> — how a packet builder rolls
    /// back a record that did not fit.</summary>
    public void Rewind(int bitCount)
    {
        if ((uint)bitCount > (uint)BitCount) throw new ArgumentOutOfRangeException(nameof(bitCount));
        var firstByte = bitCount >> 3;
        // Keep the low bits of a partly kept byte, zero the rest: later writes OR into zeroed storage.
        if ((bitCount & 7) != 0) _buffer[firstByte++] &= (byte)((1 << (bitCount & 7)) - 1);
        _buffer.AsSpan(firstByte, ByteCount - firstByte).Clear();
        BitCount = bitCount;
    }

    /// <summary>Replaces <paramref name="bits"/> already-written bits at <paramref name="bitPosition"/> —
    /// how a sender fills in a header it reserved before writing the body.</summary>
    public void Overwrite(int bitPosition, ulong value, int bits)
    {
        if (bitPosition < 0 || bits < 0 || bits > 64 || bitPosition + bits > BitCount) throw new ArgumentOutOfRangeException(nameof(bitPosition));
        for (var i = 0; i < bits; i++)
        {
            var bit = bitPosition + i;
            var mask = (byte)(1 << (bit & 7));
            if (((value >> i) & 1) != 0) _buffer[bit >> 3] |= mask;
            else _buffer[bit >> 3] &= (byte)~mask;
        }
    }

    private static int CheckWidth(int bits, int max) =>
        bits >= 0 && bits <= max ? bits : throw new ArgumentOutOfRangeException(nameof(bits));

    private void EnsureCapacity(int bits)
    {
        var bytes = (bits + 7) >> 3;
        if (bytes > _buffer.Length) Array.Resize(ref _buffer, Math.Max(bytes, _buffer.Length * 2));
    }
}
