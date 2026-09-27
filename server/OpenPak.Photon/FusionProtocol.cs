using System.Buffers.Binary;
using System.Text;

namespace OpenPak.Photon;

/// <summary>
/// Minimal codec for Fusion's own session-negotiation protocol, which rides on top of classic
/// Photon as <c>OpRaiseEvent</c> code 100 ("protocol package"). This is NOT part of classic
/// Photon/PUN — it's Fusion 2's private wire format (message-type byte, then a bit-packed body;
/// see <see cref="FusionBitReader"/>/<see cref="FusionBitWriter"/>). Reverse-engineered by reading
/// Photon's own decompiled Fusion.Sockets client SDK as a spec (never by loading/executing that
/// proprietary DLL — see earlier-outbound/HANDOFF.md for the research trail) and reimplemented
/// here from scratch. Only the <c>Join</c> message (protocol id 1) is handled: enough to answer a
/// solo host's own join request so its client-side session bootstrap can complete. The other nine
/// message types (NetworkConfigSync, ReflexiveInfo, Disconnect, Start, Snapshot, HostMigration,
/// PlayerRefMapping, ChangeMasterClient, DummyTrafficSync) are unimplemented — needed once a
/// second real peer must actually join a room.
/// </summary>
public static class FusionProtocol
{
    public const byte ProtocolPackageEventCode = 100;
    private const byte MsgIdJoin = 1;
    public const byte MsgIdNetworkConfigSync = 2;
    private const byte MsgIdStart = 5;
    private const byte MsgIdPlayerRefMapping = 8;

    /// <summary>Reads just the leading message-id byte of a protocol-package payload without fully
    /// decoding it (a cheap way to tell messages apart before deciding which decoder, if any, to
    /// use). Returns null for an empty payload.</summary>
    public static byte? PeekMessageId(byte[] payload) => payload.Length > 0 ? payload[0] : null;

    public const byte JoinTypeRequest = 1, JoinTypeConfirmation = 2, JoinTypeRejoin = 3;
    public const byte PeerModeNone = 0, PeerModeServer = 1, PeerModeClient = 2;
    public const uint StartRequestsNone = 0, StartRequestsConnectToShared = 2;
    public const uint JoinRequestsNone = 0, JoinRequestsNetworkConfig = 2;

    public sealed record JoinMessage(byte ProtocolVersion, int Major, int Minor, int Build,
                                      byte Type, byte GameMode, byte PeerMode, uint JoinRequests,
                                      byte[]? UniqueId, int PlayerRef, byte[]? EncryptionKey,
                                      byte[]? EncryptionKeySecret, int PlayerCounterRef, string? CustomData);

    public sealed record StartMessage(byte ProtocolVersion, int Major, int Minor, int Build,
                                      int RemoteServerID, uint StartRequests, string? CustomData);

    /// <summary>Decodes a protocol-package payload as a <c>Start</c> message, or null if it isn't
    /// one or is malformed. Used for tests/diagnostics — the server only ever sends this message,
    /// never needs to receive one.</summary>
    public static StartMessage? TryDecodeStart(byte[] payload)
    {
        try
        {
            var r = new FusionBitReader(payload);
            if (r.ReadBits(8) != MsgIdStart) return null;

            byte protocolVersion = r.ReadBits(8);
            int major = 0, minor = 0, build = 0;
            if (protocolVersion >= 2) { major = r.ReadIntN(32); minor = r.ReadIntN(32); build = r.ReadIntN(32); }

            int remoteServerId = r.ReadIntN(32);
            uint startRequests = r.ReadUIntN(32);
            string? customData = protocolVersion >= 3 ? r.ReadString() : null;

            return new StartMessage(protocolVersion, major, minor, build, remoteServerId, startRequests, customData);
        }
        catch { return null; }
    }

