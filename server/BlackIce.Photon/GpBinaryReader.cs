using System.IO;
using System.Text;

namespace BlackIce.Photon;

/// <summary>
/// Reads values in GpBinary v1.8. Handles all integer encodings the client may emit
/// (zero/1-byte/2-byte/compressed variants), not just the simple forms our writer produces.
///
/// Hardened against hostile/truncated input: this reader operates directly on bytes that arrive
/// from the network, so every read is bounds-checked (<see cref="Need"/>), every length prefix is
/// validated against the remaining buffer (an N-element collection can't exceed N remaining bytes),
/// varints are length-capped, and nesting is depth-capped — a malformed datagram is rejected with a
/// catchable <see cref="InvalidDataException"/> instead of crashing the listener with an
/// IndexOutOfRange / OutOfMemory / stack overflow.
/// </summary>
public sealed class GpBinaryReader
{
    private readonly byte[] _data;
    private int _pos;
    private int _depth;

    // Cap on nested object-arrays/hashtables. Each level recurses through ReadTyped; without a cap a
    // hostile packet of deeply-nested containers would blow the stack. 64 is far beyond any real message.
    private const int MaxDepth = 64;

    public GpBinaryReader(byte[] data) => _data = data ?? throw new ArgumentNullException(nameof(data));

    public GpBinaryReader(byte[] data, int offset)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        if (offset < 0 || offset > data.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        _pos = offset;
    }

    public int Position => _pos;
    private int Remaining => _data.Length - _pos;

    /// <summary>Throws if fewer than <paramref name="n"/> bytes remain (or n is negative). Every read funnels through this.</summary>
    private void Need(int n)
    {
        if (n < 0 || _pos + n > _data.Length)
            throw new InvalidDataException($"GpBinary read past end: need {n} byte(s) at offset {_pos}, buffer is {_data.Length}");
    }

    public byte ReadByte() { Need(1); return _data[_pos++]; }
    public short ReadInt16() { Need(2); short v = (short)(_data[_pos] | (_data[_pos + 1] << 8)); _pos += 2; return v; }
    public ushort ReadUInt16() { Need(2); ushort v = (ushort)(_data[_pos] | (_data[_pos + 1] << 8)); _pos += 2; return v; }

    /// <summary>Reads one typed value (type byte + payload).</summary>
    public object? ReadTyped()
    {
        if (++_depth > MaxDepth) throw new InvalidDataException($"GpBinary value nested deeper than {MaxDepth}");
        try
        {
            byte type = ReadByte();
            // CustomTypeSlim: type bytes 128..228 encode a registered custom type, code = type - 128.
            if (type is >= 128 and <= 228) return ReadCustom((byte)(type - 128));
            switch (type)
            {
                case GpType.Custom: return ReadCustom(ReadByte());   // explicit custom: code byte follows
                case GpType.Null: return null;
                case GpType.BooleanFalse: return false;
                case GpType.BooleanTrue: return true;
                case GpType.Boolean: return ReadByte() != 0;
                case GpType.Byte: return ReadByte();
                case GpType.ByteZero: return (byte)0;
                case GpType.Short: return ReadInt16();
                case GpType.ShortZero: return (short)0;
                case GpType.Float: return ReadFloat();
                case GpType.FloatZero: return 0f;
                case GpType.Double: return ReadDouble();
                case GpType.DoubleZero: return 0d;
                case GpType.String: return ReadStringBody();
                case GpType.IntZero: return 0;
                case GpType.Int1: return (int)ReadByte();
                case GpType.Int1_: return -(int)ReadByte();
                case GpType.Int2: return (int)ReadUInt16();
                case GpType.Int2_: return -(int)ReadUInt16();
                case GpType.CompressedInt: return DecodeZigZag32(ReadUVarInt());
                case GpType.LongZero: return 0L;
                case GpType.L1: return (long)ReadByte();
                case GpType.L1_: return -(long)ReadByte();
                case GpType.L2: return (long)ReadUInt16();
                case GpType.L2_: return -(long)ReadUInt16();
                case GpType.CompressedLong: return DecodeZigZag64(ReadUVarInt64());
                case GpType.ByteArray: return ReadByteArrayBody();
                case GpType.IntArray: return ReadIntArrayBody();
                case GpType.StringArray: return ReadStringArrayBody();
                case GpType.ObjectArray: return ReadObjectArrayBody();
                case GpType.Hashtable: return ReadHashtableBody();
                default: throw new NotSupportedException($"GpBinary v1.8 read not implemented for type {type}");
            }
        }
        finally { _depth--; }
    }

