using System.IO;
using System.Text;
using OpenPak.Photon;
using Xunit;

namespace OpenPak.Photon.Tests;

/// <summary>
/// GpBinary v1.8 Dictionary (type 20), as sent by the Photon C++ SDK. Self-contained (no oracle DLL).
/// The AuthenticateOnce body mirrors the one Unrailed! (Switch) sent to our Name Server; the user id
/// and token are replaced with dummies, every type byte and the trailing parameters are the real ones.
/// </summary>
public class CppSdkDictionaryTests
{
    private static object? Read(params byte[] bytes) => new GpBinaryReader(bytes).ReadTyped();

    private static byte[] Str(byte key, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        var o = new List<byte> { key, GpType.String };
        uint n = (uint)b.Length;
        while (n >= 0x80) { o.Add((byte)(n | 0x80)); n >>= 7; }
        o.Add((byte)n);
        o.AddRange(b);
        return o.ToArray();
    }

    /// <summary>[0xF3][Operation][op 231 AuthenticateOnce][9 params] as the C++ SDK encodes it.</summary>
    private static byte[] UnrailedAuthenticateOnce()
    {
        var body = new List<byte> { WireMessage.Magic, WireMessage.Operation, 0xE7, 0x09 };
        body.AddRange(Str(224, "9d45adf2-e83f-4281-b9f5-02728f813f80"));     // ApplicationId
        body.AddRange(Str(220, "2.1-Cross"));                                 // AppVersion
        body.AddRange(Str(210, "eu"));                                        // Region
        body.AddRange(Str(225, "NIN_0000000000000000000"));                   // UserId (dummy)
        body.AddRange(new byte[] { 217, GpType.ByteZero });                   // ClientAuthenticationType = 0
        body.AddRange(Str(216, "platform=NIN&token=" + new string('x', 1270)));  // long enough for a 2-byte varint
        // 194: Dictionary<object,object> with one entry (byte)211 -> true.
        body.AddRange(new byte[] { 194, GpType.Dictionary, 0x00, 0x00, 0x01, GpType.Byte, 211, GpType.BooleanTrue });
        body.AddRange(new byte[] { 193, GpType.ByteZero });                   // EncryptionMode = 0
        body.AddRange(new byte[] { 195, GpType.ByteZero });                   // ExpectedProtocol = 0
        return body.ToArray();
    }

    [Fact]
    public void Unrailed_AuthenticateOnce_parses()
    {
        var msg = WireMessage.Parse(UnrailedAuthenticateOnce());

        Assert.Equal(WireMessage.Operation, msg.MessageType);
        Assert.Equal(231, msg.Code);
        Assert.Equal(9, msg.Parameters.Count);
        Assert.Equal("9d45adf2-e83f-4281-b9f5-02728f813f80", msg.Parameters[224]);
        Assert.Equal("2.1-Cross", msg.Parameters[220]);
        Assert.Equal((byte)0, msg.Parameters[217]);
        var dict = Assert.IsType<Dictionary<object, object>>(msg.Parameters[194]);
        Assert.Equal(true, Assert.Single(dict, kv => kv.Key.Equals((byte)211)).Value);
        Assert.Equal((byte)0, msg.Parameters[195]);
    }

    [Fact]
    public void Typed_dictionary_reads_bare_keys_and_values()
    {
        // Dictionary<string,int> { "a": 5, "bc": -3 }: key/value types declared once, entries bare.
        var d = Assert.IsType<Dictionary<object, object>>(Read(
            GpType.Dictionary, GpType.String, GpType.CompressedInt, 0x02,
            0x01, (byte)'a', 0x0A,
            0x02, (byte)'b', (byte)'c', 0x05));
        Assert.Equal(5, d["a"]);
        Assert.Equal(-3, d["bc"]);
    }

    [Fact]
    public void Mixed_dictionary_reads_typed_values()
    {
        // Dictionary<byte,object>: bare byte keys, each value carries its own type byte.
        var d = Assert.IsType<Dictionary<object, object>>(Read(
            GpType.Dictionary, GpType.Byte, GpType.Unknown, 0x02,
            0x01, GpType.String, 0x02, (byte)'h', (byte)'i',
            0x02, GpType.BooleanFalse));
        Assert.Equal("hi", d[(byte)1]);
        Assert.Equal(false, d[(byte)2]);
    }

    [Fact]
    public void Dictionary_count_past_end_is_rejected()
        => Assert.Throws<InvalidDataException>(() => Read(GpType.Dictionary, 0x00, 0x00, 0x7F, GpType.ByteZero));

    [Fact]
    public void Nested_typed_dictionaries_are_depth_capped()
    {
        // Dictionary<object, Dictionary<...>> nested past the cap: each level declares Dictionary as
        // its value type, so the values are read bare. The depth cap must still apply.
        var b = new List<byte> { GpType.Dictionary };
        for (int i = 0; i < 100; i++) b.AddRange(new byte[] { GpType.Byte, GpType.Dictionary, 0x01, 0x00 });
        Assert.Throws<InvalidDataException>(() => new GpBinaryReader(b.ToArray()).ReadTyped());
    }

    [Fact]
    public void Parse_failure_does_not_dump_the_whole_body()
    {
        // A token-bearing body followed by an unsupported type (OperationRequest, 24): the error
        // names the offset and nearby bytes, never the token.
        var body = new List<byte> { WireMessage.Magic, WireMessage.Operation, 0xE7, 0x02 };
        body.AddRange(Str(216, "token=SECRETSECRETSECRETSECRETSECRET"));
        body.AddRange(new byte[] { 194, 24, 0x00 });
        var ex = Assert.Throws<InvalidOperationException>(() => WireMessage.Parse(body.ToArray()));
        Assert.DoesNotContain(BitConverter.ToString(Encoding.UTF8.GetBytes("SECRETSECRET")), ex.Message);
        Assert.Contains("near offset", ex.Message);
    }
}