    /// <summary>Decodes a protocol-package payload as a <c>Join</c> message, or null if it isn't one
    /// (wrong message id) or is malformed (never throws — this is untrusted network input).</summary>
    public static JoinMessage? TryDecodeJoin(byte[] payload)
    {
        try
        {
            var r = new FusionBitReader(payload);
            if (r.ReadBits(8) != MsgIdJoin) return null;

            byte protocolVersion = r.ReadBits(8);
            int major = 0, minor = 0, build = 0;
            if (protocolVersion >= 2) { major = r.ReadIntN(32); minor = r.ReadIntN(32); build = r.ReadIntN(32); }

            byte type = r.ReadBits(8);
            byte gameMode = r.ReadBits(8);
            byte peerMode = r.ReadBits(8);
            uint joinRequests = r.ReadUIntN(32);

            byte[]? uniqueId = null, encKey = null, encSecret = null;
            int playerRef = 0, playerCounterRef = 0;
            if (protocolVersion >= 6) uniqueId = r.ReadByteArrayLP();
            if (protocolVersion >= 7) playerRef = r.ReadIntN(32);
            if (protocolVersion >= 9) { encKey = r.ReadByteArrayLP(); encSecret = r.ReadByteArrayLP(); }
            if (protocolVersion >= 12) playerCounterRef = r.ReadIntN(32);
            string? customData = protocolVersion >= 3 ? r.ReadString() : null;

            return new JoinMessage(protocolVersion, major, minor, build, type, gameMode, peerMode,
                                   joinRequests, uniqueId, playerRef, encKey, encSecret, playerCounterRef, customData);
        }
        catch { return null; }
    }

    /// <summary>
    /// Builds a <c>Join</c> Confirmation reply, echoing the request's protocol/serialization
    /// version and game mode. Field values (PeerMode=Client, JoinRequests=NetworkConfig, a non-null
    /// 8-byte UniqueId, empty not null CustomData) are copied exactly from a real Photon Cloud
    /// server's Confirmation, captured and decoded byte-for-byte against a live Steam session — see
    /// earlier-outbound/HANDOFF.md. Requesting NetworkConfig makes the client send its own
    /// NetworkConfigSync (protocol id 2) back; we don't need to understand or reply to it (the real
    /// client's own handler for receiving one is a no-op) so it's left unimplemented and simply
    /// falls through the generic relay path untouched.
    /// </summary>
    /// <summary>The 8-byte Fusion UniqueId the server assigns to an actor. It must be STABLE for the
    /// life of the peer's session: the client stores the Join Confirmation's UniqueId and presents it
    /// again on its event-101 relay Connect, and the hosted Fusion server keys its player mapping on it.
    /// Derived from the actor number so the server can recompute it without extra state.</summary>
    public static byte[] UniqueIdForActor(int actor)
    {
        var id = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(id, 0x5546 /* 'FU' */ * 0x100000000L + (uint)actor);
        return id;
    }

    public static byte[] EncodeJoinConfirmation(JoinMessage request, int playerRef)
    {
        var w = new FusionBitWriter();
        w.WriteBits(MsgIdJoin, 8);
        w.WriteBits(request.ProtocolVersion, 8);
        if (request.ProtocolVersion >= 2) { w.WriteIntN(request.Major, 32); w.WriteIntN(request.Minor, 32); w.WriteIntN(request.Build, 32); }
        w.WriteBits(JoinTypeConfirmation, 8);
        w.WriteBits(request.GameMode, 8);
        // Echo the peer's own mode rather than asserting Client: a ClientServer host announces itself
        // as PeerMode.Server (see docs/fusion-protocol-map.md), and inventing a value here is the only
        // field of the Confirmation we do not take from the request.
        w.WriteBits(request.PeerMode == PeerModeNone ? PeerModeClient : request.PeerMode, 8);
        w.WriteUIntN(JoinRequestsNetworkConfig, 32);
        if (request.ProtocolVersion >= 6) w.WriteByteArrayLP(UniqueIdForActor(playerRef));
        if (request.ProtocolVersion >= 7) w.WriteIntN(playerRef, 32);
        if (request.ProtocolVersion >= 9) { w.WriteByteArrayLP(null); w.WriteByteArrayLP(null); }
        if (request.ProtocolVersion >= 12) w.WriteIntN(0, 32);
        if (request.ProtocolVersion >= 3) w.WriteString("");
        return w.ToArray();
    }

