using ExitGames.Client.Photon;

namespace BlackIce.Photon.Tests;

/// <summary>Produces reference bytes using the real Photon GpBinaryV18 serializer (test-only oracle).</summary>
public static class Oracle
{
    private static readonly IProtocol Protocol = new Protocol18();

    static Oracle()
    {
        // PUN registers its custom types (e.g. Vector3 = code 86) on the peer at runtime via
        // PhotonPeer.RegisterType, which populates Protocol18's static custom-type table. The bare
        // Photon3Unity3D.dll used as our oracle has none registered, so it would throw
        // "Custom type not found" on a code it has never seen. We register a byte-passthrough for the
        // codes our relay forwards so the oracle decodes our slim wire form exactly as the real client
        // would. Serialize/deserialize are identity over the body bytes — same payload PhotonCustomData carries.
        // Photon keys its managed-type table by Type, so each code needs a DISTINCT stand-in class —
        // registering one class for several codes silently keeps only the first.
        PhotonPeer.RegisterType(typeof(Passthrough86), 86, Passthrough86.Serialize, Passthrough86.Deserialize);
        PhotonPeer.RegisterType(typeof(Passthrough68), 68, Passthrough68.Serialize, Passthrough68.Deserialize);
        PhotonPeer.RegisterType(typeof(Passthrough81), 81, Passthrough81.Serialize, Passthrough81.Deserialize);
        PhotonPeer.RegisterType(typeof(Passthrough67), 67, Passthrough67.Serialize, Passthrough67.Deserialize);
    }

    // Test-only stand-ins for registered Photon custom types: each carries the raw body bytes verbatim.
    // A distinct class per code is required because Photon's RegisterType keys by managed Type.
    private sealed class Passthrough86 { public byte[] Data = System.Array.Empty<byte>(); public static byte[] Serialize(object o) => ((Passthrough86)o).Data; public static object Deserialize(byte[] d) => new Passthrough86 { Data = d }; }
    private sealed class Passthrough68 { public byte[] Data = System.Array.Empty<byte>(); public static byte[] Serialize(object o) => ((Passthrough68)o).Data; public static object Deserialize(byte[] d) => new Passthrough68 { Data = d }; }
    private sealed class Passthrough81 { public byte[] Data = System.Array.Empty<byte>(); public static byte[] Serialize(object o) => ((Passthrough81)o).Data; public static object Deserialize(byte[] d) => new Passthrough81 { Data = d }; }
    private sealed class Passthrough67 { public byte[] Data = System.Array.Empty<byte>(); public static byte[] Serialize(object o) => ((Passthrough67)o).Data; public static object Deserialize(byte[] d) => new Passthrough67 { Data = d }; }

    /// <summary>Serializes a single typed value (type byte + payload).</summary>
    public static byte[] Serialize(object value) => Protocol.Serialize(value);

    public static byte[] SerializeOperationRequest(byte op, Dictionary<byte, object> parameters)
    {
        var sb = new StreamBuffer(64);
        Protocol.SerializeOperationRequest(sb, op, parameters, setType: true);
        return sb.ToArray();
    }

    /// <summary>Deserializes a single typed value using the real Photon codec.</summary>
    public static object? Deserialize(byte[] bytes) => Protocol.Deserialize(bytes);

    /// <summary>Parses a full message (request/response/event) using the real Photon codec.</summary>
    public static object DeserializeMessage(byte[] bytes) => Protocol.DeserializeMessage(new StreamBuffer(bytes));

    /// <summary>Serializes an operation-request body with no leading GpType byte ([op][table]) — the wire form.</summary>
    public static byte[] SerializeRequestBody(byte op, Dictionary<byte, object> parameters)
    {
        var sb = new StreamBuffer(64);
        Protocol.SerializeOperationRequest(sb, op, parameters, setType: false);
        return sb.ToArray();
    }

    /// <summary>Parses an operation-request body ([op][table]) using the real Photon codec.</summary>
    public static ExitGames.Client.Photon.OperationRequest DeserializeRequestBody(byte[] body)
        => Protocol.DeserializeOperationRequest(new StreamBuffer(body));

    public static string Hex(byte[] b) => BitConverter.ToString(b);
}
