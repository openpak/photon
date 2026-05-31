namespace BlackIce.Photon;

/// <summary>
/// The wire framing of a reliable command's payload: [0xF3][msgType][body], where the body is
/// serialized with no leading GpType byte ([opCode][paramTable] for operations). When the body
/// is AES-encrypted, msgType is OR'd with 0x80 and the body is ciphertext. Matches Photon's
/// SerializeOperationToMessage exactly.
/// </summary>
public static class WireMessage
{
    public const byte Magic = 0xF3;
    public const byte MagicAlt = 0xFD;   // the client also accepts 253 as a message magic
    public const byte EncryptedFlag = 0x80;

    // EgMessageType
    public const byte Init = 0, InitResponse = 1, Operation = 2, OperationResponse = 3, Event = 4,
                      InternalOperationRequest = 6, InternalOperationResponse = 7;

    /// <summary>The 2-byte InitResponse the client waits for before establishing encryption.</summary>
    public static byte[] InitResponseMessage() => new[] { Magic, InitResponse };

    /// <summary>True if the buffer is a Photon message; yields the (decrypted-flag-stripped) message type.</summary>
    public static bool TryPeekType(byte[] data, out byte messageType, out bool encrypted)
    {
        messageType = 0; encrypted = false;
        if (data.Length < 2 || (data[0] != Magic && data[0] != MagicAlt)) return false;
        encrypted = (data[1] & EncryptedFlag) != 0;
        messageType = (byte)(data[1] & ~EncryptedFlag);
        return true;
    }

    /// <summary>A parsed inbound message: its kind, opcode/event-code, and parameter table.</summary>
    public sealed record Parsed(byte MessageType, byte Code, Dictionary<byte, object> Parameters);

    // --- Outbound ---

    public static byte[] Response(OperationResponse resp, byte messageType = OperationResponse)
    {
        var w = new GpBinaryWriter();
        w.WriteByte(Magic);
        w.WriteByte(messageType);
        WriteResponseBody(w, resp);
        return w.ToArray();
    }

    public static byte[] EventMessage(EventData ev)
    {
        var w = new GpBinaryWriter();
        w.WriteByte(Magic);
        w.WriteByte(Event);
        w.WriteByte(ev.Code);
        WriteTable(w, ev.Parameters);
        return w.ToArray();
    }

    /// <summary>Serializes the body of an operation request (no framing): [opCode][paramTable].</summary>
    public static byte[] RequestBody(byte opCode, Dictionary<byte, object> parameters)
    {
        var w = new GpBinaryWriter();
        w.WriteByte(opCode);
        WriteTable(w, parameters);
        return w.ToArray();
    }

    private static void WriteResponseBody(GpBinaryWriter w, OperationResponse resp)
    {
        w.WriteByte(resp.OperationCode);
        w.WriteInt16(resp.ReturnCode);
        w.WriteTyped(resp.DebugMessage);    // null -> [8], string -> [7][len][utf8]
        WriteTable(w, resp.Parameters);
    }

    private static void WriteTable(GpBinaryWriter w, Dictionary<byte, object> parameters)
    {
        w.WriteByte((byte)parameters.Count);
        foreach (var kv in parameters) { w.WriteByte(kv.Key); w.WriteTyped(kv.Value); }
    }

    // --- Inbound ---

    /// <summary>
    /// Parses an inbound message. If encrypted, <paramref name="decrypt"/> is invoked on the
    /// ciphertext body to recover plaintext before parsing.
    /// </summary>
    public static Parsed Parse(byte[] data, Func<byte[], byte[]>? decrypt = null)
    {
        if (data.Length < 2 || (data[0] != Magic && data[0] != MagicAlt))
            throw new InvalidOperationException($"Not a Photon message (magic={data.ElementAtOrDefault(0):X2})");

        byte rawType = data[1];
        bool encrypted = (rawType & EncryptedFlag) != 0;
        byte messageType = (byte)(rawType & ~EncryptedFlag);

        byte[] body = data[2..];
        if (encrypted)
        {
            if (decrypt is null) throw new InvalidOperationException("Encrypted message but no key established");
            body = decrypt(body);
        }

        try
        {
            var r = new GpBinaryReader(body);
            byte code = r.ReadByte();
            // Responses carry returnCode+debug before the table; requests/events do not.
            if (messageType is OperationResponse or InternalOperationResponse)
            {
                r.ReadInt16();      // returnCode
                r.ReadTyped();      // debug message
            }
            var table = ReadTable(r);
            return new Parsed(messageType, code, table);
        }
        catch (Exception ex)
        {
            // Surface the decrypted body so an unhandled structure can be pinpointed.
            throw new InvalidOperationException(
                $"body parse failed (msgType {messageType}, {body.Length}B): {BitConverter.ToString(body)} :: {ex.Message}");
        }
    }

    private static Dictionary<byte, object> ReadTable(GpBinaryReader r)
    {
        int count = r.ReadByte();
        var p = new Dictionary<byte, object>(count);
        for (int i = 0; i < count; i++) { byte key = r.ReadByte(); p[key] = r.ReadTyped()!; }
        return p;
    }
}
