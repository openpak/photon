namespace BlackIce.Photon;

/// <summary>
/// GpBinary v1.8 wire type codes (the on-the-wire scheme, ported from the observed format —
/// distinct from the v1.6 ASCII tags). Integers are zig-zag + unsigned-LEB128 varint;
/// fixed-width numerics are little-endian.
/// </summary>
public static class GpType
{
    public const byte Unknown = 0;
    public const byte Boolean = 2;
    public const byte Byte = 3;
    public const byte Short = 4;
    public const byte Float = 5;
    public const byte Double = 6;
    public const byte String = 7;
    public const byte Null = 8;
    public const byte CompressedInt = 9;
    public const byte CompressedLong = 10;
    public const byte Int1 = 11;     // 1-byte positive int
    public const byte Int1_ = 12;    // 1-byte negative int (magnitude stored)
    public const byte Int2 = 13;     // 2-byte positive int
    public const byte Int2_ = 14;    // 2-byte negative int (magnitude stored)
    public const byte L1 = 15;
    public const byte L1_ = 16;
    public const byte L2 = 17;
    public const byte L2_ = 18;
    public const byte Custom = 19;
    public const byte Dictionary = 20;
    public const byte Hashtable = 21;
    public const byte ObjectArray = 23;
    public const byte OperationRequest = 24;
    public const byte OperationResponse = 25;
    public const byte EventData = 26;
    public const byte BooleanFalse = 27;
    public const byte BooleanTrue = 28;
    public const byte ShortZero = 29;
    public const byte IntZero = 30;
    public const byte LongZero = 31;
    public const byte FloatZero = 32;
    public const byte DoubleZero = 33;
    public const byte ByteZero = 34;
    public const byte Array = 64;
    public const byte ByteArray = 67;
    public const byte IntArray = 73;       // CompressedIntArray
    public const byte StringArray = 71;
}
