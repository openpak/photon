using OpenPak.Photon;
using Xunit;

namespace OpenPak.Photon.Tests;

public class QuantumProtocolTests
{
    [Fact]
    public void Bits_fill_each_byte_from_bit_zero_little_endian()
    {
        var w = new QuantumBitWriter();
        w.WriteBool(true);           // bit 0
        w.WriteByte(0xA5);           // bits 1..8
        w.WriteInt(0x01020304);
        var b = w.ToArray();
        Assert.Equal((byte)(1 | (0xA5 << 1) & 0xFF), b[0]);
        var r = new QuantumBitReader(b);
        Assert.True(r.ReadBool());
        Assert.Equal(0xA5, r.ReadByte());
        Assert.Equal(0x01020304, r.ReadInt());
    }

    [Fact]
    public void Strings_and_arrays_round_trip_with_their_null_and_presence_bits()
    {
        var w = new QuantumBitWriter();
        w.WriteString("2.2.0.0");
        w.WriteString(null);
        w.WriteByteArray(new byte[] { 9, 8 });
        w.WriteByteArray(null);
        w.WriteIntArray(new[] { 3, -1 });
        w.WriteDouble(1.5);
        w.WriteULong(0x1122334455667788);
        var r = new QuantumBitReader(w.ToArray());
        Assert.Equal("2.2.0.0", r.ReadString());
        Assert.Null(r.ReadString());
        Assert.Equal(new byte[] { 9, 8 }, r.ReadByteArray());
        Assert.Null(r.ReadByteArray());
        Assert.Equal(new[] { 3, -1 }, r.ReadIntArray());
        Assert.Equal(1.5, r.ReadDouble());
        Assert.Equal(0x1122334455667788UL, r.ReadULong());
    }

    [Fact]
    public void A_null_string_is_one_set_bit()
    {
        var w = new QuantumBitWriter();
        w.WriteString(null);
        Assert.Equal(1, w.BitPosition);
        Assert.Equal(new byte[] { 1 }, w.ToArray());
    }

    [Fact]
    public void A_bit_range_copies_verbatim()
    {
        var src = new QuantumBitWriter();
        src.WriteBits(0b1011, 4);
        src.WriteInt(77);
        var r = new QuantumBitReader(src.ToArray());
        var dst = new QuantumBitWriter();
        dst.WriteBool(false);
        dst.WriteBitsFrom(r, 4, 32);
        var back = new QuantumBitReader(dst.ToArray());
        Assert.False(back.ReadBool());
        Assert.Equal(77, back.ReadInt());
    }
}