    private float ReadFloat() { Need(4); var v = BitConverter.ToSingle(_data, _pos); _pos += 4; return v; }
    private double ReadDouble() { Need(8); var v = BitConverter.ToDouble(_data, _pos); _pos += 8; return v; }

    private string ReadStringBody()
    {
        int len = (int)ReadUVarInt();
        Need(len);                                          // rejects negative and past-end lengths
        var s = Encoding.UTF8.GetString(_data, _pos, len);
        _pos += len;
        return s;
    }

    private byte[] ReadByteArrayBody()
    {
        int len = (int)ReadUVarInt();
        Need(len);
        var a = new byte[len];
        Array.Copy(_data, _pos, a, 0, len);
        _pos += len;
        return a;
    }

    private int[] ReadIntArrayBody()
    {
        int len = ReadCount();                              // each element is >= 1 byte, so len <= Remaining
        var a = new int[len];
        for (int i = 0; i < len; i++) a[i] = DecodeZigZag32(ReadUVarInt());
        return a;
    }

    private string[] ReadStringArrayBody()
    {
        int len = ReadCount();
        var a = new string[len];
        for (int i = 0; i < len; i++) a[i] = ReadStringBody();
        return a;
    }

    private object?[] ReadObjectArrayBody()
    {
        int len = ReadCount();
        var a = new object?[len];
        for (int i = 0; i < len; i++) a[i] = ReadTyped();
        return a;
    }

    // Custom type: [code already read][length varint][length bytes]. Preserved verbatim.
    private PhotonCustomData ReadCustom(byte code)
    {
        int len = (int)ReadUVarInt();
        Need(len);
        var data = new byte[len];
        Array.Copy(_data, _pos, data, 0, len);
        _pos += len;
        return new PhotonCustomData(code, data);
    }

    private Dictionary<object, object> ReadHashtableBody()
    {
        // Photon Hashtable keys/values are arbitrary typed objects (room properties mix string
        // keys like "RandomSeed"/"PVP" with byte keys), so preserve the real key types. Each entry is
        // a key + value (>= 2 bytes), so count can't exceed the remaining bytes.
        int count = ReadCount();
        var d = new Dictionary<object, object>(count);
        for (int i = 0; i < count; i++)
        {
            var key = ReadTyped();
            var val = ReadTyped();
            if (key is not null) d[key] = val!;
        }
        return d;
    }

    /// <summary>Reads a collection element count and rejects one that can't possibly be satisfied
    /// (each element costs at least one byte, so a count larger than the remaining buffer is hostile).</summary>
    private int ReadCount()
    {
        int count = (int)ReadUVarInt();
        if (count < 0 || count > Remaining)
            throw new InvalidDataException($"GpBinary collection count {count} exceeds remaining {Remaining} byte(s)");
        return count;
    }

    internal uint ReadUVarInt()
    {
        uint result = 0;
        int shift = 0;
        for (int i = 0; i < 5; i++)   // a uint32 LEB128 varint is at most 5 bytes
        {
            byte b = ReadByte();
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
        throw new InvalidDataException("GpBinary uint32 varint longer than 5 bytes");
    }

    internal ulong ReadUVarInt64()
    {
        ulong result = 0;
        int shift = 0;
        for (int i = 0; i < 10; i++)  // a uint64 LEB128 varint is at most 10 bytes
        {
            byte b = ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
        throw new InvalidDataException("GpBinary uint64 varint longer than 10 bytes");
    }

    internal static int DecodeZigZag32(uint v) => (int)(v >> 1) ^ -(int)(v & 1);
    internal static long DecodeZigZag64(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);
}
