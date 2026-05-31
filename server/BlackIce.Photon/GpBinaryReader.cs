using System.Text;

namespace BlackIce.Photon;

/// <summary>
/// Reads values in GpBinary v1.8. Handles all integer encodings the client may emit
/// (zero/1-byte/2-byte/compressed variants), not just the simple forms our writer produces.
/// </summary>
public sealed class GpBinaryReader
{
    private readonly byte[] _data;
    private int _pos;

    public GpBinaryReader(byte[] data) => _data = data;
    public GpBinaryReader(byte[] data, int offset) { _data = data; _pos = offset; }

    public int Position => _pos;
    public byte ReadByte() => _data[_pos++];
    public short ReadInt16() { short v = (short)(_data[_pos] | (_data[_pos + 1] << 8)); _pos += 2; return v; }
    public ushort ReadUInt16() { ushort v = (ushort)(_data[_pos] | (_data[_pos + 1] << 8)); _pos += 2; return v; }

    /// <summary>Reads one typed value (type byte + payload).</summary>
    public object? ReadTyped()
    {
        byte type = _data[_pos++];
        // CustomTypeSlim: type bytes 128..228 encode a registered custom type, code = type - 128.
        if (type is >= 128 and <= 228) return ReadCustom((byte)(type - 128));
        switch (type)
        {
            case GpType.Custom: return ReadCustom(_data[_pos++]);   // explicit custom: code byte follows
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

    private float ReadFloat() { var v = BitConverter.ToSingle(_data, _pos); _pos += 4; return v; }
    private double ReadDouble() { var v = BitConverter.ToDouble(_data, _pos); _pos += 8; return v; }

    private string ReadStringBody()
    {
        int len = (int)ReadUVarInt();
        var s = Encoding.UTF8.GetString(_data, _pos, len);
        _pos += len;
        return s;
    }

    private byte[] ReadByteArrayBody()
    {
        int len = (int)ReadUVarInt();
        var a = new byte[len];
        Array.Copy(_data, _pos, a, 0, len);
        _pos += len;
        return a;
    }

    private int[] ReadIntArrayBody()
    {
        int len = (int)ReadUVarInt();
        var a = new int[len];
        for (int i = 0; i < len; i++) a[i] = DecodeZigZag32(ReadUVarInt());
        return a;
    }

    private string[] ReadStringArrayBody()
    {
        int len = (int)ReadUVarInt();
        var a = new string[len];
        for (int i = 0; i < len; i++) a[i] = ReadStringBody();
        return a;
    }

    private object?[] ReadObjectArrayBody()
    {
        int len = (int)ReadUVarInt();
        var a = new object?[len];
        for (int i = 0; i < len; i++) a[i] = ReadTyped();
        return a;
    }

    // Custom type: [code already read][length varint][length bytes]. Preserved verbatim.
    private PhotonCustomData ReadCustom(byte code)
    {
        int len = (int)ReadUVarInt();
        var data = new byte[len];
        Array.Copy(_data, _pos, data, 0, len);
        _pos += len;
        return new PhotonCustomData(code, data);
    }

    private Dictionary<object, object> ReadHashtableBody()
    {
        // Photon Hashtable keys/values are arbitrary typed objects (room properties mix string
        // keys like "RandomSeed"/"PVP" with byte keys), so preserve the real key types.
        int count = (int)ReadUVarInt();
        var d = new Dictionary<object, object>(count);
        for (int i = 0; i < count; i++)
        {
            var key = ReadTyped();
            var val = ReadTyped();
            if (key is not null) d[key] = val!;
        }
        return d;
    }

    internal uint ReadUVarInt()
    {
        uint result = 0; int shift = 0; byte b;
        do { b = _data[_pos++]; result |= (uint)(b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0);
        return result;
    }

    internal ulong ReadUVarInt64()
    {
        ulong result = 0; int shift = 0; byte b;
        do { b = _data[_pos++]; result |= (ulong)(b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0);
        return result;
    }

    internal static int DecodeZigZag32(uint v) => (int)(v >> 1) ^ -(int)(v & 1);
    internal static long DecodeZigZag64(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);
}
