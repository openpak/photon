using System.IO;
using BlackIce.Photon;
using Xunit;

namespace BlackIce.Photon.Tests;

/// <summary>
/// Hostile-input tests for the GpBinary reader: a malformed/truncated/oversized datagram must be
/// rejected with a catchable exception (so the listener logs-and-drops it), never crash the process
/// with IndexOutOfRange / OutOfMemory / StackOverflow. These are self-contained (no Photon oracle DLL).
/// </summary>
public class GpBinaryReaderHardeningTests
{
    private static object? Read(params byte[] bytes) => new GpBinaryReader(bytes).ReadTyped();

    [Fact]
    public void Empty_buffer_is_rejected_not_crashed()
        => Assert.ThrowsAny<System.Exception>(() => Read());

    [Fact]
    public void Truncated_short_is_rejected()
        => Assert.Throws<InvalidDataException>(() => Read(GpType.Short, 0x01));   // claims 2 bytes, only 1 follows

    [Fact]
    public void Truncated_float_is_rejected()
        => Assert.Throws<InvalidDataException>(() => Read(GpType.Float, 0x00, 0x00));   // needs 4, has 2

    [Fact]
    public void String_length_past_end_is_rejected()
        => Assert.Throws<InvalidDataException>(() => Read(GpType.String, 0x7F, (byte)'h', (byte)'i'));   // claims 127 bytes, 2 follow

    [Fact]
    public void Byte_array_length_past_end_is_rejected()
        => Assert.Throws<InvalidDataException>(() => Read(GpType.ByteArray, 0xFF, 0xFF, 0xFF, 0x7F, 0x00));   // ~268M claimed

    [Fact]
    public void Custom_type_length_past_end_is_rejected()
        => Assert.Throws<InvalidDataException>(() => Read(128 + 86, 0x40, 0x01, 0x02));   // Vec3 slim claims 64 bytes, 2 follow

    [Fact]
    public void Object_array_count_larger_than_buffer_is_rejected()
        => Assert.Throws<InvalidDataException>(() => Read(GpType.ObjectArray, 0xFF, 0xFF, 0xFF, 0x7F));   // count ~268M, no elements

    [Fact]
    public void Overlong_varint_is_rejected()
        => Assert.Throws<InvalidDataException>(() => Read(GpType.CompressedInt, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01));

    [Fact]
    public void Deeply_nested_object_arrays_are_rejected_not_stack_overflowed()
    {
        // 70 levels of ObjectArray(count=1) — exceeds the depth cap (64) before any stack damage.
        var bytes = new System.Collections.Generic.List<byte>();
        for (int i = 0; i < 70; i++) { bytes.Add(GpType.ObjectArray); bytes.Add(0x01); }
        bytes.Add(GpType.Null);
        Assert.Throws<InvalidDataException>(() => new GpBinaryReader(bytes.ToArray()).ReadTyped());
    }

    [Fact]
    public void Valid_values_still_round_trip()
    {
        // Guard regression check: well-formed values our own writer emits must still decode unchanged.
        var bytes = new GpBinaryWriter().WriteTyped(new object[] { 7, "ok", true, 3.5f }).ToArray();
        var arr = Assert.IsType<object?[]>(new GpBinaryReader(bytes).ReadTyped());
        Assert.Equal(4, arr.Length);
        Assert.Equal(7, arr[0]);
        Assert.Equal("ok", arr[1]);
        Assert.Equal(true, arr[2]);
        Assert.Equal(3.5f, arr[3]);
    }
}
