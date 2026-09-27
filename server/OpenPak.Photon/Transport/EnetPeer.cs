using System.Buffers.Binary;

namespace OpenPak.Photon.Transport;

/// <summary>
/// Per-peer eNet transport state: the CONNECT/VERIFYCONNECT handshake, reliable-command
/// acknowledgements, per-channel outgoing sequence numbers, and reliable-fragment reassembly.
/// </summary>
public sealed class EnetPeer
{
    private static int _peerCounter;

    // Reassemblies keyed by (channel, startSeq): unsequenced fragment groups (type 15) arrive and
    // are retransmitted interleaved, so several can be in flight on one channel at once.
    // ponytail: never-completed groups are never evicted; add an age sweep if a peer leaks them.
    private sealed class FragmentAssembly
    {
        public int FragmentCount;
        public byte[] Buffer = Array.Empty<byte>();
        public bool[] Received = Array.Empty<bool>();
        public int ReceivedCount;
    }
    private readonly Dictionary<(byte channel, int startSeq), FragmentAssembly> _fragments = new();
    // Guards the outgoing sequence dictionaries below. The relay is single-threaded by design (the
    // Game listener thread), but a stray cross-thread send (a console command, a timer, a future
    // code path) that corrupts these maps stamps a bad sequence number — which the client treats as
    // an unacked reliable command and force-disconnects the live peer ~10s later. This lock is
    // defense in depth so no caller can silently corrupt sequence state. It protects ONLY these two
    // dictionaries; it intentionally does not change wire behavior.
    private readonly object _seqLock = new();
    private readonly Dictionary<byte, int> _outgoingSeq = new();
    private readonly Dictionary<byte, int> _outgoingUnreliableSeq = new();

    public short PeerId { get; private set; } = -1;
    /// <summary>The client's per-connection challenge, echoed in every outgoing packet header.</summary>
    public int Challenge { get; private set; }

    public void NoteChallenge(int challenge) => Challenge = challenge;

    /// <summary>
    /// Processes one incoming command. Returns control commands to send back (VERIFYCONNECT, ACK);
    /// if the command carried an application payload (reliable send), it is returned via <paramref name="appPayload"/>.
    /// </summary>
    public List<NCommand> HandleCommand(NCommand cmd, int incomingSentTime, out byte[]? appPayload)
    {
        appPayload = null;
        var outgoing = new List<NCommand>();

        // eNet rule: EVERY reliable command must be acknowledged, regardless of type. The client
        // buffers each unacked reliable command and retransmits it with exponential backoff; after
        // the resend timeout (~10s) it force-disconnects the peer — even mid-game. The control
        // channel (255) carries reliable Connect + CT_EG_SERVERTIME(12) commands that we were not
        // acking, which silently killed in-room sessions ~10s after join. CT_ACK is itself never
        // acked. Connect additionally gets a VerifyConnect below.
        if ((cmd.Flags & NCommand.FlagReliable) != 0 && cmd.CommandType != NCommand.Acknowledge)
            outgoing.Add(Ack(cmd, incomingSentTime));

        switch (cmd.CommandType)
        {
            case NCommand.Connect:
                if (PeerId < 0) PeerId = (short)Interlocked.Increment(ref _peerCounter);
                outgoing.Add(VerifyConnect());
                break;
            case NCommand.SendReliable:
            case NCommand.SendReliableUnsequenced:
            case NCommand.SendUnreliable:
            case NCommand.SendUnsequenced:
                appPayload = cmd.Payload;
                break;
            case NCommand.SendFragment:
            case NCommand.SendFragmentUnsequenced:
                // Fusion sends every relay packet above the MTU as type 15 (two fragments). Unhandled,
                // they were dropped AND acked with the wrong type, so the host kept retransmitting the
                // world-sync burst until its ~10 s reliable timeout (ShutdownReason 1014).
                appPayload = HandleFragment(cmd);
                break;
                // Ping (5), CT_EG_SERVERTIME (12), etc.: the reliable ACK above is all the transport
                // needs to keep the peer alive. Acknowledge (1) and Disconnect (4) emit nothing.
        }
        return outgoing;
    }

