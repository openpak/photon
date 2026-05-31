namespace BlackIce.Photon;

/// <summary>
/// A registered Photon custom type as it appears on the wire: a 1-byte type code plus its raw
/// serialized bytes (e.g. PUN's Vector3 = code 86, three big-endian floats). Phase 1 preserves
/// these verbatim so the stream stays aligned; decoding specific custom types is a later concern.
/// </summary>
public sealed record PhotonCustomData(byte Code, byte[] Data);
