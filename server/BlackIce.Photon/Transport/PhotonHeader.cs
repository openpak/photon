using System.Buffers.Binary;

namespace BlackIce.Photon.Transport;

/// <summary>
/// The 12-byte Photon UDP packet header (one per datagram, followed by N commands).
/// The transport layer is big-endian (network byte order), unlike the v1.8 payload.
/// </summary>
public readonly record struct PhotonHeader(short PeerId, bool CrcEnabled, byte CommandCount, int ServerTime, int Challenge)
{
    public const int Size = 12;

    public void WriteTo(Span<byte> b)
    {
        BinaryPrimitives.WriteInt16BigEndian(b, PeerId);
        b[2] = (byte)(CrcEnabled ? 1 : 0);
        b[3] = CommandCount;
        BinaryPrimitives.WriteInt32BigEndian(b[4..], ServerTime);
        BinaryPrimitives.WriteInt32BigEndian(b[8..], Challenge);
    }

    public static PhotonHeader ReadFrom(ReadOnlySpan<byte> b) => new(
        BinaryPrimitives.ReadInt16BigEndian(b),
        b[2] != 0,
        b[3],
        BinaryPrimitives.ReadInt32BigEndian(b[4..]),
        BinaryPrimitives.ReadInt32BigEndian(b[8..]));
}
