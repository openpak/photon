namespace BlackIce.Photon;

/// <summary>A client-to-server operation request: an op code plus a byte-keyed parameter table.</summary>
public sealed record OperationRequest(byte OperationCode, Dictionary<byte, object> Parameters);

/// <summary>A server-to-client response to an operation, with a return code and optional debug text.</summary>
public sealed record OperationResponse(byte OperationCode, short ReturnCode, string? DebugMessage, Dictionary<byte, object> Parameters);

/// <summary>A server-to-client (or broadcast) event.</summary>
public sealed record EventData(byte Code, Dictionary<byte, object> Parameters);