    /// <summary>
    /// Builds a <c>Start</c> message (protocol id 5) — required separately from the Join
    /// Confirmation: the decompiled client's <c>HandleStartMessage</c> is what actually resolves
    /// its <c>StartGameOperation</c> task (the Join Confirmation alone only unblocks
    /// <c>ConfirmJoin()</c>, gating message processing, not session startup — confirmed live: a
    /// Confirmation-only reply pushed the client's failure from ~30s to exactly the client's own
    /// 120s StartGameOperation timeout instead of resolving it). <paramref name="startRequests"/>
    /// should stay <see cref="StartRequestsNone"/> for a solo host — setting ConnectToShared makes
    /// a Shared-mode client attempt Fusion's own raw-UDP relay transport (NetPeerGroup/NAT-punch),
    /// which isn't implemented server-side at all.
    /// </summary>
    /// <summary>
    /// Builds a <c>PlayerRefMapping</c> message (protocol id 8): tells a client that an actor's
    /// UniqueId maps to a PlayerRef, so it registers (and can spawn) that player. Photon Cloud's Fusion
    /// plugin sends one per peer as peers come and go; a self-hosted Shared session must do the same or
    /// clients relay state but never enter each other's world. Wire layout mirrors the SDK's
    /// <c>PlayerRefMapping.SerializeProtected</c> (ActorId:int, PlayerRef:int, UniqueId:byte[]) wrapped in
    /// the standard Message version header + trailing CustomData, exactly like the Join/Start encoders.
    /// </summary>
    public static byte[] EncodePlayerRefMapping(byte protocolVersion, int major, int minor, int build,
                                                int actorId, int playerRef, byte[] uniqueId)
    {
        var w = new FusionBitWriter();
        w.WriteBits(MsgIdPlayerRefMapping, 8);
        w.WriteBits(protocolVersion, 8);
        if (protocolVersion >= 2) { w.WriteIntN(major, 32); w.WriteIntN(minor, 32); w.WriteIntN(build, 32); }
        w.WriteIntN(actorId, 32);
        w.WriteIntN(playerRef, 32);
        w.WriteByteArrayLP(uniqueId);
        if (protocolVersion >= 3) w.WriteString("");
        return w.ToArray();
    }

    public static byte[] EncodeStart(byte protocolVersion, int major, int minor, int build,
                                     int remoteServerId, uint startRequests)
    {
        var w = new FusionBitWriter();
        w.WriteBits(MsgIdStart, 8);
        w.WriteBits(protocolVersion, 8);
        if (protocolVersion >= 2) { w.WriteIntN(major, 32); w.WriteIntN(minor, 32); w.WriteIntN(build, 32); }
        w.WriteIntN(remoteServerId, 32);
        w.WriteUIntN(startRequests, 32);
        if (protocolVersion >= 3) w.WriteString("");
        return w.ToArray();
    }
}

/// <summary>
/// Bit-packed reader matching Fusion.Protocol.BitStream's exact algorithm (LSB-first within each
/// byte, per-field variable bit widths, ints/uints written low-byte-first). Verified by decoding a
/// real captured client payload end-to-end with zero leftover bits beyond byte-alignment padding.
/// </summary>
public sealed class FusionBitReader
{
    private readonly byte[] _data;
    private int _ptr;

    public FusionBitReader(byte[] data) => _data = data;

    public byte ReadBits(int bits)
    {
        if (bits <= 0) return 0;
        int byteIdx = _ptr >> 3;
        int bitOff = _ptr % 8;
        byte result;
        if (bitOff == 0 && bits == 8) result = _data[byteIdx];
        else
        {
            int lo = _data[byteIdx] >> bitOff;
            int spillover = bits - (8 - bitOff);
            if (spillover < 1) result = (byte)(lo & (255 >> (8 - bits)));
            else
            {
                int hi = _data[byteIdx + 1] & (255 >> (8 - spillover));
                result = (byte)(lo | (hi << (bits - spillover)));
            }
        }
        _ptr += bits;
        return result;
    }

