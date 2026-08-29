using System.Buffers.Binary;

namespace BlackIce.Photon.Transport;

/// <summary>
/// Per-peer eNet transport state: the CONNECT/VERIFYCONNECT handshake, reliable-command
/// acknowledgements, per-channel outgoing sequence numbers, and reliable-fragment reassembly.
/// </summary>
public sealed class EnetPeer
{
    private static int _peerCounter;

    // One in-flight reassembly per channel: eNet only fragments reliable sends, and a channel
    // delivers them in order, so a second fragment group can't start until the first finishes.
    private sealed class FragmentAssembly
    {
        public int StartSequenceNumber;
        public int FragmentCount;
        public byte[] Buffer = Array.Empty<byte>();
        public bool[] Received = Array.Empty<bool>();
        public int ReceivedCount;
    }
    private readonly Dictionary<byte, FragmentAssembly> _fragments = new();
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
                appPayload = cmd.Payload;
                break;
            case NCommand.SendUnreliable:
                appPayload = cmd.Payload;
                break;
            case NCommand.SendFragment:
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

        if (!_fragments.TryGetValue(cmd.ChannelId, out var asm) || asm.StartSequenceNumber != startSeq)
        {
            asm = new FragmentAssembly
            {
                StartSequenceNumber = startSeq,
                FragmentCount = fragmentCount,
                Buffer = new byte[totalLength],
                Received = new bool[fragmentCount],
            };
            _fragments[cmd.ChannelId] = asm;
        }

        if (!asm.Received[fragmentNumber])
        {
            data.CopyTo(asm.Buffer.AsSpan(fragmentOffset));
            asm.Received[fragmentNumber] = true;
            asm.ReceivedCount++;
        }

        if (asm.ReceivedCount < asm.FragmentCount) return null;
        _fragments.Remove(cmd.ChannelId);
        return asm.Buffer;
    }

    /// <summary>Wraps an application payload ([0xF3]... message) as a reliable command on channel 0.</summary>
    public NCommand WrapReliable(byte[] payload, byte channel = 0)
    {
        lock (_seqLock)
            return new(NCommand.SendReliable, channel, NCommand.FlagReliable, 4, NextSeq(channel), payload);
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
        return new NCommand(NCommand.Acknowledge, acked.ChannelId, 0, 4, acked.ReliableSequenceNumber, payload);
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