    /// <summary>
    /// Reassembles a SendFragment command. Payload is a 20-byte fragment sub-header (startSeq,
    /// fragmentCount, fragmentNumber, totalLength, fragmentOffset, all big-endian int32 — verified
    /// against a real capture; there is no separate fragmentLength field, it's implicit as
    /// payload.Length - 20) followed by that fragment's slice of the data. Returns the full
    /// reassembled payload once every fragment in the group has arrived, else null.
    /// Malformed/out-of-range fragments are dropped (return null) rather than throwing — this is
    /// untrusted network input.
    /// </summary>
    private byte[]? HandleFragment(NCommand cmd)
    {
        var p = cmd.Payload;
        if (p.Length < 20) return null;
        int startSeq = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(0));
        int fragmentCount = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(4));
        int fragmentNumber = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(8));
        int totalLength = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(12));
        int fragmentOffset = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(16));
        var data = p.AsSpan(20);
        int fragmentLength = data.Length;
        if (fragmentCount <= 0 || totalLength < 0 ||
            fragmentOffset < 0 || fragmentOffset + fragmentLength > totalLength ||
            fragmentNumber < 0 || fragmentNumber >= fragmentCount)
            return null;

        var key = (cmd.ChannelId, startSeq);
        if (!_fragments.TryGetValue(key, out var asm))
        {
            asm = new FragmentAssembly
            {
                FragmentCount = fragmentCount,
                Buffer = new byte[totalLength],
                Received = new bool[fragmentCount],
            };
            _fragments[key] = asm;
        }
        if (fragmentCount != asm.FragmentCount || totalLength != asm.Buffer.Length) return null;   // inconsistent group

        if (!asm.Received[fragmentNumber])
        {
            data.CopyTo(asm.Buffer.AsSpan(fragmentOffset));
            asm.Received[fragmentNumber] = true;
            asm.ReceivedCount++;
        }

        if (asm.ReceivedCount < asm.FragmentCount) return null;
        _fragments.Remove(key);
        return asm.Buffer;
    }

    /// <summary>
    /// The client's UDP receive buffer is exactly one MTU (Photon's <c>SocketUdp.ReceiveLoop</c> reads
    /// into <c>new byte[MTU]</c>), and its default MTU is 1200. A bigger datagram is truncated and then
    /// thrown away by its command parser — measured against the real client library, which logs
    /// "Exception while reading commands from incoming data: ArgumentException". Nothing we send may
    /// exceed this, and anything larger goes out as eNet fragments.
    /// </summary>
    public const int Mtu = 1200;

    /// <summary>Bytes of application payload that fit in one fragment: the client's own
    /// <c>GetFragmentLength()</c> is <c>mtu - 12 - 36</c> (datagram header + command header + the
    /// 20-byte fragment sub-header, rounded as Photon does it).</summary>
    public const int FragmentPayload = Mtu - 12 - 36;

    /// <summary>Wraps an application payload ([0xF3]... message) as a reliable command on channel 0.</summary>
    public NCommand WrapReliable(byte[] payload, byte channel = 0)
    {
        lock (_seqLock)
            return new(NCommand.SendReliable, channel, NCommand.FlagReliable, 4, NextSeq(channel), payload);
    }

    /// <summary>
    /// Wraps an application payload as reliable command(s), splitting it into eNet fragments
    /// (type 8) when it does not fit one datagram. Fragments take consecutive sequence numbers on
    /// the channel and all carry the first one's sequence as <c>startSequenceNumber</c>, which is
    /// how the client reassembles them (it keys the group on that and dispatches when the start
    /// fragment is the channel's next expected sequence).
    /// </summary>
    public List<NCommand> WrapReliableFragmented(byte[] payload, byte channel = 0)
    {
        if (payload.Length <= FragmentPayload) return new() { WrapReliable(payload, channel) };

        int fragmentCount = (payload.Length + FragmentPayload - 1) / FragmentPayload;
        var commands = new List<NCommand>(fragmentCount);
        lock (_seqLock)
        {
            int startSeq = _outgoingSeq.TryGetValue(channel, out int n) ? n + 1 : 1;
            for (int i = 0, offset = 0; i < fragmentCount; i++, offset += FragmentPayload)
            {
                int length = Math.Min(FragmentPayload, payload.Length - offset);
                var body = new byte[20 + length];
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(0), startSeq);
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(4), fragmentCount);
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(8), i);
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(12), payload.Length);
                BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(16), offset);
                payload.AsSpan(offset, length).CopyTo(body.AsSpan(20));
                commands.Add(new NCommand(NCommand.SendFragment, channel, NCommand.FlagReliable, 4,
                                          NextSeq(channel), body));
            }
        }
        return commands;
    }

    /// <summary>
    /// Wraps an application payload as an UNRELIABLE command (type 7) on the given channel. Stamps the
    /// per-channel monotonically increasing unreliable sequence the client requires (else it discards
    /// the packet as stale/duplicate) and the channel's current reliable sequence (the client only
    /// delivers an unreliable command once its reliableSeq has been reached). Not acked.
    /// </summary>
    public NCommand WrapUnreliable(byte[] payload, byte channel = 0)
    {
        lock (_seqLock)
        {
            _outgoingUnreliableSeq.TryGetValue(channel, out int u);
            u++;
            _outgoingUnreliableSeq[channel] = u;
            _outgoingSeq.TryGetValue(channel, out int reliableSoFar);
            return new NCommand(NCommand.SendUnreliable, channel, 0, 4, reliableSoFar, payload) { UnreliableSequenceNumber = u };
        }
    }

    /// <summary>
    /// Builds a server-initiated keepalive Ping (RTT probe) on the control channel. The client's
    /// eNet layer acks it like any reliable command; we use it to actively probe a peer that's gone
    /// quiet before deciding it's dead. (The client also pings us on its own ~1s cadence.)
    /// </summary>
    public NCommand Ping()
    {
        lock (_seqLock)
            return new(NCommand.Ping, 0xFF, NCommand.FlagReliable, 4, NextSeq(0xFF), Array.Empty<byte>());
    }

    /// <summary>Builds a server-initiated reliable Disconnect command — tells the client the server is
    /// tearing the connection down (a hard kick), rather than letting it discover the silence on timeout.</summary>
    public NCommand Disconnect()
    {
        lock (_seqLock)
            return new(NCommand.Disconnect, 0xFF, NCommand.FlagReliable, 4, NextSeq(0xFF), Array.Empty<byte>());
    }

    private NCommand VerifyConnect()
    {
        var payload = new byte[32];                       // client reads peerId then skips 30 bytes
        BinaryPrimitives.WriteInt16BigEndian(payload, PeerId);
        lock (_seqLock)
            return new NCommand(NCommand.VerifyConnect, 0xFF, NCommand.FlagReliable, 4, NextSeq(0xFF), payload);
    }

    private static NCommand Ack(NCommand acked, int sentTime)
    {
        var payload = new byte[8];                        // [ackedReliableSeq][ackedSentTime], big-endian
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0), acked.ReliableSequenceNumber);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), sentTime);
        // The client matches an ack against its sent list by (seq, channel, unsequenced?) where
        // "unsequenced?" comes from the ACK TYPE: 1 for sequenced, 16 for reliable-unsequenced (14/15).
        byte type = (acked.Flags & NCommand.FlagUnsequenced) != 0 ? NCommand.AcknowledgeUnsequenced : NCommand.Acknowledge;
        return new NCommand(type, acked.ChannelId, 0, 4, acked.ReliableSequenceNumber, payload);
    }

    /// <summary>Returns the next reliable sequence for a channel. Callers must hold <c>_seqLock</c>.</summary>
    private int NextSeq(byte channel)
    {
        _outgoingSeq.TryGetValue(channel, out int n);
        n++;
        _outgoingSeq[channel] = n;
        return n;
    }
}
