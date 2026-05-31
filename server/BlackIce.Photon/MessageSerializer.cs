namespace BlackIce.Photon;

/// <summary>
/// Serializes/deserializes Photon v1.8 message envelopes (operation request/response, event).
/// Layout: [messageType][opcode-or-eventcode][...][parameter table]. The parameter table is
/// [count:byte] then, per entry, [key:byte][typed value]. Matches Photon3Unity3D exactly.
/// </summary>
public static class MessageSerializer
{
    public static byte[] SerializeRequest(OperationRequest req)
    {
        var w = new GpBinaryWriter();
        w.WriteByte(GpType.OperationRequest);   // 24
        w.WriteByte(req.OperationCode);
        WriteParameterTable(w, req.Parameters);
        return w.ToArray();
    }

    public static byte[] SerializeResponse(OperationResponse resp)
    {
        var w = new GpBinaryWriter();
        w.WriteByte(GpType.OperationResponse);  // 25
        w.WriteByte(resp.OperationCode);
        w.WriteInt16(resp.ReturnCode);
        w.WriteTyped(resp.DebugMessage);        // null -> [8], string -> [7][len][utf8]
        WriteParameterTable(w, resp.Parameters);
        return w.ToArray();
    }

    public static byte[] SerializeEvent(EventData ev)
    {
        var w = new GpBinaryWriter();
        w.WriteByte(GpType.EventData);          // 26
        w.WriteByte(ev.Code);
        WriteParameterTable(w, ev.Parameters);
        return w.ToArray();
    }

    private static void WriteParameterTable(GpBinaryWriter w, Dictionary<byte, object> parameters)
    {
        w.WriteByte((byte)parameters.Count);
        foreach (var kv in parameters) { w.WriteByte(kv.Key); w.WriteTyped(kv.Value); }
    }

    public static OperationRequest DeserializeRequest(byte[] data)
    {
        var r = new GpBinaryReader(data);
        byte messageType = r.ReadByte();        // 24
        if (messageType != GpType.OperationRequest)
            throw new InvalidOperationException($"Expected OperationRequest (24), got {messageType}");
        byte op = r.ReadByte();
        return new OperationRequest(op, ReadParameterTable(r));
    }

    private static Dictionary<byte, object> ReadParameterTable(GpBinaryReader r)
    {
        int count = r.ReadByte();
        var p = new Dictionary<byte, object>(count);
        for (int i = 0; i < count; i++) { byte key = r.ReadByte(); p[key] = r.ReadTyped()!; }
        return p;
    }
}
