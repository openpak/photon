namespace OpenPak.Server.LoadBalancing.Fusion;

/// <summary><c>NetConnectionStatus</c> (spec §5). Connecting is client-only; Created is the instant
/// between allocating a slot and answering the Connect that caused it.</summary>
public enum NetConnectionStatus { Created = 1, Connecting = 2, Connected = 3, Disconnected = 4, Shutdown = 5 }

/// <summary>
/// One Fusion client's connection to the server side of the relay transport: its slot ids, Counter,
/// and the notify / fragment / reliable-channel state of spec §6-§8. The simulation layer reads the
/// public members and passes the object back to <see cref="FusionRelayPeer"/>; all state changes are
/// the peer's.
/// </summary>
public sealed class FusionRelayConnection
{
    internal FusionRelayConnection(int actor, int playerId, ulong uniqueId, byte[] connectToken, NetConnectionId localId)
    {
        Actor = actor;
        PlayerId = playerId;
        UniqueId = uniqueId;
        ConnectToken = connectToken;
        LocalId = localId;
    }

    /// <summary>The Photon actor this connection's datagrams arrive from and are sent to.</summary>
    public int Actor { get; internal set; }

    /// <summary>The player id given at registration — the value this player's reliable slices carry in
    /// <see cref="ReliableId.Source"/>/<see cref="ReliableId.Target"/>.</summary>
    public int PlayerId { get; }

    /// <summary>The 8-byte UniqueId from the client's Connect, as a little-endian uint64.</summary>
    public ulong UniqueId { get; }

    /// <summary>The game's opaque connection token (≤ 128 bytes, often empty).</summary>
    public byte[] ConnectToken { get; }

    /// <summary>The server's id for this connection (Accepted's AcceptedLocalId).</summary>
    public NetConnectionId LocalId { get; }

    /// <summary>The client's own id, echoed in Accepted's AcceptedRemoteId.</summary>
    public NetConnectionId RemoteId { get; internal set; }

    /// <summary>The client's NetworkId namespace (spec §5.3): unique per connection, never 0.</summary>
    public uint Counter { get; internal set; }

    public NetConnectionStatus Status { get; internal set; } = NetConnectionStatus.Created;

    /// <summary>Why the connection ended; meaningful once <see cref="Status"/> is Disconnected or later.</summary>
    public NetDisconnectReason DisconnectReason { get; internal set; }

    /// <summary>Last measured round trip in seconds (spec §6.5): unsmoothed, 0.1 until the first exact ack.</summary>
    public double Rtt { get; internal set; } = 0.1;

    /// <summary>True when <see cref="FusionRelayPeer.TrySendNotify"/> would accept a packet now — check it
    /// before building one, the way the SDK simulation skips a tick when the window is full.</summary>
    public bool CanSendNotify => Status == NetConnectionStatus.Connected && SendWindow.Count < FusionRelayPeer.NotifyWindowSize;

    // Notify channel (spec §6).
    internal ushort SendSequence;
    internal ushort RecvSequence;
    internal ulong RecvMask;
    internal int RecvUnacked;
    internal double LastNotifySend;
    internal double LastRecv;
    internal readonly Queue<NotifyEnvelope> SendWindow = new();

    // Fragment reassembly (spec §7). The buffer holds the first piece's header followed by every chunk.
    internal byte[]? FragmentBuffer;
    internal int FragmentLength;
    internal int FragmentIndex;
    internal int FragmentDistance;

    // Reliable channel (spec §8).
    internal readonly LinkedList<ReliableSlice> ReliableQueue = new();
    internal uint ReliableSendSequence;
    internal uint ReliableLastDelivered;
    internal readonly Dictionary<uint, ReliableSlice> ReliableStore = new();
    internal double LastReliableSend = double.NegativeInfinity;

    // Teardown (spec §5.4).
    internal bool FollowUpDisconnectPending;
    internal double ReleaseAt;

    internal void ResetFragments()
    {
        FragmentLength = 0;
        FragmentIndex = 0;
        FragmentDistance = 0;
    }
}

/// <summary>A send-window entry: one NotifyData (token = the simulation's) or NotifyReliableData (token =
/// the <see cref="ReliableSlice"/> to requeue if lost).</summary>
internal readonly record struct NotifyEnvelope(ushort Sequence, double SendTime, NetPacketType Type, object? Token);

/// <summary>One reliable-channel slice: its id (Sequence already assigned for the receiving side) and bytes.</summary>
internal sealed record ReliableSlice(ReliableId Id, byte[] Data);