    public bool ReadBool() => ReadBits(1) == 1;

    public uint ReadUIntN(int bits)
    {
        int n0 = 0, n1 = 0, n2 = 0, n3 = 0;
        switch ((bits + 7) / 8)
        {
            case 1: n0 = ReadBits(bits); break;
            case 2: n0 = ReadBits(8); n1 = ReadBits(bits - 8); break;
            case 3: n0 = ReadBits(8); n1 = ReadBits(8); n2 = ReadBits(bits - 16); break;
            case 4: n0 = ReadBits(8); n1 = ReadBits(8); n2 = ReadBits(8); n3 = ReadBits(bits - 24); break;
        }
        return (uint)(n0 | (n1 << 8) | (n2 << 16) | (n3 << 24));
    }

    public int ReadIntN(int bits) => (int)ReadUIntN(bits);
    public ushort ReadUShort() => (ushort)ReadUIntN(16);

    public byte[]? ReadByteArrayLP()
    {
        if (!ReadBool()) return null;
        int len = ReadUShort();
        var arr = new byte[len];
        for (int i = 0; i < len; i++) arr[i] = ReadBits(8);
        return arr;
    }

    public string? ReadString()
    {
        if (ReadBool()) return null;
        int len = ReadUShort();
        if (len == 0) return "";
        var arr = new byte[len];
        for (int i = 0; i < len; i++) arr[i] = ReadBits(8);
        return Encoding.UTF8.GetString(arr);
    }
}

/// <summary>Bit-packed writer, the mirror of <see cref="FusionBitReader"/>.</summary>
public sealed class FusionBitWriter
{
    private byte[] _data = new byte[64];
    private int _ptr;

    public void WriteBits(byte value, int bits)
    {
        if (bits <= 0) return;
        EnsureCapacity(_ptr + bits);
        value = (byte)(value & (255 >> (8 - bits)));
        int byteIdx = _ptr >> 3;
        int bitOff = _ptr & 7;
        int room = 8 - bitOff;
        int spillover = room - bits;
        if (spillover >= 0)
        {
            int mask = (255 >> room) | (255 << (8 - spillover));
            _data[byteIdx] = (byte)((_data[byteIdx] & mask) | (value << bitOff));
        }
        else
        {
            _data[byteIdx] = (byte)((_data[byteIdx] & (255 >> room)) | (value << bitOff));
            _data[byteIdx + 1] = (byte)((_data[byteIdx + 1] & (255 << (bits - room))) | (value >> room));
        }
        _ptr += bits;
    }

    public void WriteBool(bool value) => WriteBits((byte)(value ? 1 : 0), 1);

    public void WriteUIntN(uint value, int bits)
    {
        byte b0 = (byte)value, b1 = (byte)(value >> 8), b2 = (byte)(value >> 16), b3 = (byte)(value >> 24);
        switch ((bits + 7) / 8)
        {
            case 1: WriteBits(b0, bits); break;
            case 2: WriteBits(b0, 8); WriteBits(b1, bits - 8); break;
            case 3: WriteBits(b0, 8); WriteBits(b1, 8); WriteBits(b2, bits - 16); break;
            case 4: WriteBits(b0, 8); WriteBits(b1, 8); WriteBits(b2, 8); WriteBits(b3, bits - 24); break;
        }
    }

    public void WriteIntN(int value, int bits) => WriteUIntN((uint)value, bits);
    public void WriteUShort(ushort value) => WriteUIntN(value, 16);

    public void WriteByteArrayLP(byte[]? array)
    {
        WriteBool(array != null);
        if (array == null) return;
        WriteUShort((ushort)array.Length);
        foreach (var b in array) WriteBits(b, 8);
    }

    public void WriteString(string? value)
    {
        WriteBool(value == null);
        if (value == null) return;
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUShort((ushort)bytes.Length);
        foreach (var b in bytes) WriteBits(b, 8);
    }

    private void EnsureCapacity(int bits)
    {
        int neededBytes = (bits + 7) / 8;
        if (neededBytes > _data.Length) Array.Resize(ref _data, Math.Max(neededBytes, _data.Length * 2));
    }

    public byte[] ToArray()
    {
        int byteLen = (_ptr + 7) / 8;
        var result = new byte[byteLen];
        Array.Copy(_data, result, byteLen);
        return result;
    }
}

/// <summary>
/// Codec for the OTHER Fusion channel — <c>OpRaiseEvent</c> code 101 ("DATA"), Fusion's own
/// separate low-level transport (<c>NetPeerGroup</c>/<c>NetSocketRelay</c>). Unlike the code-100
/// "protocol package" messages (bit-packed, see <see cref="FusionProtocol"/>), these are raw C
/// struct memory layouts (<c>[StructLayout(LayoutKind.Explicit)]</c>, native byte order — this
/// machine and the Switch/Steam client are both little-endian, so no translation needed) —
/// verified against a real Photon Cloud capture by mirroring the exact struct in an unsafe C#
/// type locally and reading captured bytes through it, not by hand-counting offsets. Only
/// Connect→Accepted is implemented: the minimal handshake needed for a solo host's own client to
/// stop timing out. This is NOT the real-time simulation/snapshot data itself — that rides the
/// same event code once a connection here is established, and remains unimplemented.
/// </summary>
public static class FusionRelayProtocol
{
    public const byte DataEventCode = 101;
    private const byte NetPacketTypeCommand = 1;
    private const byte NetPacketTypeData = 4;
    private const byte NetCommandConnect = 1, NetCommandAccepted = 2;

    // NetCommandConnect (Fusion.Sockets, decompiled): [FieldOffset(0)] byte PacketType,
    // [FieldOffset(1)] byte Command, [FieldOffset(4)] int TokenLength, [FieldOffset(8)]
    // NetConnectionId (8B), [FieldOffset(16)] byte TokenData[128], [FieldOffset(144)]
    // byte UniqueId[8]. Total 152 bytes — a real captured Connect matched this exactly.
    private const int ConnectSize = 152, ConnectUniqueIdOffset = 144, ConnectUniqueIdLength = 8;

    public sealed record ConnectRequest(byte[] UniqueId);

    /// <summary>Returns true for Fusion relay data packets. In a solo Shared-mode room the
    /// relay is a self-loop, so these packets must be delivered back to the sole actor.</summary>
    public static bool IsDataPacket(byte[] payload) => payload.Length >= 14 && payload[0] == NetPacketTypeData;

    /// <summary>Decodes a code-101 payload as a <c>NetCommandConnect</c>, or null if it isn't one
    /// (wrong packet/command type, or too short) — never throws, this is untrusted network input.</summary>
    public static ConnectRequest? TryDecodeConnect(byte[] payload)
    {
        if (payload.Length < ConnectSize) return null;
        if (payload[0] != NetPacketTypeCommand || payload[1] != NetCommandConnect) return null;
        return new ConnectRequest(payload[ConnectUniqueIdOffset..(ConnectUniqueIdOffset + ConnectUniqueIdLength)]);
    }

    /// <summary>
    /// Builds a <c>NetCommandAccepted</c> reply. Field values (Group=0, GroupIndex=0,
    /// Generation=1 for both AcceptedLocalId and AcceptedRemoteId, Counter=1) are copied exactly
    /// from a real Photon Cloud server's Accepted reply to a solo host's own Connect, captured and
    /// decoded live — see earlier-outbound/HANDOFF.md.
    /// </summary>
    public static byte[] EncodeAccepted()
    {
        var b = new byte[32];
        b[0] = NetPacketTypeCommand;
        b[1] = NetCommandAccepted;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), 1);   // AcceptedLocalId.Generation
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), 1);   // AcceptedRemoteId.Generation
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), 1);   // Counter
        return b;
    }
}
